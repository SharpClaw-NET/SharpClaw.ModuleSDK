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
            {
                throw new SidecarProtocolException(
                    SidecarProtocolErrors.ModulePayloadTooLarge,
                    "The sidecar message exceeds its payload authority.");
            }
            if (next == measured)
                return message;
            measured = next;
        }

        throw new SidecarProtocolException(
            SidecarProtocolErrors.MalformedMessage,
            "The sidecar message size did not become stable.");
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
            header => CreateEnvelope(graph, header));
    }

    /// <summary>Creates a measured discovery document with application metadata.</summary>
    public static SidecarDiscoveryDocument CreateDocument(
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
            header =>
            {
                var discovery = CreateEnvelope(graph, header);
                return new SidecarDiscoveryDocument(
                    discovery.Header,
                    discovery.ModuleId,
                    discovery.ContractHash,
                    discovery.Protocol,
                    discovery.Actions,
                    discovery.Events,
                    discovery.ActionDefinitions,
                    discovery.EventDefinitions,
                    discovery.ToolHandlers,
                    discovery.LifecycleHandlers,
                    discovery.Features,
                    graph.Storage,
                    graph.CreateSidecarApplicationDiscovery());
            });
    }

    private static SidecarDiscoveryEnvelope CreateEnvelope(
        ModuleContributionGraph graph,
        SidecarMessageHeader header) =>
        new(
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
            graph.Features);

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
            hook.Kind == ModuleEventHookKind.Interceptor
                ? SidecarEventSubscriptionKind.Interceptor
                : SidecarEventSubscriptionKind.Listener,
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
            action.DefaultTimeout,
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
            tool.Descriptor.ParametersSchema.Clone(),
            tool.Descriptor.Version,
            tool.Descriptor.ContainsSensitiveData,
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
        if (!validation.Accepted
            && !string.Equals(
                validation.ErrorCode,
                SidecarProtocolErrors.UnknownHostDescriptor,
                StringComparison.Ordinal))
        {
            throw new SidecarDiscoveryAuthorizationException(
                validation.ErrorCode ?? SidecarProtocolErrors.UnsupportedCapability,
                validation.ErrorMessage ?? "The sidecar discovery was rejected.");
        }

        ValidateUniqueSubscriptions(discovery);
        var selfActions = FindSelfOwnedActions(discovery);
        var selfEvents = FindSelfOwnedEvents(discovery);
        if (!validation.Accepted
            && !OnlyUnknownSubscriptionsAreSelfOwned(
                discovery,
                hostCatalog,
                selfActions,
                selfEvents))
        {
            throw new SidecarDiscoveryAuthorizationException(
                validation.ErrorCode ?? SidecarProtocolErrors.UnsupportedCapability,
                validation.ErrorMessage ?? "The sidecar discovery was rejected.");
        }

        ValidateSelfOwnedActionSubscriptions(
            selfActions,
            hostCatalog.NegotiatedProtocolVersion);
        ValidateSelfOwnedEventSubscriptions(
            selfEvents,
            hostCatalog.NegotiatedProtocolVersion);
        var validationDiscovery = RemoveSelfOwnedSubscriptions(
            discovery,
            selfActions,
            selfEvents);
        var hostValidation = SidecarDiscoveryValidator.Validate(
            validationDiscovery,
            hostCatalog);
        if (!hostValidation.Accepted)
        {
            throw new SidecarDiscoveryAuthorizationException(
                hostValidation.ErrorCode ?? SidecarProtocolErrors.UnsupportedCapability,
                hostValidation.ErrorMessage ?? "The sidecar discovery was rejected.");
        }

        var actionGrants = selfActions
            .Select(self => new ActionCapabilityGrant(
                self.Definition.ActionKey,
                self.Definition.Version,
                self.Subscription.Capabilities,
                SensitiveApproved: self.Definition.ContainsSensitiveData,
                AcceptUnknownSchemas: self.Subscription.AcceptUnknownNonSensitiveSchemas
                    && !self.Definition.ContainsSensitiveData))
            .Concat(validationDiscovery.Actions
            .SelectMany(subscription => hostCatalog.Actions
                .Where(descriptor => Matches(subscription, descriptor))
                .Select(descriptor => new ActionCapabilityGrant(
                    descriptor.ActionKey,
                    descriptor.Version,
                    subscription.Capabilities,
                    SensitiveApproved: descriptor.ContainsSensitiveData,
                    AcceptUnknownSchemas: subscription.AcceptUnknownNonSensitiveSchemas
                        && !descriptor.ContainsSensitiveData))))
            .Distinct()
            .ToArray();
        var eventGrants = selfEvents
            .Select(self => new EventCapabilityGrant(
                self.Definition.EventKey,
                self.Definition.Version,
                self.Subscription.Capabilities,
                SensitiveApproved: self.Definition.ContainsSensitiveData,
                AcceptUnknownSchemas: self.Subscription.AcceptUnknownNonSensitiveSchemas
                    && !self.Definition.ContainsSensitiveData))
            .Concat(validationDiscovery.Events
            .SelectMany(subscription => hostCatalog.Events
                .Where(descriptor => Matches(subscription, descriptor))
                .Select(descriptor => new EventCapabilityGrant(
                    descriptor.EventKey,
                    descriptor.Version,
                    subscription.Capabilities,
                    SensitiveApproved: descriptor.ContainsSensitiveData,
                    AcceptUnknownSchemas: subscription.AcceptUnknownNonSensitiveSchemas
                        && !descriptor.ContainsSensitiveData))))
            .Distinct()
            .ToArray();
        return new SidecarHostAuthorization(
            discovery.ModuleId,
            Array.AsReadOnly(actionGrants),
            Array.AsReadOnly(eventGrants),
            hostCatalog.SensitiveWildcardApproval);
    }

    private static IReadOnlyList<SelfOwnedAction> FindSelfOwnedActions(
        SidecarDiscoveryEnvelope discovery) => discovery.Actions
        .Where(subscription =>
            subscription.TargetKind == SidecarHookTargetKind.Exact
            && subscription.ActionKey is not null)
        .Select(subscription =>
        {
            var definition = discovery.ActionDefinitions.SingleOrDefault(item =>
                item.ActionKey == subscription.ActionKey!.Value);
            return definition is null
                ? null
                : new SelfOwnedAction(subscription, definition);
        })
        .Where(value => value is not null)
        .Select(value => value!)
        .ToArray();

    private static IReadOnlyList<SelfOwnedEvent> FindSelfOwnedEvents(
        SidecarDiscoveryEnvelope discovery) => discovery.Events
        .Where(subscription =>
            subscription.TargetKind == SidecarHookTargetKind.Exact
            && subscription.EventKey is not null)
        .Select(subscription =>
        {
            var definition = discovery.EventDefinitions.SingleOrDefault(item =>
                item.EventKey == subscription.EventKey!.Value);
            return definition is null
                ? null
                : new SelfOwnedEvent(subscription, definition);
        })
        .Where(value => value is not null)
        .Select(value => value!)
        .ToArray();

    private static SidecarDiscoveryEnvelope RemoveSelfOwnedSubscriptions(
        SidecarDiscoveryEnvelope discovery,
        IReadOnlyList<SelfOwnedAction> selfActions,
        IReadOnlyList<SelfOwnedEvent> selfEvents)
    {
        var selfActionSubscriptions = selfActions
            .Select(value => value.Subscription)
            .ToHashSet();
        var selfEventSubscriptions = selfEvents
            .Select(value => value.Subscription)
            .ToHashSet();
        var selfActionKeys = selfActions
            .Select(value => value.Definition.ActionKey)
            .ToHashSet();
        var selfEventKeys = selfEvents
            .Select(value => value.Definition.EventKey)
            .ToHashSet();
        return discovery with
        {
            Actions = discovery.Actions
                .Where(subscription => !selfActionSubscriptions.Contains(subscription))
                .ToArray(),
            Events = discovery.Events
                .Where(subscription => !selfEventSubscriptions.Contains(subscription))
                .ToArray(),
            ActionDefinitions = discovery.ActionDefinitions
                .Where(definition => !selfActionKeys.Contains(definition.ActionKey))
                .ToArray(),
            EventDefinitions = discovery.EventDefinitions
                .Where(definition => !selfEventKeys.Contains(definition.EventKey))
                .ToArray(),
        };
    }

    private static bool OnlyUnknownSubscriptionsAreSelfOwned(
        SidecarDiscoveryEnvelope discovery,
        SidecarHostDescriptorCatalog hostCatalog,
        IReadOnlyList<SelfOwnedAction> selfActions,
        IReadOnlyList<SelfOwnedEvent> selfEvents)
    {
        var selfActionSubscriptions = selfActions
            .Select(value => value.Subscription)
            .ToHashSet();
        var selfEventSubscriptions = selfEvents
            .Select(value => value.Subscription)
            .ToHashSet();
        var unknownAction = discovery.Actions.Any(subscription =>
            !hostCatalog.Actions.Any(descriptor => Matches(subscription, descriptor))
            && !selfActionSubscriptions.Contains(subscription));
        var unknownEvent = discovery.Events.Any(subscription =>
            !hostCatalog.Events.Any(descriptor => Matches(subscription, descriptor))
            && !selfEventSubscriptions.Contains(subscription));
        return !unknownAction && !unknownEvent;
    }

    private static void ValidateUniqueSubscriptions(SidecarDiscoveryEnvelope discovery)
    {
        var actionSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscription in discovery.Actions)
        {
            var identity = subscription.TargetKind == SidecarHookTargetKind.Exact
                ? $"exact:{subscription.ActionKey?.Value}"
                : $"{subscription.TargetKind}:{subscription.Category}";
            if (!actionSubscriptions.Add(identity))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.DuplicateDescriptor,
                    "The discovery contains duplicate action subscriptions.");
            }
        }

        var eventSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscription in discovery.Events)
        {
            var identity = subscription.TargetKind == SidecarHookTargetKind.Exact
                ? $"exact:{subscription.EventKey?.Value}"
                : $"{subscription.TargetKind}:{subscription.Category}";
            if (!eventSubscriptions.Add(identity))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.DuplicateDescriptor,
                    "The discovery contains duplicate event subscriptions.");
            }
        }
    }

    private static void ValidateSelfOwnedActionSubscriptions(
        IReadOnlyList<SelfOwnedAction> subscriptions,
        int negotiatedProtocolVersion)
    {
        foreach (var self in subscriptions)
        {
            var subscription = self.Subscription;
            var definition = self.Definition;
            if (!definition.ProtocolVersionRange.Contains(negotiatedProtocolVersion))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedVersion,
                    "A self-owned action definition does not support the negotiated protocol version.");
            }
            if (!subscription.VersionRange.Contains(definition.Version))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedVersion,
                    "A self-owned action subscription does not cover its definition version.");
            }
            if (!string.Equals(subscription.Category, definition.Category, StringComparison.Ordinal))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.CategoryMismatch,
                    "A self-owned action subscription category does not match its definition.");
            }
            if (!SameSchema(subscription.InputSchema, definition.InputSchema)
                || !SameSchema(subscription.ResultSchema, definition.ResultSchema))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.SchemaMismatch,
                    "A self-owned action subscription schema does not match its definition.");
            }
            if ((subscription.Capabilities & ~definition.Capabilities) != 0)
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedCapability,
                    "A self-owned action subscription requests an ungranted capability.");
            }
        }
    }

    private static void ValidateSelfOwnedEventSubscriptions(
        IReadOnlyList<SelfOwnedEvent> subscriptions,
        int negotiatedProtocolVersion)
    {
        foreach (var self in subscriptions)
        {
            var subscription = self.Subscription;
            var definition = self.Definition;
            if (!definition.ProtocolVersionRange.Contains(negotiatedProtocolVersion))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedVersion,
                    "A self-owned event definition does not support the negotiated protocol version.");
            }
            if (!subscription.VersionRange.Contains(definition.Version))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedVersion,
                    "A self-owned event subscription does not cover its definition version.");
            }
            if (!string.Equals(subscription.Category, definition.Category, StringComparison.Ordinal))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.CategoryMismatch,
                    "A self-owned event subscription category does not match its definition.");
            }
            if (!SameSchema(subscription.PayloadSchema, definition.PayloadSchema))
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.SchemaMismatch,
                    "A self-owned event subscription schema does not match its definition.");
            }
            if ((subscription.Capabilities & ~definition.Capabilities) != 0)
            {
                throw new SidecarDiscoveryAuthorizationException(
                    SidecarProtocolErrors.UnsupportedCapability,
                    "A self-owned event subscription requests an ungranted capability.");
            }
        }
    }

    private static bool SameSchema(
        JsonSchemaReference left,
        JsonSchemaReference right) =>
        string.Equals(left.ContractName, right.ContractName, StringComparison.Ordinal)
        && left.Version == right.Version
        && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

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

    private sealed record SelfOwnedAction(
        SidecarActionSubscription Subscription,
        SidecarActionDefinition Definition);

    private sealed record SelfOwnedEvent(
        SidecarEventSubscription Subscription,
        SidecarEventDefinition Definition);
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
