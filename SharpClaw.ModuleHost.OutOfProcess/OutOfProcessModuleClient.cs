using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Contains one completed action exchange and its optional durable continuation.</summary>
public sealed record OutOfProcessActionResult(
    HookCompleted Completion,
    ContinuationToken? Continuation);

/// <summary>Invokes one authorized .NET module sidecar.</summary>
public sealed class OutOfProcessModuleClient : IAsyncDisposable
{
    private readonly Uri _controlAddress;
    private readonly string _controlToken;
    private readonly HttpClient _httpClient;
    private OutOfProcessCapabilityHostSession? _capabilitySession;
    private Task? _capabilityRun;
    private OutOfProcessHostActionEntryContextRegistry? _hostActionEntryContexts;

    private OutOfProcessModuleClient(
        Uri controlAddress,
        string controlToken,
        HttpClient httpClient,
        SidecarDiscoveryEnvelope discovery,
        SidecarApplicationDiscovery application,
        SidecarHostAuthorization authorization,
        SidecarPayloadLimits hostLimits)
    {
        _controlAddress = controlAddress;
        _controlToken = controlToken;
        _httpClient = httpClient;
        Discovery = discovery;
        Application = application;
        Authorization = authorization;
        HostLimits = hostLimits;
    }

    /// <summary>Gets the validated module discovery.</summary>
    public SidecarDiscoveryEnvelope Discovery { get; }

    /// <summary>Gets the typed endpoint and CLI contributions from the same graph.</summary>
    public SidecarApplicationDiscovery Application { get; }

    /// <summary>Gets the exact grants issued for this client.</summary>
    public SidecarHostAuthorization Authorization { get; }

    /// <summary>Gets the host payload limits.</summary>
    public SidecarPayloadLimits HostLimits { get; }

    /// <summary>Gets the one-use host context registry for this capability binding.</summary>
    public OutOfProcessHostActionEntryContextRegistry HostActionEntryContexts =>
        Volatile.Read(ref _hostActionEntryContexts)
        ?? throw new InvalidOperationException(
            "The sidecar capability channel is not connected.");

    /// <summary>Creates a capability grant for this authorized module.</summary>
    public SidecarCapabilityGrant CreateCapabilityGrant(
        DateTimeOffset? expiresAt = null) =>
        OutOfProcessCapabilityGrantFactory.Create(
            Discovery,
            Authorization,
            expiresAt);

    /// <summary>Issues one host context for a typed ingress carrier.</summary>
    public HostActionEntryRequestContext IssueHostActionContext<TAction, TResult>(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string secondaryIdentity,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        DateTimeOffset deadline,
        Guid? invocationId = null) =>
        HostActionEntryContexts.Issue(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            descriptor,
            action,
            caller,
            features,
            deadline,
            invocationId);

