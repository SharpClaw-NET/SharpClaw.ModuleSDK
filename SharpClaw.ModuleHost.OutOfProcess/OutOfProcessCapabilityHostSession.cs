using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly OutOfProcessCapabilityHostOptions _options;
    private readonly SidecarHostAuthorization _authorization;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly ConcurrentDictionary<Guid, PendingOutgoingAction> _outgoingActions = new();
    private readonly ConcurrentDictionary<Guid, ActiveCall> _calls = new();
    private readonly CancellationTokenSource _disconnect = new();
    private readonly BoundedExecutionQueue _capabilityQueue;
    private readonly object _rotationSync = new();
    private TaskCompletionSource? _rotationReady;
    private TaskCompletionSource<SidecarCapabilityValidationResult>? _rotationAcknowledgement;
    private Task? _rotationTask;
    private Task? _rotationRetryTask;
    private TaskCompletionSource _rotationRetryWake = CreateSignal();
    private int _completedCallsForBinding;
    private long _sequence;
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private Exception? _runFailure;
    private Exception? _lastHandledFailure;
    private int _disposed;

    private sealed class ActiveCall(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public SidecarActionCapabilityRequest? ActionRequest { get; set; }

        public HostActionEntryRequestContext? HostContext { get; set; }

        public int Completed;

        public int CompletionAccepted;
    }

    private sealed class PendingOutgoingAction(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource<SidecarActionCapabilityResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SidecarActionCapabilityRequest? Request { get; set; }
    }

    public OutOfProcessCapabilityHostSession(
        WebSocket socket,
        SidecarCapabilitySessionBinding binding,
        string controlToken,
        SidecarPayloadLimits limits,
        OutOfProcessCapabilityHostOptions options,
        SidecarHostAuthorization authorization)
    {
        _socket = socket;
        _controlToken = controlToken;
        _limits = limits;
        _options = options;
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _session = CreateSession(binding, controlToken);
        SendGate = new SemaphoreSlim(1, 1);
        _capabilityQueue = new BoundedExecutionQueue(
            Math.Max(binding.ConcurrencyLimits.MaximumInFlightCalls, 1),
            Math.Max(binding.ConcurrencyLimits.MaximumInFlightCalls, 1));
    }

    public SemaphoreSlim SendGate { get; }

    internal Exception? RunFailure => Volatile.Read(ref _runFailure);

    internal Exception? LastHandledFailure => Volatile.Read(ref _lastHandledFailure);

    private SidecarCapabilitySession Session => Volatile.Read(ref _session);

    internal HostActionEntryRequestContext IssueHostActionEntryContext(
        HostActionEntryContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Session.IssueHostActionEntryContext(
            request,
            DateTimeOffset.UtcNow,
            out var context);
        if (!validation.Accepted || context is null)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.Unauthorized,
                validation.Message
                    ?? "The capability session rejected the host action entry context.");
        }

        return context;
    }

    internal HostActionEntryRequestContext IssueHostActionEntryContext<TAction, TResult>(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId = null)
        => _options.HostActionEntryContexts.Issue(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            descriptor,
            action,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            invocationId);

    internal HostActionEntryRequestContext ExecuteContextIssuance(
        Func<HostActionEntryRequestContext> issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        while (true)
        {
            Task? rotation = null;
            _rotationGate.Wait(_disconnect.Token);
            try
            {
                lock (_rotationSync)
                {
                    var maximumCalls = Session.Binding.ConcurrencyLimits.MaximumCallsPerRequest;
                    if (_rotationReady is null
                        && (_rotationTask is null || _rotationTask.IsCompleted)
                        && Volatile.Read(ref _completedCallsForBinding)
                            >= Math.Max(maximumCalls - 2, 1))
                    {
                        _rotationReady = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    if (_rotationReady is null
                        && (_rotationTask is null || _rotationTask.IsCompleted))
                        return issue();

                    rotation = _rotationTask ?? _rotationReady?.Task;
                }
            }
            finally
            {
                _rotationGate.Release();
            }

            RequestRotationRetry();
            rotation?.GetAwaiter().GetResult();
        }
    }

    internal async ValueTask<IActionOutcome<TResult>> InvokeModuleActionEntryAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        SidecarActionDescriptorIdentity identity,
        SidecarSerializedPayload actionPayload,
        HostActionEntryRequestContext hostContext,
        Guid terminalId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(actionPayload);
        ArgumentNullException.ThrowIfNull(hostContext);
        if (!hostContext.IsWellFormed(DateTimeOffset.UtcNow)
            || hostContext.Contribution is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The module action entry host context is invalid.");
        }

        if (!OutOfProcessActionDescriptorIdentity.Matches(
                identity,
                OutOfProcessActionDescriptorIdentity.Create(descriptor)))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The typed module action descriptor does not match its host identity.");
        }

        using var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _disconnect.Token);
        var remaining = hostContext.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            dispatchCancellation.Cancel();
        else
            dispatchCancellation.CancelAfter(remaining);

        var terminalCalls = 0;
        try
        {
            async ValueTask<TResult> InvokeTerminalAsync(
                ActionContext<TAction> dispatcherContext,
                CancellationToken terminalCancellation)
            {
                if (Interlocked.Exchange(ref terminalCalls, 1) != 0)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The typed module action dispatcher invoked its terminal more than once.");
                }

                var response = await InvokeModuleActionEntryExchangeAsync(
                    identity,
                    actionPayload,
                    hostContext,
                    dispatcherContext,
                    terminalId,
                    terminalCancellation);
                var terminalOutcome = OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
                if (terminalOutcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred)
                    return terminalOutcome.Result;

                throw new OutOfProcessCapabilityException(
                    terminalOutcome.Error?.Code
                        ?? response.SafeFailure?.Code
                        ?? SidecarCapabilityErrors.HostFailure,
                    terminalOutcome.Error?.Message
                        ?? response.SafeFailure?.Message
                        ?? "The module action entry failed.");
            }

            var outcome = await _options.ActionDispatcher.RunAsync(
                descriptor,
                action,
                InvokeTerminalAsync,
                _options.ActionSnapshot,
                dispatchCancellation.Token);
            if (outcome.Kind == ActionOutcomeKind.Completed
                && terminalCalls != 1)
            {
                return new OutOfProcessActionOutcome<TResult>(
                    ActionOutcomeKind.Failed,
                    default!,
                    new ExecutionError(
                        SidecarCapabilityErrors.HostFailure,
                        "The typed module action completed without an authenticated terminal exchange."),
                    null,
                    null);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (dispatchCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfProcessCapabilityException exception)
        {
            return new OutOfProcessActionOutcome<TResult>(
                ActionOutcomeKind.Failed,
                default!,
                new ExecutionError(exception.Code, exception.Message),
                null,
                null);
        }
        catch (Exception)
        {
            return new OutOfProcessActionOutcome<TResult>(
                ActionOutcomeKind.Failed,
                default!,
                new ExecutionError(
                    SidecarCapabilityErrors.HostFailure,
                    "The typed module action dispatcher failed."),
                null,
                null);
        }
    }

    private async ValueTask<SidecarActionCapabilityResponse> InvokeModuleActionEntryExchangeAsync<TAction>(
        SidecarActionDescriptorIdentity identity,
        SidecarSerializedPayload initiatingAction,
        HostActionEntryRequestContext hostContext,
        ActionContext<TAction> dispatcherContext,
        Guid terminalId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(initiatingAction);
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(dispatcherContext);
        if (!hostContext.IsWellFormed(DateTimeOffset.UtcNow)
            || hostContext.Contribution is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The module action entry host context is invalid.");
        }

        var initiatingLineage = hostContext.Contribution.Lineage;
        if (!string.Equals(
                initiatingLineage.PayloadContentHash,
                initiatingAction.ContentHash,
                StringComparison.Ordinal)
            || initiatingLineage.PayloadByteLength != initiatingAction.ByteLength)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The typed module action entry does not preserve its initiating payload authority.");
        }

        await WaitForRotationAsync(ct);
        var deadline = hostContext.Deadline;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disconnect.Token);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            linked.Cancel();
        else
            linked.CancelAfter(remaining);

        var call = CreateOutgoingCall(deadline);
        var cancellation = new SidecarCancellationIdentity(
            call.CancellationId,
            SidecarCapabilitySessionValidator.ComputeBindingHash(Session.Binding),
            deadline);
        var effectiveAction = CreatePayload(
            dispatcherContext.Action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var request = SidecarActionCapabilityRequest.HostEntry(
            call,
            identity,
            effectiveAction,
            cancellation,
            deadline,
            hostContext,
            new SidecarActionTerminalRegistration(
                terminalId,
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.ResultTypeIdentity,
                identity.ResultSchemaVersion,
                identity.DescriptorHash)) with
        {
            EffectiveHostEntryContext = CreateEffectiveHostEntryContext(
                call,
                identity,
                effectiveAction,
                cancellation,
                deadline,
                hostContext,
                dispatcherContext,
                terminalId),
        };
        var begin = Session.BeginCall(
            call,
            SidecarCapabilityKind.Action,
            effectiveAction,
            effectiveAction.ByteLength,
            DateTimeOffset.UtcNow);
        if (!begin.Accepted)
        {
            linked.Dispose();
            throw new OutOfProcessCapabilityException(
                begin.Code ?? SidecarCapabilityErrors.Unauthorized,
                begin.Message ?? "The capability session rejected the module action entry.");
        }

        var pending = new PendingOutgoingAction(linked)
        {
            Request = request,
        };
        if (!_outgoingActions.TryAdd(call.CallId, pending))
        {
            CompleteSessionCall(call.CallId, 0);
            linked.Dispose();
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The module action entry call identifier was reused.");
        }

        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                linked.Token);
            var response = await pending.Completion.Task.WaitAsync(linked.Token);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                Session.Binding,
                Session);
            if (!responseValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    responseValidation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                    responseValidation.Message
                        ?? "The module action entry response was rejected.");
            }

            var completion = CompleteSessionCall(
                call.CallId,
                response.Outcome.TerminalCallCount);
            if (!completion)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The module action entry call could not be completed.");
            }

            return response;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await SendCancellationAsync(
                call,
                cancellation,
                deadline,
                linked.Token,
                ct);
            CompleteSessionCall(call.CallId, 0);
            throw;
        }
        finally
        {
            _outgoingActions.TryRemove(call.CallId, out _);
            linked.Dispose();
            RequestRotationRetry();
        }
    }

    private SidecarActionEffectiveHostEntryContext CreateEffectiveHostEntryContext<TAction>(
        SidecarCapabilityCallIdentity call,
        SidecarActionDescriptorIdentity identity,
        SidecarSerializedPayload effectiveAction,
        SidecarCancellationIdentity cancellation,
        DateTimeOffset deadline,
        HostActionEntryRequestContext initiatingContext,
        ActionContext<TAction> dispatcherContext,
        Guid terminalId)
    {
        var receipt = new SidecarTerminalReceipt(
            Guid.NewGuid().ToString("N"),
            identity.Key,
            identity.Version,
            call.CallId,
            dispatcherContext.Attempt,
            $"{Session.Binding.GraphId}:{call.CallId:N}",
            effectiveAction.ContentHash);
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = deadline < Session.Binding.ExpiresAt
            ? deadline
            : Session.Binding.ExpiresAt;
        var authority = new SidecarHostTerminalAuthority(
            Guid.NewGuid(),
            call.SessionId,
            call.RequestId,
            call.CancellationId,
            call.CallId,
            Session.Binding.ModuleId,
            Session.Binding.GraphId,
            SidecarActionInvocationKind.HostEntry,
            identity.Key,
            identity.Version,
            identity.DescriptorHash,
            effectiveAction.TypeIdentity,
            effectiveAction.SchemaVersion,
            effectiveAction.ContentHash,
            effectiveAction.ByteLength,
            receipt.ReceiptId,
            receipt.ActionKey,
            receipt.ActionVersion,
            receipt.CallId,
            receipt.Attempt,
            receipt.IdempotencyScope,
            receipt.ContentHash,
            deadline,
            issuedAt,
            expiresAt,
            "pending")
        {
            TerminalId = terminalId,
            SnapshotContentHash = SidecarCapabilityTransportValidation.ComputeSnapshotHash(
                dispatcherContext.Snapshot),
            Caller = dispatcherContext.Caller,
            Features = dispatcherContext.Features,
            TraceId = dispatcherContext.TraceId,
            IdempotencyKey = dispatcherContext.IdempotencyKey,
            InvocationId = dispatcherContext.InvocationId,
            ParentInvocationId = dispatcherContext.ParentInvocationId,
            Depth = dispatcherContext.Depth,
            Attempt = dispatcherContext.Attempt,
            HostContextBindingHash = SidecarCapabilityTransportValidation
                .ComputeHostActionEntryContextBindingHash(initiatingContext),
        };
        authority = authority with
        {
            CanonicalBindingHash = SidecarCapabilityTransportValidation
                .ComputeTerminalAuthorityBindingHash(authority),
            Proof = OutOfProcessCapabilitySecurity.CreateTerminalProof(
                authority,
                _controlToken),
        };
        var effectiveContext = new SidecarActionTerminalExecutionContext(
            call,
            SidecarActionInvocationKind.HostEntry,
            identity,
            effectiveAction,
            dispatcherContext.Snapshot,
            dispatcherContext.InvocationId,
            dispatcherContext.ParentInvocationId,
            dispatcherContext.Depth,
            dispatcherContext.Attempt,
            dispatcherContext.Caller,
            dispatcherContext.Features,
            dispatcherContext.TraceId,
            dispatcherContext.IdempotencyKey,
            cancellation,
            receipt,
            deadline);
        return new SidecarActionEffectiveHostEntryContext(
            initiatingContext,
            effectiveContext,
            authority);
    }

    private SidecarCapabilityCallIdentity CreateOutgoingCall(DateTimeOffset deadline)
    {
        var binding = Session.Binding;
        var sequence = Interlocked.Increment(ref _sequence);
        return new SidecarCapabilityCallIdentity(
            binding.SessionId,
            binding.RequestId,
            binding.CancellationId,
            Guid.NewGuid(),
            $"{binding.SessionId:N}:{sequence}:{Guid.NewGuid():N}",
            binding.ModuleId,
            binding.GraphId,
            SidecarCapabilityKind.Action,
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

    internal HostActionEntryCarrierAuthority BeginHostActionEntryCarrier(
        HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Contribution is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host action context has no ingress contribution.");
        }
        HostActionEntryCarrierAuthority? authority = null;
        Task? rotation = null;
        _rotationGate.Wait(_disconnect.Token);
        try
        {
            if (!_options.HostActionEntryContexts.TryBeginCarrier(context))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The host action context is not pending for a carrier.");
            }

            RequestRotationRetry();
            var beforeCarrierSessionBegin = _options.BeforeCarrierSessionBeginAsync;
            _options.BeforeCarrierSessionBeginAsync = null;
            if (beforeCarrierSessionBegin is not null)
                beforeCarrierSessionBegin().GetAwaiter().GetResult();

            var carrier = new HostActionEntryCarrierIdentity(
                context.Ingress,
                context.InvocationId,
                context.Contribution.IngressBinding);
            var validation = Session.BeginHostActionEntryCarrier(
                OutOfProcessHostActionEntryContextRegistry.WithoutPayloadBinding(context),
                carrier,
                DateTimeOffset.UtcNow,
                out authority);
            if (!validation.Accepted || authority is null)
            {
                throw new OutOfProcessCapabilityException(
                    validation.Code ?? SidecarCapabilityErrors.Unauthorized,
                    validation.Message
                        ?? "The host action entry carrier was rejected.");
            }

            lock (_rotationSync)
            {
                var maximumCalls = Session.Binding.ConcurrencyLimits.MaximumCallsPerRequest;
                if (_rotationReady is null
                    && (_rotationTask is null || _rotationTask.IsCompleted)
                    && Volatile.Read(ref _completedCallsForBinding)
                        >= Math.Max(maximumCalls - 2, 1))
                {
                    _rotationReady = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                rotation = _rotationTask ?? _rotationReady?.Task;
            }
            RequestRotationRetry();
        }
        catch
        {
            _options.HostActionEntryContexts.RestorePendingCarrier(context);
            RequestRotationRetry();
            throw;
        }
        finally
        {
            _rotationGate.Release();
        }

        if (rotation is not null)
        {
            RequestRotationRetry();
            rotation.GetAwaiter().GetResult();
        }

        return authority!;
    }

    internal void CompleteHostActionEntryCarrier(
        HostActionEntryCarrierAuthority authority,
        HostActionEntryCarrierCompletionKind completion)
    {
        ArgumentNullException.ThrowIfNull(authority);
        try
        {
            var validation = Session.CompleteHostActionEntryCarrier(
                authority,
                completion,
                DateTimeOffset.UtcNow);
            if (!validation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    validation.Code ?? SidecarCapabilityErrors.Unauthorized,
                    validation.Message
                        ?? "The host action entry carrier completion was rejected.");
            }
        }
        finally
        {
            _options.HostActionEntryContexts.CompleteCarrier(authority.CapabilityId);
            RequestRotationRetry();
        }
    }

    private static SidecarCapabilitySession CreateSession(
        SidecarCapabilitySessionBinding binding,
        string controlToken) =>
        new(
            binding,
            authority => OutOfProcessCapabilitySecurity.Authenticate(authority, controlToken),
            _ => true,
            DateTimeOffset.UtcNow);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disconnect.Token);
        await RunCoreAsync(linked.Token);
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await OutOfProcessCapabilityWire.ReceiveAsync(
                    _socket,
                    _limits.ProtocolMessageBytes,
                    ct);
                switch (frame.Kind)
                {
                    case OutOfProcessCapabilityFrameKind.ActionRequest:
                        await ScheduleActionRequestAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarActionCapabilityRequest>(frame.Payload),
                            ct);
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionResponse:
                        CompleteOutgoingAction(
                            OutOfProcessCapabilityWire.Deserialize<SidecarActionCapabilityResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.StorageRequest:
                        await ScheduleStorageRequestAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarStorageCapabilityRequest>(frame.Payload),
                            ct);
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityCancellation:
                        CancelCall(OutOfProcessCapabilityWire.Deserialize<OutOfProcessCapabilityCancellation>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityRebindAccepted:
                        CompleteRebind(
                            OutOfProcessCapabilityWire.Deserialize<SidecarCapabilityValidationResult>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalRequest:
                        await HandleNestedTerminalRequestAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportRequest>(frame.Payload),
                            ct);
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalResponse:
                        CompleteTerminal(
                            OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.Error:
                        throw ReadError(frame.Payload);
                    default:
                        throw new OutOfProcessCapabilityException(
                            SidecarCapabilityErrors.MalformedMessage,
                            $"The host received unsupported capability frame '{frame.Kind}'.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OutOfProcessCapabilityException ex)
        {
            Volatile.Write(ref _runFailure, ex);
        }
        finally
        {
            var binding = Session.Binding;
            _options.HostActionEntryContexts.Invalidate(binding);
            Session.Disconnect();
            lock (_rotationSync)
            {
                _rotationAcknowledgement?.TrySetException(
                    new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Disconnected,
                        "The sidecar capability channel disconnected."));
                _rotationReady?.TrySetException(
                    new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Disconnected,
                        "The sidecar capability channel disconnected."));
            }
            foreach (var terminal in _terminals.Values)
            {
                terminal.TrySetException(new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The sidecar capability channel disconnected."));
            }
            foreach (var action in _outgoingActions.Values)
            {
                action.Cancellation.Cancel();
                action.Completion.TrySetException(new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The sidecar capability channel disconnected."));
            }
            foreach (var call in _calls.Values)
                call.Cancellation.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _disconnect.Cancel();
        var binding = Session.Binding;
        _options.HostActionEntryContexts.Invalidate(binding);
        Session.Disconnect();
        lock (_rotationSync)
        {
            _rotationAcknowledgement?.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessCapabilityHostSession)));
            _rotationReady?.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessCapabilityHostSession)));
        }
        foreach (var call in _calls.Values)
            call.Cancellation.Cancel();
        foreach (var terminal in _terminals.Values)
        {
            terminal.TrySetException(new ObjectDisposedException(nameof(OutOfProcessCapabilityHostSession)));
        }
        foreach (var action in _outgoingActions.Values)
        {
            action.Cancellation.Cancel();
            action.Completion.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessCapabilityHostSession)));
        }

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
        await _capabilityQueue.DisposeAsync();
        await _rotationGate.WaitAsync(CancellationToken.None);
        _rotationGate.Release();
        _rotationGate.Dispose();
        SendGate.Dispose();
        _disconnect.Dispose();
    }

    private async Task ScheduleActionRequestAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken channelCt)
    {
        await WaitForRotationAsync(channelCt);
        if (_capabilityQueue.TrySchedule(
                ct => HandleActionRequestAsync(request, ct),
                channelCt,
                out var completion))
        {
            _ = ObserveQueueCompletionAsync(completion);
            return;
        }

        await SendActionFailureAsync(
            request,
            SidecarCapabilityErrors.ModuleBusy,
            "The host capability execution queue is full.",
            channelCt);
    }

    private async Task ScheduleStorageRequestAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken channelCt)
    {
        await WaitForRotationAsync(channelCt);
        if (_capabilityQueue.TrySchedule(
                ct => HandleStorageRequestAsync(request, ct),
                channelCt,
                out var completion))
        {
            _ = ObserveQueueCompletionAsync(completion);
            return;
        }

        await SendStorageFailureAsync(
            request,
            SidecarCapabilityErrors.ModuleBusy,
            "The host capability execution queue is full.",
            channelCt);
    }

    private async Task ObserveQueueCompletionAsync(Task completion)
    {
        try
        {
            await completion;
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
        }
        catch
        {
            _disconnect.Cancel();
        }
    }

    private void CancelCall(OutOfProcessCapabilityCancellation cancellation)
    {
        if (!_calls.TryGetValue(cancellation.Call.CallId, out var active)
            || !cancellation.Call.Equals(CreateExpectedCall(cancellation.Call)))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The capability cancellation does not match an active call.");
        }

        if (cancellation.Cancellation.CancellationId != cancellation.Call.CancellationId
            || !string.Equals(
                cancellation.Cancellation.AuthorityHash,
                SidecarCapabilitySessionValidator.ComputeBindingHash(Session.Binding),
                StringComparison.Ordinal)
            || cancellation.Cancellation.ExpiresAt != cancellation.Call.Deadline)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The capability cancellation identity is invalid.");
        }

        active.Cancellation.Cancel();
    }

    private ActiveCall RegisterCall(
        SidecarCapabilityCallIdentity call,
        CancellationToken channelCancellation,
        SidecarActionCapabilityRequest? actionRequest = null)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(channelCancellation);
        var remaining = call.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            cancellation.Cancel();
        else
            cancellation.CancelAfter(remaining);
        var active = new ActiveCall(cancellation)
        {
            ActionRequest = actionRequest,
        };
        if (!_calls.TryAdd(call.CallId, active))
        {
            cancellation.Dispose();
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The capability call identifier was reused.");
        }

        return active;
    }

    private void AbandonCall(Guid callId, ActiveCall active)
    {
        if (_calls.TryRemove(callId, out var removed))
            removed.Cancellation.Dispose();
        else
            active.Cancellation.Dispose();
    }

    private bool CompleteCall(Guid callId, int terminalCallCount)
    {
        if (!_calls.TryGetValue(callId, out var active)
            || Interlocked.Exchange(ref active.Completed, 1) != 0)
        {
            return active is not null
                && Volatile.Read(ref active.CompletionAccepted) != 0;
        }

        var effectiveTerminalCallCount = terminalCallCount;
        if (effectiveTerminalCallCount == 0
            && Session.TryGetTerminalReceipt(callId, out _))
        {
            effectiveTerminalCallCount = 1;
        }

        var accepted = CompleteSessionCall(callId, effectiveTerminalCallCount);
        if (!accepted && effectiveTerminalCallCount != 0)
            accepted = CompleteSessionCall(callId, 0);
        if (accepted)
            Volatile.Write(ref active.CompletionAccepted, 1);
        return accepted;
    }

    private async ValueTask FinishCallAsync(
        Guid callId,
        ActiveCall? active,
        CancellationToken channelCt)
    {
        if (active is null || !_calls.TryRemove(callId, out var removed))
            return;
        if (Interlocked.Exchange(ref removed.Completed, 1) == 0
            || Volatile.Read(ref removed.CompletionAccepted) == 0)
        {
            if (CompleteSessionCall(callId, 0))
                Volatile.Write(ref removed.CompletionAccepted, 1);
        }
        removed.Cancellation.Dispose();
        try
        {
            await StartRotationIfReadyAsync(channelCt);
        }
        finally
        {
            RequestRotationRetry();
        }
    }

    private bool CompleteSessionCall(Guid callId, int terminalCallCount)
    {
        var session = Session;
        var result = session.CompleteCall(callId, terminalCallCount);
        if (result.Accepted
            && Interlocked.Increment(ref _completedCallsForBinding)
                >= session.Binding.ConcurrencyLimits.MaximumCallsPerRequest)
        {
            lock (_rotationSync)
            {
                _rotationReady ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return result.Accepted;
    }

    private async Task WaitForRotationAsync(CancellationToken ct)
    {
        Task? rotation;
        lock (_rotationSync)
            rotation = _rotationReady?.Task;
        if (rotation is not null)
            await rotation.WaitAsync(ct);
    }

    private async ValueTask<DateTimeOffset?> StartRotationIfReadyAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Session.SweepExpiredHostActionEntryCarriers(now);
        _options.HostActionEntryContexts.SweepExpired(now);
        Task? rotation = null;
        await _rotationGate.WaitAsync(ct);
        try
        {
            Func<Task>? beforeRotationStart;
            TaskCompletionSource ready;
            lock (_rotationSync)
            {
                if (_rotationReady is null
                    || !_calls.IsEmpty)
                    return null;
                var nextPendingExpiration = _options.HostActionEntryContexts
                    .NextPendingContextExpiration();
                if (nextPendingExpiration is not null)
                    return nextPendingExpiration;

                ready = _rotationReady;
                beforeRotationStart = _options.BeforeRotationStartAsync;
                _options.BeforeRotationStartAsync = null;
            }

            if (beforeRotationStart is not null)
                await beforeRotationStart();

            lock (_rotationSync)
            {
                if (_rotationReady is null || !_calls.IsEmpty)
                    return null;
                if (_rotationTask is null || _rotationTask.IsCompleted)
                    _rotationTask = RotateBindingAsync(ready, ct);
                rotation = _rotationTask;
            }

            if (rotation is not null)
                await rotation;
            return null;
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    internal void RequestRotationRetry()
    {
        lock (_rotationSync)
        {
            if (_rotationReady is null
                || Volatile.Read(ref _disposed) != 0
                || _disconnect.IsCancellationRequested)
            {
                return;
            }

            _rotationRetryWake.TrySetResult();
            if (_rotationRetryTask is null || _rotationRetryTask.IsCompleted)
                _rotationRetryTask = RunRotationRetryAsync();
        }
    }

    private async Task RunRotationRetryAsync()
    {
        try
        {
            while (!_disconnect.IsCancellationRequested)
            {
                var retryAt = await StartRotationIfReadyAsync(_disconnect.Token);
                if (retryAt is null)
                    return;

                var delay = retryAt.Value - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                    continue;

                Task wake;
                lock (_rotationSync)
                {
                    if (_rotationRetryWake.Task.IsCompleted)
                    {
                        _rotationRetryWake = CreateSignal();
                        continue;
                    }

                    wake = _rotationRetryWake.Task;
                }

                await Task.WhenAny(
                    Task.Delay(delay, _disconnect.Token),
                    wake);
            }
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
        }
        catch
        {
            _disconnect.Cancel();
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RotateBindingAsync(
        TaskCompletionSource ready,
        CancellationToken ct)
    {
        var acknowledgement = new TaskCompletionSource<SidecarCapabilityValidationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_rotationSync)
            _rotationAcknowledgement = acknowledgement;

        try
        {
            var nextBinding = OutOfProcessCapabilitySecurity.CreateBinding(
                Session.Binding.GraphId,
                Session.Binding.ModuleId,
                Session.Binding.ProtocolVersion,
                _options.Grant,
                _limits,
                _controlToken);
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.CapabilityRebind,
                nextBinding,
                _limits.ProtocolMessageBytes,
                SendGate,
                ct);
            var accepted = await acknowledgement.Task.WaitAsync(ct);
            if (!accepted.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    accepted.Code ?? SidecarCapabilityErrors.Unauthorized,
                    accepted.Message ?? "The sidecar rejected the capability binding rotation.");
            }

            var rotation = Session.RotateBinding(nextBinding, DateTimeOffset.UtcNow);
            if (!rotation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    rotation.Code ?? SidecarCapabilityErrors.Unauthorized,
                    rotation.Message
                        ?? "The capability session rejected binding rotation.");
            }
            _options.HostActionEntryContexts.Bind(
                nextBinding,
                IssueHostActionEntryContext,
                preserveActiveContexts: true,
                issueCoordinator: ExecuteContextIssuance);
            Interlocked.Exchange(ref _completedCallsForBinding, 0);
            lock (_rotationSync)
            {
                _rotationAcknowledgement = null;
                _rotationReady = null;
                ready.TrySetResult();
                _rotationTask = null;
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            _disconnect.Cancel();
            throw;
        }
        finally
        {
            lock (_rotationSync)
            {
                if (_rotationAcknowledgement == acknowledgement)
                    _rotationAcknowledgement = null;
            }
        }
    }

    private void CompleteRebind(SidecarCapabilityValidationResult result)
    {
        lock (_rotationSync)
        {
            if (_rotationAcknowledgement is null)
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthorized,
                    "The host received an unsolicited capability binding acknowledgement.");
            _rotationAcknowledgement.TrySetResult(result);
        }
    }

    private void CompleteOutgoingAction(SidecarActionCapabilityResponse response)
    {
        var callId = response.ResultIdentity?.CallId
            ?? response.Outcome.Receipt?.CallId;
        if (callId is null || callId == Guid.Empty)
        {
            var pending = _outgoingActions.Values
                .Select(value => value.Request?.Call.CallId)
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .Take(2)
                .ToArray();
            if (pending.Length != 1)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.MalformedMessage,
                    "The module action response has no unique pending call.");
            }

            callId = pending[0];
        }

        if (_outgoingActions.TryGetValue(callId.Value, out var action))
        {
            action.Completion.TrySetResult(response);
            return;
        }

        throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.Unauthorized,
            "The module action response does not match an active call.");
    }

    private bool IsHostActionAuthorized(SidecarActionCapabilityRequest request)
    {
        var snapshotGrant = _options.ActionSnapshot.ActionGrants.SingleOrDefault(grant =>
            grant.ActionKey == request.Descriptor.Key
            && grant.ActionVersion == request.Descriptor.Version);
        var authorizationGrant = _authorization.ActionGrants.SingleOrDefault(grant =>
            grant.ActionKey == request.Descriptor.Key
            && grant.ActionVersion == request.Descriptor.Version);
        if (authorizationGrant is null || snapshotGrant is null)
            return false;

        if (authorizationGrant.Capabilities != snapshotGrant.Capabilities
            || authorizationGrant.SensitiveApproved != snapshotGrant.SensitiveApproved
            || authorizationGrant.AcceptUnknownSchemas != snapshotGrant.AcceptUnknownSchemas)
            return false;

        if (request.Invocation == SidecarActionInvocationKind.HostEntry)
            return request.Snapshot is null
                && (request.HostContext is not null || request.NestedCarrier is not null)
                && request.Terminal is { IsWellFormed: true }
                && request.Terminal.DescriptorHash == request.Descriptor.DescriptorHash;

        return request.Snapshot is not null
            && request.HostContext is null
            && string.Equals(
                request.Snapshot.ContractHash,
                _options.ActionSnapshot.ContractHash,
                StringComparison.Ordinal);
    }

    private SidecarCapabilityCallIdentity CreateExpectedCall(
        SidecarCapabilityCallIdentity call) =>
        new(
            _session.Binding.SessionId,
            _session.Binding.RequestId,
            _session.Binding.CancellationId,
            call.CallId,
            call.ReplayNonce,
            _session.Binding.ModuleId,
            _session.Binding.GraphId,
            call.Capability,
            call.Sequence,
            call.Deadline);

    private async Task HandleActionRequestAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken channelCt)
    {
        ActiveCall? active = null;
        try
        {
            if (request.Invocation == SidecarActionInvocationKind.HostEntry
                && request.NestedCarrier is not null)
            {
                request = ResolveNestedActionRequest(request);
            }

            var validation = SidecarCapabilityTransportValidation.ValidateActionRequest(
                request,
                _session.Binding,
                DateTimeOffset.UtcNow,
                ValidateTerminalAuthority);
            if (!validation.Accepted)
            {
                await SendActionFailureAsync(request, validation.Code, validation.Message, channelCt);
                return;
            }

            ObserveSequence(request.Call.Sequence);

            if (!IsHostActionAuthorized(request))
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.Unauthorized,
                    "The action request does not match the host-owned action snapshot.",
                    channelCt);
                return;
            }

            active = RegisterCall(request.Call, channelCt, request);
            var contractRequest = request.HostContext is null
                ? request
                : request with
                {
                    HostContext = OutOfProcessHostActionEntryContextRegistry
                        .WithoutPayloadBinding(request.HostContext),
                };
            var begin = Session.BeginActionCall(
                contractRequest,
                request.Action.ByteLength,
                DateTimeOffset.UtcNow,
                out var hostContext);
            active.HostContext = hostContext;
            if (!begin.Accepted)
            {
                AbandonCall(request.Call.CallId, active);
                active = null;
                await SendActionFailureAsync(request, begin.Code, begin.Message, channelCt);
                return;
            }

            if (!_options.ActionDescriptors.TryGet(request.Descriptor, out var registration))
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.UnknownAction,
                    "The host action descriptor was not registered for this sidecar.",
                    channelCt);
                return;
            }

            var outcome = await registration.Dispatch(
                this,
                request,
                active.Cancellation.Token);
            var response = CreateActionResponse(request, registration, outcome);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                _session.Binding,
                _session);
            if (!responseValidation.Accepted)
            {
                await SendActionFailureAsync(
                    request,
                    responseValidation.Code,
                    $"{responseValidation.Message} request={request.Descriptor.Key.Value}:{request.Descriptor.Version}:{request.Descriptor.DescriptorHash}; "
                    + $"response={response.ResultIdentity?.ActionKey.Value}:{response.ResultIdentity?.ActionVersion}:{response.ResultIdentity?.ResultTypeIdentity}:{response.ResultIdentity?.ContentHash}; "
                    + $"outcome={response.Outcome.Kind}:{response.Outcome.TerminalCallCount}:{response.Outcome.Receipt?.ReceiptId}",
                    channelCt);
                return;
            }

            var completion = CompleteCall(
                request.Call.CallId,
                response.Outcome.TerminalCallCount);
            if (!completion)
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.HostFailure,
                    "The host capability call could not be completed.",
                    channelCt);
                return;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionResponse,
                response,
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
        catch (OperationCanceledException) when (
            active is not null
            && active.Cancellation.IsCancellationRequested
            && !channelCt.IsCancellationRequested)
        {
            var terminalCallCount = Session.TryGetTerminalReceipt(request.Call.CallId, out _)
                ? 1
                : 0;
            if (active is not null)
                CompleteCall(request.Call.CallId, terminalCallCount);
            await SendActionFailureAsync(
                request,
                SidecarCapabilityErrors.Cancelled,
                request.Deadline <= DateTimeOffset.UtcNow
                    ? "The host action deadline expired."
                    : "The sidecar cancelled the host action.",
                channelCt);
        }
        catch (OperationCanceledException) when (channelCt.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastHandledFailure, ex);
            var terminalCallCount = Session.TryGetTerminalReceipt(request.Call.CallId, out _)
                ? 1
                : 0;
            if (active is not null)
                CompleteCall(request.Call.CallId, terminalCallCount);
            await SendActionFailureAsync(
                request,
                SidecarCapabilityErrors.HostFailure,
                "The host action dispatcher failed.",
                channelCt);
        }
        finally
        {
            await FinishCallAsync(request.Call.CallId, active, channelCt);
        }
    }

    private async Task HandleNestedTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        if (request.CrossSidecarActionRequest is not null)
        {
            await HandleCrossSidecarTerminalRequestAsync(request, ct);
            return;
        }

        if (request.NestedCarrierRequest is null
            || !_calls.TryGetValue(request.Call.CallId, out var active)
            || active.ActionRequest is not { } initiatingRequest
            || !_terminals.ContainsKey(request.Call.CallId))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The nested terminal request has no active parent terminal exchange.");
        }

        var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalRequest(
            initiatingRequest,
            request,
            Session.Binding,
            DateTimeOffset.UtcNow,
            ValidateTerminalAuthority);
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                validation.Message ?? "The nested terminal request was rejected.");
        }

        var record = Session.RecordTerminal(
            request.Call.CallId,
            request.Authority.AuthorityId,
            request.Receipt);
        if (!record.Accepted && !Session.TryGetTerminalReceipt(request.Call.CallId, out _))
        {
            throw new OutOfProcessCapabilityException(
                record.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                record.Message ?? "The parent terminal authority was rejected.");
        }

        var resolvedNestedRequest = ResolveNestedRelayRequest(
            request.NestedCarrierRequest,
            active.HostContext,
            out var resolvedDescriptor,
            out var resolvedContribution);
        var issue = Session.IssueNestedHostActionEntryRelay(
            request.Call,
            resolvedNestedRequest,
            resolvedDescriptor,
            resolvedContribution,
            DateTimeOffset.UtcNow,
            out var relay);
        var outcomeKind = issue.Accepted && relay is not null
            ? SidecarNestedHostActionEntryRelayOutcomeKind.Issued
            : SidecarNestedHostActionEntryRelayOutcomeKind.Failed;
        var failure = issue.Accepted && relay is not null
            ? null
            : new SidecarSafeFailureIdentity(
                Guid.NewGuid(),
                issue.Code ?? SidecarCapabilityErrors.HostFailure,
                issue.Message ?? "The host could not issue the nested action carrier.",
                Retryable: false);
        var response = CreateNestedRelayResponse(
            request,
            relay,
            outcomeKind,
            failure);
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private SidecarNestedHostActionEntryRequest ResolveNestedRelayRequest(
        SidecarNestedHostActionEntryRequest request,
        HostActionEntryRequestContext? parentContext,
        out SidecarActionDescriptorIdentity resolvedDescriptor,
        out HostActionEntryContribution resolvedContribution)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.ActionDescriptors.TryResolve(
                request.ActionKey,
                request.ActionVersion,
                out var registration))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnknownAction,
                $"The nested action '{request.ActionKey.Value}:{request.ActionVersion}' "
                + "is not registered in host descriptor authority.");
        }

        if (parentContext is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The nested action has no authenticated parent context.");
        }

        var parentContribution = parentContext.Contribution
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The nested action has no authenticated parent contribution.");
        resolvedDescriptor = registration.Identity;
        resolvedContribution = parentContribution with
        {
            Lineage = new HostActionEntryLineage(
                resolvedDescriptor.Key,
                resolvedDescriptor.Version,
                resolvedDescriptor.DescriptorHash,
                resolvedDescriptor.InputTypeIdentity,
                resolvedDescriptor.InputSchemaVersion,
                resolvedDescriptor.InputSchemaHash,
                null,
                null),
        };
        var validation = SidecarCapabilityTransportValidation
            .ValidateResolvedNestedHostActionEntryRequest(
                request,
                resolvedDescriptor,
                resolvedContribution,
                Session.Binding,
                DateTimeOffset.UtcNow);
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                validation.Message ?? "The nested action does not match host descriptor authority.");
        }

        return request;
    }

    private SidecarActionCapabilityRequest ResolveNestedActionRequest(
        SidecarActionCapabilityRequest request)
    {
        var carrier = request.NestedCarrier
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The nested action request has no host-issued carrier.");
        if (request.Descriptor.Key != carrier.ActionKey
            || request.Descriptor.Version != carrier.ActionVersion)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The nested action request does not select its host-issued carrier action.");
        }

        if (!_options.ActionDescriptors.TryResolve(
                carrier.ActionKey,
                carrier.ActionVersion,
                out var registration)
            || !OutOfProcessActionDescriptorIdentity.Matches(
                registration.Identity,
                request.Descriptor)
            || !string.Equals(
                registration.Identity.DescriptorHash,
                carrier.DescriptorHash,
                StringComparison.Ordinal))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnknownAction,
                "The nested carrier does not identify a registered host descriptor.");
        }

        var action = request.Action;
        if (!string.Equals(
                action.TypeIdentity,
                request.Descriptor.InputTypeIdentity,
                StringComparison.Ordinal)
            || action.SchemaVersion != request.Descriptor.InputSchemaVersion
            || !string.Equals(
                action.ContentHash,
                carrier.ActionContentHash,
                StringComparison.Ordinal)
            || action.ByteLength != carrier.ActionByteLength)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The nested action payload does not match its host-issued carrier.");
        }

        var terminal = request.Terminal
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The nested action request has no terminal registration.");
        return request with
        {
            Descriptor = request.Descriptor,
            Action = action,
            Terminal = terminal,
        };
    }

    private async Task HandleStorageRequestAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken channelCt)
    {
        ActiveCall? active = null;
        try
        {
            var validation = SidecarCapabilityTransportValidation.ValidateStorageRequest(
                request,
                _session.Binding,
                DateTimeOffset.UtcNow);
            if (!validation.Accepted)
            {
                await SendStorageFailureAsync(request, validation.Code, validation.Message, channelCt);
                return;
            }

            ObserveSequence(request.Call.Sequence);

            if (!string.Equals(request.ModuleId, _session.Binding.ModuleId, StringComparison.Ordinal)
                || (request.Operation != SidecarStorageOperationKind.ListContracts
                    && !_options.OwnedStorageNames.Contains(request.StorageName)))
            {
                await SendStorageFailureAsync(
                    request,
                    SidecarCapabilityErrors.Unauthorized,
                    "The storage request is not owned by the authorized module.",
                    channelCt);
                return;
            }

            var requestPayload = request.RequestPayload;
            var requestFramePayload = requestPayload ?? EmptyPayload();
            active = RegisterCall(request.Call, channelCt);
            var begin = Session.BeginCall(
                request.Call,
                SidecarCapabilityKind.Storage,
                requestFramePayload,
                requestFramePayload.ByteLength,
                DateTimeOffset.UtcNow);
            if (!begin.Accepted)
            {
                AbandonCall(request.Call.CallId, active);
                active = null;
                await SendStorageFailureAsync(request, begin.Code, begin.Message, channelCt);
                return;
            }

            var response = await InvokeStorageAsync(request, active.Cancellation.Token);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateStorageResponse(
                request,
                response,
                _session.Binding);
            if (!responseValidation.Accepted)
            {
                await SendStorageFailureAsync(
                    request,
                    responseValidation.Code,
                    responseValidation.Message,
                    channelCt);
                return;
            }

            if (!CompleteCall(request.Call.CallId, 0))
            {
                await SendStorageFailureAsync(
                    request,
                    SidecarCapabilityErrors.HostFailure,
                    "The host capability call could not be completed.",
                    channelCt);
                return;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.StorageResponse,
                response,
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
        catch (OperationCanceledException) when (
            active is not null
            && active.Cancellation.IsCancellationRequested
            && !channelCt.IsCancellationRequested)
        {
            if (active is not null)
                CompleteCall(request.Call.CallId, 0);
            await SendStorageFailureAsync(
                request,
                SidecarCapabilityErrors.Cancelled,
                request.Deadline <= DateTimeOffset.UtcNow
                    ? "The host storage deadline expired."
                    : "The sidecar cancelled the host storage call.",
                channelCt);
        }
        catch (OperationCanceledException) when (channelCt.IsCancellationRequested)
        {
        }
        catch (ModuleStorageContractException ex)
        {
            if (active is not null)
                CompleteCall(request.Call.CallId, 0);
            await SendStorageFailureAsync(
                request,
                ex.Failure.Code,
                ex.Failure.Message,
                channelCt,
                ex.Failure);
        }
        catch (Exception)
        {
            if (active is not null)
                CompleteCall(request.Call.CallId, 0);
            await SendStorageFailureAsync(
                request,
                SidecarCapabilityErrors.HostFailure,
                "The host storage gateway failed.",
                channelCt);
        }
        finally
        {
            await FinishCallAsync(request.Call.CallId, active, channelCt);
        }
    }

    internal async ValueTask<OutOfProcessActionDispatchResult> DispatchAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        SidecarActionDescriptorIdentity identity,
        SidecarActionCapabilityRequest request,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>>? hostTerminal,
        CancellationToken ct)
    {
        var action = Deserialize<TAction>(request.Action);
        if (request.Invocation == SidecarActionInvocationKind.HostEntry)
        {
            if (request.NestedCarrier is not null)
            {
                if (!_calls.TryGetValue(request.Call.CallId, out var active)
                    || active.HostContext is null)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The nested host action call has no authenticated host context.");
                }

                var nestedContext = active.HostContext;
                var nestedOutcome = await _options.ActionDispatcher.RunAsync(
                    descriptor,
                    action,
                    (context, terminalCancellation) => InvokeTerminalAsync<TAction, TResult>(
                        request,
                        identity,
                        context,
                        nestedContext,
                        terminalCancellation),
                    _options.ActionSnapshot,
                    ct);
                return new OutOfProcessActionDispatchResult(
                    nestedOutcome.Kind,
                    nestedOutcome.Result,
                    nestedOutcome.Error,
                    nestedOutcome.Uncertainty,
                    nestedOutcome.Continuation,
                    _session.TryGetTerminalReceipt(request.Call.CallId, out _)
                        ? 1
                        : 0);
            }

            var context = request.HostContext
                ?? throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthorized,
                    "The host action request has no host context.");
            if (request.Terminal is null || !request.Terminal.IsWellFormed)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnknownAction,
                    "The host action request has no valid terminal registration.");
            }

            var entryRequest = new HostActionEntryRequest<TAction, TResult>(
                descriptor,
                action,
                context);
            if (!_options.HostActionEntryContexts.TryConsume(
                    entryRequest,
                    DateTimeOffset.UtcNow))
            {
                throw new OutOfProcessCapabilityException(
                    SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                    "The host action context is invalid, expired, or already used.");
            }
            var issued = _session.IssueHostActionEntry(
                entryRequest with
                {
                    Context = OutOfProcessHostActionEntryContextRegistry
                        .WithoutPayloadBinding(entryRequest.Context),
                },
                request.Call.CallId,
                DateTimeOffset.UtcNow,
                authority => OutOfProcessCapabilitySecurity.CreateHostActionEntryProof(
                    authority,
                    _controlToken),
                out var transport);
            if (!issued.Accepted || transport is null)
            {
                throw new OutOfProcessCapabilityException(
                    issued.Code ?? SidecarCapabilityErrors.Unauthorized,
                    issued.Message ?? "The host action entry authority was rejected.");
            }

            var authorityValidation = _session.ValidateHostActionEntry(
                transport,
                DateTimeOffset.UtcNow,
                authority => OutOfProcessCapabilitySecurity.ValidateHostActionEntryProof(
                    authority,
                    _controlToken));
            if (!authorityValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    authorityValidation.Code ?? SidecarCapabilityErrors.Unauthorized,
                    authorityValidation.Message ?? "The host action entry authority was rejected.");
            }

            var hostOutcome = await _options.ActionDispatcher.RunAsync(
                descriptor,
                action,
                    (context, terminalCancellation) => InvokeTerminalAsync<TAction, TResult>(
                        request,
                        identity,
                        context,
                        request.HostContext,
                        terminalCancellation),
                _options.ActionSnapshot,
                ct);
            return new OutOfProcessActionDispatchResult(
                hostOutcome.Kind,
                hostOutcome.Result,
                hostOutcome.Error,
                hostOutcome.Uncertainty,
                hostOutcome.Continuation,
                _session.TryGetTerminalReceipt(request.Call.CallId, out _)
                    ? 1
                    : 0);
        }

        var outcome = await _options.ActionDispatcher.RunAsync(
            descriptor,
            action,
            (context, terminalCancellation) => InvokeTerminalAsync<TAction, TResult>(
                request,
                identity,
                context,
                null,
                terminalCancellation),
            _options.ActionSnapshot,
            ct);
        return new OutOfProcessActionDispatchResult(
            outcome.Kind,
            outcome.Result,
            outcome.Error,
            outcome.Uncertainty,
            outcome.Continuation,
            outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred
                ? _session.TryGetTerminalReceipt(request.Call.CallId, out _)
                    ? 1
                    : 0
                : 0);
    }

    private async ValueTask<TResult> InvokeTerminalAsync<TAction, TResult>(
        SidecarActionCapabilityRequest request,
        SidecarActionDescriptorIdentity identity,
        ActionContext<TAction> context,
        HostActionEntryRequestContext? hostContext,
        CancellationToken ct)
    {
        var effectiveContext = request.Invocation == SidecarActionInvocationKind.HostEntry
            ? BindHostEntryDispatcherContext(request, hostContext, context)
            : context;

        var actionPayload = CreatePayload(
            effectiveContext.Action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var receipt = new SidecarTerminalReceipt(
            Guid.NewGuid().ToString("N"),
            identity.Key,
            identity.Version,
            request.Call.CallId,
            1,
            $"{_session.Binding.GraphId}:{request.Call.CallId:N}",
            actionPayload.ContentHash);
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = request.Deadline < _session.Binding.ExpiresAt
            ? request.Deadline
            : _session.Binding.ExpiresAt;
        var authority = new SidecarHostTerminalAuthority(
            Guid.NewGuid(),
            request.Call.SessionId,
            request.Call.RequestId,
            request.Call.CancellationId,
            request.Call.CallId,
            _session.Binding.ModuleId,
            _session.Binding.GraphId,
            request.Invocation,
            identity.Key,
            identity.Version,
            identity.DescriptorHash,
            actionPayload.TypeIdentity,
            actionPayload.SchemaVersion,
            actionPayload.ContentHash,
            actionPayload.ByteLength,
            receipt.ReceiptId,
            receipt.ActionKey,
            receipt.ActionVersion,
            receipt.CallId,
            receipt.Attempt,
            receipt.IdempotencyScope,
            receipt.ContentHash,
            request.Deadline,
            issuedAt,
            expiresAt,
            "pending");
        authority = authority with
        {
            TerminalId = request.Terminal?.TerminalId ?? Guid.Empty,
            SnapshotContentHash = SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(_options.ActionSnapshot)),
            Caller = effectiveContext.Caller,
            Features = effectiveContext.Features,
            TraceId = effectiveContext.TraceId,
            IdempotencyKey = effectiveContext.IdempotencyKey,
            InvocationId = effectiveContext.InvocationId,
            ParentInvocationId = effectiveContext.ParentInvocationId,
            Depth = effectiveContext.Depth,
            Attempt = effectiveContext.Attempt,
            HostContextBindingHash = hostContext is null
                ? null
                : SidecarCapabilityTransportValidation
                    .ComputeHostActionEntryContextBindingHash(hostContext),
        };
        authority = authority with
        {
            CanonicalBindingHash = SidecarCapabilityTransportValidation
                .ComputeTerminalAuthorityBindingHash(authority),
            Proof = OutOfProcessCapabilitySecurity.CreateTerminalProof(authority, _controlToken),
        };
        var terminalRequest = new SidecarActionTerminalTransportRequest(
            request.Call,
            request.Invocation,
            request.Descriptor,
            actionPayload,
            authority,
            receipt,
            request.Cancellation,
            request.Deadline)
        {
            Context = new SidecarActionTerminalExecutionContext(
                request.Call,
                request.Invocation,
                request.Descriptor,
                actionPayload,
                _options.ActionSnapshot,
                effectiveContext.InvocationId,
                effectiveContext.ParentInvocationId,
                effectiveContext.Depth,
                effectiveContext.Attempt,
                effectiveContext.Caller,
                effectiveContext.Features,
                effectiveContext.TraceId,
                effectiveContext.IdempotencyKey,
                request.Cancellation,
                receipt,
                request.Invocation == SidecarActionInvocationKind.HostEntry
                    ? effectiveContext.Deadline
                    : request.Deadline),
            TerminalId = request.Terminal?.TerminalId ?? Guid.Empty,
        };
        var terminalResponse = await SendTerminalAsync(terminalRequest, ct);
        var responseValidation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
            terminalRequest,
            terminalResponse,
            _session.Binding);
        if (!responseValidation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                responseValidation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                responseValidation.Message ?? "The terminal response was rejected.");
        }

        if (!Session.TryGetTerminalReceipt(request.Call.CallId, out _))
        {
            var record = _session.RecordTerminal(
                request.Call.CallId,
                authority.AuthorityId,
                receipt);
            if (!record.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    record.Code ?? SidecarCapabilityErrors.MalformedMessage,
                    record.Message ?? "The terminal receipt was rejected.");
            }
        }

        if (!terminalResponse.Execution.Completed || terminalResponse.Execution.Result is null)
        {
            throw new OutOfProcessCapabilityException(
                terminalResponse.SafeFailure?.Code ?? SidecarCapabilityErrors.HostFailure,
                terminalResponse.SafeFailure?.Message ?? "The sidecar terminal callback failed.");
        }

        return JsonSerializer.Deserialize<TResult>(
                terminalResponse.Execution.Result.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The sidecar terminal callback returned no result.");
    }

    private static ActionContext<TAction> BindHostEntryDispatcherContext<TAction>(
        SidecarActionCapabilityRequest request,
        HostActionEntryRequestContext? hostContext,
        ActionContext<TAction> context)
    {
        var expected = hostContext
            ?? throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                "The host action entry request has no initiating host context.");
        if (context.ActionKey != request.Descriptor.Key)
        {
            throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                "The dispatcher action context does not match the host action descriptor.");
        }

        return new ActionContext<TAction>(
            expected.InvocationId,
            expected.ParentInvocationId,
            expected.TraceId,
            expected.IdempotencyKey,
            expected.Depth,
            expected.Attempt,
            expected.Deadline,
            context.ActionKey,
            context.OwnerModuleId,
            expected.Caller,
            context.Action,
            expected.Features,
            context.Snapshot);
    }

    private SidecarActionTerminalTransportResponse CreateNestedRelayResponse(
        SidecarActionTerminalTransportRequest request,
        SidecarNestedHostActionEntryRelay? relay,
        SidecarNestedHostActionEntryRelayOutcomeKind outcomeKind,
        SidecarSafeFailureIdentity? failure)
    {
        var authority = request.Authority with
        {
            NestedCarrierRelay = relay,
            NestedCarrierOutcomeKind = outcomeKind,
            NestedCarrierRequestFingerprint =
                SidecarCapabilityTransportValidation.ComputeNestedCarrierRequestFingerprint(
                    request.NestedCarrierRequest!),
            Proof = "pending",
        };
        authority = authority with
        {
            CanonicalBindingHash = SidecarCapabilityTransportValidation
                .ComputeTerminalAuthorityBindingHash(authority),
            Proof = OutOfProcessCapabilitySecurity.CreateTerminalProof(
                authority,
                _controlToken),
        };

        var issued = outcomeKind == SidecarNestedHostActionEntryRelayOutcomeKind.Issued;
        var result = issued
            ? CreateNullPayload(
                request.Descriptor.ResultTypeIdentity,
                request.Descriptor.ResultSchemaVersion)
            : null;
        return new SidecarActionTerminalTransportResponse(
            issued
                ? new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    request.Descriptor.Key,
                    request.Descriptor.Version,
                    request.Descriptor.ResultTypeIdentity,
                    result!.ContentHash)
                : null,
            new SidecarTerminalExecutionResult(
                result,
                issued ? null : failure ?? _session.Binding.SafeFailure,
                Completed: true),
            request.Receipt,
            _session.Binding.SafeFailure)
        {
            TerminalId = request.TerminalId,
            NestedCarrierRelay = relay,
            NestedCarrierAuthority = authority,
            NestedCarrierOutcome = new(outcomeKind, issued ? null : failure),
        };
    }

    private static SidecarSerializedPayload CreateNullPayload(
        string typeIdentity,
        int schemaVersion)
    {
        using var document = JsonDocument.Parse("null");
        var canonical = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        return new SidecarSerializedPayload(
            typeIdentity,
            schemaVersion,
            SidecarCapabilityTransportCodec.ComputeSha256(canonical),
            document.RootElement.Clone(),
            canonical.Length);
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

    private async Task<SidecarActionTerminalTransportResponse> SendTerminalAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var completion = new TaskCompletionSource<SidecarActionTerminalTransportResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_terminals.TryAdd(request.Call.CallId, completion))
        {
            throw new OutOfProcessCapabilityException(
                "sidecar_replay",
                "The terminal call identifier was reused.");
        }

        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionTerminalRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                ct);
            return await completion.Task.WaitAsync(ct);
        }
        finally
        {
            _terminals.TryRemove(request.Call.CallId, out _);
        }
    }

    private async Task<SidecarStorageCapabilityResponse> InvokeStorageAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken ct)
    {
        switch (request.Operation)
        {
            case SidecarStorageOperationKind.ListContracts:
                return CreateStorageResponse(
                    request,
                    _options.StorageGateway.ListContracts(),
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    alreadyCommitted: false);
            case SidecarStorageOperationKind.Invoke:
            {
                var invoke = Deserialize<OutOfProcessStorageInvokePayload>(request.RequestPayload);
                var value = await _options.StorageGateway.InvokeAsync(
                    request.ModuleId,
                    request.StorageName,
                    invoke.Operation,
                    invoke.Value,
                    ct);
                return CreateStorageResponse(
                    request,
                    value,
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    alreadyCommitted: false);
            }
            case SidecarStorageOperationKind.CommitMutationAndOutbox:
            {
                var value = await _options.StorageGateway.CommitMutationAndOutboxAsync(
                    request.ModuleId,
                    request.StorageName,
                    Deserialize<ModuleStorageMutationAndOutboxRequest>(request.RequestPayload),
                    ct);
                return CreateStorageResponse(
                    request,
                    value,
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    value.AlreadyCommitted);
            }
            case SidecarStorageOperationKind.Claim:
            {
                var value = await _options.StorageGateway.ClaimAsync<JsonElement>(
                    request.ModuleId,
                    request.StorageName,
                    Deserialize<ModuleStorageClaimRequest>(request.RequestPayload),
                    ct);
                return CreateStorageResponse(
                    request,
                    value,
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    alreadyCommitted: false);
            }
            case SidecarStorageOperationKind.RenewClaim:
            {
                var value = await _options.StorageGateway.RenewClaimAsync(
                    request.ModuleId,
                    request.StorageName,
                    Deserialize<ModuleStorageClaimRenewalRequest>(request.RequestPayload),
                    ct);
                return CreateStorageResponse(
                    request,
                    value,
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    alreadyCommitted: false);
            }
            case SidecarStorageOperationKind.RecoverClaim:
            {
                var value = await _options.StorageGateway.RecoverClaimAsync(
                    request.ModuleId,
                    request.StorageName,
                    Deserialize<ModuleStorageClaimRecoveryRequest>(request.RequestPayload),
                    ct);
                return CreateStorageResponse(
                    request,
                    value,
                    request.ResultPayloadType.TypeIdentity,
                    request.ResultPayloadType.SchemaVersion,
                    alreadyCommitted: false);
            }
            default:
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnsupportedCapability,
                    $"Storage operation '{request.Operation}' is not supported.");
        }
    }

    private SidecarStorageCapabilityResponse CreateStorageResponse<T>(
        SidecarStorageCapabilityRequest request,
        T value,
        string typeIdentity,
        int schemaVersion,
        bool alreadyCommitted)
    {
        var payload = CreatePayload(
            value,
            typeIdentity,
            schemaVersion);
        return new SidecarStorageCapabilityResponse(
            new SidecarStorageResultIdentity(
                Guid.NewGuid(),
                request.Call.CallId,
                payload.ContentHash,
                alreadyCommitted),
            payload,
            null!,
            _session.Binding.SafeFailure,
            Completed: true);
    }

    private async Task SendActionFailureAsync(
        SidecarActionCapabilityRequest request,
        string? code,
        string? message,
        CancellationToken ct)
    {
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionResponse,
            CreateActionFailure(request, code, message),
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private async Task SendStorageFailureAsync(
        SidecarStorageCapabilityRequest request,
        string? code,
        string? message,
        CancellationToken ct,
        ModuleStorageContractFailure? failure = null)
    {
        await SendStorageResponseAsync(
            request,
            new SidecarStorageCapabilityResponse(
                new SidecarStorageResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    string.Empty,
                    false),
                null!,
                failure ?? new ModuleStorageContractFailure(
                    code ?? SidecarCapabilityErrors.HostFailure,
                    message ?? "The storage request failed.",
                    request.StorageName,
                    null,
                    null),
                _session.Binding.SafeFailure,
                Completed: false),
            ct);
    }

    private async Task SendStorageResponseAsync(
        SidecarStorageCapabilityRequest request,
        SidecarStorageCapabilityResponse response,
        CancellationToken ct)
    {
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.StorageResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
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

    private SidecarActionCapabilityResponse CreateActionResponse(
        SidecarActionCapabilityRequest request,
        OutOfProcessActionDescriptorCatalog.Registration registration,
        OutOfProcessActionDispatchResult outcome)
    {
        SidecarSerializedPayload? resultPayload = null;
        if (outcome.Result is not null
            && (outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred))
        {
            resultPayload = CreatePayload(
                outcome.Result,
                registration.Identity.ResultTypeIdentity,
                registration.Identity.ResultSchemaVersion);
        }

        _session.TryGetTerminalReceipt(request.Call.CallId, out var terminalReceipt);
        SidecarTerminalContinuationResponse? continuation = null;
        if (request.Continuation is not null)
        {
            continuation = new SidecarTerminalContinuationResponse(
                request.Continuation.ContinuationRequestId,
                false,
                null,
                _session.Binding.SafeFailure);
        }

        var envelope = new SidecarActionOutcomeEnvelope(
            outcome.Kind,
            resultPayload!,
            outcome.Continuation!,
            outcome.Error!,
            outcome.Uncertainty!,
            terminalReceipt,
            _session.Binding.SafeFailure,
            outcome.TerminalCallCount);
        return new SidecarActionCapabilityResponse(
            resultPayload is null
                ? null
                : new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    registration.Identity.Key,
                    registration.Identity.Version,
                    registration.Identity.ResultTypeIdentity,
                    resultPayload.ContentHash),
            envelope,
            continuation,
            _session.Binding.SafeFailure,
            Completed: true);
    }

    private SidecarActionCapabilityResponse CreateActionFailure(
        SidecarActionCapabilityRequest request,
        string? code,
        string? message) =>
        new(
            null,
            new SidecarActionOutcomeEnvelope(
                ActionOutcomeKind.Failed,
                null!,
                null!,
                new ExecutionError(
                    code ?? SidecarCapabilityErrors.HostFailure,
                    message ?? "The host action request failed."),
                null!,
                null!,
                _session.Binding.SafeFailure,
                0),
            request.Continuation is null
                ? null
                : new SidecarTerminalContinuationResponse(
                    request.Continuation.ContinuationRequestId,
                    false,
                    null!,
                    _session.Binding.SafeFailure),
            _session.Binding.SafeFailure,
            Completed: true);

    private static T Deserialize<T>(SidecarSerializedPayload? payload)
    {
        if (payload is null)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                $"The payload '{typeof(T).FullName}' is missing.");
        return JsonSerializer.Deserialize<T>(
                payload.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                $"The payload '{typeof(T).FullName}' is empty.");
    }

    private static SidecarSerializedPayload CreatePayload<T>(
        T value,
        string typeIdentity,
        int schemaVersion)
    {
        var bytes = SidecarCapabilityTransportCodec.Serialize(value);
        using var document = JsonDocument.Parse(bytes);
        var canonicalBytes = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        var hash = SidecarCapabilityTransportCodec.ComputeSha256(canonicalBytes);
        return new SidecarSerializedPayload(
            typeIdentity,
            schemaVersion,
            hash,
            document.RootElement.Clone(),
            canonicalBytes.Length);
    }

    private static SidecarSerializedPayload EmptyPayload() =>
        new(
            "system.empty",
            1,
            SidecarCapabilityTransportCodec.ComputeSha256("null"u8),
            JsonDocument.Parse("null").RootElement.Clone(),
            4);

    private void CompleteTerminal(SidecarActionTerminalTransportResponse response)
    {
        var callId = response.ResultIdentity?.CallId
            ?? response.Receipt?.CallId
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal response has no result identity or receipt.");
        if (!_terminals.TryGetValue(callId, out var completion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The terminal response does not match an active terminal call.");
        }
        completion.TrySetResult(response);
    }

    private static OutOfProcessCapabilityException ReadError(byte[] payload)
    {
        var error = OutOfProcessCapabilityWire.Deserialize<SidecarSafeFailureIdentity>(payload);
        return new OutOfProcessCapabilityException(error.Code, error.Message);
    }

}

internal sealed record OutOfProcessStorageInvokePayload(
    string Operation,
    JsonElement Value);
