using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Describes one module-owned action.</summary>
public sealed record ModuleActionDefinition(
    string OwnerId,
    UntypedActionDescriptor Descriptor,
    Type ActionType,
    Type ResultType,
    object TypedDescriptor,
    bool HasIrreversibleEffects,
    ActionRepeatPolicy RepeatPolicy,
    ActionContinuationPolicy? ContinuationPolicy,
    TimeSpan DefaultTimeout,
    IReadOnlyList<ActionSafePoint> SafePoints);

/// <summary>Describes one module-owned event.</summary>
public sealed record ModuleEventDefinition(
    string OwnerId,
    UntypedEventDescriptor Descriptor,
    Type EventType,
    object TypedDescriptor,
    bool DurableByDefault,
    IReadOnlyList<EventDelivery> DeliveryClasses);

/// <summary>Describes one compiled action hook.</summary>
public sealed record ModuleActionHook(
    string OwnerId,
    SidecarHookTargetKind TargetKind,
    SharpClawActionKey? ActionKey,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    HookOrdering Ordering,
    ActionInterceptionCapabilities RequestedCapabilities,
    ContractVersionRange VersionRange,
    JsonSchemaReference InputSchema,
    JsonSchemaReference ResultSchema,
    bool SensitiveWildcardApprovalRequired,
    bool AcceptUnknownNonSensitiveSchemas)
{
    /// <summary>Gets the stable hook identifier.</summary>
    public string HookId => Ordering.Id;

    /// <summary>Gets the compiled input type for a typed hook.</summary>
    public Type? ActionType { get; init; }

    /// <summary>Gets the compiled result type for a typed hook.</summary>
    public Type? ResultType { get; init; }

    /// <summary>Gets the payload form used by this hook.</summary>
    public SidecarPayloadMode PayloadMode => IsUntyped
        ? SidecarPayloadMode.Untyped
        : SidecarPayloadMode.Typed;
}

/// <summary>Identifies the behavior of one event hook registration.</summary>
public enum ModuleEventHookKind
{
    /// <summary>The handler can change preview event delivery.</summary>
    Interceptor,

    /// <summary>The handler observes an event delivery.</summary>
    Listener,
}

/// <summary>Describes one compiled event hook or listener.</summary>
public sealed record ModuleEventHook(
    string OwnerId,
    SidecarHookTargetKind TargetKind,
    SharpClawEventKey? EventKey,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    ModuleEventHookKind Kind,
    EventDelivery Delivery,
    HookOrdering Ordering,
    EventInterceptionCapabilities RequestedCapabilities,
    ContractVersionRange VersionRange,
    JsonSchemaReference PayloadSchema,
    bool SensitiveWildcardApprovalRequired,
    bool AcceptUnknownNonSensitiveSchemas)
{
    /// <summary>Gets the stable hook identifier.</summary>
    public string HookId => Ordering.Id;

    /// <summary>Gets the payload form used by this hook.</summary>
    public SidecarPayloadMode PayloadMode => IsUntyped
        ? SidecarPayloadMode.Untyped
        : SidecarPayloadMode.Typed;
}

/// <summary>Describes one tool handler and its transport schemas.</summary>
public sealed record ModuleToolRegistration(
    string OwnerId,
    ToolDescriptor Descriptor,
    Type HandlerType,
    string HandlerId,
    JsonSchemaReference InputSchema,
    JsonSchemaReference ResultSchema);

/// <summary>Describes one exported or required module contract.</summary>
public sealed record ModuleContractContribution(
    string OwnerId,
    string ContractName,
    Type ServiceType,
    int SchemaVersion,
    int MaxBytes,
    bool IsExport,
    bool Optional);

/// <summary>Describes one module CLI handler.</summary>
public sealed record ModuleCliContribution(
    CliCommandDescriptor Descriptor,
    Type HandlerType);

/// <summary>Describes one module endpoint route and its handler.</summary>
public sealed record ModuleEndpointContribution(
    EndpointRouteDescriptor Descriptor,
    Type HandlerType);

/// <summary>Contains application-level module contributions.</summary>
public sealed record ModuleApplicationContributions(
    IReadOnlyList<ModuleEndpointContribution> Endpoints,
    IReadOnlyList<ModuleCliContribution> CliCommands,
    IReadOnlyList<Type> UiContributionTypes,
    IReadOnlyList<ModuleActionEntryRegistration> ActionEntries)
{
    /// <summary>Gets an empty contribution set.</summary>
    public static ModuleApplicationContributions Empty { get; } = new([], [], [], []);

    /// <summary>Gets whether the module declares an application contribution.</summary>
    public bool IsEmpty =>
        Endpoints.Count == 0
        && CliCommands.Count == 0
        && UiContributionTypes.Count == 0
        && ActionEntries.Count == 0;
}

