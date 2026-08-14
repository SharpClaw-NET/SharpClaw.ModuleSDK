using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessCapabilityHostSession : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly OutOfProcessCapabilityHostOptions _options;
    private readonly SidecarHostAuthorization _authorization;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly ConcurrentDictionary<Guid, ActiveCall> _calls = new();
    private readonly CancellationTokenSource _disconnect = new();
    private readonly BoundedExecutionQueue _capabilityQueue;
    private int _disposed;

    private sealed class ActiveCall(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public int Completed;
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
        var authenticate = new Func<SidecarCapabilityAuthenticationAuthority, bool>(
            authority => OutOfProcessCapabilitySecurity.Authenticate(authority, controlToken));
        _session = new SidecarCapabilitySession(binding, authenticate, _ => true, DateTimeOffset.UtcNow);
        SendGate = new SemaphoreSlim(1, 1);
        _capabilityQueue = new BoundedExecutionQueue(
            Math.Max(binding.ConcurrencyLimits.MaximumInFlightCalls, 1),
            Math.Max(binding.ConcurrencyLimits.MaximumInFlightCalls, 1));
    }

    public SemaphoreSlim SendGate { get; }

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
                    case OutOfProcessCapabilityFrameKind.StorageRequest:
                        await ScheduleStorageRequestAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarStorageCapabilityRequest>(frame.Payload),
                            ct);
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityCancellation:
                        CancelCall(OutOfProcessCapabilityWire.Deserialize<OutOfProcessCapabilityCancellation>(frame.Payload));
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
        catch (OutOfProcessCapabilityException)
        {
        }
        finally
        {
            _session.Disconnect();
            foreach (var terminal in _terminals.Values)
            {
                terminal.TrySetException(new OutOfProcessCapabilityException(
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
        _session.Disconnect();
        foreach (var call in _calls.Values)
            call.Cancellation.Cancel();
        foreach (var terminal in _terminals.Values)
        {
            terminal.TrySetException(new ObjectDisposedException(nameof(OutOfProcessCapabilityHostSession)));
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
        SendGate.Dispose();
        _disconnect.Dispose();
    }

    private async Task ScheduleActionRequestAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken channelCt)
    {
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
                SidecarCapabilitySessionValidator.ComputeBindingHash(_session.Binding),
                StringComparison.Ordinal)
            || cancellation.Cancellation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The capability cancellation identity is invalid.");
        }

        active.Cancellation.Cancel();
    }

    private ActiveCall RegisterCall(
        SidecarCapabilityCallIdentity call,
        CancellationToken channelCancellation)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(channelCancellation);
        var remaining = call.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            cancellation.Cancel();
        else
            cancellation.CancelAfter(remaining);
        var active = new ActiveCall(cancellation);
        if (!_calls.TryAdd(call.CallId, active))
        {
            cancellation.Dispose();
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The capability call identifier was reused.");
        }

        return active;
    }

    private bool CompleteCall(Guid callId, int terminalCallCount)
    {
        if (!_calls.TryGetValue(callId, out var active)
            || Interlocked.Exchange(ref active.Completed, 1) != 0)
            return false;
        return _session.CompleteCall(callId, terminalCallCount).Accepted;
    }

    private void FinishCall(Guid callId, ActiveCall? active)
    {
        if (active is null || !_calls.TryRemove(callId, out var removed))
            return;
        if (Interlocked.Exchange(ref removed.Completed, 1) == 0)
            _session.CompleteCall(callId, 0);
        removed.Cancellation.Dispose();
    }

    private bool IsHostActionAuthorized(SidecarActionCapabilityRequest request)
    {
        if (!string.Equals(
                OutOfProcessCapabilitySnapshot.ComputeHash(request.Snapshot),
                _options.ActionSnapshotHash,
                StringComparison.Ordinal))
            return false;

        var authorizationGrant = _authorization.ActionGrants.SingleOrDefault(grant =>
            grant.ActionKey == request.Descriptor.Key
            && grant.ActionVersion == request.Descriptor.Version);
        var snapshotGrant = _options.ActionSnapshot.ActionGrants.SingleOrDefault(grant =>
            grant.ActionKey == request.Descriptor.Key
            && grant.ActionVersion == request.Descriptor.Version);
        return authorizationGrant is not null
            && snapshotGrant is not null
            && authorizationGrant.Capabilities == snapshotGrant.Capabilities
            && authorizationGrant.SensitiveApproved == snapshotGrant.SensitiveApproved
            && authorizationGrant.AcceptUnknownSchemas == snapshotGrant.AcceptUnknownSchemas;
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
            var validation = SidecarCapabilityTransportValidation.ValidateActionRequest(
                request,
                _session.Binding,
                DateTimeOffset.UtcNow);
            if (!validation.Accepted)
            {
                await SendActionFailureAsync(request, validation.Code, validation.Message, channelCt);
                return;
            }

            if (!IsHostActionAuthorized(request))
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.Unauthorized,
                    "The action request does not match the host-owned action snapshot.",
                    channelCt);
                return;
            }

            var begin = _session.BeginCall(
                request.Call,
                SidecarCapabilityKind.Action,
                request.Action,
                request.Action.ByteLength,
                DateTimeOffset.UtcNow);
            if (!begin.Accepted)
            {
                await SendActionFailureAsync(request, begin.Code, begin.Message, channelCt);
                return;
            }

            active = RegisterCall(request.Call, channelCt);

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
                    responseValidation.Message,
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
            CompleteCall(request.Call.CallId, 0);
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
        catch (Exception)
        {
            CompleteCall(request.Call.CallId, 0);
            await SendActionFailureAsync(
                request,
                SidecarCapabilityErrors.HostFailure,
                "The host action dispatcher failed.",
                channelCt);
        }
        finally
        {
            FinishCall(request.Call.CallId, active);
        }
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
            var begin = _session.BeginCall(
                request.Call,
                SidecarCapabilityKind.Storage,
                requestFramePayload,
                requestFramePayload.ByteLength,
                DateTimeOffset.UtcNow);
            if (!begin.Accepted)
            {
                await SendStorageFailureAsync(request, begin.Code, begin.Message, channelCt);
                return;
            }

            active = RegisterCall(request.Call, channelCt);

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
            CompleteCall(request.Call.CallId, 0);
            await SendStorageFailureAsync(
                request,
                SidecarCapabilityErrors.HostFailure,
                "The host storage gateway failed.",
                channelCt);
        }
        finally
        {
            FinishCall(request.Call.CallId, active);
        }
    }

    internal async ValueTask<OutOfProcessActionDispatchResult> DispatchAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        SidecarActionDescriptorIdentity identity,
        SidecarActionCapabilityRequest request,
        CancellationToken ct)
    {
        var action = Deserialize<TAction>(request.Action);
        var outcome = await _options.ActionDispatcher.RunAsync(
            descriptor,
            action,
            (value, terminalCancellation) => InvokeTerminalAsync<TAction, TResult>(
                request,
                identity,
                value,
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
        TAction action,
        CancellationToken ct)
    {
        var actionPayload = CreatePayload(
            action,
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
            request.Deadline);
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

    private SidecarActionCapabilityResponse CreateActionResponse(
        SidecarActionCapabilityRequest request,
        OutOfProcessActionDescriptorCatalog.Registration registration,
        OutOfProcessActionDispatchResult outcome)
    {
        SidecarSerializedPayload? resultPayload = null;
        if (outcome.Result is not null
            && outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred)
        {
            resultPayload = CreatePayload(
                outcome.Result,
                registration.Identity.ResultTypeIdentity,
                registration.Identity.ResultSchemaVersion);
        }

        var continuationRequestId = request.Continuation?.ContinuationRequestId
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The action request has no continuation identity.");
        _session.TryGetTerminalReceipt(request.Call.CallId, out var terminalReceipt);

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
            new SidecarActionResultIdentity(
                Guid.NewGuid(),
                request.Call.CallId,
                registration.Identity.Key,
                registration.Identity.Version,
                registration.Identity.ResultTypeIdentity,
                resultPayload?.ContentHash ?? string.Empty),
            envelope,
            new SidecarTerminalContinuationResponse(
                continuationRequestId,
                false,
                null!,
                _session.Binding.SafeFailure),
            _session.Binding.SafeFailure,
            Completed: true);
    }

    private SidecarActionCapabilityResponse CreateActionFailure(
        SidecarActionCapabilityRequest request,
        string? code,
        string? message) =>
        new(
            new SidecarActionResultIdentity(
                Guid.NewGuid(),
                request.Call.CallId,
                request.Descriptor.Key,
                request.Descriptor.Version,
                request.Descriptor.ResultTypeIdentity,
                string.Empty),
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
            new SidecarTerminalContinuationResponse(
                request.Continuation?.ContinuationRequestId ?? Guid.Empty,
                false,
                null!,
                _session.Binding.SafeFailure),
            _session.Binding.SafeFailure,
            Completed: false);

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
        var resultIdentity = response.ResultIdentity
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal response has no result identity.");
        if (!_terminals.TryGetValue(resultIdentity.CallId, out var completion))
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
