using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal static class OutOfProcessCapabilityFrameKind
{
    public const string Bind = "bind";
    public const string BindAccepted = "bind_accepted";
    public const string ActionRequest = "action_request";
    public const string ActionResponse = "action_response";
    public const string ActionTerminalRequest = "action_terminal_request";
    public const string ActionTerminalResponse = "action_terminal_response";
    public const string StorageRequest = "storage_request";
    public const string StorageResponse = "storage_response";
    public const string CapabilityCancellation = "capability_cancellation";
    public const string CapabilityRebind = "capability_rebind";
    public const string CapabilityRebindAccepted = "capability_rebind_accepted";
    public const string Error = "error";
}

internal sealed record OutOfProcessCapabilityFrame(
    string Kind,
    SidecarTransportFrameIdentity PayloadIdentity,
    JsonElement Payload);

internal sealed record OutOfProcessCapabilityCancellation(
    SidecarCapabilityCallIdentity Call,
    SidecarCancellationIdentity Cancellation,
    string Reason,
    DateTimeOffset SentAt);

internal static class OutOfProcessCapabilityWire
{
    private const int FrameOverheadBytes = 65_536;

    public static async ValueTask SendAsync<T>(
        WebSocket socket,
        string kind,
        T payload,
        int maximumBytes,
        SemaphoreSlim sendGate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(sendGate);
        var payloadBytes = SidecarCapabilityTransportCodec.Serialize(payload);
        if (payloadBytes.Length > maximumBytes)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.PayloadTooLarge,
                "The capability payload exceeds its configured byte limit.");
        }

        using var document = JsonDocument.Parse(payloadBytes);
        var canonicalPayloadBytes = SidecarCapabilityTransportCodec.Serialize(
            document.RootElement);
        if (canonicalPayloadBytes.Length > maximumBytes)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.PayloadTooLarge,
                "The canonical capability payload exceeds its configured byte limit.");
        }
        var frame = new OutOfProcessCapabilityFrame(
            kind,
            new SidecarTransportFrameIdentity(
                SidecarCapabilityTransportCodec.ComputeSha256(canonicalPayloadBytes),
                canonicalPayloadBytes.Length),
            document.RootElement.Clone());
        var frameBytes = SidecarCapabilityTransportCodec.Serialize(frame);
        if (frameBytes.Length > checked(maximumBytes + FrameOverheadBytes))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.PayloadTooLarge,
                "The capability frame exceeds its configured byte limit.");
        }

        await sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(
                frameBytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public static async ValueTask<(string Kind, byte[] Payload)> ReceiveAsync(
        WebSocket socket,
        int maximumBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var maximumFrameBytes = checked(maximumBytes + FrameOverheadBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(16_384, maximumFrameBytes));
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Disconnected,
                        "The capability channel disconnected.");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.MalformedMessage,
                        "The capability channel requires text frames.");
                }

                if (stream.Length + result.Count > maximumFrameBytes)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.PayloadTooLarge,
                        "The capability frame exceeds its configured byte limit.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }

            var frameBytes = stream.ToArray();
            var frame = SidecarCapabilityTransportCodec.Deserialize<OutOfProcessCapabilityFrame>(frameBytes);
            if (frame is null || string.IsNullOrWhiteSpace(frame.Kind))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.MalformedMessage,
                    "The capability frame is empty.");
            }

            var payloadBytes = SidecarCapabilityTransportCodec.Serialize(frame.Payload);
            var identityValidation = SidecarCapabilityTransportValidation.ValidateFrame(
                payloadBytes,
                frame.PayloadIdentity,
                maximumBytes);
            if (!identityValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    identityValidation.Code ?? SidecarCapabilityErrors.MalformedMessage,
                    identityValidation.Message ?? "The capability payload was rejected.");
            }

            return (frame.Kind, payloadBytes);
        }
        catch (JsonException ex)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The capability frame is not valid JSON.",
                ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static T Deserialize<T>(byte[] payload) =>
        SidecarCapabilityTransportCodec.Deserialize<T>(payload)
        ?? throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.MalformedMessage,
            $"The capability payload '{typeof(T).Name}' is empty.");
}

internal static class SidecarCapabilityErrors
{
    public const string Disconnected = "sidecar_disconnected";
    public const string MalformedMessage = "sidecar_malformed_message";
    public const string PayloadTooLarge = "sidecar_payload_too_large";
    public const string Unauthorized = "sidecar_unauthorized";
    public const string Unauthenticated = "sidecar_unauthenticated";
    public const string UnsupportedCapability = "sidecar_unsupported_capability";
    public const string UnknownAction = "sidecar_unknown_action";
    public const string UnknownStorage = "sidecar_unknown_storage";
    public const string HostFailure = "sidecar_host_failure";
    public const string ModuleBusy = "sidecar_module_busy";
    public const string Cancelled = "sidecar_cancelled";
    public const string Replay = "sidecar_replay";
}

internal sealed class OutOfProcessCapabilityException : Exception
{
    public OutOfProcessCapabilityException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public string Code { get; }
}

internal static class OutOfProcessCapabilitySecurity
{
    public const string Scheme = "hmac-sha256";
    public const string KeyId = "module-control-token";

    public static string ComputeAuthorizationHash(SidecarHostAuthorization authorization) =>
        SidecarCapabilityTransportCodec.ComputeSha256(
            JsonSerializer.SerializeToUtf8Bytes(
                authorization,
                OutOfProcessProtocolCodec.JsonOptions));

