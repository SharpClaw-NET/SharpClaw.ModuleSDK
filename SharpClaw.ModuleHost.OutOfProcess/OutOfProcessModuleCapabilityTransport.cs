using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

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
    private SidecarHostAuthorization? _authorization;

    public void Initialize(
        string moduleId,
        string graphId,
        SidecarPayloadLimits payloadLimits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentNullException.ThrowIfNull(payloadLimits);
        lock (_sync)
        {
            if (_moduleId is not null)
                throw new InvalidOperationException("The module capability transport is already initialized.");
            _moduleId = moduleId;
            _graphId = graphId;
            _payloadLimits = payloadLimits;
        }
    }

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
            RegisterAuthenticationNonce);
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

internal sealed class OutOfProcessModuleCapabilityConnection : IAsyncDisposable
{
    private sealed record PendingAction(
        SidecarActionCapabilityRequest Request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? Terminal,
        TaskCompletionSource<SidecarActionCapabilityResponse> Completion);

    private readonly WebSocket _socket;
    private SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly SidecarHostAuthorization _authorization;
    private readonly Func<string, bool> _registerAuthenticationNonce;
    private readonly ConcurrentDictionary<Guid, PendingAction> _actions = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarStorageCapabilityResponse>> _storage = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retiredCalls = new();
    private readonly CancellationTokenSource _disconnect = new();
    private readonly object _rotationSync = new();
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
        Func<string, bool> registerAuthenticationNonce)
    {
        _socket = socket;
        _session = session;
        _controlToken = controlToken;
        _limits = limits;
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _registerAuthenticationNonce = registerAuthenticationNonce
            ?? throw new ArgumentNullException(nameof(registerAuthenticationNonce));
        SendGate = new SemaphoreSlim(1, 1);
    }

    public SemaphoreSlim SendGate { get; }

    public SidecarCapabilitySessionBinding Binding =>
        Volatile.Read(ref _session).Binding;

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

    private void WaitForRebindIfReady(CancellationToken ct)
    {
        Task? rebind;
        lock (_rotationSync)
            rebind = _rebindReady?.Task;
        rebind?.WaitAsync(ct).GetAwaiter().GetResult();
    }

    private bool CompleteCall(Guid callId, int terminalCallCount)
    {
        var session = Volatile.Read(ref _session);
        var result = session.CompleteCall(callId, terminalCallCount);
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

        return result.Accepted;
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
                request,
                response,
                Binding,
                _session);
            ThrowIfRejected(validation);
            if (!CompleteCall(request.Call.CallId, response.Outcome.TerminalCallCount))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The sidecar action call could not be completed.");
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
            var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                request,
                response,
                Binding);
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
        ThrowIfRejected(_session.BeginCall(
            request.Call,
            SidecarCapabilityKind.Storage,
            payload,
            payload.ByteLength,
            DateTimeOffset.UtcNow));
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
                        await HandleTerminalRequestAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportRequest>(frame.Payload),
                            linked.Token);
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
        if (!_actions.TryGetValue(request.Call.CallId, out var pending)
            || pending.Terminal is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The terminal request has no initiating action call.");
        }

        var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalRequest(
            pending.Request,
            request,
            Binding,
            DateTimeOffset.UtcNow,
            (authority, proof) => ValidateTerminalAuthority(authority, proof));
        if (!validation.Accepted)
        {
            Console.Error.WriteLine($"beta11-terminal-validation: {validation.Code}: {validation.Message}");
            var diagnosticPath = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_DIAGNOSTIC_LOG");
            if (!string.IsNullOrWhiteSpace(diagnosticPath))
                File.AppendAllText(
                    diagnosticPath,
                    $"{validation.Code}: {validation.Message}{Environment.NewLine}"
                    + JsonSerializer.Serialize(
                        new { Initiating = pending.Request, Terminal = request },
                        SidecarCapabilityTransportCodec.CreateJsonOptions())
                    + Environment.NewLine
                    + $"contextWellFormed={request.Context?.IsWellFormed}; "
                    + $"deadlineMatchesCall={request.Deadline == request.Call.Deadline}; "
                    + $"snapshotHash={SidecarCapabilityTransportCodec.ComputeSha256(SidecarCapabilityTransportCodec.Serialize(request.Context?.Snapshot))}; "
                    + $"authoritySnapshotHash={request.Authority.SnapshotContentHash}; "
                    + $"canonicalHash={SidecarCapabilityTransportValidation.ComputeTerminalAuthorityBindingHash(request.Authority)}; "
                    + $"authorityCanonicalHash={request.Authority.CanonicalBindingHash}; "
                    + $"proofValid={ValidateTerminalAuthority(request.Authority, request.Authority.Proof)}; "
                    + $"expectedCallerHash={SidecarCapabilityTransportCodec.ComputeSha256(SidecarCapabilityTransportCodec.Serialize(pending.Request.HostContext?.Caller))}; "
                    + $"actualCallerHash={SidecarCapabilityTransportCodec.ComputeSha256(SidecarCapabilityTransportCodec.Serialize(request.Context?.Caller))}; "
                    + $"expectedFeaturesHash={SidecarCapabilityTransportCodec.ComputeSha256(SidecarCapabilityTransportCodec.Serialize(pending.Request.HostContext?.Features))}; "
                    + $"actualFeaturesHash={SidecarCapabilityTransportCodec.ComputeSha256(SidecarCapabilityTransportCodec.Serialize(request.Context?.Features))}; "
                    + $"callerMatches={OutOfProcessHostActionEntryContextRegistry.MatchesCaller(pending.Request.HostContext?.Caller, request.Context?.Caller)}; "
                    + $"callEquals={request.Call == pending.Request.Call}; "
                    + $"descriptorEquals={request.Descriptor == pending.Request.Descriptor}; "
                    + $"cancellationEquals={request.Cancellation == pending.Request.Cancellation}; "
                    + $"deadlineEquals={request.Deadline == pending.Request.Deadline}; "
                    + $"terminalIdEquals={request.TerminalId == pending.Request.Terminal?.TerminalId}; "
                    + $"contextCallEquals={request.Context?.Call == request.Call}; "
                    + $"contextInvocationEquals={request.Context?.Invocation == request.Invocation}; "
                    + $"contextDescriptorEquals={request.Context?.Descriptor == request.Descriptor}; "
                    + $"contextPayloadEquals={request.Context?.EffectiveAction == request.EffectiveAction}; "
                    + $"contextCancellationEquals={request.Context?.Cancellation == request.Cancellation}; "
                    + $"contextReceiptEquals={request.Context?.Receipt == request.Receipt}; "
                    + $"contextDeadlineEquals={request.Context?.Deadline == request.Deadline}; "
                    + $"contextAttemptEquals={request.Context?.Attempt == request.Receipt?.Attempt}; "
                    + $"receiptValid={request.Receipt is not null && request.Receipt.ReceiptId is not null && request.Receipt.CallId == request.Call.CallId && request.Receipt.ActionKey == request.Descriptor.Key && request.Receipt.ActionVersion == request.Descriptor.Version && request.Receipt.Attempt >= 1 && request.Receipt.IdempotencyScope is not null && request.Receipt.ContentHash is not null}; "
                    + $"issuedAtBeforeNow={request.Authority.IssuedAt <= DateTimeOffset.UtcNow}; "
                    + $"authorityExpiryAfterDeadline={request.Authority.ExpiresAt >= request.Deadline}; "
                    + $"authorityExpiryBeforeBinding={request.Authority.ExpiresAt <= Binding.ExpiresAt}; "
                    + $"requestDeadlineAfterNow={request.Deadline > DateTimeOffset.UtcNow}; "
                    + $"cancellationExpiryAfterDeadline={request.Cancellation.ExpiresAt >= request.Deadline}{Environment.NewLine}");
        }
        ThrowIfRejected(validation);
        ThrowIfRejected(_session.RecordTerminal(
            request.Call.CallId,
            request.Authority.AuthorityId,
            request.Receipt));
        var response = await pending.Terminal(request, ct);
        ThrowIfRejected(SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
            request,
            response,
            Binding));
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

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
        string proof) =>
        string.Equals(
            OutOfProcessCapabilitySecurity.CreateTerminalProof(authority, _controlToken),
            proof,
            StringComparison.Ordinal);

    private void CompleteAction(SidecarActionCapabilityResponse response)
    {
        var resultIdentity = response.ResultIdentity
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The action response has no result identity.");
        if (_actions.TryGetValue(resultIdentity.CallId, out var pending))
            pending.Completion.TrySetResult(response);
        else if (!IsRetired(resultIdentity.CallId))
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
        var resultIdentity = response.ResultIdentity
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal response has no result identity.");
        if (_terminals.TryGetValue(resultIdentity.CallId, out var completion))
            completion.TrySetResult(response);
        else
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The terminal response does not match an active terminal call.");
    }

    private void ValidateActionRequest(SidecarActionCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfRejected(SidecarCapabilityTransportValidation.ValidateActionRequest(
            request,
            Binding,
            DateTimeOffset.UtcNow));
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
