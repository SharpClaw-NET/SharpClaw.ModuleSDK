using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed class OutOfProcessModuleStorageGateway : IScopedStorageGateway
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;
    private readonly string _moduleId;
    private readonly IReadOnlySet<string> _storageNames;

    public OutOfProcessModuleStorageGateway(
        OutOfProcessModuleCapabilityTransport transport,
        string SourceId,
        IEnumerable<string> storageNames)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceId);
        _moduleId = SourceId;
        ArgumentNullException.ThrowIfNull(storageNames);
        _storageNames = new HashSet<string>(storageNames, StringComparer.Ordinal);
    }

    public IReadOnlyList<ScopedStorageContractDescriptor> ListContracts()
    {
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline);
        try
        {
            var request = SidecarStorageCapabilityRequest.ListContracts(
                call,
                _moduleId,
                PayloadType<IReadOnlyList<ScopedStorageContractDescriptor>>(),
                Cancellation(call, deadline),
                deadline);
            var response = _transport.InvokeStorageAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return Deserialize<IReadOnlyList<ScopedStorageContractDescriptor>>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<JsonElement> InvokeAsync(
        string SourceId,
        string storageName,
        string operation,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        ValidateModule(SourceId);
        ValidateStorage(storageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline, cancellationToken);
        try
        {
            var payload = Payload(
                new OutOfProcessStorageInvokePayload(operation, value),
                typeof(OutOfProcessStorageInvokePayload));
            var request = SidecarStorageCapabilityRequest.Invoke(
                call,
                _moduleId,
                storageName,
                payload,
                PayloadType<JsonElement>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(request, cancellationToken);
            return Deserialize<JsonElement>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ScopedStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
        string SourceId,
        string storageName,
        ScopedStorageMutationAndOutboxRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(SourceId);
        ValidateStorage(storageName);
        ArgumentNullException.ThrowIfNull(request);
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline, cancellationToken);
        try
        {
            var transportRequest = SidecarStorageCapabilityRequest.CommitMutationAndOutbox(
                call,
                _moduleId,
                storageName,
                Payload(request, typeof(ScopedStorageMutationAndOutboxRequest)),
                PayloadType<ScopedStorageMutationAndOutboxResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ScopedStorageMutationAndOutboxResult>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ScopedStorageClaimResult<T>> ClaimAsync<T>(
        string SourceId,
        string storageName,
        ScopedStorageClaimRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(SourceId);
        ValidateStorage(storageName);
        ArgumentNullException.ThrowIfNull(request);
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline, cancellationToken);
        try
        {
            var transportRequest = SidecarStorageCapabilityRequest.Claim(
                call,
                _moduleId,
                storageName,
                Payload(request, typeof(ScopedStorageClaimRequest)),
                PayloadType<ScopedStorageClaimResult<T>>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ScopedStorageClaimResult<T>>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ScopedStorageClaimRenewalResult> RenewClaimAsync(
        string SourceId,
        string storageName,
        ScopedStorageClaimRenewalRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(SourceId);
        ValidateStorage(storageName);
        ArgumentNullException.ThrowIfNull(request);
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline, cancellationToken);
        try
        {
            var transportRequest = SidecarStorageCapabilityRequest.RenewClaim(
                call,
                _moduleId,
                storageName,
                Payload(request, typeof(ScopedStorageClaimRenewalRequest)),
                PayloadType<ScopedStorageClaimRenewalResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ScopedStorageClaimRenewalResult>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ScopedStorageClaimRecoveryResult> RecoverClaimAsync(
        string SourceId,
        string storageName,
        ScopedStorageClaimRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(SourceId);
        ValidateStorage(storageName);
        ArgumentNullException.ThrowIfNull(request);
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline, cancellationToken);
        try
        {
            var transportRequest = SidecarStorageCapabilityRequest.RecoverClaim(
                call,
                _moduleId,
                storageName,
                Payload(request, typeof(ScopedStorageClaimRecoveryRequest)),
                PayloadType<ScopedStorageClaimRecoveryResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ScopedStorageClaimRecoveryResult>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    private SidecarCancellationIdentity Cancellation(
        SidecarCapabilityCallIdentity call,
        DateTimeOffset deadline) =>
        new(
            call.CancellationId,
            SidecarCapabilitySessionValidator.ComputeBindingHash(_transport.Binding),
            deadline);

    private DateTimeOffset Deadline()
    {
        var normalDeadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var activeCarrierDeadline = _transport.ActiveCarrierCall?.Deadline;
        return activeCarrierDeadline is { } deadline && deadline < normalDeadline
            ? deadline
            : normalDeadline;
    }

    private void ValidateModule(string SourceId)
    {
        if (!string.Equals(SourceId, _moduleId, StringComparison.Ordinal))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The storage request identifies a different module.");
    }

    private void ValidateStorage(string storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);
        if (!_storageNames.Contains(storageName))
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                $"The storage contract '{storageName}' is not owned by this module.");
    }

    private static void ThrowIfFailed(SidecarStorageCapabilityResponse response)
    {
        if (response.Error is not null)
            throw new ScopedStorageContractException(response.Error);
        if (!response.Completed)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.HostFailure,
                response.SafeFailure?.Message ?? "The host storage gateway did not complete the request.");
    }

    private static SidecarSerializedPayload RequirePayload(
        SidecarStorageCapabilityResponse response)
    {
        ThrowIfFailed(response);
        return response.ResultPayload
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host storage gateway returned no result payload.");
    }

    private static SidecarSerializedPayload Payload<T>(T value, Type type) =>
        Payload(value, type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

    private static SidecarSerializedPayload Payload<T>(T value, string typeIdentity)
    {
        var bytes = SidecarCapabilityTransportCodec.Serialize(value);
        using var document = JsonDocument.Parse(bytes);
        var canonicalBytes = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        var hash = SidecarCapabilityTransportCodec.ComputeSha256(canonicalBytes);
        return new SidecarSerializedPayload(
            typeIdentity,
            1,
            hash,
            document.RootElement.Clone(),
            canonicalBytes.Length);
    }

    private static SidecarPayloadTypeIdentity PayloadType<T>()
    {
        var type = typeof(T);
        var identity = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        return new SidecarPayloadTypeIdentity(
            identity,
            1,
            SidecarCapabilityTransportCodec.ComputeSha256(
                System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    private static T Deserialize<T>(SidecarSerializedPayload payload) =>
        JsonSerializer.Deserialize<T>(
            payload.Value.GetRawText(),
            SidecarCapabilityTransportCodec.CreateJsonOptions())
        ?? throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.MalformedMessage,
            $"The host returned no '{typeof(T).FullName}' value.");
}
