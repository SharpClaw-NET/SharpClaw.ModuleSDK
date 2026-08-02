using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Creates protocol headers with measured payload authority.</summary>
public static class SidecarMessageHeaderFactory
{
    /// <summary>Creates one message with a stable measured header.</summary>
    public static TMessage CreateMeasured<TMessage>(
        int protocolVersion,
        long sequence,
        DateTimeOffset deadline,
        int maximumPayloadBytes,
        Func<SidecarMessageHeader, TMessage> factory)
        where TMessage : ISidecarProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (protocolVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (maximumPayloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        var measured = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var header = new SidecarMessageHeader(
                protocolVersion,
                sequence,
                deadline,
                new SidecarMessageSizeAuthority(measured, maximumPayloadBytes));
            var message = factory(header);
            var next = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType()).Length;
            if (next > maximumPayloadBytes)
                throw new InvalidOperationException("The sidecar message exceeds its payload authority.");
            if (next == measured)
                return message;
            measured = next;
        }

        throw new InvalidOperationException("The sidecar message size did not become stable.");
    }
}

/// <summary>Builds the published sidecar discovery envelope from a compiled graph.</summary>
public static class SidecarDiscoveryFactory
{
    /// <summary>Creates one measured sidecar discovery message.</summary>
    public static SidecarDiscoveryEnvelope Create(
        ModuleContributionGraph graph,
        int protocolVersion,
        long sequence,
        DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.HostingMode != ModuleHostingMode.OutOfProcess)
            throw new InvalidOperationException("Sidecar discovery requires an out-of-process graph.");
        if (!graph.ProtocolVersionRange.Contains(protocolVersion))
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));

        return SidecarMessageHeaderFactory.CreateMeasured(
            protocolVersion,
            sequence,
            deadline,
            graph.PayloadLimits.ProtocolMessageBytes,
            header => new SidecarDiscoveryEnvelope(
                header,
                graph.Identity.Id,
                graph.ContractHash,
                new SidecarProtocolOffer(
                    graph.ProtocolVersionRange.Minimum,
                    graph.ProtocolVersionRange.Maximum,
                    [SidecarPayloadMode.Typed, SidecarPayloadMode.Untyped],
                    graph.PayloadLimits),
                Array.AsReadOnly(graph.ActionHooks.Select(ToSubscription).ToArray()),
                Array.AsReadOnly(graph.EventHooks.Select(ToSubscription).ToArray()),
                Array.AsReadOnly(graph.Actions.Select(ToDefinition).ToArray()),
                Array.AsReadOnly(graph.Events.Select(ToDefinition).ToArray()),
                Array.AsReadOnly(graph.Tools.Select(ToDefinition).ToArray()),
                LifecycleDefinitions(graph),
                graph.Features));
    }

    private static SidecarActionSubscription ToSubscription(ModuleActionHook hook) =>
        new(
            hook.TargetKind,
            hook.ActionKey,
            hook.Category,
            hook.VersionRange,
            hook.InputSchema,
            hook.ResultSchema,
            hook.RequestedCapabilities,
            hook.PayloadMode,
            hook.Ordering,
            hook.SensitiveWildcardApprovalRequired,
            hook.AcceptUnknownNonSensitiveSchemas);

    private static SidecarEventSubscription ToSubscription(ModuleEventHook hook) =>
        new(
            hook.TargetKind,
            hook.EventKey,
            hook.Category,
            hook.VersionRange,
            hook.PayloadSchema,
            hook.RequestedCapabilities,
            hook.Delivery,
            hook.PayloadMode,
            hook.Ordering,
            hook.SensitiveWildcardApprovalRequired,
            hook.AcceptUnknownNonSensitiveSchemas);

    private static SidecarActionDefinition ToDefinition(ModuleActionDefinition action) =>
        new(
            action.Descriptor.Key,
            action.Descriptor.Version,
            action.Descriptor.Category,
            action.Descriptor.InputSchema,
            action.Descriptor.ResultSchema,
            action.Descriptor.Capabilities,
            action.Descriptor.ContainsSensitiveData,
            action.HasIrreversibleEffects,
            action.RepeatPolicy,
            action.ContinuationPolicy,
            action.SafePoints,
            action.Descriptor.ProtocolVersionRange);

    private static SidecarEventDefinition ToDefinition(ModuleEventDefinition evt) =>
        new(
            evt.Descriptor.Key,
            evt.Descriptor.Version,
            evt.Descriptor.Category,
            evt.Descriptor.PayloadSchema,
            evt.Descriptor.Capabilities,
            evt.Descriptor.ContainsSensitiveData,
            evt.DurableByDefault,
            evt.DeliveryClasses,
            evt.Descriptor.ProtocolVersionRange);

    private static SidecarToolHandlerDefinition ToDefinition(ModuleToolRegistration tool) =>
        new(
            tool.Descriptor.Name,
            tool.HandlerId,
            tool.Descriptor.Description,
            tool.InputSchema,
            tool.ResultSchema,
            SupportsStreaming: false,
            Durable: false,
            RequiresApproval: false);

    private static IReadOnlyList<SidecarLifecycleHandlerDefinition> LifecycleDefinitions(
        ModuleContributionGraph graph)
    {
        var protocol = graph.ProtocolVersionRange;
        var startInput = ModuleSchemaIdentity.ActionInput(
            new SharpClawActionKey("module.start"),
            1,
            typeof(ModuleStartContext));
        return Array.AsReadOnly(new[]
        {
            new SidecarLifecycleHandlerDefinition(
                SidecarLifecycleCallKind.Start,
                $"{graph.Identity.Id}:lifecycle:start",
                startInput,
                null,
                protocol,
                TimeSpan.FromSeconds(30)),
            new SidecarLifecycleHandlerDefinition(
                SidecarLifecycleCallKind.Stop,
                $"{graph.Identity.Id}:lifecycle:stop",
                null,
                null,
                protocol,
                TimeSpan.FromSeconds(30)),
        });
    }
}

