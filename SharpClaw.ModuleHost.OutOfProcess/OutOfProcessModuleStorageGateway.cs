using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessModuleStorageGateway : IModuleStorageGateway
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;
    private readonly string _moduleId;
    private readonly IReadOnlySet<string> _storageNames;

    public OutOfProcessModuleStorageGateway(
        OutOfProcessModuleCapabilityTransport transport,
        string moduleId,
        IEnumerable<string> storageNames)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _moduleId = moduleId;
        ArgumentNullException.ThrowIfNull(storageNames);
        _storageNames = new HashSet<string>(storageNames, StringComparer.Ordinal);
    }

    public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts()
    {
        var deadline = Deadline();
        var call = _transport.CreateCall(SidecarCapabilityKind.Storage, deadline);
        try
        {
            var request = SidecarStorageCapabilityRequest.ListContracts(
                call,
                _moduleId,
                PayloadType<IReadOnlyList<ModuleStorageContractDescriptor>>(),
                Cancellation(call, deadline),
                deadline);
            var response = _transport.InvokeStorageAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return Deserialize<IReadOnlyList<ModuleStorageContractDescriptor>>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<JsonElement> InvokeAsync(
        string moduleId,
        string storageName,
        string operation,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        ValidateModule(moduleId);
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

    public async Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
        string moduleId,
        string storageName,
        ModuleStorageMutationAndOutboxRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(moduleId);
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
                Payload(request, typeof(ModuleStorageMutationAndOutboxRequest)),
                PayloadType<ModuleStorageMutationAndOutboxResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ModuleStorageMutationAndOutboxResult>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
        string moduleId,
        string storageName,
        ModuleStorageClaimRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(moduleId);
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
                Payload(request, typeof(ModuleStorageClaimRequest)),
                PayloadType<ModuleStorageClaimResult<T>>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ModuleStorageClaimResult<T>>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
        string moduleId,
        string storageName,
        ModuleStorageClaimRenewalRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(moduleId);
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
                Payload(request, typeof(ModuleStorageClaimRenewalRequest)),
                PayloadType<ModuleStorageClaimRenewalResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ModuleStorageClaimRenewalResult>(RequirePayload(response));
        }
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public async Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
        string moduleId,
        string storageName,
        ModuleStorageClaimRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateModule(moduleId);
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
                Payload(request, typeof(ModuleStorageClaimRecoveryRequest)),
                PayloadType<ModuleStorageClaimRecoveryResult>(),
                Cancellation(call, deadline),
                deadline);
            var response = await _transport.InvokeStorageAsync(transportRequest, cancellationToken);
            return Deserialize<ModuleStorageClaimRecoveryResult>(RequirePayload(response));
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

    private static DateTimeOffset Deadline() => DateTimeOffset.UtcNow.AddMinutes(1);

    private void ValidateModule(string moduleId)
    {
        if (!string.Equals(moduleId, _moduleId, StringComparison.Ordinal))
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
            throw new ModuleStorageContractException(response.Error);
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
