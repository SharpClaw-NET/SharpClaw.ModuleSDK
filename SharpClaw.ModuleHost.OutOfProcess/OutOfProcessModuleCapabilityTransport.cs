using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessModuleCapabilityTransport : ISidecarCapabilityTransport, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _authenticationNonces = new(StringComparer.Ordinal);
    private TaskCompletionSource<OutOfProcessModuleCapabilityConnection> _ready = CreateReadySource();
    private OutOfProcessModuleCapabilityConnection? _connection;
    private string? _moduleId;
    private string? _graphId;
    private SidecarPayloadLimits? _payloadLimits;
    private IReadOnlyList<ModuleActionHook>? _actionHooks;
    private ModuleContributionGraph? _graph;
    private IServiceProvider? _services;
    private SidecarHostAuthorization? _authorization;
    private Exception? _lastConnectionFailure;
    private Exception? _lastTerminalFailure;

    internal Exception? LastConnectionFailure => Volatile.Read(ref _lastConnectionFailure);
    internal Exception? LastTerminalFailure => Volatile.Read(ref _lastTerminalFailure);

    internal void RecordTerminalFailure(Exception exception) =>
        Volatile.Write(ref _lastTerminalFailure, exception);
    public void Initialize(
        string moduleId,
        string graphId,
        SidecarPayloadLimits payloadLimits,
        IReadOnlyList<ModuleActionHook> actionHooks,
        ModuleContributionGraph? graph = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentNullException.ThrowIfNull(payloadLimits);
        ArgumentNullException.ThrowIfNull(actionHooks);
        lock (_sync)
        {
            if (_moduleId is not null)
                throw new InvalidOperationException("The module capability transport is already initialized.");
            _moduleId = moduleId;
            _graphId = graphId;
            _payloadLimits = payloadLimits;
            _actionHooks = actionHooks;
            _graph = graph;
        }
    }

    internal void SetServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (_sync)
            _services = services;
    }

    internal ModuleNestedActionMetadata ResolveNestedActionMetadata<TAction, TResult>(
        SharpClawActionKey actionKey,
        int actionVersion)
    {
        var actionHooks = Volatile.Read(ref _actionHooks)
            ?? throw new InvalidOperationException(
                "The module action hook graph is not initialized.");
        ModuleActionHook? match = null;
        foreach (var hook in actionHooks)
        {
            if (hook.TargetKind != SidecarHookTargetKind.Exact
                || hook.ActionKey != actionKey
                || hook.IsUntyped
                || !hook.VersionRange.Contains(actionVersion))
            {
                continue;
            }

            if (match is not null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnknownAction,
                    $"The module action hook graph contains an ambiguous nested action '{actionKey.Value}:{actionVersion}'.");
            }

            match = hook;
        }

        if (match is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnknownAction,
                $"The module has no exact typed action hook for '{actionKey.Value}:{actionVersion}'.");
        }

        if (match.ActionType != typeof(TAction)
            || match.ResultType != typeof(TResult))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                $"The nested action hook type does not match '{actionKey.Value}:{actionVersion}'.");
        }

        return new ModuleNestedActionMetadata(
            match,
            TypeIdentity(typeof(TAction)),
            TypeIdentity(typeof(TResult)));
    }

    internal static bool MatchesNestedActionMetadata(
        SidecarActionDescriptorIdentity descriptor,
        ModuleNestedActionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(metadata);
        return string.Equals(descriptor.Category, metadata.Hook.Category, StringComparison.Ordinal)
            && string.Equals(
                descriptor.InputTypeIdentity,
                metadata.InputTypeIdentity,
                StringComparison.Ordinal)
            && descriptor.InputSchemaVersion == metadata.Hook.InputSchema.Version
            && string.Equals(
                descriptor.InputSchemaHash,
                metadata.Hook.InputSchema.ContentHash,
                StringComparison.Ordinal)
            && string.Equals(
                descriptor.ResultTypeIdentity,
                metadata.ResultTypeIdentity,
                StringComparison.Ordinal)
            && descriptor.ResultSchemaVersion == metadata.Hook.ResultSchema.Version
            && string.Equals(
                descriptor.ResultSchemaHash,
                metadata.Hook.ResultSchema.ContentHash,
                StringComparison.Ordinal);
    }

    private static string TypeIdentity(Type type) =>
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    internal void SetAuthorization(SidecarHostAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_sync)
        {
            if (_moduleId is null || _graphId is null)
                throw new InvalidOperationException(
                    "The module capability transport is not initialized.");
            if (!string.Equals(authorization.ModuleId, _moduleId, StringComparison.Ordinal))
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthorized,
                    "The host authorization identifies a different module.");
            _authorization = authorization;
        }
    }

    public async ValueTask<SidecarActionCapabilityResponse> InvokeActionAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        return await connection.InvokeActionAsync(request, terminal: null, ct);
    }

    public async ValueTask<SidecarActionTerminalTransportResponse> InvokeActionTerminalAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        return await connection.InvokeActionTerminalAsync(request, ct);
    }

    public async ValueTask<SidecarStorageCapabilityResponse> InvokeStorageAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken ct)
    {
        var connection = await GetConnectionAsync(ct);
        return await connection.InvokeStorageAsync(request, ct);
    }

    internal SidecarCapabilityCallIdentity CreateCall(
        SidecarCapabilityKind capability,
        DateTimeOffset deadline,
        CancellationToken ct = default) =>
        GetRequiredConnection().CreateCall(capability, deadline, ct);

    internal ValueTask<SidecarActionCapabilityResponse> InvokeActionAsync(
        SidecarActionCapabilityRequest request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? terminal,
        CancellationToken ct) =>
        GetRequiredConnection().InvokeActionAsync(request, terminal, ct);

    internal SidecarCapabilitySessionBinding Binding => GetRequiredConnection().Binding;

    internal async Task AcceptAsync(
        WebSocket socket,
        string controlToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        var limits = _payloadLimits
            ?? throw new InvalidOperationException("The module capability transport is not initialized.");
        var authorization = Volatile.Read(ref _authorization)
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The module capability transport is not authorized.");
        var graph = Volatile.Read(ref _graph)
            ?? throw new InvalidOperationException(
                "The module capability transport has no compiled graph.");
        var services = Volatile.Read(ref _services)
            ?? throw new InvalidOperationException(
                "The module capability transport has no module service provider.");
        var first = await OutOfProcessCapabilityWire.ReceiveAsync(
            socket,
            limits.ProtocolMessageBytes,
            ct);
        if (!string.Equals(first.Kind, OutOfProcessCapabilityFrameKind.Bind, StringComparison.Ordinal))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthenticated,
                "The capability channel requires a binding frame first.");
        }

        var binding = OutOfProcessCapabilityWire.Deserialize<SidecarCapabilitySessionBinding>(first.Payload);
        if (!string.Equals(binding.ModuleId, _moduleId, StringComparison.Ordinal)
            || !string.Equals(binding.GraphId, _graphId, StringComparison.Ordinal)
            || binding.ProtocolVersion != OutOfProcessModuleHostProtocol.Version)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The capability binding does not identify this module graph.");
        }

        var authenticate = new Func<SidecarCapabilityAuthenticationAuthority, bool>(
            authority => OutOfProcessCapabilitySecurity.Authenticate(authority, controlToken));
        var validation = SidecarCapabilitySessionValidator.Validate(
            binding,
            authenticate,
            RegisterAuthenticationNonce,
            DateTimeOffset.UtcNow,
            RegisterAuthenticationNonce: true);
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.Unauthenticated,
                validation.Message ?? "The capability binding was rejected.");
        }

        if (!OutOfProcessCapabilitySecurity.ValidateGrant(
                binding.Grant,
                authorization,
                _graphId!,
                _moduleId!,
                DateTimeOffset.UtcNow))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The capability grant is not authorized for this module graph.");
        }

        var session = new SidecarCapabilitySession(
            binding,
            authenticate,
            _ => true,
            DateTimeOffset.UtcNow);
        var connection = new OutOfProcessModuleCapabilityConnection(
            socket,
            session,
            controlToken,
            limits,
            authorization,
            RegisterAuthenticationNonce,
            this,
            graph.ActionEntries,
            services);
        lock (_sync)
        {
            if (_connection is not null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthorized,
                    "The module already has an active capability channel.");
            }

            _connection = connection;
            _ready.TrySetResult(connection);
        }

        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                socket,
                OutOfProcessCapabilityFrameKind.BindAccepted,
                SidecarCapabilityValidationResult.Accept(),
                limits.ProtocolMessageBytes,
                connection.SendGate,
                ct);
            await connection.RunAsync(ct);
        }
        finally
        {
            Volatile.Write(ref _lastConnectionFailure, connection.RunFailure);
            lock (_sync)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                    _ready = CreateReadySource();
                }
            }

            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        OutOfProcessModuleCapabilityConnection? connection;
        lock (_sync)
        {
            connection = _connection;
            _connection = null;
            _ready.TrySetException(new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityTransport)));
        }

        if (connection is not null)
            await connection.DisposeAsync();
    }

    private async ValueTask<OutOfProcessModuleCapabilityConnection> GetConnectionAsync(
        CancellationToken ct)
    {
        Task<OutOfProcessModuleCapabilityConnection> task;
        lock (_sync)
            task = _ready.Task;
        return await task.WaitAsync(ct);
    }

    private OutOfProcessModuleCapabilityConnection GetRequiredConnection() =>
        Volatile.Read(ref _connection)
        ?? throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.Disconnected,
            "The sidecar capability channel is not connected.");

    private bool RegisterAuthenticationNonce(string nonce)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _authenticationNonces)
        {
            if (entry.Value <= now)
                _authenticationNonces.TryRemove(entry.Key, out _);
        }

        return _authenticationNonces.TryAdd(nonce, now.AddMinutes(10));
    }

    private static TaskCompletionSource<OutOfProcessModuleCapabilityConnection> CreateReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record ModuleNestedActionMetadata(
    ModuleActionHook Hook,
    string InputTypeIdentity,
    string ResultTypeIdentity);

