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
        DateTimeOffset deadline) =>
        GetRequiredConnection().CreateCall(capability, deadline);

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
            limits);
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
    private readonly SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly ConcurrentDictionary<Guid, PendingAction> _actions = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarStorageCapabilityResponse>> _storage = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly CancellationTokenSource _disconnect = new();
    private long _sequence;
    private int _disposed;

    public OutOfProcessModuleCapabilityConnection(
        WebSocket socket,
        SidecarCapabilitySession session,
        string controlToken,
        SidecarPayloadLimits limits)
    {
        _socket = socket;
        _session = session;
        _controlToken = controlToken;
        _limits = limits;
        SendGate = new SemaphoreSlim(1, 1);
    }

    public SemaphoreSlim SendGate { get; }

    public SidecarCapabilitySessionBinding Binding => _session.Binding;

    public SidecarCapabilityCallIdentity CreateCall(
        SidecarCapabilityKind capability,
        DateTimeOffset deadline)
    {
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

    public async ValueTask<SidecarActionCapabilityResponse> InvokeActionAsync(
        SidecarActionCapabilityRequest request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? terminal,
        CancellationToken ct)
    {
        ValidateActionRequest(request);
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
            throw new OutOfProcessCapabilityException("sidecar_replay", "The action call identifier was reused.");

        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                ct);
            var response = await completion.Task.WaitAsync(ct);
            var validation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                Binding,
                _session);
            ThrowIfRejected(validation);
            ThrowIfRejected(_session.CompleteCall(request.Call.CallId, response.GetPayloadBytes()));
            return response;
        }
        finally
        {
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
        var payload = request.RequestPayload ?? EmptyPayload();
        ThrowIfRejected(_session.BeginCall(
            request.Call,
            SidecarCapabilityKind.Storage,
            payload,
            payload.ByteLength,
            DateTimeOffset.UtcNow));
        var completion = NewCompletion<SidecarStorageCapabilityResponse>();
        if (!_storage.TryAdd(request.Call.CallId, completion))
            throw new OutOfProcessCapabilityException("sidecar_replay", "The storage call identifier was reused.");
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.StorageRequest,
                request,
                _limits.ProtocolMessageBytes,
                SendGate,
                ct);
            var response = await completion.Task.WaitAsync(ct);
        var responseValidation = SidecarCapabilityTransportValidation.ValidateStorageResponse(
            request,
            response,
            Binding);
        if (!responseValidation.Accepted)
        {
            var resultPayload = response.ResultPayload;
            var canonicalBytes = resultPayload is null
                ? Array.Empty<byte>()
                : SidecarCapabilityTransportCodec.Serialize(resultPayload.Value);
            throw new OutOfProcessCapabilityException(
                responseValidation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                $"{responseValidation.Message} resultHash={response.ResultIdentity?.ContentHash}; "
                + $"payloadHash={resultPayload?.ContentHash}; computedHash="
                + $"{SidecarCapabilityTransportCodec.ComputeSha256(canonicalBytes)}; "
                + $"resultLength={resultPayload?.ByteLength}; computedLength={canonicalBytes.Length}.");
        }
            ThrowIfRejected(_session.CompleteCall(request.Call.CallId, response.GetPayloadBytes()));
            return response;
        }
        finally
        {
            _storage.TryRemove(request.Call.CallId, out _);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disconnect.Token);
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
        finally
        {
            _session.Disconnect();
            FailPending(new OutOfProcessCapabilityException(
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
            authority => ValidateTerminalAuthority(authority));
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

    private bool ValidateTerminalAuthority(SidecarHostTerminalAuthority authority) =>
        string.Equals(
            OutOfProcessCapabilitySecurity.CreateTerminalProof(authority, _controlToken),
            authority.Proof,
            StringComparison.Ordinal);

    private void CompleteAction(SidecarActionCapabilityResponse response)
    {
        var resultIdentity = response.ResultIdentity
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The action response has no result identity.");
        if (_actions.TryGetValue(resultIdentity.CallId, out var pending))
            pending.Completion.TrySetResult(response);
        else
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
        else
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
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                $"{validation.Message} callModule={request.Call.ModuleId}; "
                + $"requestModule={request.ModuleId}; callGraph={request.Call.GraphId}; "
                + $"bindingGraph={Binding.GraphId}; callCapability={request.Call.Capability}; "
                + $"requestCapability=storage");
        }
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

internal static class SidecarCapabilityTransportResponseExtensions
{
    public static int GetPayloadBytes(this SidecarActionCapabilityResponse response) =>
        response.Outcome?.Result?.ByteLength ?? 0;

    public static int GetPayloadBytes(this SidecarStorageCapabilityResponse response) =>
        response.ResultPayload?.ByteLength ?? 0;
}