/// <summary>Contains chat lifecycle registrations for one module.</summary>
public sealed record ModuleChatContributions(
    Type? ConversationResolver,
    ExclusiveClaim? ConversationResolverRegistration,
    Type? ProfileResolver,
    ExclusiveClaim? ProfileResolverRegistration,
    IReadOnlyList<Type> ContextContributors)
{
    /// <summary>Gets an empty chat contribution set.</summary>
    public static ModuleChatContributions Empty { get; } = new(null, null, null, null, []);
}

/// <summary>Contains the immutable result of one module compilation.</summary>
public sealed class ModuleContributionGraph
{
    internal ModuleContributionGraph(
        ModuleIdentity identity,
        ModuleHostingMode hostingMode,
        IReadOnlyList<ServiceDescriptor> services,
        IReadOnlyList<ModuleContractContribution> contracts,
        IReadOnlyList<ScopedStorageContractDescriptor> storage,
        IReadOnlyList<ModuleActionDefinition> actions,
        IReadOnlyList<ModuleEventDefinition> events,
        IReadOnlyList<ModuleActionHook> actionHooks,
        IReadOnlyList<ModuleEventHook> eventHooks,
        IReadOnlyList<ModuleToolRegistration> tools,
        IReadOnlyList<ModuleActionEntryRegistration> actionEntries,
        ModuleChatContributions chat,
        ModuleApplicationContributions application,
        ModuleActionDispatchMap actionDispatch,
        ModuleEventDispatchMap eventDispatch,
        ModuleToolDispatchMap toolDispatch,
        string contractHash,
        ContractVersionRange protocolVersionRange,
        SidecarPayloadLimits payloadLimits,
        IReadOnlyList<FeatureDescriptor> features)
    {
        Identity = identity;
        HostingMode = hostingMode;
        Services = services;
        Contracts = contracts;
        Storage = storage;
        Actions = actions;
        Events = events;
        ActionHooks = actionHooks;
        EventHooks = eventHooks;
        Tools = tools;
        ActionEntries = actionEntries;
        Chat = chat;
        Application = application;
        ActionDispatch = actionDispatch;
        EventDispatch = eventDispatch;
        ToolDispatch = toolDispatch;
        ContractHash = contractHash;
        ProtocolVersionRange = protocolVersionRange;
        PayloadLimits = payloadLimits;
        Features = features;
    }

    /// <summary>Gets the module identity.</summary>
    public ModuleIdentity Identity { get; }

    /// <summary>Gets the target host mode.</summary>
    public ModuleHostingMode HostingMode { get; }

    /// <summary>Gets copied module service descriptors.</summary>
    public IReadOnlyList<ServiceDescriptor> Services { get; }

    /// <summary>Gets module contract declarations.</summary>
    public IReadOnlyList<ModuleContractContribution> Contracts { get; }

    /// <summary>Gets module storage declarations.</summary>
    public IReadOnlyList<ScopedStorageContractDescriptor> Storage { get; }

    /// <summary>Gets module-owned action definitions.</summary>
    public IReadOnlyList<ModuleActionDefinition> Actions { get; }

    /// <summary>Gets module-owned event definitions.</summary>
    public IReadOnlyList<ModuleEventDefinition> Events { get; }

    /// <summary>Gets action hook registrations.</summary>
    public IReadOnlyList<ModuleActionHook> ActionHooks { get; }

    /// <summary>Gets event hook and listener registrations.</summary>
    public IReadOnlyList<ModuleEventHook> EventHooks { get; }

    /// <summary>Gets tool registrations.</summary>
    public IReadOnlyList<ModuleToolRegistration> Tools { get; }

    /// <summary>Gets module-owned action terminal registrations.</summary>
    public IReadOnlyList<ModuleActionEntryRegistration> ActionEntries { get; }

    /// <summary>Gets chat lifecycle contributions.</summary>
    public ModuleChatContributions Chat { get; }

    /// <summary>Gets application contributions.</summary>
    public ModuleApplicationContributions Application { get; }

    /// <summary>Gets the action dispatch map.</summary>
    public ModuleActionDispatchMap ActionDispatch { get; }

    /// <summary>Gets the event dispatch map.</summary>
    public ModuleEventDispatchMap EventDispatch { get; }

    /// <summary>Gets the tool dispatch map.</summary>
    public ModuleToolDispatchMap ToolDispatch { get; }

    /// <summary>Gets the deterministic contribution contract hash.</summary>
    public string ContractHash { get; }

    /// <summary>Gets supported sidecar protocol versions.</summary>
    public ContractVersionRange ProtocolVersionRange { get; }

    /// <summary>Gets configured sidecar payload limits.</summary>
    public SidecarPayloadLimits PayloadLimits { get; }

    /// <summary>Gets manifest feature declarations.</summary>
    public IReadOnlyList<FeatureDescriptor> Features { get; }
}
