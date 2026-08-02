using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed record OutOfProcessProtocolFrame(
    SidecarProtocolMessageKind MessageKind,
    JsonElement Payload,
    bool HasFollowingMessage = false);

internal static class OutOfProcessProtocolCodec
{
    private const int FrameOverheadBytes = 65_536;

    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static async ValueTask SendAsync(
        WebSocket socket,
        ISidecarProtocolMessage message,
        bool hasFollowingMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToElement(message, message.GetType(), JsonOptions);
        var frame = new OutOfProcessProtocolFrame(
            message.MessageKind,
            payload,
            hasFollowingMessage);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public static async ValueTask<(ISidecarProtocolMessage Message, bool HasFollowingMessage)> ReceiveAsync(
        WebSocket socket,
        int maximumProtocolBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (maximumProtocolBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumProtocolBytes));

        var maximumFrameBytes = checked(maximumProtocolBytes + FrameOverheadBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(16_384, maximumFrameBytes));
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var code = string.IsNullOrWhiteSpace(socket.CloseStatusDescription)
                        ? SidecarProtocolErrors.Disconnected
                        : socket.CloseStatusDescription;
                    throw new OutOfProcessProtocolException(
                        code,
                        "The sidecar exchange disconnected before a terminal outcome.");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new OutOfProcessProtocolException(
                        SidecarProtocolErrors.MalformedMessage,
                        "The sidecar exchange requires text frames.");
                }

                if (stream.Length + result.Count > maximumFrameBytes)
                {
                    throw new OutOfProcessProtocolException(
                        SidecarProtocolErrors.ModulePayloadTooLarge,
                        "The sidecar transport frame exceeds its byte limit.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }

            var frame = JsonSerializer.Deserialize<OutOfProcessProtocolFrame>(
                stream.GetBuffer().AsSpan(0, checked((int)stream.Length)),
                JsonOptions)
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar transport frame is empty.");
            var message = DeserializeMessage(frame.MessageKind, frame.Payload);
            return (message, frame.HasFollowingMessage);
        }
        catch (JsonException ex)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                $"The sidecar transport frame is invalid: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ISidecarProtocolMessage DeserializeMessage(
        SidecarProtocolMessageKind kind,
        JsonElement payload) =>
        kind switch
        {
            SidecarProtocolMessageKind.DiscoveryDecision => Read<SidecarDiscoveryDecision>(payload),
            SidecarProtocolMessageKind.NegotiationRequest => Read<SidecarProtocolNegotiationRequest>(payload),
            SidecarProtocolMessageKind.NegotiationResponse => Read<SidecarProtocolNegotiationResponse>(payload),
            SidecarProtocolMessageKind.HookInvokeStart => Read<HookInvokeStart>(payload),
            SidecarProtocolMessageKind.EffectRequest => Read<SidecarEffectRequest>(payload),
            SidecarProtocolMessageKind.EffectAccepted => Read<ContinuationAccepted>(payload),
            SidecarProtocolMessageKind.ContinuationOutcome => Read<ContinuationOutcome>(payload),
            SidecarProtocolMessageKind.HookOutcome => Read<HookOutcome>(payload),
            SidecarProtocolMessageKind.HookCompleted => Read<HookCompleted>(payload),
            SidecarProtocolMessageKind.EventInterceptStart => Read<EventInterceptStart>(payload),
            SidecarProtocolMessageKind.EventInterceptOutcome => Read<EventInterceptOutcome>(payload),
            SidecarProtocolMessageKind.ToolHandlerInvokeStart => Read<SidecarToolHandlerInvokeStart>(payload),
            SidecarProtocolMessageKind.ToolHandlerResult => Read<SidecarToolHandlerResult>(payload),
            SidecarProtocolMessageKind.ToolHandlerCancelled => Read<SidecarToolHandlerCancelled>(payload),
            SidecarProtocolMessageKind.ToolHandlerFailed => Read<SidecarToolHandlerFailed>(payload),
            SidecarProtocolMessageKind.LifecycleHandlerInvokeStart => Read<SidecarLifecycleHandlerInvokeStart>(payload),
            SidecarProtocolMessageKind.LifecycleHandlerResult => Read<SidecarLifecycleHandlerResult>(payload),
            SidecarProtocolMessageKind.LifecycleHandlerCancelled => Read<SidecarLifecycleHandlerCancelled>(payload),
            SidecarProtocolMessageKind.LifecycleHandlerFailed => Read<SidecarLifecycleHandlerFailed>(payload),
            SidecarProtocolMessageKind.EventListenerDelivery => Read<SidecarEventListenerDelivery>(payload),
            SidecarProtocolMessageKind.EventListenerAcknowledgement => Read<SidecarEventListenerAcknowledgement>(payload),
            SidecarProtocolMessageKind.HostTerminalCancellation => Read<SidecarHostTerminalCancellation>(payload),
            SidecarProtocolMessageKind.ResultReplacement => Read<SidecarResultReplacement>(payload),
            SidecarProtocolMessageKind.StreamChunk => Read<SidecarStreamChunk>(payload),
            SidecarProtocolMessageKind.StreamControl => Read<SidecarStreamControl>(payload),
            SidecarProtocolMessageKind.StreamAcknowledgement => Read<SidecarStreamAcknowledgement>(payload),
            SidecarProtocolMessageKind.Error => Read<SidecarProtocolError>(payload),
            _ => throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                $"Message kind '{kind}' is not valid on the duplex exchange."),
        };

    private static T Read<T>(JsonElement payload) where T : ISidecarProtocolMessage =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new OutOfProcessProtocolException(
            SidecarProtocolErrors.MalformedMessage,
            $"Message '{typeof(T).Name}' has no payload.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)(Activator.CreateInstance(
            typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType))
            ?? throw new InvalidOperationException("The read-only set converter could not be created."));
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T>? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options);

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
