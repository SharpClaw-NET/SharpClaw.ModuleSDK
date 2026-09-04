using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

/// <summary>Issues one-use host action entry contexts for one authenticated capability binding.</summary>
public sealed class OutOfProcessHostActionEntryContextRegistry
{
    private readonly ConcurrentDictionary<Guid, IssuedContext> _issued = new();
    private readonly ConcurrentDictionary<Guid, IssuedContext> _active = new();
    private readonly ConcurrentDictionary<Guid, byte> _consumed = new();
    private SidecarCapabilitySessionBinding? _binding;
    private Func<HostActionEntryContextRequest, HostActionEntryRequestContext>? _issuer;
    private Func<Func<HostActionEntryRequestContext>, HostActionEntryRequestContext>? _issueCoordinator;

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
        Guid? invocationId = null,
        Guid? parentInvocationId = null,
        int depth = 0,
        int attempt = 1)
    {
        var coordinator = Volatile.Read(ref _issueCoordinator);
        if (coordinator is null)
        {
            return IssueCore(
                ingress,
                primaryIdentity,
                secondaryIdentity,
                descriptor,
                action,
                caller,
                features,
                traceId,
                idempotencyKey,
                deadline,
                invocationId,
                parentInvocationId,
                depth,
                attempt);
        }

        return coordinator(() => IssueCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            descriptor,
            action,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            invocationId,
            parentInvocationId,
            depth,
            attempt));
    }

    internal HostActionEntryRequestContext IssueWithinBinding<TAction, TResult>(
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
        Guid? invocationId = null,
        Guid? parentInvocationId = null,
        int depth = 0,
        int attempt = 1)
    {
        var coordinator = Volatile.Read(ref _issueCoordinator);
        HostActionEntryRequestContext Issue() => IssueCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            descriptor,
            action,
            caller,
            features,
            traceId,
            idempotencyKey,
            BoundToActiveBinding(deadline),
            invocationId,
            parentInvocationId,
            depth,
            attempt);
        return coordinator is null ? Issue() : coordinator(Issue);
    }

    /// <summary>Issues a context from exact discovery metadata and canonical JSON.</summary>
    public HostActionEntryRequestContext Issue(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptor,
        JsonElement action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId = null,
        Guid? parentInvocationId = null,
        int depth = 0,
        int attempt = 1)
    {
        var coordinator = Volatile.Read(ref _issueCoordinator);
        if (coordinator is null)
        {
            return IssueSerializedCore(
                ingress,
                primaryIdentity,
                secondaryIdentity,
                definition,
                descriptor,
                action,
                caller,
                features,
                traceId,
                idempotencyKey,
                deadline,
                invocationId,
                parentInvocationId,
                depth,
                attempt);
        }

        return coordinator(() => IssueSerializedCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            definition,
            descriptor,
            action,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            invocationId,
            parentInvocationId,
            depth,
            attempt));
    }

    internal HostActionEntryRequestContext IssueWithinBinding(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptor,
        JsonElement action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId = null,
        Guid? parentInvocationId = null,
        int depth = 0,
        int attempt = 1)
    {
        var coordinator = Volatile.Read(ref _issueCoordinator);
        HostActionEntryRequestContext Issue() => IssueSerializedCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            definition,
            descriptor,
            action,
            caller,
            features,
            traceId,
            idempotencyKey,
            BoundToActiveBinding(deadline),
            invocationId,
            parentInvocationId,
            depth,
            attempt);
        return coordinator is null ? Issue() : coordinator(Issue);
    }

    private DateTimeOffset BoundToActiveBinding(DateTimeOffset deadline)
    {
        var binding = Volatile.Read(ref _binding)
            ?? throw new InvalidOperationException(
                "The capability binding must be accepted before issuing a host action context.");
        return deadline < binding.ExpiresAt ? deadline : binding.ExpiresAt;
    }

    private HostActionEntryRequestContext IssueCore<TAction, TResult>(
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
        Guid? invocationId,
        Guid? parentInvocationId,
        int depth,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
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
        var payload = OutOfProcessActionDispatcher.Payload(
            action,
            identity.InputTypeIdentity,
            inputSchema.Version);
        return IssueCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            identity,
            inputSchema,
            payload,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            invocationId,
            parentInvocationId,
            depth,
            attempt);
    }

    private HostActionEntryRequestContext IssueSerializedCore(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptor,
        JsonElement action,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId,
        Guid? parentInvocationId,
        int depth,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                descriptor,
                definition))
        {
            throw new ArgumentException(
                "The discovered action definition does not match its transport identity.",
                nameof(descriptor));
        }
        var payload = OutOfProcessActionDispatcher.Payload(
            action,
            descriptor.InputTypeIdentity,
            descriptor.InputSchemaVersion);
        return IssueCore(
            ingress,
            primaryIdentity,
            secondaryIdentity,
            descriptor,
            definition.InputSchema,
            payload,
            caller,
            features,
            traceId,
            idempotencyKey,
            deadline,
            invocationId,
            parentInvocationId,
            depth,
            attempt);
    }

    private HostActionEntryRequestContext IssueCore(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        string? secondaryIdentity,
        SidecarActionDescriptorIdentity identity,
        JsonSchemaReference inputSchema,
        SidecarSerializedPayload payload,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey,
        DateTimeOffset deadline,
        Guid? invocationId,
        Guid? parentInvocationId,
        int depth,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(payload);
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
        if (depth < 0)
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        if (ingress == HostActionEntryIngress.CrossRegistration)
            ArgumentException.ThrowIfNullOrWhiteSpace(secondaryIdentity);
        if (ingress == HostActionEntryIngress.Tool
            && secondaryIdentity is not null
            && (!Guid.TryParseExact(secondaryIdentity, "D", out var conversationId)
                || conversationId == Guid.Empty
                || !string.Equals(
                    secondaryIdentity,
                    conversationId.ToString("D"),
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The tool conversation identity must use canonical D formatting.",
                nameof(secondaryIdentity));
        }

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

        var inputSchemaHash = inputSchema.ContentHash
            ?? throw new ArgumentException(
                "The host action descriptor must declare an input schema hash.",
                nameof(inputSchema));
        if (string.IsNullOrWhiteSpace(inputSchemaHash))
        {
            throw new ArgumentException(
                "The host action descriptor must declare an input schema hash.",
                nameof(inputSchema));
        }
        if (inputSchema.Version != identity.InputSchemaVersion
            || !string.Equals(
                inputSchemaHash,
                identity.InputSchemaHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The host action input schema does not match its descriptor identity.",
                nameof(inputSchema));
        }
        if (!payload.IsValid
            || payload.ByteLength > binding.PayloadLimits.ActionInputBytes)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.PayloadTooLarge,
                "The host action context payload exceeds the configured action input limit.");
        }
        var ingressBinding = new HostActionEntryIngressBinding(
            ingress,
            primaryIdentity,
            ingress is HostActionEntryIngress.CrossRegistration or HostActionEntryIngress.Tool
                ? secondaryIdentity
                : null);
        var payloadBoundLineage = new HostActionEntryLineage(
            identity.Key,
            identity.Version,
            identity.DescriptorHash,
            identity.InputTypeIdentity,
            inputSchema.Version,
            inputSchemaHash,
            payload.ContentHash,
            payload.ByteLength);
        var contribution = new HostActionEntryContribution(
            ingressBinding,
            payloadBoundLineage);
        var contextRequestContribution = new HostActionEntryContribution(
            ingressBinding,
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
            Contribution = contextRequestContribution,
            ParentInvocationId = parentInvocationId,
            Depth = depth,
            Attempt = attempt,
        };
        var context = issuer(request) with
        {
            Contribution = contribution,
        };
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

    /// <summary>Reissues one validated Tool carrier inside this capability binding.</summary>
    public HostActionEntryRequestContext IssueToolCarrier(
        HostActionEntryRequestContext source)
    {
        var coordinator = Volatile.Read(ref _issueCoordinator);
        return coordinator is null
            ? IssueToolCarrierCore(source)
            : coordinator(() => IssueToolCarrierCore(source));
    }

    private HostActionEntryRequestContext IssueToolCarrierCore(
        HostActionEntryRequestContext source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var now = DateTimeOffset.UtcNow;
        var contribution = source.Contribution;
        if (!source.IsWellFormed(now)
            || source.Ingress != HostActionEntryIngress.Tool
            || contribution is null
            || contribution.IngressBinding.Ingress != HostActionEntryIngress.Tool)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The source Tool action context is invalid.");
        }

        var binding = Volatile.Read(ref _binding)
            ?? throw new InvalidOperationException(
                "The capability binding must be accepted before issuing a Tool carrier.");
        var issuer = Volatile.Read(ref _issuer)
            ?? throw new InvalidOperationException(
                "The capability session must be ready before issuing a Tool carrier.");
        if (source.Deadline > binding.ExpiresAt)
        {
            throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Kernel.SidecarCapabilityErrors.Expired,
                "The Tool action deadline exceeds the capability binding lifetime.");
        }

        var lineage = contribution.Lineage;
        var request = new HostActionEntryContextRequest(
            HostActionEntryIngress.Tool,
            source.InvocationId,
            binding.RequestId,
            binding.CancellationId,
            source.Caller,
            source.Features,
            source.TraceId,
            source.IdempotencyKey,
            source.Deadline,
            binding.ExpiresAt)
        {
            Contribution = new HostActionEntryContribution(
                contribution.IngressBinding,
                lineage with
                {
                    PayloadContentHash = null,
                    PayloadByteLength = null,
                }),
            ParentInvocationId = source.ParentInvocationId,
            Depth = source.Depth,
            Attempt = source.Attempt,
        };
        var context = issuer(request) with { Contribution = contribution };
        if (!context.IsWellFormed(now)
            || context.Ingress != HostActionEntryIngress.Tool
            || context.InvocationId != source.InvocationId)
        {
            throw new InvalidOperationException(
                "The capability session did not issue the requested Tool carrier.");
        }

        if (!_issued.TryAdd(
                context.CapabilityId,
                new IssuedContext(binding.RequestId, binding.CancellationId, context)))
        {
            throw new InvalidOperationException("The Tool carrier capability identifier was reused.");
        }

        return context;
    }

    internal void Bind(
        SidecarCapabilitySessionBinding binding,
        Func<HostActionEntryContextRequest, HostActionEntryRequestContext> issuer,
        bool preserveActiveContexts = false,
        Func<Func<HostActionEntryRequestContext>, HostActionEntryRequestContext>? issueCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(issuer);
        _issued.Clear();
        if (!preserveActiveContexts)
        {
            _active.Clear();
            _consumed.Clear();
        }
        else
        {
            foreach (var pair in _active.ToArray())
            {
                var context = pair.Value.Context with
                {
                    RequestId = binding.RequestId,
                    CancellationId = binding.CancellationId,
                };
                _active[pair.Key] = new IssuedContext(
                    binding.RequestId,
                    binding.CancellationId,
                    context);
            }

            foreach (var capabilityId in _consumed.Keys)
            {
                if (!_active.ContainsKey(capabilityId))
                    _consumed.TryRemove(capabilityId, out _);
            }
        }
        Volatile.Write(ref _issuer, issuer);
        Volatile.Write(ref _binding, binding);
        Volatile.Write(ref _issueCoordinator, issueCoordinator);
    }

    internal void Invalidate(SidecarCapabilitySessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(Volatile.Read(ref _binding), binding))
            return;

        _issued.Clear();
        _active.Clear();
        _consumed.Clear();
        Volatile.Write(ref _issuer, null);
        Volatile.Write(ref _binding, null);
        Volatile.Write(ref _issueCoordinator, null);
    }

    internal bool HasPendingContexts => !_issued.IsEmpty;

    internal bool HasActiveContexts => !_active.IsEmpty;

    internal bool IsPending(Guid capabilityId) => _issued.ContainsKey(capabilityId);

    internal bool IsActive(Guid capabilityId) => _active.ContainsKey(capabilityId);

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
            {
                _active.TryRemove(pair.Key, out _);
                _consumed.TryRemove(pair.Key, out _);
            }
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
        {
            _consumed.TryRemove(context.CapabilityId, out _);
            _issued.TryAdd(context.CapabilityId, issued);
        }
    }

    internal void CompleteCarrier(Guid capabilityId)
    {
        _issued.TryRemove(capabilityId, out _);
        _active.TryRemove(capabilityId, out _);
        _consumed.TryRemove(capabilityId, out _);
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

    internal static HostActionEntryRequestContext WithoutPayloadBinding(
        HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Contribution is null || !context.Contribution.Lineage.IsPayloadBound)
            return context;

        return context with
        {
            Contribution = context.Contribution with
            {
                Lineage = context.Contribution.Lineage with
                {
                    PayloadContentHash = null,
                    PayloadByteLength = null,
                },
            },
        };
    }

    internal static HostActionEntryRequestContext BindContributionLineage(
        HostActionEntryRequestContext context,
        SidecarActionDescriptorIdentity descriptor,
        SidecarSerializedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(payload);
        var contribution = context.Contribution
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The application carrier child has no contribution authority.");
        return context with
        {
            Contribution = contribution with
            {
                Lineage = new HostActionEntryLineage(
                    descriptor.Key,
                    descriptor.Version,
                    descriptor.DescriptorHash,
                    descriptor.InputTypeIdentity,
                    descriptor.InputSchemaVersion,
                    descriptor.InputSchemaHash,
                    payload.ContentHash,
                    payload.ByteLength),
            },
        };
    }

    internal bool TryConsume<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context;
        if (!_active.TryGetValue(context.CapabilityId, out var issued)
            || !_consumed.TryAdd(context.CapabilityId, 0))
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

    internal bool TryConsume(
        SidecarActionDescriptorIdentity descriptor,
        SidecarSerializedPayload payload,
        HostActionEntryRequestContext context,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        if (!_active.TryGetValue(context.CapabilityId, out var issued)
            || !_consumed.TryAdd(context.CapabilityId, 0))
        {
            return false;
        }

        var binding = Volatile.Read(ref _binding);
        return binding is not null
            && context.IsWellFormed(now)
            && issued.RequestId == context.RequestId
            && issued.CancellationId == context.CancellationId
            && HostActionEntryAuthorityValidator.SameContext(issued.Context, context)
            && MatchesLineage(context.Contribution?.Lineage, descriptor, payload);
    }

    internal static bool MatchesLineage(
        HostActionEntryLineage? lineage,
        SidecarActionDescriptorIdentity descriptor,
        SidecarSerializedPayload payload) =>
        lineage is not null
        && lineage.ActionKey == descriptor.Key
        && lineage.ActionVersion == descriptor.Version
        && string.Equals(lineage.DescriptorHash, descriptor.DescriptorHash, StringComparison.Ordinal)
        && string.Equals(lineage.InputTypeIdentity, descriptor.InputTypeIdentity, StringComparison.Ordinal)
        && lineage.InputSchemaVersion == descriptor.InputSchemaVersion
        && string.Equals(lineage.InputSchemaHash, descriptor.InputSchemaHash, StringComparison.Ordinal)
        && string.Equals(lineage.PayloadContentHash, payload.ContentHash, StringComparison.Ordinal)
        && lineage.PayloadByteLength == payload.ByteLength;

    private sealed record IssuedContext(
        Guid RequestId,
        Guid CancellationId,
        HostActionEntryRequestContext Context);
}