/// <summary>Creates exact host grants for one validated sidecar discovery.</summary>
public static class SidecarAuthorizationFactory
{
    /// <summary>Validates discovery and creates immutable action and event grants.</summary>
    public static SidecarHostAuthorization Create(
        SidecarDiscoveryEnvelope discovery,
        SidecarHostDescriptorCatalog hostCatalog)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(hostCatalog);
        var validation = SidecarDiscoveryValidator.Validate(discovery, hostCatalog);
        if (!validation.Accepted)
        {
            throw new SidecarDiscoveryAuthorizationException(
                validation.ErrorCode ?? SidecarProtocolErrors.UnsupportedCapability,
                validation.ErrorMessage ?? "The sidecar discovery was rejected.");
        }

        var actionGrants = discovery.Actions
            .SelectMany(subscription => hostCatalog.Actions
                .Where(descriptor => Matches(subscription, descriptor))
                .Select(descriptor => new ActionCapabilityGrant(
                    descriptor.ActionKey,
                    descriptor.Version,
                    subscription.Capabilities,
                    SensitiveApproved: descriptor.ContainsSensitiveData,
                    AcceptUnknownSchemas: subscription.AcceptUnknownNonSensitiveSchemas
                        && !descriptor.ContainsSensitiveData)))
            .Distinct()
            .ToArray();
        var eventGrants = discovery.Events
            .SelectMany(subscription => hostCatalog.Events
                .Where(descriptor => Matches(subscription, descriptor))
                .Select(descriptor => new EventCapabilityGrant(
                    descriptor.EventKey,
                    descriptor.Version,
                    subscription.Capabilities,
                    SensitiveApproved: descriptor.ContainsSensitiveData,
                    AcceptUnknownSchemas: subscription.AcceptUnknownNonSensitiveSchemas
                        && !descriptor.ContainsSensitiveData)))
            .Distinct()
            .ToArray();
        return new SidecarHostAuthorization(
            discovery.ModuleId,
            Array.AsReadOnly(actionGrants),
            Array.AsReadOnly(eventGrants),
            hostCatalog.SensitiveWildcardApproval);
    }

    private static bool Matches(
        SidecarActionSubscription subscription,
        SidecarHostActionDescriptor descriptor) =>
        subscription.VersionRange.Contains(descriptor.Version)
        && subscription.TargetKind switch
        {
            SidecarHookTargetKind.Exact => subscription.ActionKey == descriptor.ActionKey,
            SidecarHookTargetKind.Category => string.Equals(
                subscription.Category,
                descriptor.Category,
                StringComparison.Ordinal),
            SidecarHookTargetKind.Wildcard => true,
            _ => false,
        };

    private static bool Matches(
        SidecarEventSubscription subscription,
        SidecarHostEventDescriptor descriptor) =>
        subscription.VersionRange.Contains(descriptor.Version)
        && subscription.TargetKind switch
        {
            SidecarHookTargetKind.Exact => subscription.EventKey == descriptor.EventKey,
            SidecarHookTargetKind.Category => string.Equals(
                subscription.Category,
                descriptor.Category,
                StringComparison.Ordinal),
            SidecarHookTargetKind.Wildcard => true,
            _ => false,
        };
}

/// <summary>Reports a rejected sidecar discovery authorization.</summary>
public sealed class SidecarDiscoveryAuthorizationException : Exception
{
    /// <summary>Initializes one discovery authorization error.</summary>
    public SidecarDiscoveryAuthorizationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the stable discovery error code.</summary>
    public string Code { get; }
}

/// <summary>Adds discovery creation to a compiled module graph.</summary>
public static class ModuleContributionGraphSidecarExtensions
{
    /// <summary>Creates one measured discovery envelope.</summary>
    public static SidecarDiscoveryEnvelope CreateSidecarDiscovery(
        this ModuleContributionGraph graph,
        int protocolVersion,
        long sequence,
        DateTimeOffset deadline) =>
        SidecarDiscoveryFactory.Create(graph, protocolVersion, sequence, deadline);
}
