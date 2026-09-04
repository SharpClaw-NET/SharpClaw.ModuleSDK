using System.Net.WebSockets;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed class OutOfProcessProtocolSession
{
    private readonly WebSocket _socket;

    public OutOfProcessProtocolSession(WebSocket socket, SidecarProtocolState state)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(state);
        _socket = socket;
        State = state;
    }

    public SidecarProtocolState State { get; private set; }

    public void Accept(ISidecarProtocolMessage message) => Apply(message);

    public TMessage Create<TMessage>(
        SidecarProtocolMessageKind kind,
        Func<SidecarMessageHeader, TMessage> factory)
        where TMessage : ISidecarProtocolMessage =>
        SidecarMessageHeaderFactory.CreateMeasured(
            State.NegotiatedProtocolVersion,
            checked(State.LastSequence + 1),
            State.Deadline,
            State.HostLimits.MaximumFor(kind),
            factory);

    public async ValueTask SendAsync(
        ISidecarProtocolMessage message,
        bool hasFollowingMessage = false,
        CancellationToken ct = default)
    {
        Apply(message);
        await OutOfProcessProtocolCodec.SendAsync(_socket, message, hasFollowingMessage, ct);
    }

    public async ValueTask<(ISidecarProtocolMessage Message, bool HasFollowingMessage)> ReceiveAsync(
        CancellationToken ct = default)
    {
        var frame = await OutOfProcessProtocolCodec.ReceiveAsync(
            _socket,
            State.HostLimits.ProtocolMessageBytes,
            ct);
        Apply(frame.Message);
        return frame;
    }

    public async ValueTask SendErrorAsync(
        string code,
        string message,
        CancellationToken ct = default)
    {
        var error = Create(
            SidecarProtocolMessageKind.Error,
            header => new SidecarProtocolError(header, code, message));
        await SendAsync(error, ct: ct);
    }

    public async ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string description,
        CancellationToken ct = default)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await _socket.CloseAsync(status, description, ct);
    }

    private void Apply(ISidecarProtocolMessage message)
    {
        var validationMessage = message switch
        {
            SidecarToolHandlerInvokeStart start when start.HostActionContext is not null =>
                start with
                {
                    HostActionContext = OutOfProcessHostActionEntryContextRegistry
                        .WithoutPayloadBinding(start.HostActionContext),
                },
            _ => message,
        };
        var result = SidecarProtocolStateMachine.Validate(
            State,
            validationMessage,
            DateTimeOffset.UtcNow);
        if (!result.Accepted || result.State is null)
        {
            throw new OutOfProcessProtocolException(
                result.ErrorCode ?? SidecarProtocolErrors.MalformedMessage,
                result.ErrorMessage ?? "The sidecar protocol message was rejected.");
        }

        State = result.State;
    }
}
