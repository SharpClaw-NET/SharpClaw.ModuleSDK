using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession
{
    private readonly ConcurrentDictionary<Guid, EndpointWebSocketBridge>
        _endpointWebSocketBridges = new();

    internal sealed class EndpointWebSocketBridge : IAsyncDisposable
    {
        private readonly OutOfProcessCapabilityHostSession _owner;
        private readonly EndpointRouteLease _lease;
        private readonly IModuleWebSocketChannel _channel;
        private readonly Channel<ModuleWebSocketMessage> _moduleMessages;
        private readonly CancellationTokenSource _lifetime;
        private readonly TaskCompletionSource<bool> _moduleReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _moduleCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _outputPump;
        private readonly object _sync = new();
        private long _hostSequence;
        private long _moduleSequence;
        private bool _hostCompleted;
        private bool _moduleCompleted;
        private bool _moduleClosed;
        private bool _moduleReadyReceived;
        private int _disposed;

        internal EndpointWebSocketBridge(
            OutOfProcessCapabilityHostSession owner,
            EndpointRouteLease lease,
            IModuleWebSocketChannel channel,
            int capacity,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _lease = lease;
            _channel = channel;
            _moduleMessages = Channel.CreateBounded<ModuleWebSocketMessage>(
                new BoundedChannelOptions(Math.Max(capacity, 1))
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._disconnect.Token);
            _outputPump = PumpModuleMessagesAsync(_lifetime.Token);
        }

        internal Guid InvocationId => _lease.Request.Invocation.InvocationId;

        internal async Task RunHostInputAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            await _moduleReady.Task.WaitAsync(linked.Token);
            while (true)
            {
                var message = await _channel.ReceiveAsync(linked.Token);
                if (message is null)
                {
                    await CompleteHostAsync(linked.Token);
                    return;
                }

                if (!message.IsWellFormed)
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The host WebSocket message is invalid.");

                await SendHostMessageAsync(message, linked.Token);
                if (message.Type == ModuleWebSocketMessageType.Close)
                {
                    await CompleteHostAsync(linked.Token);
                    return;
                }
            }
        }

        internal async Task WaitForModuleOutputAsync(CancellationToken cancellationToken) =>
            await _outputPump.WaitAsync(cancellationToken);

        internal async Task WaitForModuleCompletionAsync(CancellationToken cancellationToken) =>
            await _moduleCompletion.Task.WaitAsync(cancellationToken);

        internal void AcceptModuleReady(OutOfProcessEndpointWebSocketReady frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (_sync)
            {
                if (_moduleReadyReceived || _moduleCompleted ||
                    frame.InvocationId != InvocationId)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The module WebSocket readiness does not match the active endpoint route.");
                }

                _moduleReadyReceived = true;
                _moduleReady.TrySetResult(true);
            }
        }

        internal void AcceptModuleMessage(OutOfProcessEndpointWebSocketMessage frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (_sync)
            {
                if (_moduleCompleted || _moduleClosed || frame.InvocationId != InvocationId ||
                    frame.Sequence != checked(_moduleSequence + 1) ||
                    frame.Message is null || !frame.Message.IsWellFormed)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The module WebSocket message does not match the active endpoint route.");
                }

                _moduleSequence = frame.Sequence;
                _moduleClosed = frame.Message.Type == ModuleWebSocketMessageType.Close;
                if (!_moduleMessages.Writer.TryWrite(frame.Message))
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.ModuleBusy,
                        "The host WebSocket output queue is full.");
            }
        }

        internal void CompleteModule(OutOfProcessEndpointWebSocketCompleted frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            lock (_sync)
            {
                if (_moduleCompleted || frame.InvocationId != InvocationId ||
                    frame.LastSequence != _moduleSequence)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidBinding,
                        "The module WebSocket completion does not match the active endpoint route.");
                }

                _moduleCompleted = true;
                _moduleMessages.Writer.TryComplete();
                _moduleCompletion.TrySetResult(true);
            }
        }

        private async ValueTask SendHostMessageAsync(
            ModuleWebSocketMessage message,
            CancellationToken cancellationToken)
        {
            long sequence;
            lock (_sync)
            {
                if (_hostCompleted)
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The host WebSocket input is complete.");
                sequence = ++_hostSequence;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _owner._socket,
                OutOfProcessCapabilityFrameKind.EndpointWebSocketHostMessage,
                new OutOfProcessEndpointWebSocketMessage(InvocationId, sequence, message),
                _owner._limits.ProtocolMessageBytes,
                _owner.SendGate,
                cancellationToken);
        }

        private async ValueTask CompleteHostAsync(CancellationToken cancellationToken)
        {
            long lastSequence;
            lock (_sync)
            {
                if (_hostCompleted)
                    return;
                _hostCompleted = true;
                lastSequence = _hostSequence;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _owner._socket,
                OutOfProcessCapabilityFrameKind.EndpointWebSocketHostCompleted,
                new OutOfProcessEndpointWebSocketCompleted(InvocationId, lastSequence),
                _owner._limits.ProtocolMessageBytes,
                _owner.SendGate,
                cancellationToken);
        }

        private async Task PumpModuleMessagesAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in _moduleMessages.Reader.ReadAllAsync(cancellationToken))
            {
                if (message.Type == ModuleWebSocketMessageType.Close)
                {
                    await _channel.CloseAsync(
                        message.CloseStatus!.Value,
                        message.CloseDescription,
                        cancellationToken);
                    continue;
                }

                await _channel.SendAsync(message, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _owner._endpointWebSocketBridges.TryRemove(InvocationId, out _);
            _lifetime.Cancel();
            _moduleReady.TrySetCanceled(_lifetime.Token);
            _moduleCompletion.TrySetCanceled(_lifetime.Token);
            _moduleMessages.Writer.TryComplete();
            try
            {
                await _outputPump;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    internal EndpointWebSocketBridge OpenEndpointWebSocketBridge(
        EndpointRouteLease lease,
        IModuleWebSocketChannel channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(channel);
        if (lease.Request.Route.Transport != HostEndpointTransport.WebSocket)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.InvalidBinding,
                "The endpoint route is not a WebSocket route.");

        var bridge = new EndpointWebSocketBridge(
            this,
            lease,
            channel,
            Session.Binding.ConcurrencyLimits.MaximumInFlightCalls,
            cancellationToken);
        if (!_endpointWebSocketBridges.TryAdd(bridge.InvocationId, bridge))
        {
            bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The endpoint WebSocket invocation is already active.");
        }

        return bridge;
    }

    private void HandleEndpointWebSocketModuleMessage(
        OutOfProcessEndpointWebSocketMessage frame)
    {
        if (!_endpointWebSocketBridges.TryGetValue(frame.InvocationId, out var bridge))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The module WebSocket message has no active endpoint route.");
        bridge.AcceptModuleMessage(frame);
    }

    private void HandleEndpointWebSocketModuleReady(
        OutOfProcessEndpointWebSocketReady frame)
    {
        if (!_endpointWebSocketBridges.TryGetValue(frame.InvocationId, out var bridge))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The module WebSocket readiness has no active endpoint route.");
        bridge.AcceptModuleReady(frame);
    }

    private void HandleEndpointWebSocketModuleCompleted(
        OutOfProcessEndpointWebSocketCompleted frame)
    {
        if (!_endpointWebSocketBridges.TryGetValue(frame.InvocationId, out var bridge))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The module WebSocket completion has no active endpoint route.");
        bridge.CompleteModule(frame);
    }

    private async ValueTask DisposeEndpointWebSocketBridgesAsync()
    {
        foreach (var bridge in _endpointWebSocketBridges.Values)
            await bridge.DisposeAsync();
        _endpointWebSocketBridges.Clear();
    }
}