    public static bool ValidateGrant(
        SidecarCapabilityGrant grant,
        SidecarHostAuthorization authorization,
        string graphId,
        string moduleId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(authorization);
        return grant.IssuedAt <= now
            && grant.ExpiresAt > now
            && string.Equals(grant.GraphId, graphId, StringComparison.Ordinal)
            && string.Equals(grant.ModuleId, moduleId, StringComparison.Ordinal)
            && string.Equals(authorization.ModuleId, moduleId, StringComparison.Ordinal)
            && grant.Allows(SidecarCapabilityKind.Action)
            && grant.Allows(SidecarCapabilityKind.Storage)
            && string.Equals(
                grant.AuthorizationHash,
                ComputeAuthorizationHash(authorization),
                StringComparison.Ordinal);
    }

    public static SidecarCapabilitySessionBinding CreateBinding(
        string graphId,
        string moduleId,
        int protocolVersion,
        SidecarCapabilityGrant grant,
        SidecarPayloadLimits payloadLimits,
        string controlToken,
        HostActionEntryRequestContext? hostActionContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = grant.ExpiresAt < issuedAt.AddMinutes(5)
            ? grant.ExpiresAt
            : issuedAt.AddMinutes(5);
        if (expiresAt <= issuedAt)
            throw new ArgumentException("The capability grant has expired.", nameof(grant));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var proof = new SidecarAuthenticationProof(
            Scheme,
            KeyId,
            nonce,
            "pending",
            string.Empty,
            issuedAt,
            expiresAt);
        var binding = new SidecarCapabilitySessionBinding(
            moduleId,
            graphId,
            protocolVersion,
            grant,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            expiresAt,
            payloadLimits,
            new SidecarConcurrencyLimits(32, 8),
            new SidecarSafeFailureIdentity(
                Guid.NewGuid(),
                "sidecar_capability_failed",
                "The sidecar capability call failed.",
                Retryable: true),
            KeyId,
            proof);
        if (hostActionContext is not null)
        {
            var boundHostActionContext = hostActionContext with
            {
                RequestId = binding.RequestId,
            };
            if (hostActionContext.RequestId != Guid.Empty
                || !boundHostActionContext.IsWellFormed(issuedAt)
                || boundHostActionContext.ExpiresAt > expiresAt)
            {
                throw new ArgumentException(
                    "The host action context is not valid for the capability binding.",
                    nameof(hostActionContext));
            }

            binding = binding with
            {
                HostActionContext = boundHostActionContext,
            };
        }

        var bindingHash = SidecarCapabilitySessionValidator.ComputeBindingHash(binding);
        var signature = ComputeAuthenticationSignature(controlToken, proof, bindingHash);
        return binding with
        {
            Authentication = new SidecarAuthenticationProof(
                Scheme,
                KeyId,
                nonce,
                signature,
                bindingHash,
                issuedAt,
                expiresAt),
        };
    }

    public static bool Authenticate(
        SidecarCapabilityAuthenticationAuthority authority,
        string controlToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        var binding = authority.Binding;
        var proof = binding.Authentication;
        if (proof is null)
            return false;
        var hash = SidecarCapabilitySessionValidator.ComputeBindingHash(binding);
        if (!string.Equals(hash, authority.BindingHash, StringComparison.Ordinal)
            || !string.Equals(hash, proof.BindingHash, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = ComputeAuthenticationSignature(controlToken, proof, hash);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(proof.Signature));
    }

    public static string CreateTerminalProof(
        SidecarHostTerminalAuthority authority,
        string controlToken)
    {
        var value = string.Join(
            "|",
            authority.AuthorityId,
            authority.SessionId,
            authority.RequestId,
            authority.CallId,
            authority.CancellationId,
            authority.GraphId,
            authority.ModuleId,
            authority.ActionKey.Value,
            authority.ActionVersion,
            authority.DescriptorHash,
            authority.EffectiveActionContentHash,
            authority.ReceiptId,
            authority.ReceiptContentHash,
            authority.IssuedAt.ToUniversalTime().Ticks,
            authority.ExpiresAt.ToUniversalTime().Ticks,
            authority.Deadline.ToUniversalTime().Ticks);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(controlToken),
            Encoding.UTF8.GetBytes(value)));
    }

    public static string CreateHostActionEntryProof(
        HostActionEntryAuthority authority,
        string controlToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        var value = "host-action-entry|"
            + HostActionEntryAuthorityValidator.ComputeAuthorityHash(authority);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(controlToken),
            Encoding.UTF8.GetBytes(value)));
    }

    public static bool ValidateHostActionEntryProof(
        HostActionEntryAuthority authority,
        string controlToken)
    {
        if (authority is null || string.IsNullOrWhiteSpace(authority.Proof))
            return false;
        var expected = CreateHostActionEntryProof(authority with { Proof = string.Empty }, controlToken);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(authority.Proof));
    }

    private static string ComputeAuthenticationSignature(
        string controlToken,
        SidecarAuthenticationProof proof,
        string bindingHash)
    {
        var value = string.Join(
            "|",
            proof.Scheme,
            proof.KeyId,
            proof.Nonce,
            bindingHash,
            proof.IssuedAt.ToUniversalTime().Ticks,
            proof.ExpiresAt.ToUniversalTime().Ticks);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(controlToken),
            Encoding.UTF8.GetBytes(value)));
    }
}
