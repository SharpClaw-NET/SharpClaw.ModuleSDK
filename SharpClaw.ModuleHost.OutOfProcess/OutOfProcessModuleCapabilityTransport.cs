using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessModuleCapabilityTransport : ISidecarCapabilityTransport, IAsyncDisposable
{
    internal readonly record struct ModuleActionEntryTerminalBinding(
        Guid TerminalId,
        bool IsAuthorized);

    private readonly object _sync = new();
    private readonly AsyncLocal<ActiveCarrierState?> _activeCarrier = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _authenticationNonces = new(StringComparer.Ordinal);
    private TaskCompletionSource<OutOfProcessModuleCapabilityConnection> _ready = CreateReadySource();
    private TaskCompletionSource _connectionReleased = CreateReleasedSource(completed: true);
    private readonly TaskCompletionSource _connectionWaitObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OutOfProcessModuleCapabilityConnection? _connection;
    private bool _disposed;
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
    internal Task ConnectionWaitObserved => _connectionWaitObserved.Task;

    internal Guid? ActiveCarrierId => _activeCarrier.Value?.CapabilityId;

    internal SidecarCapabilityCallIdentity? ActiveCarrierCall =>
        _activeCarrier.Value?.ParentCall;

    internal IDisposable PushActiveCarrier(Guid capabilityId)
    {
        if (capabilityId == Guid.Empty)
            throw new ArgumentException("The active carrier identifier is required.", nameof(capabilityId));

        return PushActiveCarrierCore(capabilityId, parentCall: null);
    }

    internal IDisposable PushActiveCarrier(
        Guid capabilityId,
        SidecarCapabilityCallIdentity parentCall)
    {
        if (capabilityId == Guid.Empty)
            throw new ArgumentException("The active carrier identifier is required.", nameof(capabilityId));
        ArgumentNullException.ThrowIfNull(parentCall);
        return PushActiveCarrierCore(capabilityId, parentCall);
    }

    private IDisposable PushActiveCarrierCore(
        Guid capabilityId,
        SidecarCapabilityCallIdentity? parentCall)
    {
        var previous = _activeCarrier.Value;
        _activeCarrier.Value = new ActiveCarrierState(capabilityId, parentCall);
        return new ActiveCarrierScope(_activeCarrier, previous);
    }

    private sealed record ActiveCarrierState(
        Guid CapabilityId,
        SidecarCapabilityCallIdentity? ParentCall);

    private sealed class ActiveCarrierScope(
        AsyncLocal<ActiveCarrierState?> carrier,
        ActiveCarrierState? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                carrier.Value = previous;
        }
    }

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

    internal ModuleActionEntryTerminalBinding ResolveActionEntryTerminal(
        SidecarActionDescriptorIdentity descriptor,
        Guid terminalId,
        Type terminalType)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(terminalType);
        if (terminalId == Guid.Empty)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The action terminal has no identity.");
        var graph = Volatile.Read(ref _graph)
            ?? throw new InvalidOperationException(
                "The module capability transport has no compiled graph.");
        var moduleId = Volatile.Read(ref _moduleId)
            ?? throw new InvalidOperationException(
                "The module capability transport is not initialized.");
        ModuleActionEntryRegistration? match = null;
        foreach (var entry in graph.ActionEntries)
        {
            if (!string.Equals(entry.OwnerModuleId, moduleId, StringComparison.Ordinal)
                || !OutOfProcessActionDescriptorIdentity.Matches(entry.Descriptor, descriptor))
            {
                continue;
            }

            if (match is not null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnknownAction,
                    "The module graph contains ambiguous action-entry registrations.");
            }

            match = entry;
        }

        if (match is null)
            return new(terminalId, IsAuthorized: true);

        if (match.TerminalId != terminalId
            || match.TerminalType != terminalType)
        {
            return new(Guid.NewGuid(), IsAuthorized: false);
        }

        return new(match.TerminalId, IsAuthorized: true);
    }

    internal SidecarCapabilityValidationResult ImportNestedHostActionEntryRelay(
        SidecarNestedHostActionEntryRelay relay,
        SidecarNestedHostActionEntryRequest request,
        SidecarHostTerminalAuthority authority,
        SidecarCapabilityCallIdentity parentCall,
        DateTimeOffset now,
        out SidecarNestedHostActionEntryCarrier? importedCarrier) =>
        GetRequiredConnection().ImportNestedHostActionEntryRelay(
            relay,
            request,
            authority,
            parentCall,
            now,
            out importedCarrier);

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
        CancellationToken ct = default,
        Guid? activeCarrierId = null) =>
        GetRequiredConnection().CreateCall(capability, deadline, ct, activeCarrierId);

    internal void ReleaseCallReservation(SidecarCapabilityCallIdentity call) =>
        Volatile.Read(ref _connection)?.ReleaseCallReservation(call);

    internal ValueTask<SidecarActionCapabilityResponse> InvokeActionAsync(
        SidecarActionCapabilityRequest request,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? terminal,
        CancellationToken ct) =>
        GetRequiredConnection().InvokeActionAsync(request, terminal, ct);

    internal ValueTask<IDisposable> EnterRootActionExchangeAsync(CancellationToken ct) =>
        GetRequiredConnection().EnterRootActionExchangeAsync(ct);

    internal SidecarCapabilitySessionBinding Binding => GetRequiredConnection().Binding;

    internal async Task AcceptAsync(
        WebSocket socket,
        string controlToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        ThrowIfDisposed();
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

        ThrowIfDisposed();
        OutOfProcessModuleCapabilityConnection? connection = null;
        while (connection is null)
        {
            Task waitForRelease;
            lock (_sync)
            {
                ThrowIfDisposedNoLock();
                if (_connection is not null)
                {
                    _connectionWaitObserved.TrySetResult();
                    waitForRelease = _connectionReleased.Task;
                }
                else
                {
                    var authenticateHostTerminalAuthority =
                        new Func<SidecarHostTerminalAuthority, string, bool>(
                            (authority, canonicalBindingHash) =>
                                string.Equals(
                                    authority.CanonicalBindingHash,
                                    canonicalBindingHash,
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(
                                    OutOfProcessCapabilitySecurity.CreateTerminalProof(
                                        authority,
                                        controlToken),
                                    authority.Proof,
                                    StringComparison.Ordinal));
                    var authenticateStorageContinuationAuthority =
                        new Func<SidecarHostEntryStorageContinuationAuthority, string, bool>(
                            (authority, canonicalBindingHash) =>
                                string.Equals(
                                    authority.CanonicalBindingHash,
                                    canonicalBindingHash,
                                    StringComparison.OrdinalIgnoreCase)
                                && OutOfProcessCapabilitySecurity.ValidateStorageContinuationProof(
                                    authority,
                                    controlToken));
                    var session = new SidecarCapabilitySession(
                        binding,
                        authenticate,
                        _ => true,
                        DateTimeOffset.UtcNow,
                        authenticateHostTerminalAuthority,
                        authenticateStorageContinuationAuthority);
                    connection = new OutOfProcessModuleCapabilityConnection(
                        socket,
                        session,
                        controlToken,
                        limits,
                        authorization,
                        RegisterAuthenticationNonce,
                        this,
                        graph.ActionEntries,
                        services);
                    _connectionReleased = CreateReleasedSource(completed: false);
                    _connection = connection;
                    _ready.TrySetResult(connection);
                    break;
                }
            }

            await waitForRelease.WaitAsync(ct);
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
                    _connectionReleased.TrySetResult();
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
            if (_disposed)
                return;
            _disposed = true;
            connection = _connection;
            _connection = null;
            _connectionReleased.TrySetResult();
            _ready.TrySetException(new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityTransport)));
        }

        if (connection is not null)
            await connection.DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
            ThrowIfDisposedNoLock();
    }

    private void ThrowIfDisposedNoLock()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(OutOfProcessModuleCapabilityTransport));
        }
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

    private static TaskCompletionSource CreateReleasedSource(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
            source.TrySetResult();
        return source;
    }

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

        public bool SessionCallStarted { get; set; }
    }

    private sealed class IncomingAction(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public SidecarActionCapabilityRequest? Request { get; set; }
    }

    private sealed class IncomingTerminal(
        SidecarActionTerminalTransportRequest request,
        CancellationTokenSource cancellation) : IDisposable
    {
        public SidecarActionTerminalTransportRequest Request { get; } = request;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public object Sync { get; } = new();

        private bool _relayImportStarted;

        private bool _peerCancellationConsumed;

        public bool RelayImportStarted
        {
            get
            {
                lock (Sync)
                    return _relayImportStarted;
            }
        }

        public bool PeerCancellationConsumed
        {
            get
            {
                lock (Sync)
                    return _peerCancellationConsumed;
            }
        }

        public bool TryBeginRelayImport()
        {
            lock (Sync)
            {
                if (_relayImportStarted || _peerCancellationConsumed)
                    return false;
                _relayImportStarted = true;
                return true;
            }
        }

        public bool TryConsumePeerCancellation(Func<bool> consume)
        {
            lock (Sync)
            {
                if (_relayImportStarted || _peerCancellationConsumed)
                    return false;
                if (!consume())
                    return false;
                _peerCancellationConsumed = true;
                return true;
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }

    private readonly WebSocket _socket;
    private SidecarCapabilitySession _session;
    private readonly string _controlToken;
    private readonly SidecarPayloadLimits _limits;
    private readonly SidecarHostAuthorization _authorization;
    private readonly ActionPipelineSnapshot _hostActionSnapshot;
    private readonly Func<string, bool> _registerAuthenticationNonce;
    private readonly OutOfProcessModuleCapabilityTransport _transport;
    private readonly IReadOnlyList<ModuleActionEntryRegistration> _actionEntries;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<Guid, PendingAction> _actions = new();
    private readonly ConcurrentDictionary<Guid, IncomingAction> _incomingActions = new();
    private readonly ConcurrentDictionary<Guid, IncomingTerminal> _incomingTerminals = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarStorageCapabilityResponse>> _storage = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SidecarActionTerminalTransportResponse>> _terminals = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retiredCalls = new();
    private readonly CancellationTokenSource _disconnect = new();
    private readonly BoundedExecutionQueue _actionEntryQueue;
    private readonly BoundedExecutionQueue _terminalQueue;
    private readonly SemaphoreSlim _rootActionExchangeGate = new(1, 1);
    private readonly SemaphoreSlim _rootRelayImportGate = new(1, 1);
    private readonly SemaphoreSlim _callAdmissionGate = new(1, 1);
    private readonly object _rotationSync = new();
    private readonly object _outgoingSequenceSync = new();
    private readonly SortedSet<long> _createdOutgoingSequences = new();
    private readonly Dictionary<Guid, long> _outgoingCallReservations = new();
    private TaskCompletionSource _outgoingSequenceChanged = CreateSignal();
    private Exception? _runFailure;
    private TaskCompletionSource? _rebindReady;
    private TaskCompletionSource? _rebindInProgress;
    private bool _rebindAdmissionClosed;
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
        _hostActionSnapshot = new ActionPipelineSnapshot(
            session.Binding.GraphId,
            authorization.ActionGrants,
            authorization.EventGrants);
        _registerAuthenticationNonce = registerAuthenticationNonce
            ?? throw new ArgumentNullException(nameof(registerAuthenticationNonce));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _actionEntries = actionEntries ?? throw new ArgumentNullException(nameof(actionEntries));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _terminalQueue = new BoundedExecutionQueue(
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1),
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1));
        _actionEntryQueue = new BoundedExecutionQueue(
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1),
            Math.Max(session.Binding.ConcurrencyLimits.MaximumInFlightCalls, 1));
        SendGate = new SemaphoreSlim(1, 1);
    }

    public SemaphoreSlim SendGate { get; }

    internal async ValueTask<IDisposable> EnterRootActionExchangeAsync(CancellationToken ct)
    {
        await _rootActionExchangeGate.WaitAsync(ct);
        return new SemaphoreLease(_rootActionExchangeGate);
    }

    private sealed class SemaphoreLease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                gate.Release();
        }
    }

    public SidecarCapabilitySessionBinding Binding =>
        Volatile.Read(ref _session).Binding;

    internal SidecarCapabilityValidationResult ImportNestedHostActionEntryRelay(
        SidecarNestedHostActionEntryRelay relay,
        SidecarNestedHostActionEntryRequest request,
        SidecarHostTerminalAuthority authority,
        SidecarCapabilityCallIdentity parentCall,
        DateTimeOffset now,
        out SidecarNestedHostActionEntryCarrier? importedCarrier) =>
        _session.ImportNestedHostActionEntryRelay(
            relay,
            request,
            authority,
            parentCall,
            now,
            out importedCarrier);

    internal Exception? RunFailure => Volatile.Read(ref _runFailure);

    public SidecarCapabilityCallIdentity CreateCall(
        SidecarCapabilityKind capability,
        DateTimeOffset deadline,
        CancellationToken ct = default,
        Guid? activeCarrierId = null)
    {
        var carrierId = activeCarrierId ?? _transport.ActiveCarrierId;
        while (true)
        {
            Task? waitForRebind = null;
            SidecarCapabilityCallIdentity? createdCall = null;
            _callAdmissionGate.Wait(ct);
            try
            {
                lock (_rotationSync)
                {
                    var activeCarrier = carrierId.HasValue
                        && _transport.ActiveCarrierCall is not null;
                    if (_rebindAdmissionClosed
                        || (!activeCarrier
                            && (_rebindInProgress is not null
                                || _rebindReady is not null)))
                    {
                        waitForRebind = _rebindInProgress?.Task
                            ?? _rebindReady?.Task;
                    }

                    if (waitForRebind is null)
                    {
                        var sequence = Interlocked.Increment(ref _sequence);
                        var binding = Binding;
                        var call = new SidecarCapabilityCallIdentity(
                            binding.SessionId,
                            binding.RequestId,
                            binding.CancellationId,
                            Guid.NewGuid(),
                            $"{binding.SessionId:N}:{sequence}:{Guid.NewGuid():N}",
                            binding.ModuleId,
                            binding.GraphId,
                            capability,
                            sequence,
                            deadline);
                        lock (_outgoingSequenceSync)
                        {
                            _createdOutgoingSequences.Add(sequence);
                            _outgoingCallReservations.Add(call.CallId, sequence);
                        }

                        createdCall = call;
                    }
                }
            }
            finally
            {
                _callAdmissionGate.Release();
            }

            if (createdCall is not null)
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                OutOfProcessProtocolTestFixture.RecordCallCreated(createdCall);
#endif
                return createdCall;
            }

            if (waitForRebind is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The capability call could not be admitted during binding rotation.");
            }

            waitForRebind.WaitAsync(ct).GetAwaiter().GetResult();
        }
    }

    private void EnsureOutgoingCallSequence(
        SidecarCapabilityCallIdentity call,
        CancellationToken ct,
        bool allowActiveCarrier)
    {
        while (true)
        {
            Task? waitForRebind = null;
            var registered = false;
            _callAdmissionGate.Wait(ct);
            try
            {
                lock (_rotationSync)
                {
                    lock (_outgoingSequenceSync)
                    {
                        if (_outgoingCallReservations.ContainsKey(call.CallId))
                        {
                            registered = true;
                        }
                        else
                        {
                            var activeCarrier = allowActiveCarrier
                                && _transport.ActiveCarrierCall is not null;
                            if (!_rebindAdmissionClosed
                                && (activeCarrier
                                    || (_rebindInProgress is null
                                        && _rebindReady is null)))
                            {
                                _createdOutgoingSequences.Add(call.Sequence);
                                _outgoingCallReservations.Add(
                                    call.CallId,
                                    call.Sequence);
                                registered = true;
                            }
                            else
                            {
                                waitForRebind = _rebindInProgress?.Task
                                    ?? _rebindReady?.Task;
                            }
                        }
                    }
                }
            }
            finally
            {
                _callAdmissionGate.Release();
            }

            if (registered)
                return;

            if (waitForRebind is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The capability call could not be registered during binding rotation.");
            }

            waitForRebind.WaitAsync(ct).GetAwaiter().GetResult();
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask WaitForOutgoingCallTurnAsync(
        SidecarCapabilityCallIdentity call,
        CancellationToken ct,
        bool allowActiveCarrier)
    {
        EnsureOutgoingCallSequence(call, ct, allowActiveCarrier);
        while (true)
        {
            Task changed;
            lock (_outgoingSequenceSync)
            {
                if (_createdOutgoingSequences.Min == call.Sequence)
                    return;

                changed = _outgoingSequenceChanged.Task;
            }

            await changed.WaitAsync(ct);
        }
    }

    private void CompleteOutgoingCallSequence(SidecarCapabilityCallIdentity call)
    {
        lock (_outgoingSequenceSync)
        {
            if (!_outgoingCallReservations.Remove(call.CallId, out var sequence))
            {
                if (!_createdOutgoingSequences.Remove(call.Sequence))
                    return;
            }
            else
            {
                _createdOutgoingSequences.Remove(sequence);
            }

            _outgoingSequenceChanged.TrySetResult();
            _outgoingSequenceChanged = CreateSignal();
        }
    }

    internal void ReleaseCallReservation(SidecarCapabilityCallIdentity call) =>
        CompleteOutgoingCallSequence(call);

    private bool HasOutgoingCallReservations()
    {
        lock (_outgoingSequenceSync)
            return _createdOutgoingSequences.Count != 0;
    }

    private void ReleaseAllOutgoingCallReservations()
    {
        lock (_outgoingSequenceSync)
        {
            if (_createdOutgoingSequences.Count == 0
                && _outgoingCallReservations.Count == 0)
                return;

            _createdOutgoingSequences.Clear();
            _outgoingCallReservations.Clear();
            _outgoingSequenceChanged.TrySetResult();
            _outgoingSequenceChanged = CreateSignal();
        }
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
                >= Math.Max(session.Binding.ConcurrencyLimits.MaximumCallsPerRequest - 2, 1))
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
        var retainPending = false;
        var outgoingSequenceCompleted = false;
        try
        {
            ValidateActionRequest(request);
            using var deadline = CreateCallCancellation(request.Deadline, ct);
            var callCancellation = deadline.Token;
            var deferredRootHostEntry = request.Invocation == SidecarActionInvocationKind.HostEntry
                && request.NestedCarrier is null
                && request.HostContext is not null;

            await WaitForOutgoingCallTurnAsync(
                request.Call,
                callCancellation,
                request.NestedCarrier is not null);
            var begin = request.NestedCarrier is { } nestedCarrier
                ? _session.BeginNestedHostActionEntryCall(
                    nestedCarrier,
                    request.Call,
                    request.Action,
                    request.Action.ByteLength,
                    DateTimeOffset.UtcNow,
                    out _)
                : deferredRootHostEntry
                    ? SidecarCapabilityValidationResult.Accept()
                    : _session.BeginCall(
                        request.Call,
                        SidecarCapabilityKind.Action,
                        request.Action,
                        request.Action.ByteLength,
                        DateTimeOffset.UtcNow);
            ThrowIfRejected(begin);
            ObserveSequence(request.Call.Sequence);
            var completion = NewCompletion<SidecarActionCapabilityResponse>();
            var pending = new PendingAction(request, terminal, completion)
            {
                SessionCallStarted = !deferredRootHostEntry,
            };
            if (!_actions.TryAdd(request.Call.CallId, pending))
            {
                CompleteCall(request.Call.CallId, 0);
                throw new OutOfProcessCapabilityException(
                    "sidecar_replay",
                    "The action call identifier was reused.");
            }

            try
            {
                await OutOfProcessCapabilityWire.SendAsync(
                    _socket,
                    OutOfProcessCapabilityFrameKind.ActionRequest,
                    request,
                    _limits.ProtocolMessageBytes,
                    SendGate,
                    callCancellation);
                CompleteOutgoingCallSequence(request.Call);
                outgoingSequenceCompleted = true;
                var response = await completion.Task.WaitAsync(callCancellation);
                var validation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                    pending.ResolvedRequest ?? request,
                    response,
                    Binding,
                    _session);
                ThrowIfRejected(validation);
                if (deferredRootHostEntry
                    && !pending.SessionCallStarted)
                {
                    var lateBegin = _session.BeginCall(
                        request.Call,
                        SidecarCapabilityKind.Action,
                        request.Action,
                        request.Action.ByteLength,
                        DateTimeOffset.UtcNow);
                    ThrowIfRejected(lateBegin);
                    pending.SessionCallStarted = true;
                }

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
                {
                    if (_actions.TryRemove(request.Call.CallId, out _))
                    {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                        RecordStateRelease("actions", request.Call.CallId);
#endif
                    }
                }
            }
        }
        finally
        {
            if (!outgoingSequenceCompleted)
                CompleteOutgoingCallSequence(request.Call);
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

                if (response.CrossSidecarOutcome is not { } crossOutcome)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The cross-sidecar response has no signed target outcome.");
                }

                crossOutcome = NormalizeCrossSidecarOutcomeForValidation(crossOutcome);
                ThrowIfRejected(
                    SidecarCrossSidecarActionEntryValidation.ValidateOutcome(
                        crossOutcome,
                        Binding,
                        DateTimeOffset.UtcNow,
                        ValidateCrossSidecarOutcomeProof));

                var authority = crossOutcome.Authority;
                var executionMatches = CanonicalCrossSidecarValueMatches(
                    response.Execution,
                    authority.Execution);
                var outcomeEnvelopeMatches = crossOutcome.Outcome is { } outcome
                    && authority.OutcomeEnvelope is { } authorityOutcome
                    && CanonicalCrossSidecarValueMatches(outcome, authorityOutcome);
                if (!crossOutcome.IsWellFormed
                    || authority.TargetChildCall != relay.Carrier.Authority.TargetChildCall
                    || authority.TargetEntry != relay.TargetEntry
                    || response.ResultIdentity != authority.ResultIdentity
                    || !executionMatches
                    || response.SafeFailure != authority.ResponseSafeFailure
                    || !outcomeEnvelopeMatches
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

                return response with { CrossSidecarOutcome = crossOutcome };
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
            if (_terminals.TryRemove(request.Call.CallId, out _))
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordStateRelease("terminals", request.Call.CallId);
#endif
            }
        }
    }

    public async ValueTask<SidecarStorageCapabilityResponse> InvokeStorageAsync(
        SidecarStorageCapabilityRequest request,
        CancellationToken ct)
    {
        var outgoingSequenceCompleted = false;
        var retainPending = false;
        try
        {
            ValidateStorageRequest(request);
            using var deadline = CreateCallCancellation(request.Deadline, ct);
            var callCancellation = deadline.Token;
            await WaitForOutgoingCallTurnAsync(
                request.Call,
                callCancellation,
                _transport.ActiveCarrierCall is not null);
            var payload = request.RequestPayload ?? EmptyPayload();
            var parentCall = _transport.ActiveCarrierCall;
            var usesStorageContinuation = parentCall is not null
                && request.Call.Sequence
                    > _session.Binding.ConcurrencyLimits.MaximumCallsPerRequest;
            if (usesStorageContinuation)
            {
                var issue = _session.IssueHostEntryStorageContinuation(
                    _session,
                    parentCall!,
                    parentCall!,
                    request,
                    DateTimeOffset.UtcNow,
                    (authority, _) => OutOfProcessCapabilitySecurity.CreateStorageContinuationProof(
                        authority,
                        _controlToken),
                    out var authority);
                ThrowIfRejected(issue);
                var wireAuthority = SidecarCapabilityTransportCodec.Deserialize<
                    SidecarHostEntryStorageContinuationAuthority>(
                    SidecarCapabilityTransportCodec.Serialize(authority));
                ThrowIfRejected(_session.ImportHostEntryStorageContinuationAuthority(
                    wireAuthority,
                    DateTimeOffset.UtcNow));
                request = request with
                {
                    HostEntryContinuationAuthority = wireAuthority,
                };
            }

            var begin = usesStorageContinuation
                ? _session.BeginStorageContinuationCall(
                    request,
                    payload.ByteLength,
                    DateTimeOffset.UtcNow,
                    out _)
                : _session.BeginCall(
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
                throw new OutOfProcessCapabilityException(
                    "sidecar_replay",
                    "The storage call identifier was reused.");
            }

            try
            {
                await OutOfProcessCapabilityWire.SendAsync(
                    _socket,
                    OutOfProcessCapabilityFrameKind.StorageRequest,
                    request,
                    _limits.ProtocolMessageBytes,
                    SendGate,
                    callCancellation);
                CompleteOutgoingCallSequence(request.Call);
                outgoingSequenceCompleted = true;
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
                await SendCancellationAsync(
                    request.Call,
                    request.Cancellation,
                    request.Deadline,
                    callCancellation,
                    ct);
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
                {
                    if (_storage.TryRemove(request.Call.CallId, out _))
                    {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                        RecordStateRelease("storage", request.Call.CallId);
#endif
                    }
                }
            }
        }
        finally
        {
            if (!outgoingSequenceCompleted)
                CompleteOutgoingCallSequence(request.Call);
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
                    case OutOfProcessCapabilityFrameKind.ActionRequest:
                    {
                        var actionRequest = OutOfProcessCapabilityWire.Deserialize<SidecarActionCapabilityRequest>(
                            frame.Payload);
                        if (!_actionEntryQueue.TrySchedule(
                                queueCt => HandleIncomingActionRequestAsync(actionRequest, queueCt),
                                linked.Token,
                                out var actionCompletion))
                        {
                            throw new OutOfProcessCapabilityException(
                                SidecarCapabilityErrors.ModuleBusy,
                                "The module action-entry execution queue is full.");
                        }

                        _ = ObserveActionEntryCompletionAsync(actionCompletion);
                        break;
                    }
                    case OutOfProcessCapabilityFrameKind.ActionResponse:
                        CompleteAction(OutOfProcessCapabilityWire.Deserialize<SidecarActionCapabilityResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.StorageResponse:
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                        RecordRebindState("storage-frame-received");
#endif
                        CompleteStorage(OutOfProcessCapabilityWire.Deserialize<SidecarStorageCapabilityResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalRequest:
                        var terminalRequest = OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportRequest>(
                            frame.Payload);
                        var incomingTerminal = new IncomingTerminal(
                            terminalRequest,
                            CancellationTokenSource.CreateLinkedTokenSource(linked.Token));
                        if (!_incomingTerminals.TryAdd(terminalRequest.Call.CallId, incomingTerminal))
                        {
                            incomingTerminal.Dispose();
                            throw new OutOfProcessCapabilityException(
                                SidecarCapabilityErrors.Replay,
                                "The terminal call identifier was reused.");
                        }
                        if (!_terminalQueue.TrySchedule(
                                queueCt => HandleTerminalRequestAsync(
                                    terminalRequest,
                                    incomingTerminal,
                                    queueCt),
                                linked.Token,
                                out var terminalCompletion))
                        {
                            if (_incomingTerminals.TryRemove(
                                    terminalRequest.Call.CallId,
                                    out var removedTerminal))
                            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                                RecordStateRelease(
                                    "incomingTerminals",
                                    terminalRequest.Call.CallId);
#endif
                                removedTerminal.Dispose();
                            }
                            throw new OutOfProcessCapabilityException(
                                SidecarCapabilityErrors.ModuleBusy,
                                "The module terminal execution queue is full.");
                        }

                        _ = ObserveTerminalCompletionAsync(terminalCompletion);
                        break;
                    case OutOfProcessCapabilityFrameKind.ActionTerminalResponse:
                        CompleteTerminal(OutOfProcessCapabilityWire.Deserialize<SidecarActionTerminalTransportResponse>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityCancellation:
                        CancelIncomingAction(
                            OutOfProcessCapabilityWire.Deserialize<OutOfProcessCapabilityCancellation>(frame.Payload));
                        break;
                    case OutOfProcessCapabilityFrameKind.CapabilityRebind:
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                        RecordRebindState("rebind-frame-received");
#endif
                        var rebind = BeginRebind(linked.Token);
                        var rebindTask = HandleRebindAsync(
                            OutOfProcessCapabilityWire.Deserialize<SidecarCapabilitySessionBinding>(frame.Payload),
                            rebind,
                            linked.Token);
                        _ = ObserveRebindCompletionAsync(rebindTask, rebind);
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
                var disconnect = failure ?? new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The sidecar capability channel disconnected.");
                _rebindReady?.TrySetException(disconnect);
                _rebindInProgress?.TrySetException(disconnect);
            }
            ReleaseAllOutgoingCallReservations();
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
        foreach (var incoming in _incomingActions.Values)
            incoming.Cancellation.Cancel();
        foreach (var incoming in _incomingTerminals.Values)
            incoming.Cancellation.Cancel();
        lock (_rotationSync)
        {
            _rebindReady?.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityConnection)));
            _rebindInProgress?.TrySetException(
                new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityConnection)));
        }
        ReleaseAllOutgoingCallReservations();
        FailPending(new ObjectDisposedException(nameof(OutOfProcessModuleCapabilityConnection)));
        await _actionEntryQueue.DisposeAsync();
        await _terminalQueue.DisposeAsync();
        _rootActionExchangeGate.Dispose();
        _rootRelayImportGate.Dispose();
        _callAdmissionGate.Dispose();
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
        IncomingTerminal incoming,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            incoming.Cancellation.Token);
        ct = linked.Token;
        try
        {
            if (request.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar
                && request.Context is not null
                && request.CrossSidecarActionRequest is not null)
            {
                await HandleCrossSidecarTerminalRequestAsync(request, incoming, ct);
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
        if (pending.Request.Invocation == SidecarActionInvocationKind.HostEntry
            && pending.Request.NestedCarrier is null
            && pending.Request.HostContext is { } initiatingContext)
        {
            await _rootRelayImportGate.WaitAsync(ct);
            try
            {
                var terminal = pending.Request.Terminal
                    ?? throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.MalformedMessage,
                        "The root action has no terminal registration.");
                var terminalContext = request.Context
                    ?? throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.MalformedMessage,
                        "The root terminal request has no execution context.");
                if (request.Authority.RootPeerCall != request.Call)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The root terminal authority has no receiving peer call.");
                }

                var relay = new SidecarHostActionEntryRootRelay(
                    request.Call,
                    request.Call,
                    initiatingContext,
                    request.Descriptor,
                    request.EffectiveAction,
                    terminal,
                    terminalContext.Snapshot,
                    request.Authority,
                    request.Authority.ReceivingPeerBindingGeneration,
                    request.Authority.ReceivingRootBudgetId);
                var relayImport = _session.ImportHostActionEntryPeerRootRelay(
                    relay,
                    DateTimeOffset.UtcNow,
                    out var importedHostContext);
                ThrowIfRejected(relayImport);
                if (importedHostContext is null)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The receiving root HostEntry relay returned no authenticated context.");
                }

                var rootRequest = pending.Request with
                {
                    Call = request.Call,
                    Action = request.EffectiveAction,
                    HostContext = importedHostContext,
                    EffectiveHostEntryContext = new SidecarActionEffectiveHostEntryContext(
                        importedHostContext,
                        terminalContext,
                        request.Authority),
                };
                ThrowIfRejected(_session.BeginActionCall(
                    rootRequest,
                    request.EffectiveAction.ByteLength,
                    DateTimeOffset.UtcNow,
                    out _,
                    static (_, _) => false,
                    ValidateTerminalAuthority));
                pending.SessionCallStarted = true;
            }
            finally
            {
                _rootRelayImportGate.Release();
            }
        }
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
        finally
        {
            if (_incomingTerminals.TryRemove(request.Call.CallId, out var removed))
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordStateRelease("incomingTerminals", request.Call.CallId);
#endif
                removed.Dispose();
            }
            else
                incoming.Dispose();
        }
    }

    private async Task HandleIncomingActionRequestAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken channelCt)
    {
        IncomingAction? active = null;
        var sessionCompleted = false;
        try
        {
            var validation = SidecarCapabilityTransportValidation.ValidateActionRequest(
                request,
                Binding,
                DateTimeOffset.UtcNow,
                ValidateTerminalAuthority);
            if (!validation.Accepted)
            {
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            validation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                            validation.Message ?? "The module action entry request is invalid.")),
                    channelCt);
                return;
            }

            if (request.Invocation != SidecarActionInvocationKind.HostEntry
                || request.HostContext is null
                || request.Terminal is not { IsWellFormed: true })
            {
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            SidecarCapabilityErrors.Unauthorized,
                            "The module action entry request has no authenticated host authority.")),
                    channelCt);
                return;
            }

            var registration = _actionEntries.SingleOrDefault(entry =>
                entry.TerminalId == request.Terminal.TerminalId
                && OutOfProcessActionDescriptorMatches(entry.Descriptor, request.Descriptor));
            if (registration is null)
            {
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            SidecarCapabilityErrors.UnknownAction,
                            "The module action entry is not registered in the compiled graph.")),
                    channelCt);
                return;
            }

            var contribution = request.HostContext.Contribution;
            var lineage = contribution?.Lineage;
            if (contribution is null
                || lineage is null
                || !HostLineageMatchesDescriptor(lineage, request.Descriptor)
                || request.EffectiveHostEntryContext is null
                    && (!string.Equals(
                        lineage.PayloadContentHash,
                        request.Action.ContentHash,
                        StringComparison.Ordinal)
                        || lineage.PayloadByteLength != request.Action.ByteLength))
            {
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            SidecarCapabilityErrors.SpoofedIdentity,
                            "The module action entry request payload is not bound to its host authority.")),
                    channelCt);
                return;
            }

            ObserveSequence(request.Call.Sequence);

            var cancellation = CreateCallCancellation(request.Deadline, channelCt);
            active = new IncomingAction(cancellation)
            {
                Request = request,
            };
            if (!_incomingActions.TryAdd(request.Call.CallId, active))
            {
                cancellation.Dispose();
                active = null;
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            SidecarCapabilityErrors.Replay,
                            "The module action entry call identifier was reused.")),
                    channelCt);
                return;
            }

            var sessionRequest = request;
            if (request.Invocation == SidecarActionInvocationKind.HostEntry
                && request.NestedCarrier is null
                && request.CrossSidecarCarrier is null
                && request.HostContext is not null
                && request.EffectiveHostEntryContext is { } effectiveHostEntry)
            {
                var peerCall = request.Call with
                {
                    SessionId = _session.Binding.SessionId,
                    RequestId = _session.Binding.RequestId,
                    CancellationId = _session.Binding.CancellationId,
                    ModuleId = _session.Binding.ModuleId,
                    GraphId = _session.Binding.GraphId,
                };
                var rootRelay = new SidecarHostActionEntryRootRelay(
                    request.Call,
                    peerCall,
                    request.HostContext,
                    request.Descriptor,
                    request.Action,
                    request.Terminal!,
                    effectiveHostEntry.EffectiveContext.Snapshot,
                    effectiveHostEntry.Authority,
                    effectiveHostEntry.Authority.ReceivingPeerBindingGeneration,
                    effectiveHostEntry.Authority.ReceivingRootBudgetId);
                var import = _session.ImportHostActionEntryPeerRootRelay(
                    rootRelay,
                    DateTimeOffset.UtcNow,
                    out var importedHostContext);
                if (!import.Accepted || importedHostContext is null)
                {
                    AbandonIncomingCall(request.Call.CallId, active);
                    active = null;
                    await SendIncomingActionResponseAsync(
                        CreateIncomingActionFailure(
                            request,
                            ActionOutcomeKind.Failed,
                            new ExecutionError(
                                import.Code ?? SidecarCapabilityErrors.Unauthorized,
                                import.Message ?? "The receiving root HostEntry relay was rejected.")),
                        channelCt);
                    return;
                }

                sessionRequest = request with
                {
                    Call = peerCall,
                    HostContext = importedHostContext,
                };
            }

            var begin = _session.BeginActionCall(
                sessionRequest,
                sessionRequest.Action.ByteLength,
                DateTimeOffset.UtcNow,
                out var sessionHostContext,
                static (_, _) => false,
                ValidateTerminalAuthority);
            if (!begin.Accepted)
            {
                AbandonIncomingCall(request.Call.CallId, active);
                active = null;
                await SendIncomingActionResponseAsync(
                    CreateIncomingActionFailure(
                        request,
                        ActionOutcomeKind.Failed,
                        new ExecutionError(
                            begin.Code ?? SidecarCapabilityErrors.Unauthorized,
                            begin.Message ?? "The capability session rejected the module action entry.")),
                    channelCt);
                return;
            }
            active.Request = sessionRequest;

            var effectiveContext = sessionHostContext ?? request.HostContext!;
            var effectiveTerminalContext = request.EffectiveHostEntryContext?.EffectiveContext;
            var receipt = new SidecarTerminalReceipt(
                Guid.NewGuid().ToString("N"),
                request.Descriptor.Key,
                request.Descriptor.Version,
                request.Call.CallId,
                effectiveContext.Attempt,
                effectiveContext.IdempotencyKey.ToString("N"),
                request.Action.ContentHash);
            var terminalContext = effectiveTerminalContext
                ?? new SidecarActionTerminalExecutionContext(
                    request.Call,
                    request.Invocation,
                    request.Descriptor,
                    request.Action,
                    request.Invocation == SidecarActionInvocationKind.HostEntry
                        ? _hostActionSnapshot
                        : request.Snapshot!,
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
                     effectiveContext.Deadline);
            using var carrierScope = _transport.PushActiveCarrier(
                effectiveContext.CapabilityId,
                sessionRequest.Call);
            await using var invocationScope = _services.CreateAsyncScope();
            var hostActionEntry = request.EffectiveHostEntryContext is { } incomingHostEntry
                ? OutOfProcessHostActionEntry.CreateForIncomingAction(
                    _transport,
                    sessionRequest,
                    terminalContext,
                    incomingHostEntry,
                    sessionRequest.HostContext?.Contribution)
                : new OutOfProcessHostActionEntry(_transport);
            var execution = await registration.Invoker.InvokeAsync(
                invocationScope.ServiceProvider,
                terminalContext,
                hostActionEntry,
                active.Cancellation.Token);
            var response = CreateIncomingActionResponse(request, execution, ActionOutcomeKind.Completed, null);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                Binding,
                _session);
            if (!responseValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    responseValidation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                    responseValidation.Message ?? "The module action entry response is invalid.");
            }

            if (!CompleteCall(request.Call.CallId, response.Outcome.TerminalCallCount))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The module action entry call could not be completed.");
            }

            sessionCompleted = true;
            await SendIncomingActionResponseAsync(response, channelCt);
        }
        catch (OperationCanceledException) when (
            active is not null
            && active.Cancellation.IsCancellationRequested
            && !channelCt.IsCancellationRequested)
        {
            if (!sessionCompleted)
            {
                CompleteCall(request.Call.CallId, 0);
                sessionCompleted = true;
            }

            await SendIncomingActionResponseAsync(
                CreateIncomingActionFailure(request, ActionOutcomeKind.Cancelled, null),
                channelCt);
        }
        catch (OperationCanceledException) when (channelCt.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (active is not null && !sessionCompleted)
            {
                CompleteCall(request.Call.CallId, 0);
                sessionCompleted = true;
            }

            await SendIncomingActionResponseAsync(
                CreateIncomingActionFailure(
                    request,
                    ActionOutcomeKind.Failed,
                    new ExecutionError(
                        SidecarCapabilityErrors.HostFailure,
                        "The module action entry failed.")),
                    channelCt);
        }
        finally
        {
            if (active is not null)
            {
                if (_incomingActions.TryRemove(request.Call.CallId, out _))
                {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                    RecordStateRelease("incomingActions", request.Call.CallId);
#endif
                }
                active.Cancellation.Dispose();
            }
        }
    }

    private async Task ObserveActionEntryCompletionAsync(Task completion)
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
            _disconnect.Cancel();
        }
    }

    private void CancelIncomingAction(OutOfProcessCapabilityCancellation cancellation)
    {
        if (cancellation.PeerCancellation is { } peerCancellation)
        {
            if (_incomingTerminals.TryGetValue(
                    cancellation.Call.CallId,
                    out var terminalWithPeerCancellation))
            {
                CancelIncomingTerminal(cancellation, terminalWithPeerCancellation);
                return;
            }

            IncomingAction? peerAction = null;
            if (_incomingActions.TryGetValue(cancellation.Call.CallId, out var activePeerAction))
            {
                ValidateIncomingCancellation(cancellation, activePeerAction.Request?.Call);
                peerAction = activePeerAction;
            }

            ConsumePeerCancellation(cancellation, peerCancellation);
            peerAction?.Cancellation.Cancel();
            return;
        }

        if (_incomingActions.TryGetValue(cancellation.Call.CallId, out var active))
        {
            ValidateIncomingCancellation(cancellation, active.Request?.Call);
            active.Cancellation.Cancel();
            return;
        }

        if (!_incomingTerminals.TryGetValue(cancellation.Call.CallId, out var incoming))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The module cancellation does not match an active call.");
        }

        CancelIncomingTerminal(cancellation, incoming);
    }

    private void CancelIncomingTerminal(
        OutOfProcessCapabilityCancellation cancellation,
        IncomingTerminal incoming)
    {
        ValidateIncomingCancellation(cancellation, incoming.Request.Call);
        var peerRelay = incoming.Request.CrossSidecarPeerRelay;
        var crossSidecar = incoming.Request.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar
            && peerRelay is not null;
        if (crossSidecar && !incoming.RelayImportStarted)
        {
            var peerCancellation = cancellation.PeerCancellation;
            if (peerCancellation is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthorized,
                    "The cross-sidecar cancellation has no signed peer authority.");
            }

            if (!string.Equals(
                    SidecarCapabilityTransportValidation.ComputeCrossSidecarPeerRelayBindingHash(
                        peerRelay!),
                    SidecarCapabilityTransportValidation.ComputeCrossSidecarPeerRelayBindingHash(
                        peerCancellation.Relay),
                    StringComparison.Ordinal))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The cross-sidecar cancellation relay does not match the terminal request.");
            }

            var consumed = incoming.TryConsumePeerCancellation(() =>
            {
                ConsumePeerCancellation(cancellation, peerCancellation);
                return true;
            });
            if (!consumed && incoming.PeerCancellationConsumed)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The cross-sidecar cancellation was already consumed.");
            }
        }

        incoming.Cancellation.Cancel();
    }

    private void ConsumePeerCancellation(
        OutOfProcessCapabilityCancellation cancellation,
        SidecarCrossSidecarActionEntryPeerCancellation peerCancellation)
    {
        ValidateCancellationEnvelope(cancellation);
        if (!MatchesCapabilityCall(
                cancellation.Call,
                peerCancellation.Relay.Carrier.Authority.TargetChildCall)
            || peerCancellation.CancelledAt != cancellation.SentAt)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The cross-sidecar cancellation is not bound to its receiving call.");
        }

        var validation = _session.ConsumeCancelledCrossSidecarActionEntryPeerRelay(
            peerCancellation,
            cancellation.SentAt,
            ValidateCrossSidecarOutcomeProof);
        ThrowIfRejected(validation);
    }

    private void ValidateIncomingCancellation(
        OutOfProcessCapabilityCancellation cancellation,
        SidecarCapabilityCallIdentity? expectedCall)
    {
        if (expectedCall is null
            || !MatchesCapabilityCall(cancellation.Call, expectedCall))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The module cancellation does not match the active call.");
        }

        ValidateCancellationEnvelope(cancellation);
    }

    private void ValidateCancellationEnvelope(
        OutOfProcessCapabilityCancellation cancellation)
    {
        if (!cancellation.Call.Equals(CreateExpectedCall(cancellation.Call))
            || cancellation.Cancellation.CancellationId != cancellation.Call.CancellationId
            || !string.Equals(
                cancellation.Cancellation.AuthorityHash,
                SidecarCapabilitySessionValidator.ComputeBindingHash(Binding),
                StringComparison.Ordinal)
            || cancellation.Cancellation.ExpiresAt != cancellation.Call.Deadline)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The module cancellation does not match the active call.");
        }
    }

    private SidecarCapabilityCallIdentity CreateExpectedCall(
        SidecarCapabilityCallIdentity call) =>
        new(
            Binding.SessionId,
            Binding.RequestId,
            Binding.CancellationId,
            call.CallId,
            call.ReplayNonce,
            Binding.ModuleId,
            Binding.GraphId,
            call.Capability,
            call.Sequence,
            call.Deadline);

    private void AbandonIncomingCall(Guid callId, IncomingAction active)
    {
        if (_incomingActions.TryRemove(callId, out var removed))
        {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
            RecordStateRelease("incomingActions", callId);
#endif
            removed.Cancellation.Dispose();
        }
        else
            active.Cancellation.Dispose();
    }

    private async Task SendIncomingActionResponseAsync(
        SidecarActionCapabilityResponse response,
        CancellationToken ct)
    {
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private SidecarActionCapabilityResponse CreateIncomingActionResponse(
        SidecarActionCapabilityRequest request,
        SidecarTerminalExecutionResult execution,
        ActionOutcomeKind kind,
        ExecutionError? error)
    {
        var result = kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred
            ? execution.Result
            : null;
        var outcome = new SidecarActionOutcomeEnvelope(
            kind,
            result!,
            null!,
            error!,
            null!,
            null!,
            Binding.SafeFailure,
            TerminalCallCount: 0);
        return new SidecarActionCapabilityResponse(
            result is null
                ? null
                : new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    request.Descriptor.Key,
                    request.Descriptor.Version,
                    result.TypeIdentity,
                    result.ContentHash),
            outcome,
            null!,
            Binding.SafeFailure,
            Completed: true);
    }

    private SidecarActionCapabilityResponse CreateIncomingActionFailure(
        SidecarActionCapabilityRequest request,
        ActionOutcomeKind kind,
        ExecutionError? error) =>
        new(
            null,
            new SidecarActionOutcomeEnvelope(
                kind,
                null!,
                null!,
                error!,
                null!,
                null!,
                Binding.SafeFailure,
                TerminalCallCount: 0),
            null!,
            Binding.SafeFailure,
            Completed: true);

    private static bool OutOfProcessActionDescriptorMatches(
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

    private static bool HostLineageMatchesDescriptor(
        HostActionEntryLineage lineage,
        SidecarActionDescriptorIdentity descriptor) =>
        lineage.ActionKey == descriptor.Key
        && lineage.ActionVersion == descriptor.Version
        && string.Equals(lineage.DescriptorHash, descriptor.DescriptorHash, StringComparison.Ordinal)
        && string.Equals(lineage.InputTypeIdentity, descriptor.InputTypeIdentity, StringComparison.Ordinal)
        && lineage.InputSchemaVersion == descriptor.InputSchemaVersion
        && string.Equals(lineage.InputSchemaHash, descriptor.InputSchemaHash, StringComparison.Ordinal);

    private async Task HandleCrossSidecarTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        IncomingTerminal incoming,
        CancellationToken ct)
    {
        var context = request.Context
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The cross-sidecar terminal request has no execution context.");
        var authorityValid = ValidateTerminalAuthority(
                request.Authority,
                SidecarCapabilityTransportValidation.ComputeTerminalAuthorityBindingHash(
                    request.Authority))
            && request.Authority.ModuleId == Binding.ModuleId
            && request.Authority.GraphId == Binding.GraphId
            && request.Authority.Invocation == SidecarActionInvocationKind.HostEntryCrossSidecar
            && request.Authority.CallId == request.Call.CallId
            && request.Authority.TerminalId == request.TerminalId
            && request.Authority.InvocationId == context.InvocationId
            && request.Authority.ParentInvocationId == context.ParentInvocationId
            && request.Authority.TraceId == context.TraceId
            && request.Authority.IdempotencyKey == context.IdempotencyKey
            && request.Authority.Depth == context.Depth
            && request.Authority.Attempt == context.Attempt
            && request.Authority.Caller is not null
            && context.Caller is not null
            && OutOfProcessHostActionEntryContextRegistry.MatchesCaller(
                request.Authority.Caller,
                context.Caller)
            && string.Equals(
                SidecarCapabilityTransportCodec.ComputeSha256(
                    SidecarCapabilityTransportCodec.Serialize(request.Authority.Features)),
                SidecarCapabilityTransportCodec.ComputeSha256(
                    SidecarCapabilityTransportCodec.Serialize(context.Features)),
                StringComparison.Ordinal)
            && request.Descriptor.Key == context.Descriptor.Key
            && request.Descriptor.Version == context.Descriptor.Version
            && OutOfProcessCapabilityTransportPayloadMatches(
                request.EffectiveAction,
                context.EffectiveAction)
            && string.Equals(
                request.Authority.SnapshotContentHash,
                SidecarCapabilityTransportCodec.ComputeSha256(
                    SidecarCapabilityTransportCodec.Serialize(context.Snapshot)),
                StringComparison.Ordinal);
        if (!authorityValid)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The cross-sidecar terminal authority does not match its execution context.");
        }

        if (request.CrossSidecarActionRequest is null
            || request.CrossSidecarPeerRelay is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The cross-sidecar terminal request has no authenticated peer relay.");
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

        if (!incoming.TryBeginRelayImport())
        {
            if (!incoming.PeerCancellationConsumed)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Duplicate,
                    "The cross-sidecar peer relay import was started more than once.");
            }

            await SendCancelledCrossSidecarTerminalResponseAsync(request, ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var peerImport = _session.ImportCrossSidecarActionEntryPeerRelay(
            request,
            now,
            ValidateCrossSidecarOutcomeProof,
            out var importedCarrier);
        ThrowIfRejected(peerImport);
        if (importedCarrier is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The cross-sidecar peer relay returned no carrier.");
        }

        var peerCall = importedCarrier.Authority.PeerCall
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The receiving peer relay has no local call identity.");
        ObserveSequence(peerCall.Sequence);
        var peerReceipt = request.Receipt with { CallId = peerCall.CallId };

        var terminal = new SidecarActionTerminalRegistration(
            request.TerminalId,
            request.Descriptor.InputTypeIdentity,
            request.Descriptor.InputSchemaVersion,
            request.Descriptor.ResultTypeIdentity,
            request.Descriptor.ResultSchemaVersion,
            request.Descriptor.DescriptorHash);
        var begin = _session.BeginCrossSidecarActionEntryCall(
            importedCarrier,
            terminal,
            request.EffectiveAction.ByteLength,
            now,
            out var importedHostContext,
            ValidateCrossSidecarOutcomeProof);
        if (!begin.Accepted || importedHostContext is null)
        {
            _session.RevokeCrossSidecarActionEntry(
                importedCarrier.CarrierId,
                DateTimeOffset.UtcNow);
            ThrowIfRejected(begin);
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The receiving cross-sidecar carrier returned no host context.");
        }

        var effectiveHostEntry = new SidecarActionEffectiveHostEntryContext(
            importedHostContext,
            context,
            request.Authority);
        var incomingRequest = SidecarActionCapabilityRequest.HostEntryCrossSidecar(
            request.Call,
            request.Descriptor,
            request.EffectiveAction,
            request.Cancellation,
            request.Deadline,
            importedCarrier,
            terminal) with
        {
            EffectiveHostEntryContext = effectiveHostEntry,
        };
        var hostEntry = OutOfProcessHostActionEntry.CreateForIncomingAction(
            _transport,
            incomingRequest,
            context,
            effectiveHostEntry,
            importedHostContext.Contribution);
        var revoked = false;
        using var carrierScope = _transport.PushActiveCarrier(
            importedHostContext.CapabilityId,
            peerCall);
        try
        {
            SidecarTerminalExecutionResult execution;
            await using var invocationScope = _services.CreateAsyncScope();
            try
            {
                execution = await registration.Invoker.InvokeAsync(
                    invocationScope.ServiceProvider,
                    context,
                    hostEntry,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                execution = new SidecarTerminalExecutionResult(
                    null,
                    _session.Binding.SafeFailure,
                    Completed: true);
            }
            catch (Exception)
            {
                execution = new SidecarTerminalExecutionResult(
                    null,
                    _session.Binding.SafeFailure,
                    Completed: true);
            }

            var resultIdentity = execution.Result is null
                ? null
                : new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    request.Descriptor.Key,
                    request.Descriptor.Version,
                    execution.Result.TypeIdentity,
                    execution.Result.ContentHash);
            ThrowIfRejected(_session.RecordTerminal(
                peerCall.CallId,
                request.Authority.AuthorityId,
                peerReceipt));
            var response = new SidecarActionTerminalTransportResponse(
                resultIdentity,
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

            var revocation = _session.RevokeCrossSidecarActionEntry(
                importedCarrier.CarrierId,
                DateTimeOffset.UtcNow);
            if (!revocation.Accepted
                && !string.Equals(
                    revocation.Code,
                    SidecarCapabilityErrors.Duplicate,
                    StringComparison.Ordinal))
            {
                ThrowIfRejected(revocation);
            }

            revoked = true;
        }
        finally
        {
            if (!revoked)
            {
                _ = _session.RevokeCrossSidecarActionEntry(
                    importedCarrier.CarrierId,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    private async Task SendCancelledCrossSidecarTerminalResponseAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var cancellationFailure = new SidecarSafeFailureIdentity(
            Guid.NewGuid(),
            SidecarCapabilityErrors.Cancelled,
            "The cross-sidecar target action was cancelled before relay import.",
            Retryable: false);
        var response = new SidecarActionTerminalTransportResponse(
            null,
            new SidecarTerminalExecutionResult(null, cancellationFailure, Completed: true),
            request.Receipt,
            _session.Binding.SafeFailure)
        {
            TerminalId = request.TerminalId,
        };
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            sendTimeout.Token);
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

    private TaskCompletionSource BeginRebind(CancellationToken ct)
    {
        _callAdmissionGate.Wait(ct);
        try
        {
            lock (_rotationSync)
            {
                if (_rebindInProgress is not null)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The capability binding rotation was received more than once.");
                }

                var rebind = CreateSignal();
                _rebindInProgress = rebind;
                _rebindAdmissionClosed = false;
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordRebindState("rebind-admitted");
#endif
                return rebind;
            }
        }
        finally
        {
            _callAdmissionGate.Release();
        }
    }

    private async Task ObserveRebindCompletionAsync(
        Task rebindTask,
        TaskCompletionSource rebind)
    {
        try
        {
            await rebindTask;
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
            rebind.TrySetException(
                new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Disconnected,
                    "The sidecar capability channel disconnected during binding rotation."));
        }
        catch (Exception ex)
        {
            rebind.TrySetException(ex);
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

#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
    private void RecordRebindState(string phase) =>
        OutOfProcessProtocolTestFixture.RecordRebindState(
            phase,
            string.Join(
                ";",
                DescribePendingCollection("actions", _actions.Keys),
                DescribePendingCollection("incomingActions", _incomingActions.Keys),
                DescribePendingCollection("storage", _storage.Keys),
                DescribePendingCollection("terminals", _terminals.Keys),
                DescribePendingCollection("incomingTerminals", _incomingTerminals.Keys),
                DescribeOutgoingReservations()));

    private static string DescribePendingCollection(
        string name,
        IEnumerable<Guid> callIds) =>
        $"{name}=[{string.Join(",", callIds.OrderBy(callId => callId).Select(callId => callId.ToString("N")))}]";

    private string DescribeOutgoingReservations()
    {
        lock (_outgoingSequenceSync)
        {
            return $"outgoing=[{string.Join(",", _outgoingCallReservations
                .OrderBy(entry => entry.Value)
                .Select(entry => $"{entry.Key:N}:{entry.Value}"))}]";
        }
    }

    private static void RecordStateRelease(string collection, Guid callId) =>
        OutOfProcessProtocolTestFixture.RecordRebindState(
            "state-released",
            $"{collection}={callId:N}");
#endif

    private bool HasPendingRebindWork() =>
        !_actions.IsEmpty
        || !_incomingActions.IsEmpty
        || !_storage.IsEmpty
        || !_terminals.IsEmpty
        || !_incomingTerminals.IsEmpty
        || HasOutgoingCallReservations();

    private async Task HandleRebindAsync(
        SidecarCapabilitySessionBinding binding,
        TaskCompletionSource rebind,
        CancellationToken ct)
    {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
        RecordRebindState("rebind-received");
#endif
        while (true)
        {
            while (HasPendingRebindWork())
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordRebindState("rebind-wait");
#endif
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            }

            await _callAdmissionGate.WaitAsync(ct);
            try
            {
                lock (_rotationSync)
                {
                    if (HasPendingRebindWork())
                        continue;

                    _rebindAdmissionClosed = true;
                }

#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordRebindState("rebind-drained");
#endif

                SidecarCapabilityValidationResult validation;
                if (!string.Equals(
                        binding.ModuleId,
                        _authorization.ModuleId,
                        StringComparison.Ordinal)
                    || binding.ProtocolVersion != OutOfProcessModuleHostProtocol.Version)
                {
                    validation = SidecarCapabilityValidationResult.Reject(
                        SidecarCapabilityErrors.Unauthorized,
                        "The capability binding rotation identifies a different module or protocol.");
                }
                else
                {
                    var authenticate = new Func<SidecarCapabilityAuthenticationAuthority, bool>(
                        authority => OutOfProcessCapabilitySecurity.Authenticate(
                            authority,
                            _controlToken));
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
                {
                    throw new OutOfProcessCapabilityException(
                        validation.Code ?? SidecarCapabilityErrors.Unauthorized,
                        validation.Message
                            ?? "The rotated capability binding was rejected.");
                }

                var rotation = Volatile.Read(ref _session).RotateBinding(
                    binding,
                    DateTimeOffset.UtcNow);
                if (!rotation.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        rotation.Code ?? SidecarCapabilityErrors.Unauthorized,
                        rotation.Message
                            ?? "The rotated capability binding could not replace the active binding.");
                }
                TaskCompletionSource? ready;
                lock (_rotationSync)
                {
                    ready = _rebindReady;
                    _rebindReady = null;
                    if (ReferenceEquals(_rebindInProgress, rebind))
                        _rebindInProgress = null;
                    _rebindAdmissionClosed = false;
                    Interlocked.Exchange(ref _completedCallsForBinding, 0);
                    Interlocked.Exchange(ref _sequence, 0);
                }
                ready?.TrySetResult();
                rebind.TrySetResult();
                return;
            }
            finally
            {
                _callAdmissionGate.Release();
            }
        }
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
            if (_actions.TryRemove(request.Call.CallId, out _))
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordStateRelease("actions", request.Call.CallId);
#endif
            }
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
            if (_storage.TryRemove(request.Call.CallId, out _))
            {
#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
                RecordStateRelease("storage", request.Call.CallId);
#endif
            }
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

    private bool ValidateCrossSidecarOutcomeProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string canonicalHash) =>
        string.Equals(
            authority.CanonicalBindingHash,
            canonicalHash,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            CreateCrossSidecarProof(
                authority with
                {
                    CanonicalBindingHash = canonicalHash,
                    Proof = string.Empty,
                },
                _controlToken),
            authority.Proof,
            StringComparison.Ordinal);

    private static string CreateCrossSidecarProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string controlToken)
    {
        var value = "cross-sidecar|"
            + SidecarCrossSidecarActionEntryValidation.ComputeAuthorityHash(authority);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(controlToken),
            Encoding.UTF8.GetBytes(value)));
    }

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
                    "The failed action response has no unique pending action call. "
                    + $"kind={response.Outcome.Kind}; "
                    + $"error={response.Outcome.Error?.Code}:{response.Outcome.Error?.Message}; "
                    + $"pending={string.Join(',', pendingCallIds)}");
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
            DateTimeOffset.UtcNow,
            ValidateTerminalAuthority);
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

        if (outcome.Outcome is not { } failedOutcome)
            return false;

        return failedOutcome.Kind is ActionOutcomeKind.Failed or ActionOutcomeKind.Cancelled
            && payload is null
            && outcome.Authority.ResultIdentity is null
            && failedOutcome.TerminalCallCount == 1
            && (failedOutcome.Error is not null) == (outcome.Kind
                == SidecarCrossSidecarActionEntryOutcomeKind.Failed)
            && (outcome.Kind != SidecarCrossSidecarActionEntryOutcomeKind.Cancelled
                || failedOutcome.Error is null);
    }

    private static bool CanonicalCrossSidecarValueMatches<T>(T actual, T expected) =>
        string.Equals(
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(actual)),
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(expected)),
            StringComparison.Ordinal);

    private static SidecarCrossSidecarActionEntryOutcome
        NormalizeCrossSidecarOutcomeForValidation(
            SidecarCrossSidecarActionEntryOutcome outcome)
    {
        var envelope = outcome.Outcome;
        var authority = outcome.Authority;
        var authorityEnvelope = authority.OutcomeEnvelope;
        var execution = authority.Execution;
        if (envelope is null ||
            authorityEnvelope is null ||
            execution is null ||
            !CanonicalCrossSidecarValueMatches(envelope, authorityEnvelope) ||
            (execution.Result is null) != (envelope.Result is null) ||
            execution.Result is not null &&
            !CanonicalCrossSidecarValueMatches(execution.Result, envelope.Result))
        {
            return outcome;
        }

        var normalizedAuthority = authority with
        {
            OutcomeEnvelope = envelope,
            Execution = execution with { Result = envelope.Result },
        };
        var originalHash = SidecarCrossSidecarActionEntryValidation
            .ComputeAuthorityHash(authority);
        var normalizedHash = SidecarCrossSidecarActionEntryValidation
            .ComputeAuthorityHash(normalizedAuthority);
        return string.Equals(originalHash, normalizedHash, StringComparison.Ordinal)
            ? outcome with { Authority = normalizedAuthority }
            : outcome;
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
        foreach (var incoming in _incomingActions.Values)
            incoming.Cancellation.Cancel();
        foreach (var incoming in _incomingTerminals.Values)
            incoming.Cancellation.Cancel();
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
