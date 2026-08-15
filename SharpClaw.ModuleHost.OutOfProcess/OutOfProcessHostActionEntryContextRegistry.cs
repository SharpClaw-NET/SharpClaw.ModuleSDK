using System.Collections.Concurrent;
using System.Security.Cryptography;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Issues one-use host action entry contexts for one authenticated capability binding.</summary>
public sealed class OutOfProcessHostActionEntryContextRegistry
{
    private readonly ConcurrentDictionary<Guid, IssuedContext> _issued = new();
    private SidecarCapabilitySessionBinding? _binding;

    /// <summary>Issues a typed context for one host-owned ingress carrier.</summary>
    public HostActionEntryRequestContext Issue<TAction, TResult>(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string secondaryIdentity,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        DateTimeOffset deadline,
        Guid? invocationId = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondaryIdentity);

        var binding = Volatile.Read(ref _binding)
            ?? throw new InvalidOperationException(
                "The capability binding must be accepted before issuing a host action context.");
        var now = DateTimeOffset.UtcNow;
        if (deadline <= now || deadline > binding.ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadline),
                "The host action context deadline must be inside the capability binding lifetime.");
        }

        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var payload = OutOfProcessActionDispatcher.Payload(
            action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var contribution = new HostActionEntryContribution(
            new HostActionEntryIngressBinding(ingress, primaryIdentity, secondaryIdentity),
            new HostActionEntryLineage(
                identity.Key,
                identity.Version,
                identity.DescriptorHash,
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.InputSchemaHash,
                payload.ContentHash,
                payload.ByteLength));
        var context = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ingress,
            invocationId ?? Guid.NewGuid(),
            binding.RequestId,
            binding.CancellationId,
            caller,
            features,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = contribution,
        };
        if (!context.IsWellFormed(now))
        {
            throw new InvalidOperationException(
                "The host action context could not be created with valid authority fields.");
        }

        if (!_issued.TryAdd(
                context.CapabilityId,
                new IssuedContext(binding.RequestId, binding.CancellationId, context)))
        {
            throw new InvalidOperationException(
                "The host action context identifier was reused.");
        }

        return context;
    }

    internal void Bind(SidecarCapabilitySessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _issued.Clear();
        Volatile.Write(ref _binding, binding);
    }

    internal bool TryConsume<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        if (!_issued.TryRemove(context.CapabilityId, out var issued))
            return false;

        var binding = Volatile.Read(ref _binding);
        return binding is not null
            && context.IsWellFormed(now)
            && issued.RequestId == binding.RequestId
            && issued.CancellationId == binding.CancellationId
            && context.RequestId == binding.RequestId
            && context.CancellationId == binding.CancellationId
            && HostActionEntryAuthorityValidator.SameContext(issued.Context, context)
            && context.Contribution is not null
            && HostActionEntryAuthorityValidator.MatchesLineage(
                context.Contribution.Lineage,
                request.Descriptor,
                request.Action);
    }

    private sealed record IssuedContext(
        Guid RequestId,
        Guid CancellationId,
        HostActionEntryRequestContext Context);
}
