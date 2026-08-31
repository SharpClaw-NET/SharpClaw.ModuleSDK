using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessModuleCapabilityConnection
{
    private readonly ConcurrentDictionary<Guid, ImportedEndpointWebSocketChannel>
        _endpointWebSocketChannels = new();

    internal sealed class ImportedEndpointWebSocketChannel :
        IModuleWebSocketChannel,
        IAsyncDisposable
    {
        private readonly OutOfProcessModuleCapabilityConnection _owner;
        private readonly ImportedEndpointRouteLease _lease;
        private readonly Channel<ModuleWebSocketMessage> _hostMessages;
        private readonly object _sync = new();
        private long _hostSequence;
        private long _moduleSequence;
        private bool _hostCompleted;
        private bool _moduleCompleted;
        private bool _moduleClosed;
        private bool _ready;
        private int _disposed;

        internal ImportedEndpointWebSocketChannel(
            OutOfProcessModuleCapabilityConnection owner,
            ImportedEndpointRouteLease lease,
            int capacity)
        {
            _owner = owner;
            _lease = lease;
            _hostMessages = Channel.CreateBounded<ModuleWebSocketMessage>(
                new BoundedChannelOptions(Math.Max(capacity, 1))
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
        }

        internal Guid InvocationId => _lease.Relay.Request.Invocation.InvocationId;

        internal async ValueTask AnnounceReadyAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_ready || _moduleCompleted)
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The module WebSocket channel is already ready or complete.");
                _ready = true;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _owner._socket,
                OutOfProcessCapabilityFrameKind.EndpointWebSocketModuleReady,
                new OutOfProcessEndpointWebSocketReady(InvocationId),
                _owner._limits.ProtocolMessageBytes,
                _owner.SendGate,
                cancellationToken);
        }

        public async ValueTask<ModuleWebSocketMessage?> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            while (await _hostMessages.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_hostMessages.Reader.TryRead(out var message))
                    return message;
            }

            return null;
        }

        public async ValueTask SendAsync(
            ModuleWebSocketMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!message.IsWellFormed || message.Type == ModuleWebSocketMessageType.Close)
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The module WebSocket data message is invalid.");
            await SendModuleMessageAsync(message, cancellationToken);
        }

        public async ValueTask CloseAsync(
            int closeStatus,
            string? description,
            CancellationToken cancellationToken)
        {
            var message = new ModuleWebSocketMessage(
                ModuleWebSocketMessageType.Close,
                [],
                closeStatus,
                description);
            if (!message.IsWellFormed)
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The module WebSocket close message is invalid.");
            await SendModuleMessageAsync(message, cancellationToken);
        }

        internal void AcceptHostMessage(OutOfProcessEndpointWebSocketMessage frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (_sync)
            {
                if (!_ready || _hostCompleted || frame.InvocationId != InvocationId ||
                    frame.Sequence != checked(_hostSequence + 1) ||
                    frame.Message is null || !frame.Message.IsWellFormed)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The host WebSocket message does not match the active endpoint route.");
                }

                _hostSequence = frame.Sequence;
                if (!_hostMessages.Writer.TryWrite(frame.Message))
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.ModuleBusy,
                        "The module WebSocket input queue is full.");
            }
        }

        internal void CompleteHost(OutOfProcessEndpointWebSocketCompleted frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (_sync)
            {
                if (!_ready || _hostCompleted || frame.InvocationId != InvocationId ||
                    frame.LastSequence != _hostSequence)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The host WebSocket completion does not match the active endpoint route.");
                }

                _hostCompleted = true;
                _hostMessages.Writer.TryComplete();
            }
        }

        private async ValueTask SendModuleMessageAsync(
            ModuleWebSocketMessage message,
            CancellationToken cancellationToken)
        {
            long sequence;
            lock (_sync)
            {
                if (!_ready || _moduleCompleted || _moduleClosed)
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The module WebSocket output is complete.");
                _moduleClosed = message.Type == ModuleWebSocketMessageType.Close;
                sequence = ++_moduleSequence;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _owner._socket,
                OutOfProcessCapabilityFrameKind.EndpointWebSocketModuleMessage,
                new OutOfProcessEndpointWebSocketMessage(InvocationId, sequence, message),
                _owner._limits.ProtocolMessageBytes,
                _owner.SendGate,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            long lastSequence;
            lock (_sync)
            {
                _moduleCompleted = true;
                lastSequence = _moduleSequence;
                _hostMessages.Writer.TryComplete();
            }

            _owner._endpointWebSocketChannels.TryRemove(InvocationId, out _);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _owner._disconnect.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await OutOfProcessCapabilityWire.SendAsync(
                _owner._socket,
                OutOfProcessCapabilityFrameKind.EndpointWebSocketModuleCompleted,
                new OutOfProcessEndpointWebSocketCompleted(InvocationId, lastSequence),
                _owner._limits.ProtocolMessageBytes,
                _owner.SendGate,
                timeout.Token);
        }
    }

    internal ImportedEndpointWebSocketChannel OpenImportedEndpointWebSocketChannel(
        ImportedEndpointRouteLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Relay.Request.Route.Transport != HostEndpointTransport.WebSocket ||
            !_activeEndpointRouteStates.TryGetValue(lease.Context.CapabilityId, out var active) ||
            !ReferenceEquals(active, lease))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.InvalidBinding,
                "The imported endpoint route is not an active WebSocket route.");
        }

        var channel = new ImportedEndpointWebSocketChannel(
            this,
            lease,
            Binding.ConcurrencyLimits.MaximumInFlightCalls);
        if (!_endpointWebSocketChannels.TryAdd(channel.InvocationId, channel))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The imported endpoint WebSocket invocation is already active.");
        return channel;
    }

    private void HandleEndpointWebSocketHostMessage(
        OutOfProcessEndpointWebSocketMessage frame)
    {
        if (!_endpointWebSocketChannels.TryGetValue(frame.InvocationId, out var channel))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The host WebSocket message has no active endpoint route.");
        channel.AcceptHostMessage(frame);
    }

    private void HandleEndpointWebSocketHostCompleted(
        OutOfProcessEndpointWebSocketCompleted frame)
    {
        if (!_endpointWebSocketChannels.TryGetValue(frame.InvocationId, out var channel))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The host WebSocket completion has no active endpoint route.");
        channel.CompleteHost(frame);
    }

    private async ValueTask DisposeEndpointWebSocketChannelsAsync()
    {
        foreach (var channel in _endpointWebSocketChannels.Values)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
            {
            }
        }
        _endpointWebSocketChannels.Clear();
    }
}