internal sealed class OutOfProcessModuleCapabilityConnection : IAsyncDisposable
{
    private sealed record PendingAction(
        SidecarActionCapabilityRequest Request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? Terminal,
        TaskCompletionSource<SidecarActionCapabilityResponse> Completion)
    {
        public SidecarActionCapabilityRequest? ResolvedRequest { get; set; }
    }

    private readonly WebSocket _socket;
    private SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly SidecarHostAuthorization _authorization;
    private readonly Func<string, bool> _registerAuthenticationNonce;
    private readonly OutOfProcessModuleCapabilityTransport _transport;
    private readonly IReadOnlyList<ModuleActionEntryRegistration> _actionEntries;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<Guid, PendingAction> _actions = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarStorageCapabilityResponse>> _storage = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retiredCalls = new();
    private readonly CancellationTokenSource _disconnect = new();
    private readonly BoundedExecutionQueue _terminalQueue;
    private readonly object _rotationSync = new();
    private Exception? _runFailure;
    private TaskCompletionSource? _rebindReady;
    private int _completedCallsForBinding;
    private long _sequence;
    private int _disposed;

    public OutOfProcessModuleCapabilityConnection(
        WebSocket socket,
        SidecarCapabilitySession session,
        string controlToken,
        SidecarPayloadLimits limits,
        SidecarHostAuthorization authorization,
        Func<string, bool> registerAuthenticationNonce,
        OutOfProcessModuleCapabilityTransport transport,
        IReadOnlyList<ModuleActionEntryRegistration> actionEntries,
        IServiceProvider services)
    {
        _socket = socket;
        _session = session;
        _controlToken = controlToken;
        _limits = limits;
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _registerAuthenticationNonce = registerAuthenticationNonce
            ?? throw new ArgumentNullException(nameof(registerAuthenticationNonce));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _actionEntries = actionEntries ?? throw new ArgumentNullException(nameof(actionEntries));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _terminalQueue = new BoundedExecutionQueue(
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1),
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1));
        SendGate = new SemaphoreSlim(1, 1);
    }

    public SemaphoreSlim SendGate { get; }

    public SidecarCapabilitySessionBinding Binding =>
        Volatile.Read(ref _session).Binding;

    internal Exception? RunFailure => Volatile.Read(ref _runFailure);

    public SidecarCapabilityCallIdentity CreateCall(
        SidecarCapabilityKind capability,
        DateTimeOffset deadline,
        CancellationToken ct = default)
    {
        WaitForRebindIfReady(ct);
        var sequence = Interlocked.Increment(ref _sequence);
        var callId = Guid.NewGuid();
        return new SidecarCapabilityCallIdentity(
            Binding.SessionId,
            Binding.RequestId,
            Binding.CancellationId,
            callId,
            $"{Binding.SessionId:N}:{sequence}:{Guid.NewGuid():N}",
            Binding.ModuleId,
            Binding.GraphId,
            capability,
            sequence,
            deadline);
    }

    private void ObserveSequence(long sequence)
    {
        while (true)
        {
            var current = Volatile.Read(ref _sequence);
            if (sequence <= current
                || Interlocked.CompareExchange(ref _sequence, sequence, current) == current)
                return;
        }
    }

    private void WaitForRebindIfReady(CancellationToken ct)
    {
        Task? rebind;
        lock (_rotationSync)
            rebind = _rebindReady?.Task;
        rebind?.WaitAsync(ct).GetAwaiter().GetResult();
    }

    private bool CompleteCall(Guid callId, int terminalCallCount) =>
        CompleteCallResult(callId, terminalCallCount).Accepted;

    private SidecarCapabilityValidationResult CompleteCallResult(
        Guid callId,
        int terminalCallCount)
    {
        var session = Volatile.Read(ref _session);
        var effectiveTerminalCallCount = terminalCallCount;
        if (effectiveTerminalCallCount == 0
            && session.TryGetTerminalReceipt(callId, out _))
        {
            effectiveTerminalCallCount = 1;
        }

        var result = session.CompleteCall(callId, effectiveTerminalCallCount);
        if (result.Accepted
            && Interlocked.Increment(ref _completedCallsForBinding)
                >= session.Binding.ConcurrencyLimits.MaximumCallsPerRequest)
        {
            lock (_rotationSync)
            {
                _rebindReady ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return result;
    }

    public async ValueTask<SidecarActionCapabilityResponse> InvokeActionAsync(
        SidecarActionCapabilityRequest request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? terminal,
        CancellationToken ct)
    {
        ValidateActionRequest(request);
        using var deadline = CreateCallCancellation(request.Deadline, ct);
        var callCancellation = deadline.Token;
        var begin = _session.BeginCall(
            request.Call,
            SidecarCapabilityKind.Action,
            request.Action,
            request.Action.ByteLength,
            DateTimeOffset.UtcNow);
        ThrowIfRejected(begin);
        ObserveSequence(request.Call.Sequence);
        var completion = NewCompletion<SidecarActionCapabilityResponse>();
        var pending = new PendingAction(request, terminal, completion);
        if (!_actions.TryAdd(request.Call.CallId, pending))
        {
            CompleteCall(request.Call.CallId, 0);
            throw new OutOfProcessCapabilityException("sidecar_replay", "The action call identifier was reused.");
        }

        var retainPending = false;
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                callCancellation);
            var response = await completion.Task.WaitAsync(callCancellation);
            var validation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                pending.ResolvedRequest ?? request,
                response,
                Binding,
                _session);
            ThrowIfRejected(validation);
            var completionResult = CompleteCallResult(
                request.Call.CallId,
                response.Outcome.TerminalCallCount);
            if (!completionResult.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    $"The sidecar action call could not be completed: "
                    + $"{completionResult.Code}: {completionResult.Message}; "
                    + $"terminalCallCount={response.Outcome.TerminalCallCount}");
            }
            return response;
        }
        catch (OperationCanceledException) when (callCancellation.IsCancellationRequested)
        {
            retainPending = true;
            await SendCancellationAsync(
                request.Call,
                request.Cancellation,
                request.Deadline,
                callCancellation,
                ct);
            _ = RetireActionAsync(request, completion);
            throw;
        }
        catch
        {
            CompleteCall(request.Call.CallId, 0);
            throw;
        }
        finally
        {
            if (!retainPending)
                _actions.TryRemove(request.Call.CallId, out _);
        }
    }

    public async ValueTask<SidecarActionTerminalTransportResponse> InvokeActionTerminalAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var completion = NewCompletion<SidecarActionTerminalTransportResponse>();
        if (!_terminals.TryAdd(request.Call.CallId, completion))
            throw new OutOfProcessCapabilityException("sidecar_replay", "The terminal call identifier was reused.");
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionTerminalRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                ct);
            var response = await completion.Task.WaitAsync(ct);
            if (request.CrossSidecarActionRequest is { } crossRequest)
            {
                ThrowIfRejected(
                    SidecarCrossSidecarActionEntryValidation.ValidateRequest(
                        crossRequest,
                        request.Call,
                        Binding,
                        DateTimeOffset.UtcNow));
                var relay = response.CrossSidecarRelay;
                var relayParentCall = relay?.Carrier.Authority.SourceParentCall;
                var parentCallMatches = relayParentCall is not null
                    && MatchesCapabilityCall(
                        relayParentCall,
                        request.Call);
                if (relay is null
                    || !relay.IsWellFormed
                    || relay.TargetEntry.Descriptor.Key != crossRequest.ActionKey
                    || relay.TargetEntry.Descriptor.Version != crossRequest.ActionVersion
                    || !parentCallMatches
                    || !response.Execution.Completed
                    || response.Receipt != request.Receipt
                    || response.TerminalId != request.TerminalId)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The cross-sidecar relay does not bind to the parent terminal request.");
                }

                if (response.CrossSidecarOutcome is { } crossOutcome)
                {
                    var authority = crossOutcome.Authority;
                    if (!crossOutcome.IsWellFormed
                        || authority.TargetChildCall != relay.Carrier.Authority.TargetChildCall
                        || authority.TargetEntry != relay.TargetEntry
                        || response.ResultIdentity != authority.ResultIdentity
                        || response.Execution != authority.Execution
                        || response.SafeFailure != authority.ResponseSafeFailure
                        || crossOutcome.Outcome != authority.OutcomeEnvelope
                        || !string.Equals(
                            authority.CanonicalBindingHash,
                            SidecarCrossSidecarActionEntryValidation.ComputeAuthorityHash(authority),
                            StringComparison.OrdinalIgnoreCase)
                        || !CrossSidecarOutcomeMatchesDescriptor(
                            crossOutcome,
                            relay.TargetEntry.Descriptor))
                    {
                        throw new OutOfProcessCapabilityException(
                            SidecarCapabilityErrors.SpoofedIdentity,
                            "The cross-sidecar outcome does not bind to the target authority.");
                    }

                    return response;
                }

                if (response.Execution.Failure is null
                    || response.Execution.Result is not null
                    || response.SafeFailure != Binding.SafeFailure)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The cross-sidecar relay failure does not bind to the parent terminal request.");
                }

                return response;
            }
            var validationRequest = request;
            var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                validationRequest,
                response,
                Binding,
                (authority, proof) => ValidateTerminalAuthority(authority, proof));
            ThrowIfRejected(validation);
            return response;
        }
        finally
        {
            _terminals.TryRemove(request.Call.CallId, out _);
        }
    }

    public async ValueTask<SidecarStorageCapabilityResponse> InvokeStorageAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken ct)
    {
        ValidateStorageRequest(request);
        using var deadline = CreateCallCancellation(request.Deadline, ct);
        var callCancellation = deadline.Token;
        var payload = request.RequestPayload ?? EmptyPayload();
        var begin = _session.BeginCall(
            request.Call,
            SidecarCapabilityKind.Storage,
            payload,
            payload.ByteLength,
            DateTimeOffset.UtcNow);
        ThrowIfRejected(begin);
        ObserveSequence(request.Call.Sequence);
        var completion = NewCompletion<SidecarStorageCapabilityResponse>();
        if (!_storage.TryAdd(request.Call.CallId, completion))
        {
            CompleteCall(request.Call.CallId, 0);
            throw new OutOfProcessCapabilityException("sidecar_replay", "The storage call identifier was reused.");
        }
        var retainPending = false;
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.StorageRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                callCancellation);
            var response = await completion.Task.WaitAsync(callCancellation);
            ThrowIfRejected(SidecarCapabilityTransportValidation.ValidateStorageResponse(
                request,
                response,
                Binding));
            if (!CompleteCall(request.Call.CallId, 0))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The sidecar storage call could not be completed.");
            }
            return response;
        }
        catch (OperationCanceledException) when (callCancellation.IsCancellationRequested)
        {
            retainPending = true;
            await SendCancellationAsync(request.Call, request.Cancellation, request.Deadline, callCancellation, ct);
            _ = RetireStorageAsync(request, completion);
            throw;
        }
        catch
        {
            CompleteCall(request.Call.CallId, 0);
            throw;
        }
        finally
        {
            if (!retainPending)
                _storage.TryRemove(request.Call.CallId, out _);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disconnect.Token);
        Exception? failure = null;
        try
        {
            while (true)
            {
                var frame = await OutOfProcessCapabilityWire.ReceiveAsync(
                    _socket,
                    _limits.ProtocolMessageBytes,
                    linked.Token);
                switch (frame.Kind)
                {
                    case OutOfProcessCapabilityFrameKind.ActionResponse:
                        CompleteAction(OutOfProcessCapabilityWire.Deserialize<SidecarActionCapabilityResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.StorageResponse:
                        CompleteStorage(OutOfProcessCapabilityWire.Deserialize<SidecarStorageCapabilityResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalRequest:
                        var terminalRequest = OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportRequest>(
                            frame.Payload);
                        if (!_terminalQueue.TrySchedule(
                                queueCt => HandleTerminalRequestAsync(terminalRequest, queueCt),
                                linked.Token,
                                out var terminalCompletion))
                        {
                            throw new OutOfProcessCapabilityException(
                                SidecarCapabilityErrors.ModuleBusy,
                                "The module terminal execution queue is full.");
                        }

                        _ = ObserveTerminalCompletionAsync(terminalCompletion);
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalResponse:
                        CompleteTerminal(OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityRebind:
                        await HandleRebindAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarCapabilitySessionBinding>(frame.Payload),
                            linked.Token);
                        break;
                    case OutOfProcessCapabilityFrameKind.Error:
                        throw ReadError(frame.Payload);
                    default:
                        throw new OutOfProcessCapabilityException(
                            SidecarCapabilityErrors.MalformedMessage,
                            $"The module received unsupported capability frame '{frame.Kind}'.");
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _runFailure, ex);
            failure = ex;
        }
        finally
        {
            _session.Disconnect();
            lock (_rotationSync)
            {
                _rebindReady?.TrySetException(failure ?? new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The sidecar capability channel disconnected."));
            }
            FailPending(failure ?? new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Disconnected,
                "The sidecar capability channel disconnected."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _disconnect.Cancel();
        _session.Disconnect();
        lock (_rotationSync)
        {
            _rebindReady?.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityConnection)));
        }
        FailPending(new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityConnection)));
        await _terminalQueue.DisposeAsync();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "capability-disconnected",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        SendGate.Dispose();
        _disconnect.Dispose();
    }

    private async Task HandleTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        if (request.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar
            && request.CrossSidecarActionRequest is null
            && request.Context is not null)
        {
            await HandleCrossSidecarTerminalRequestAsync(request, ct);
            return;
        }

        if (!_actions.TryGetValue(request.Call.CallId, out var pending)
            || pending.Terminal is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The terminal request has no initiating action call.");
        }

        var validationRequest = pending.Request;
        if (pending.Request.Invocation == SidecarActionInvocationKind.HostEntry
            && pending.Request.NestedCarrier is not null)
        {
            var terminal = pending.Request.Terminal
                ?? throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.MalformedMessage,
                    "The nested action has no terminal registration.");
            validationRequest = pending.Request with
            {
                Descriptor = request.Descriptor,
                Action = request.EffectiveAction,
                Terminal = terminal with
                {
                    ActionTypeIdentity = request.Descriptor.InputTypeIdentity,
                    ActionSchemaVersion = request.Descriptor.InputSchemaVersion,
                    ResultTypeIdentity = request.Descriptor.ResultTypeIdentity,
                    ResultSchemaVersion = request.Descriptor.ResultSchemaVersion,
                    DescriptorHash = request.Descriptor.DescriptorHash,
                },
            };
        }

        var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalRequest(
            validationRequest,
            request,
            Binding,
            DateTimeOffset.UtcNow,
            (authority, proof) => ValidateTerminalAuthority(authority, proof));
        ThrowIfRejected(validation);
        pending.ResolvedRequest = validationRequest;
        ThrowIfRejected(_session.RecordTerminal(
            request.Call.CallId,
            request.Authority.AuthorityId,
            request.Receipt));
        var response = await pending.Terminal(request, ct);
        var responseValidation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
            request,
            response,
            Binding);
        if (!responseValidation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                responseValidation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                $"{responseValidation.Message}; requestCall={request.Call.CallId}; "
                + $"responseCall={response.ResultIdentity?.CallId}; "
                + $"requestDescriptor={request.Descriptor.Key.Value}:{request.Descriptor.Version}; "
                + $"responseDescriptor={response.ResultIdentity?.ActionKey.Value}:{response.ResultIdentity?.ActionVersion}; "
                + $"requestTerminal={request.TerminalId}; responseTerminal={response.TerminalId}; "
                + $"requestReceipt={request.Receipt.CallId}; responseReceipt={response.Receipt?.CallId}; "
                + $"completed={response.Execution.Completed}; result={response.Execution.Result?.TypeIdentity}");
        }
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private async Task HandleCrossSidecarTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var context = request.Context
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The cross-sidecar terminal request has no execution context.");
        var authorityProofValid = ValidateTerminalAuthority(
                request.Authority,
                SidecarCapabilityTransportValidation.ComputeTerminalAuthorityBindingHash(
                    request.Authority));
        var authorityModuleValid = request.Authority.ModuleId == Binding.ModuleId;
        var authorityGraphValid = request.Authority.GraphId == Binding.GraphId;
        var authorityInvocationValid =
            request.Authority.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar;
        var authorityCallValid = request.Authority.CallId == request.Call.CallId;
        var authorityTerminalValid = request.Authority.TerminalId == request.TerminalId;
        var authorityInvocationIdValid = request.Authority.InvocationId == context.InvocationId;
        var authorityParentInvocationValid = request.Authority.ParentInvocationId == context.ParentInvocationId;
        var authorityTraceValid = request.Authority.TraceId == context.TraceId;
        var authorityIdempotencyValid = request.Authority.IdempotencyKey == context.IdempotencyKey;
        var authorityDepthValid = request.Authority.Depth == context.Depth;
        var authorityAttemptValid = request.Authority.Attempt == context.Attempt;
        var authorityCallerValid = OutOfProcessHostActionEntryContextRegistry.MatchesCaller(
            request.Authority.Caller,
            context.Caller);
        var authorityFeaturesValid = string.Equals(
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(request.Authority.Features)),
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(context.Features)),
            StringComparison.Ordinal);
        var authorityDescriptorValid = request.Descriptor.Key == context.Descriptor.Key
            && request.Descriptor.Version == context.Descriptor.Version;
        var authorityPayloadValid = OutOfProcessCapabilityTransportPayloadMatches(
            request.EffectiveAction,
            context.EffectiveAction);
        var authoritySnapshotValid = string.Equals(
            request.Authority.SnapshotContentHash,
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(context.Snapshot)),
            StringComparison.Ordinal);
        var authorityValid = authorityProofValid
            && authorityModuleValid
            && authorityGraphValid
            && authorityInvocationValid
            && authorityCallValid
            && authorityTerminalValid
            && authorityInvocationIdValid
            && authorityParentInvocationValid
            && authorityTraceValid
            && authorityIdempotencyValid
            && authorityDepthValid
            && authorityAttemptValid
            && authorityCallerValid
            && authorityFeaturesValid
            && authorityDescriptorValid
            && authorityPayloadValid
            && authoritySnapshotValid;
        if (!authorityValid)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The cross-sidecar terminal authority does not match its execution context. "
                + $"proof={authorityProofValid}; module={authorityModuleValid}; graph={authorityGraphValid}; "
                + $"invocation={authorityInvocationValid}; call={authorityCallValid}; terminal={authorityTerminalValid}; "
                + $"invocationId={authorityInvocationIdValid}; parentInvocation={authorityParentInvocationValid}; "
                + $"trace={authorityTraceValid}; idempotency={authorityIdempotencyValid}; depth={authorityDepthValid}; "
                + $"attempt={authorityAttemptValid}; caller={authorityCallerValid}; features={authorityFeaturesValid}; "
                + $"descriptor={authorityDescriptorValid}; payload={authorityPayloadValid}; snapshot={authoritySnapshotValid}; "
                + $"authorityCallId={request.Authority.CallId}; requestCallId={request.Call.CallId}; "
                + $"authorityTerminalId={request.Authority.TerminalId}; requestTerminalId={request.TerminalId}");
        }

        var registration = _actionEntries.SingleOrDefault(entry =>
            entry.TerminalId == request.TerminalId
            && OutOfProcessCapabilityTransportDescriptorMatches(
                entry.Descriptor,
                request.Descriptor));
        if (registration is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnknownAction,
                "The target module does not own the requested action terminal.");
        }

        var contribution = new HostActionEntryContribution(
            new HostActionEntryIngressBinding(
                HostActionEntryIngress.CrossModule,
                Binding.ModuleId,
                Binding.GraphId),
            new HostActionEntryLineage(
                request.Descriptor.Key,
                request.Descriptor.Version,
                request.Descriptor.DescriptorHash,
                request.Descriptor.InputTypeIdentity,
                request.Descriptor.InputSchemaVersion,
                request.Descriptor.InputSchemaHash,
                request.EffectiveAction.ContentHash,
                request.EffectiveAction.ByteLength));
        var hostEntry = new OutOfProcessHostActionEntry(
            _transport,
            request.Descriptor,
            request,
            contribution);
        SidecarTerminalExecutionResult execution;
        try
        {
            execution = await registration.Invoker.InvokeAsync(
                _services,
                context,
                hostEntry,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            execution = new SidecarTerminalExecutionResult(
                null,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    SidecarCapabilityErrors.Cancelled,
                    "The target action terminal was cancelled.",
                    Retryable: true),
                Completed: true);
        }
        catch (Exception)
        {
            execution = new SidecarTerminalExecutionResult(
                null,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    SidecarCapabilityErrors.HostFailure,
                    "The target action terminal failed.",
                    Retryable: false),
                Completed: true);
        }

        var response = new SidecarActionTerminalTransportResponse(
            execution.Result is null
                ? null
                : new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    request.Descriptor.Key,
                    request.Descriptor.Version,
                    execution.Result.TypeIdentity,
                    execution.Result.ContentHash),
            execution,
            request.Receipt,
            _session.Binding.SafeFailure)
        {
            TerminalId = request.TerminalId,
        };
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private static bool OutOfProcessCapabilityTransportPayloadMatches(
        SidecarSerializedPayload left,
        SidecarSerializedPayload right) =>
        string.Equals(left.TypeIdentity, right.TypeIdentity, StringComparison.Ordinal)
        && left.SchemaVersion == right.SchemaVersion
        && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
        && left.ByteLength == right.ByteLength
        && string.Equals(left.Value.GetRawText(), right.Value.GetRawText(), StringComparison.Ordinal);

    private static bool OutOfProcessCapabilityTransportDescriptorMatches(
        SidecarActionDescriptorIdentity left,
        SidecarActionDescriptorIdentity right) =>
        left.Key == right.Key
        && left.Version == right.Version
        && string.Equals(left.Category, right.Category, StringComparison.Ordinal)
        && string.Equals(left.InputTypeIdentity, right.InputTypeIdentity, StringComparison.Ordinal)
        && left.InputSchemaVersion == right.InputSchemaVersion
        && string.Equals(left.InputSchemaHash, right.InputSchemaHash, StringComparison.Ordinal)
        && string.Equals(left.ResultTypeIdentity, right.ResultTypeIdentity, StringComparison.Ordinal)
        && left.ResultSchemaVersion == right.ResultSchemaVersion
        && string.Equals(left.ResultSchemaHash, right.ResultSchemaHash, StringComparison.Ordinal)
        && string.Equals(left.DescriptorHash, right.DescriptorHash, StringComparison.Ordinal);

    private static bool MatchesCapabilityCall(
        SidecarCapabilityCallIdentity left,
        SidecarCapabilityCallIdentity right) =>
        left.SessionId == right.SessionId
        && left.RequestId == right.RequestId
        && left.CancellationId == right.CancellationId
        && left.CallId == right.CallId
        && string.Equals(left.ReplayNonce, right.ReplayNonce, StringComparison.Ordinal)
        && string.Equals(left.ModuleId, right.ModuleId, StringComparison.Ordinal)
        && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
        && left.Capability == right.Capability
        && left.Sequence == right.Sequence
        && left.Deadline == right.Deadline;

    private static string DescribeCapabilityCall(SidecarCapabilityCallIdentity? call) =>
        call is null
            ? "null"
            : $"session={call.SessionId};request={call.RequestId};cancel={call.CancellationId};"
                + $"call={call.CallId};nonce={call.ReplayNonce};module={call.ModuleId};"
                + $"graph={call.GraphId};capability={call.Capability};sequence={call.Sequence};"
                + $"deadline={call.Deadline.Ticks}/{call.Deadline.Offset.Ticks}";

    private async Task HandleRebindAsync(
        SidecarCapabilitySessionBinding binding,
        CancellationToken ct)
    {
        while (!_actions.IsEmpty || !_storage.IsEmpty || !_terminals.IsEmpty)
            await Task.Delay(TimeSpan.FromMilliseconds(5), ct);

        SidecarCapabilityValidationResult validation;
        if (!string.Equals(binding.ModuleId, _authorization.ModuleId, StringComparison.Ordinal)
            || binding.ProtocolVersion != OutOfProcessModuleHostProtocol.Version)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                SidecarCapabilityErrors.Unauthorized,
                "The capability binding rotation identifies a different module or protocol.");
        }
        else
        {
            var authenticate = new Func<SidecarCapabilityAuthenticationAuthority, bool>(
                authority => OutOfProcessCapabilitySecurity.Authenticate(authority, _controlToken));
            validation = SidecarCapabilitySessionValidator.Validate(
                binding,
                authenticate,
                _registerAuthenticationNonce,
                DateTimeOffset.UtcNow,
                RegisterAuthenticationNonce: true);
            if (validation.Accepted
                && !OutOfProcessCapabilitySecurity.ValidateGrant(
                    binding.Grant,
                    _authorization,
                    binding.GraphId,
                    binding.ModuleId,
                    DateTimeOffset.UtcNow))
            {
                validation = SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Unauthorized,
                    "The rotated capability grant is not authorized for this module graph.");
            }
        }

        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.CapabilityRebindAccepted,
            validation,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
        if (!validation.Accepted)
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.Unauthorized,
                validation.Message ?? "The rotated capability binding was rejected.");

        var rotation = Volatile.Read(ref _session).RotateBinding(
            binding,
            DateTimeOffset.UtcNow);
        if (!rotation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                rotation.Code ?? SidecarCapabilityErrors.Unauthorized,
                rotation.Message ?? "The rotated capability binding could not replace the active binding.");
        }
        TaskCompletionSource? rebind;
        lock (_rotationSync)
        {
            rebind = _rebindReady;
            _rebindReady = null;
            Interlocked.Exchange(ref _completedCallsForBinding, 0);
            Interlocked.Exchange(ref _sequence, 0);
        }
        rebind?.TrySetResult();
    }

    private async Task SendCancellationAsync(
        SidecarCapabilityCallIdentity call,
        SidecarCancellationIdentity cancellation,
        DateTimeOffset deadline,
        CancellationToken callCancellation,
        CancellationToken callerCancellation)
    {
        var reason = deadline <= DateTimeOffset.UtcNow
            ? "deadline"
            : callerCancellation.IsCancellationRequested
                ? "caller"
                : "call";
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.CapabilityCancellation,
                new OutOfProcessCapabilityCancellation(
                    call,
                    cancellation,
                    reason,
                    DateTimeOffset.UtcNow),
                _limits.ProtocolMessageBytes,
                SendGate,
                sendTimeout.Token);
        }
        catch (OperationCanceledException) when (sendTimeout.IsCancellationRequested)
        {
            _disconnect.Cancel();
        }
        catch (WebSocketException)
        {
            _disconnect.Cancel();
        }
    }

    private async Task RetireActionAsync(
        SidecarActionCapabilityRequest request,
        TaskCompletionSource<SidecarActionCapabilityResponse> completion)
    {
        try
        {
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var validation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                Binding,
                _session);
            if (validation.Accepted)
                CompleteCall(request.Call.CallId, response.Outcome.TerminalCallCount);
            else
                CompleteCall(request.Call.CallId, 0);
        }
        catch
        {
            CompleteCall(request.Call.CallId, 0);
            _retiredCalls[request.Call.CallId] = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        finally
        {
            _actions.TryRemove(request.Call.CallId, out _);
        }
    }

    private async Task RetireStorageAsync(
        SidecarStorageCapabilityRequest request,
        TaskCompletionSource<SidecarStorageCapabilityResponse> completion)
    {
        try
        {
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var validation = SidecarCapabilityTransportValidation.ValidateStorageResponse(
                request,
                response,
                Binding);
            CompleteCall(request.Call.CallId, validation.Accepted ? 0 : 0);
        }
        catch
        {
            CompleteCall(request.Call.CallId, 0);
            _retiredCalls[request.Call.CallId] = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        finally
        {
            _storage.TryRemove(request.Call.CallId, out _);
        }
    }

    private static CancellationTokenSource CreateCallCancellation(
        DateTimeOffset deadline,
        CancellationToken callerCancellation)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            source.Cancel();
        else
            source.CancelAfter(remaining);
        return source;
    }

    private bool IsRetired(Guid callId)
    {
        if (!_retiredCalls.TryGetValue(callId, out var expiresAt))
            return false;
        if (expiresAt > DateTimeOffset.UtcNow)
            return true;
        _retiredCalls.TryRemove(callId, out _);
        return false;
    }

    private bool ValidateTerminalAuthority(
        SidecarHostTerminalAuthority authority,
        string canonicalBindingHash) =>
        string.Equals(
            authority.CanonicalBindingHash,
            canonicalBindingHash,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            OutOfProcessCapabilitySecurity.CreateTerminalProof(authority, _controlToken),
            authority.Proof,
            StringComparison.Ordinal);

    private void CompleteAction(SidecarActionCapabilityResponse response)
    {
        var callId = response.ResultIdentity?.CallId
            ?? response.Outcome.Receipt?.CallId;
        if (callId is null || callId == Guid.Empty)
        {
            var pendingCallIds = _actions.Values
                .Select(pending => pending.Request.Call.CallId)
                .Take(2)
                .ToArray();
            if (pendingCallIds.Length != 1)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.MalformedMessage,
                    "The failed action response has no unique pending action call.");
            }

            callId = pendingCallIds[0];
        }

        if (_actions.TryGetValue(callId.Value, out var pending))
            pending.Completion.TrySetResult(response);
        else if (!IsRetired(callId.Value))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The action response does not match an active action call.");
    }

    private void CompleteStorage(SidecarStorageCapabilityResponse response)
    {
        var resultIdentity = response.ResultIdentity
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The storage response has no result identity.");
        if (_storage.TryGetValue(resultIdentity.CallId, out var completion))
            completion.TrySetResult(response);
        else if (!IsRetired(resultIdentity.CallId))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The storage response does not match an active storage call.");
    }

    private void CompleteTerminal(SidecarActionTerminalTransportResponse response)
    {
        if (response.ResultIdentity is { } resultIdentity
            && _terminals.TryGetValue(resultIdentity.CallId, out var resultCompletion))
        {
            resultCompletion.TrySetResult(response);
            return;
        }

        if (response.Receipt is { } receipt
            && _terminals.TryGetValue(receipt.CallId, out var receiptCompletion))
        {
            receiptCompletion.TrySetResult(response);
            return;
        }

        if (response.ResultIdentity is null && response.Receipt is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal response has no result identity or receipt.");
        }

        throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.Unauthorized,
            "The terminal response does not match an active terminal call.");
    }

    private void ValidateActionRequest(SidecarActionCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = SidecarCapabilityTransportValidation.ValidateActionRequest(
            request,
            Binding,
            DateTimeOffset.UtcNow);
        if (!validation.Accepted
            && request.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar)
        {
            var carrier = request.CrossSidecarCarrier;
            var terminal = request.Terminal;
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                $"{validation.Message}; "
                + $"requestCall={DescribeCapabilityCall(request.Call)}; "
                + $"descriptor={request.Descriptor.Key}:{request.Descriptor.Version}:{request.Descriptor.Category}:"
                + $"{request.Descriptor.InputTypeIdentity}:{request.Descriptor.InputSchemaHash}:{request.Descriptor.InputSchemaVersion}:"
                + $"{request.Descriptor.ResultTypeIdentity}:{request.Descriptor.ResultSchemaHash}:{request.Descriptor.ResultSchemaVersion}:"
                + $"{request.Descriptor.DescriptorHash}; descriptorWellFormed={request.Descriptor.IsWellFormed}; "
                + $"action={request.Action.TypeIdentity}:{request.Action.SchemaVersion}:{request.Action.ContentHash}:{request.Action.ByteLength}; "
                + $"actionValid={request.Action.IsValid}; "
                + $"carrierWellFormed={carrier?.IsWellFormed}; "
                + $"carrierAction={carrier?.Action.TypeIdentity}:{carrier?.Action.SchemaVersion}:{carrier?.Action.ContentHash}:{carrier?.Action.ByteLength}; "
                + $"carrierActionMatches={carrier is not null && carrier.Action == request.Action}; "
                + $"carrierDescriptorMatches={carrier is not null && carrier.Authority.Descriptor == request.Descriptor}; "
                + $"carrierTargetDescriptorMatches={carrier is not null && carrier.Authority.TargetEntry.Descriptor == request.Descriptor}; "
                + $"callSessionMatches={request.Call.SessionId == Binding.SessionId}; "
                + $"callRequestMatches={request.Call.RequestId == Binding.RequestId}; "
                + $"callCancellationMatches={request.Call.CancellationId == Binding.CancellationId}; "
                + $"callModuleMatches={request.Call.ModuleId == Binding.ModuleId}; "
                + $"callGraphMatches={request.Call.GraphId == Binding.GraphId}; "
                + $"requestCancellationMatches={request.Cancellation.CancellationId == request.Call.CancellationId}; "
                + $"requestCancellationAuthorityMatches={request.Cancellation.AuthorityHash == SidecarCapabilitySessionValidator.ComputeBindingHash(Binding)}; "
                + $"requestDeadlineMatches={request.Deadline == request.Call.Deadline}; "
                + $"terminalWellFormed={terminal?.IsWellFormed}; terminalId={terminal?.TerminalId}; "
                + $"terminalInput={terminal?.ActionTypeIdentity}:{terminal?.ActionSchemaVersion}; "
                + $"terminalResult={terminal?.ResultTypeIdentity}:{terminal?.ResultSchemaVersion}; "
                + $"terminalHash={terminal?.DescriptorHash};");
        }

        ThrowIfRejected(validation);
    }

    private static bool CrossSidecarOutcomeMatchesDescriptor(
        SidecarCrossSidecarActionEntryOutcome outcome,
        SidecarActionDescriptorIdentity descriptor)
    {
        var payload = outcome.Outcome?.Result;
        if (outcome.Kind == SidecarCrossSidecarActionEntryOutcomeKind.Completed)
        {
            return outcome.Outcome?.Kind == ActionOutcomeKind.Completed
                && payload is not null
                && string.Equals(
                    payload.TypeIdentity,
                    descriptor.ResultTypeIdentity,
                    StringComparison.Ordinal)
                && payload.SchemaVersion == descriptor.ResultSchemaVersion
                && outcome.Authority.ResultIdentity is not null
                && outcome.Authority.ResultIdentity.ActionKey == descriptor.Key
                && outcome.Authority.ResultIdentity.ActionVersion == descriptor.Version
                && string.Equals(
                    outcome.Authority.ResultIdentity.ResultTypeIdentity,
                    descriptor.ResultTypeIdentity,
                    StringComparison.Ordinal)
                && string.Equals(
                    outcome.Authority.ResultIdentity.ContentHash,
                    payload.ContentHash,
                    StringComparison.OrdinalIgnoreCase);
        }

        return outcome.Outcome?.Kind is ActionOutcomeKind.Failed or ActionOutcomeKind.Cancelled
            && payload is null
            && outcome.Authority.ResultIdentity is null;
    }

    private void ValidateStorageRequest(SidecarStorageCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = SidecarCapabilityTransportValidation.ValidateStorageRequest(
            request,
            Binding,
            DateTimeOffset.UtcNow);
        ThrowIfRejected(validation);
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _actions.Values)
            pending.Completion.TrySetException(error);
        foreach (var pending in _storage.Values)
            pending.TrySetException(error);
        foreach (var pending in _terminals.Values)
            pending.TrySetException(error);
    }

    private async Task ObserveTerminalCompletionAsync(Task completion)
    {
        try
        {
            await completion;
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _runFailure, ex);
            try
            {
                _disconnect.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SidecarSerializedPayload EmptyPayload() =>
        new("system.empty", 1, SidecarCapabilityTransportCodec.ComputeSha256("null"u8),
            JsonDocument.Parse("null").RootElement.Clone(), 4);

    private static OutOfProcessCapabilityException ReadError(byte[] payload)
    {
        var error = OutOfProcessCapabilityWire.Deserialize<SidecarSafeFailureIdentity>(payload);
        return new OutOfProcessCapabilityException(error.Code, error.Message);
    }

    private static void ThrowIfRejected(SidecarCapabilityValidationResult validation)
    {
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                validation.Message ?? "The capability operation was rejected.");
        }
    }

}