    /// <summary>Discovers and authorizes one sidecar against immutable host descriptors.</summary>
    public static async Task<OutOfProcessModuleClient> CreateAuthorizedAsync(
        Uri controlAddress,
        string controlToken,
        SidecarHostDescriptorCatalog hostCatalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(controlAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        ArgumentNullException.ThrowIfNull(hostCatalog);
        var http = new HttpClient
        {
            BaseAddress = controlAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Add(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            controlToken);
        try
        {
            var document = await http.GetFromJsonAsync<SidecarDiscoveryDocument>(
                OutOfProcessModuleHostProtocol.DiscoveryPath,
                OutOfProcessProtocolCodec.JsonOptions,
                ct)
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar returned no discovery envelope.");
            var discovery = document.ToDiscovery();
            var authorization = SidecarAuthorizationFactory.Create(discovery, hostCatalog);
            var decision = SidecarMessageHeaderFactory.CreateMeasured(
                hostCatalog.NegotiatedProtocolVersion,
                sequence: 2,
                DateTimeOffset.UtcNow.AddMinutes(1),
                hostCatalog.PayloadLimits.ProtocolMessageBytes,
                header => new SidecarDiscoveryDecision(
                    header,
                    discovery.ModuleId,
                    Accepted: true,
                    authorization));
            using var response = await http.PostAsJsonAsync(
                OutOfProcessModuleHostProtocol.AuthorizationPath,
                decision,
                OutOfProcessProtocolCodec.JsonOptions,
                ct);
            response.EnsureSuccessStatusCode();
            return new OutOfProcessModuleClient(
                controlAddress,
                controlToken,
                http,
                discovery,
                document.Application,
                authorization,
                hostCatalog.PayloadLimits);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>Connects one host-owned dispatcher and storage gateway to the sidecar.</summary>
    public async Task ConnectCapabilitiesAsync(
        OutOfProcessCapabilityHostOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.CompareExchange(ref _capabilitySession, null, null) is not null)
            throw new InvalidOperationException("The sidecar capability channel is already connected.");
        var grant = options.Grant;
        if (!string.Equals(grant.GraphId, Discovery.ContractHash, StringComparison.Ordinal))
            throw UnauthorizedGrant("The capability grant graph identity does not match discovery.");
        if (!string.Equals(grant.ModuleId, Discovery.ModuleId, StringComparison.Ordinal))
            throw UnauthorizedGrant("The capability grant module identity does not match discovery.");
        if (!grant.Allows(SidecarCapabilityKind.Action) || !grant.Allows(SidecarCapabilityKind.Storage))
            throw UnauthorizedGrant("The capability grant does not include the required capabilities.");
        if (!string.Equals(
                grant.AuthorizationHash,
                OutOfProcessCapabilitySecurity.ComputeAuthorizationHash(Authorization),
                StringComparison.Ordinal))
            throw UnauthorizedGrant("The capability grant authorization identity does not match.");

        static OutOfProcessCapabilityException UnauthorizedGrant(string message) =>
            new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                message);

        var binding = OutOfProcessCapabilitySecurity.CreateBinding(
            Discovery.ContractHash,
            Discovery.ModuleId,
            OutOfProcessModuleHostProtocol.Version,
            grant,
            HostLimits,
            _controlToken);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        try
        {
            await socket.ConnectAsync(CapabilitiesUri(), ct);
            var sendGate = new SemaphoreSlim(1, 1);
            try
            {
                await OutOfProcessCapabilityWire.SendAsync(
                    socket,
                    OutOfProcessCapabilityFrameKind.Bind,
                    binding,
                    HostLimits.ProtocolMessageBytes,
                    sendGate,
                    ct);
                var frame = await OutOfProcessCapabilityWire.ReceiveAsync(
                    socket,
                    HostLimits.ProtocolMessageBytes,
                    ct);
                if (string.Equals(frame.Kind, OutOfProcessCapabilityFrameKind.Error, StringComparison.Ordinal))
                {
                    var failure = OutOfProcessCapabilityWire.Deserialize<SidecarSafeFailureIdentity>(
                        frame.Payload);
                    throw new OutOfProcessCapabilityException(failure.Code, failure.Message);
                }
                if (!string.Equals(
                        frame.Kind,
                        OutOfProcessCapabilityFrameKind.BindAccepted,
                        StringComparison.Ordinal))
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Unauthorized,
                        "The sidecar did not accept the capability binding.");
                }

                var accepted = OutOfProcessCapabilityWire.Deserialize<SidecarCapabilityValidationResult>(
                    frame.Payload);
                if (!accepted.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        accepted.Code ?? SidecarCapabilityErrors.Unauthorized,
                        accepted.Message ?? "The sidecar rejected the capability binding.");
                }

                options.HostActionEntryContexts.Bind(binding);
            }
            finally
            {
                sendGate.Dispose();
            }

            var session = new OutOfProcessCapabilityHostSession(
                socket,
                binding,
                _controlToken,
                HostLimits,
                options,
                Authorization);
            _capabilitySession = session;
            Volatile.Write(ref _hostActionEntryContexts, options.HostActionEntryContexts);
            _capabilityRun = session.RunAsync(CancellationToken.None);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Runs one action hook exchange with one host-owned continuation callback.</summary>
    public async ValueTask<OutOfProcessActionResult> InvokeActionAsync(
        HookInvokeStart start,
        Func<
            SidecarEffectRequest,
            CancellationToken,
            ValueTask<(ContinuationAccepted Accepted, ContinuationOutcome Outcome)>> continuation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(continuation);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        await socket.ConnectAsync(ExchangeUri(), ct);
        var state = new SidecarProtocolState(
            SidecarExchangeKind.ActionHook,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Negotiated,
            LastSequence: 0,
            start.Header.Deadline,
            start.Header.ProtocolVersion,
            HostLimits,
            HostAuthorization: Authorization);
        var protocol = new OutOfProcessProtocolSession(socket, state);
        try
        {
            await protocol.SendAsync(start, ct: ct);
            ContinuationOutcome? hostOutcome = null;
            var frame = await protocol.ReceiveAsync(ct);
            if (frame.Message is SidecarEffectRequest request)
            {
                var response = await continuation(request, ct);
                await protocol.SendAsync(response.Accepted, ct: ct);
                await protocol.SendAsync(response.Outcome, ct: ct);
                hostOutcome = response.Outcome;
                frame = await protocol.ReceiveAsync(ct);
            }

            if (frame.Message is SidecarProtocolError protocolError)
                throw Error(protocolError);

            SidecarResultReplacement? replacement = null;
            HookOutcome? sidecarOutcome = null;
            switch (frame.Message)
            {
                case SidecarResultReplacement directReplacement:
                    replacement = directReplacement;
                    break;
                case HookOutcome hookOutcome:
                    sidecarOutcome = hookOutcome;
                    if (frame.HasFollowingMessage)
                    {
                        var replacementFrame = await protocol.ReceiveAsync(ct);
                        replacement = replacementFrame.Message as SidecarResultReplacement
                            ?? throw new OutOfProcessProtocolException(
                                SidecarProtocolErrors.MalformedMessage,
                                "The sidecar declared a missing result replacement.");
                    }
                    break;
                default:
                    throw new OutOfProcessProtocolException(
                        SidecarProtocolErrors.MalformedMessage,
                        "The sidecar did not return an action hook outcome.");
            }

            var completed = CreateCompleted(protocol, hostOutcome, sidecarOutcome, replacement);
            await protocol.SendAsync(completed.Completion, ct: ct);
            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
            return completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelAsync(protocol, socket);
            throw;
        }
    }

    /// <summary>Invokes one authorized module CLI contribution through the sidecar.</summary>
    public async ValueTask<SidecarCliExecutionResponse> InvokeCliAsync(
        string command,
        IReadOnlyList<string> arguments,
        HostActionEntryRequestContext hostActionContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(hostActionContext);
        var now = DateTimeOffset.UtcNow;
        if (!hostActionContext.IsWellFormed(now)
            || hostActionContext.Ingress != HostActionEntryIngress.Cli
            || hostActionContext.Contribution is null
            || !string.Equals(
                hostActionContext.Contribution.IngressBinding.PrimaryIdentity,
                command,
                StringComparison.Ordinal)
            || hostActionContext.InvocationId == Guid.Empty)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The CLI host action context is invalid for the requested command.");
        }
        if (!Application.CliCommands.Any(item =>
                string.Equals(item.Descriptor.Name, command, StringComparison.Ordinal)
                || item.Descriptor.Aliases.Contains(command, StringComparer.Ordinal)))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"CLI command '{command}' is not declared by the sidecar.");
        }

        var request = new SidecarCliInvocation(
            Guid.NewGuid(),
            Discovery.ModuleId,
            Discovery.ContractHash,
            command,
            arguments,
            hostActionContext);
        using var response = await _httpClient.PostAsJsonAsync(
            OutOfProcessModuleHostProtocol.ApplicationCliPath,
            request,
            OutOfProcessProtocolCodec.JsonOptions,
            ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SidecarCliExecutionResponse>(
                OutOfProcessProtocolCodec.JsonOptions,
                ct)
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The sidecar returned no CLI execution response.");
    }

    /// <summary>Runs one event interceptor exchange.</summary>
    public async ValueTask<EventInterceptOutcome> InterceptEventAsync(
        EventInterceptStart start,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        await socket.ConnectAsync(ExchangeUri(), ct);
        var state = new SidecarProtocolState(
            SidecarExchangeKind.EventIntercept,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Negotiated,
            LastSequence: 0,
            start.Header.Deadline,
            start.Header.ProtocolVersion,
            HostLimits,
            HostAuthorization: Authorization);
        var protocol = new OutOfProcessProtocolSession(socket, state);
        try
        {
            await protocol.SendAsync(start, ct: ct);
            var frame = await protocol.ReceiveAsync(ct);
            if (frame.Message is SidecarProtocolError protocolError)
                throw Error(protocolError);
            if (frame.Message is not EventInterceptOutcome outcome)
            {
                throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar did not return an event interception outcome.");
            }

            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCancelAsync(protocol, socket);
            throw;
        }
    }

    /// <summary>Delivers one event to one selected module listener.</summary>
    public async ValueTask<SidecarEventListenerAcknowledgement?> DeliverEventAsync(
        SidecarEventListenerDelivery delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        await socket.ConnectAsync(ExchangeUri(), ct);
        var descriptor = delivery.Envelope.Descriptor;
        var state = new SidecarProtocolState(
            SidecarExchangeKind.EventListener,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Negotiated,
            LastSequence: 0,
            delivery.Header.Deadline,
            delivery.Header.ProtocolVersion,
            HostLimits,
            DeliveryId: delivery.DeliveryId,
            ListenerId: delivery.ListenerId,
            Delivery: delivery.Delivery,
            EventKey: descriptor.Key,
            EventVersion: descriptor.Version,
            EventDescriptor: descriptor,
            HostAuthorization: Authorization);
        var protocol = new OutOfProcessProtocolSession(socket, state);
        try
        {
            await protocol.SendAsync(delivery, ct: ct);
            if (!delivery.RequiresAcknowledgement)
            {
                await WaitForCompletedCloseAsync(protocol, socket, ct);
                return null;
            }

            var frame = await protocol.ReceiveAsync(ct);
            if (frame.Message is SidecarProtocolError protocolError)
                throw Error(protocolError);
            if (frame.Message is not SidecarEventListenerAcknowledgement acknowledgement)
            {
                throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar did not return an event listener acknowledgement.");
            }

            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
            return acknowledgement;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCloseAsync(socket, "cancelled");
            throw;
        }
    }

    /// <summary>Invokes one discovered module tool handler.</summary>
    public async ValueTask<SidecarToolHandlerResult> InvokeToolAsync(
        SidecarToolHandlerInvokeStart start,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var now = DateTimeOffset.UtcNow;
        var hostActionContext = start.HostActionContext;
        if (!start.IsWellFormed(now)
            || hostActionContext is null
            || !hostActionContext.IsWellFormed(now)
            || hostActionContext.Ingress != HostActionEntryIngress.Tool
            || hostActionContext.InvocationId != start.InvocationId
            || hostActionContext.Deadline != start.Header.Deadline
            || hostActionContext.Contribution is null
            || !string.Equals(
                hostActionContext.Contribution.IngressBinding.PrimaryIdentity,
                start.ToolName,
                StringComparison.Ordinal)
            || !OutOfProcessHostActionEntryContextRegistry.MatchesCaller(
                hostActionContext.Caller,
                start.Caller))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The tool host action context is invalid for the requested tool.");
        }
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        await socket.ConnectAsync(ExchangeUri(), ct);
        var state = new SidecarProtocolState(
            SidecarExchangeKind.ToolHandler,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Negotiated,
            LastSequence: 0,
            start.Header.Deadline,
            start.Header.ProtocolVersion,
            HostLimits,
            HostAuthorization: Authorization);
        var protocol = new OutOfProcessProtocolSession(socket, state);
        try
        {
            await protocol.SendAsync(start, ct: ct);
            var frame = await protocol.ReceiveAsync(ct);
            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
            return frame.Message switch
            {
                SidecarToolHandlerResult result => result,
                SidecarToolHandlerCancelled cancelled => throw new OutOfProcessProtocolException(
                    cancelled.Code,
                    cancelled.Message),
                SidecarToolHandlerFailed failed => throw new OutOfProcessProtocolException(
                    failed.Error.Code,
                    failed.Error.Message),
                SidecarProtocolError protocolError => throw Error(protocolError),
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar did not return a tool handler result."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCloseAsync(socket, "cancelled");
            throw;
        }
    }

    /// <summary>Invokes one discovered module lifecycle handler.</summary>
    public async ValueTask<SidecarLifecycleHandlerResult> InvokeLifecycleAsync(
        SidecarLifecycleHandlerInvokeStart start,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            OutOfProcessModuleHostProtocol.TokenHeaderName,
            _controlToken);
        await socket.ConnectAsync(ExchangeUri(), ct);
        var state = new SidecarProtocolState(
            SidecarExchangeKind.LifecycleHandler,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Negotiated,
            LastSequence: 0,
            start.Header.Deadline,
            start.Header.ProtocolVersion,
            HostLimits,
            HostAuthorization: Authorization);
        var protocol = new OutOfProcessProtocolSession(socket, state);
        try
        {
            await protocol.SendAsync(start, ct: ct);
            var frame = await protocol.ReceiveAsync(ct);
            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
            return frame.Message switch
            {
                SidecarLifecycleHandlerResult result => result,
                SidecarLifecycleHandlerCancelled cancelled =>
                    throw new OutOfProcessProtocolException(
                        cancelled.Code,
                        cancelled.Message),
                SidecarLifecycleHandlerFailed failed => throw new OutOfProcessProtocolException(
                    failed.Error.Code,
                    failed.Error.Message),
                SidecarProtocolError protocolError => throw Error(protocolError),
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar did not return a lifecycle handler result."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCloseAsync(socket, "cancelled");
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_capabilitySession is not null)
        {
            var session = _capabilitySession;
            _capabilitySession = null;
            await DisposeCapabilityAsync(session, _capabilityRun);
            _capabilityRun = null;
        }
        _httpClient.Dispose();
    }

    private static async Task DisposeCapabilityAsync(
        OutOfProcessCapabilityHostSession session,
        Task? run)
    {
        await session.DisposeAsync();
        if (run is not null)
        {
            try
            {
                await run;
            }
            catch (OutOfProcessCapabilityException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static OutOfProcessActionResult CreateCompleted(
        OutOfProcessProtocolSession protocol,
        ContinuationOutcome? hostOutcome,
        HookOutcome? sidecarOutcome,
        SidecarResultReplacement? replacement)
    {
        var kind = hostOutcome?.Kind ?? ActionOutcomeKind.Completed;
        var certainty = hostOutcome?.Certainty ?? ActionOutcomeCertainty.Certain;
        var result = hostOutcome?.Result;
        var error = hostOutcome?.Error;
        var uncertainty = hostOutcome?.Uncertainty;
        if (sidecarOutcome?.Kind == SidecarHookOutcomeKind.Failed)
        {
            kind = ActionOutcomeKind.Failed;
            certainty = ActionOutcomeCertainty.Certain;
            result = null;
            error = sidecarOutcome.Error;
            uncertainty = null;
        }
        else if (sidecarOutcome?.Kind == SidecarHookOutcomeKind.Cancelled)
        {
            kind = ActionOutcomeKind.Cancelled;
            certainty = ActionOutcomeCertainty.Certain;
            result = null;
            error = sidecarOutcome.Error;
            uncertainty = null;
        }

        if (replacement is not null)
        {
            kind = ActionOutcomeKind.Completed;
            certainty = ActionOutcomeCertainty.Certain;
            result = replacement.Result;
            error = null;
            uncertainty = null;
        }

        if (hostOutcome is null && sidecarOutcome is null && replacement is null)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The action exchange has no terminal outcome.");
        }

        var completed = protocol.Create(
            SidecarProtocolMessageKind.HookCompleted,
            header => new HookCompleted(
                header,
                protocol.State.ContinuationHandleId,
                kind,
                certainty,
                result,
                error,
                uncertainty));
        return new OutOfProcessActionResult(completed, hostOutcome?.Continuation);
    }

    private static async Task TryCancelAsync(
        OutOfProcessProtocolSession protocol,
        ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.Open
            || !SidecarProtocolStateMachine.CanApply(
                protocol.State.Phase,
                SidecarProtocolMessageKind.HostTerminalCancellation))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var cancellation = protocol.Create(
                SidecarProtocolMessageKind.HostTerminalCancellation,
                header => new SidecarHostTerminalCancellation(
                    header,
                    protocol.State.ContinuationHandleId,
                    ActionSafePoint.BeforeTerminal,
                    "operation_cancelled",
                    "The host cancelled the sidecar exchange."));
            await protocol.SendAsync(cancellation, ct: timeout.Token);
            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "cancelled",
                timeout.Token);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task WaitForCompletedCloseAsync(
        OutOfProcessProtocolSession protocol,
        ClientWebSocket socket,
        CancellationToken ct)
    {
        try
        {
            var frame = await protocol.ReceiveAsync(ct);
            if (frame.Message is SidecarProtocolError protocolError)
                throw Error(protocolError);
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The sidecar sent a message for an event delivery that requires no acknowledgement.");
        }
        catch (OutOfProcessProtocolException ex) when (
            socket.State == WebSocketState.CloseReceived
            && string.Equals(ex.Code, "completed", StringComparison.Ordinal))
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                ct);
        }
    }

    private static async Task TryCloseAsync(ClientWebSocket socket, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                description,
                timeout.Token);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private Uri ExchangeUri()
    {
        var builder = new UriBuilder(new Uri(
            _controlAddress,
            OutOfProcessModuleHostProtocol.ExchangePath))
        {
            Scheme = string.Equals(_controlAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                ? "wss"
                : "ws",
        };
        return builder.Uri;
    }

    private Uri CapabilitiesUri()
    {
        var builder = new UriBuilder(new Uri(
            _controlAddress,
            OutOfProcessModuleHostProtocol.CapabilityPath))
        {
            Scheme = string.Equals(_controlAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                ? "wss"
                : "ws",
        };
        return builder.Uri;
    }

    private static OutOfProcessProtocolException Error(SidecarProtocolError error) =>
        new(error.Code, error.Message);
}
