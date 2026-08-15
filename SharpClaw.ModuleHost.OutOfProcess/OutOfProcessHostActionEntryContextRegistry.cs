using System.Collections.Concurrent;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Issues one-use host action entry contexts for one authenticated capability binding.</summary>
public sealed class OutOfProcessHostActionEntryContextRegistry
{
    private readonly ConcurrentDictionary<Guid, IssuedContext> _issued = new();
    private readonly ConcurrentDictionary<Guid, IssuedContext> _active = new();
    private SidecarCapabilitySessionBinding? _binding;
    private Func<HostActionEntryContextRequest, HostActionEntryRequestContext>? _issuer;

    /// <summary>Issues a typed context for one host-owned ingress carrier.</summary>
    public HostActionEntryRequestContext Issue<TAction, TResult>(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryIdentity);
        if (traceId == Guid.Empty)
            throw new ArgumentException("The host action context trace ID is required.", nameof(traceId));
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "The host action context idempotency key is required.",
                nameof(idempotencyKey));
        }
        if (ingress == HostActionEntryIngress.CrossModule)
            ArgumentException.ThrowIfNullOrWhiteSpace(secondaryIdentity);

        var binding = Volatile.Read(ref _binding)
            ?? throw new InvalidOperationException(
                "The capability binding must be accepted before issuing a host action context.");
        var issuer = Volatile.Read(ref _issuer)
            ?? throw new InvalidOperationException(
                "The capability session must be ready before issuing a host action context.");
        var now = DateTimeOffset.UtcNow;
        if (deadline <= now || deadline > binding.ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadline),
                "The host action context deadline must be inside the capability binding lifetime.");
        }

        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var inputSchema = descriptor.InputSchema
            ?? throw new ArgumentException(
                "The host action descriptor must declare an input schema.",
                nameof(descriptor));
        var inputSchemaHash = inputSchema.ContentHash
            ?? throw new ArgumentException(
                "The host action descriptor must declare an input schema hash.",
                nameof(descriptor));
        if (string.IsNullOrWhiteSpace(inputSchemaHash))
        {
            throw new ArgumentException(
                "The host action descriptor must declare an input schema hash.",
                nameof(descriptor));
        }
        var contribution = new HostActionEntryContribution(
            new HostActionEntryIngressBinding(
                ingress,
                primaryIdentity,
                ingress == HostActionEntryIngress.CrossModule
                    ? secondaryIdentity
                    : null),
            new HostActionEntryLineage(
                identity.Key,
                identity.Version,
                identity.DescriptorHash,
                identity.InputTypeIdentity,
                inputSchema.Version,
                inputSchemaHash,
                null,
                null));
        var request = new HostActionEntryContextRequest(
            ingress,
            invocationId ?? Guid.NewGuid(),
            binding.RequestId,
            binding.CancellationId,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            binding.ExpiresAt)
        {
            Contribution = contribution,
        };
        var context = issuer(request);
        ArgumentNullException.ThrowIfNull(context);
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

    internal void Bind(
        SidecarCapabilitySessionBinding binding,
        Func<HostActionEntryContextRequest, HostActionEntryRequestContext> issuer,
        bool preserveActiveContexts = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(issuer);
        _issued.Clear();
        if (!preserveActiveContexts)
            _active.Clear();
        Volatile.Write(ref _issuer, issuer);
        Volatile.Write(ref _binding, binding);
    }

    internal void Invalidate(SidecarCapabilitySessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(Volatile.Read(ref _binding), binding))
            return;

        _issued.Clear();
        _active.Clear();
        Volatile.Write(ref _issuer, null);
        Volatile.Write(ref _binding, null);
    }

    internal bool HasPendingContexts => !_issued.IsEmpty;

    internal DateTimeOffset? NextPendingContextExpiration()
    {
        DateTimeOffset? next = null;
        foreach (var pair in _issued)
        {
            var expiresAt = pair.Value.Context.ExpiresAt;
            if (next is null || expiresAt < next.Value)
                next = expiresAt;
        }

        return next;
    }

    internal void SweepExpired(DateTimeOffset now)
    {
        foreach (var pair in _issued)
        {
            if (pair.Value.Context.ExpiresAt <= now)
                _issued.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _active)
        {
            if (pair.Value.Context.ExpiresAt <= now)
                _active.TryRemove(pair.Key, out _);
        }
    }

    internal bool TryBeginCarrier(HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_issued.TryRemove(context.CapabilityId, out var issued))
            return false;
        if (_active.TryAdd(context.CapabilityId, issued))
            return true;

        _issued.TryAdd(context.CapabilityId, issued);
        return false;
    }

    internal void RestorePendingCarrier(HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_active.TryRemove(context.CapabilityId, out var issued))
            _issued.TryAdd(context.CapabilityId, issued);
    }

    internal void CompleteCarrier(Guid capabilityId)
    {
        _issued.TryRemove(capabilityId, out _);
        _active.TryRemove(capabilityId, out _);
    }

    internal static bool MatchesCaller(
        RequestPrincipal expected,
        RequestPrincipal actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (!string.Equals(expected.SubjectId, actual.SubjectId, StringComparison.Ordinal)
            || !string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal)
            || expected.IsAuthenticated != actual.IsAuthenticated)
        {
            return false;
        }

        if (expected.Roles is null || actual.Roles is null)
            return expected.Roles is null && actual.Roles is null;

        return expected.Roles.Count == actual.Roles.Count
            && expected.Roles.All(actual.Roles.Contains);
    }

    internal bool TryConsume<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        if (!_active.TryRemove(context.CapabilityId, out var issued))
            return false;

        var binding = Volatile.Read(ref _binding);
        var lineageMatches = HostActionEntryAuthorityValidator.MatchesDescriptorLineage(
            context.Contribution?.Lineage,
            request.Descriptor);
        return binding is not null
            && context.IsWellFormed(now)
            && issued.RequestId == context.RequestId
            && issued.CancellationId == context.CancellationId
            && HostActionEntryAuthorityValidator.SameContext(issued.Context, context)
            && context.Contribution is not null
            && lineageMatches;
    }

    private sealed record IssuedContext(
        Guid RequestId,
        Guid CancellationId,
        HostActionEntryRequestContext Context);
}
