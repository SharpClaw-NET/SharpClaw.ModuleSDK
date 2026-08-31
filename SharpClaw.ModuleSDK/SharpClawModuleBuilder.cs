using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

internal sealed record PendingActionHook(
    SidecarHookTargetKind TargetKind,
    SharpClawActionKey? ActionKey,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    HookOrdering Ordering,
    ActionInterceptionCapabilities? RequestedCapabilities,
    ContractVersionRange? VersionRange,
    JsonSchemaReference? InputSchema,
    JsonSchemaReference? ResultSchema,
    string? DescriptorCategory,
    int? DescriptorVersion,
    ActionInterceptionCapabilities? DescriptorCapabilities,
    bool? DescriptorContainsSensitiveData,
    bool SensitiveWildcardApprovalRequired,
    bool AcceptUnknownNonSensitiveSchemas);

internal sealed record PendingEventHook(
    SidecarHookTargetKind TargetKind,
    SharpClawEventKey? EventKey,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    ModuleEventHookKind Kind,
    EventDelivery Delivery,
    HookOrdering Ordering,
    EventInterceptionCapabilities? RequestedCapabilities,
    ContractVersionRange? VersionRange,
    JsonSchemaReference? PayloadSchema,
    string? DescriptorCategory,
    int? DescriptorVersion,
    EventInterceptionCapabilities? DescriptorCapabilities,
    bool? DescriptorContainsSensitiveData,
    bool SensitiveWildcardApprovalRequired,
    bool AcceptUnknownNonSensitiveSchemas);

internal sealed class ModuleBuilderState(ModuleIdentity identity)
{
    public ModuleIdentity Identity { get; } = identity;
    public ServiceCollection Services { get; } = [];
    public List<ModuleContractContribution> Contracts { get; } = [];
    public List<ModuleStorageContractDescriptor> Storage { get; } = [];
    public List<ModuleActionDefinition> Actions { get; } = [];
    public List<ModuleActionEntryRegistration> ActionEntries { get; } = [];
    public List<ModuleEventDefinition> Events { get; } = [];
    public List<PendingActionHook> ActionHooks { get; } = [];
    public List<PendingEventHook> EventHooks { get; } = [];
    public List<ModuleToolRegistration> Tools { get; } = [];
    public List<Type> ConversationResolvers { get; } = [];
    public List<ExclusiveRegistration> ConversationResolverRegistrations { get; } = [];
    public List<Type> ProfileResolvers { get; } = [];
    public List<ExclusiveRegistration> ProfileResolverRegistrations { get; } = [];
    public List<Type> ContextContributors { get; } = [];
    public List<ModuleEndpointContribution> Endpoints { get; } = [];
    public List<ModuleCliContribution> CliCommands { get; } = [];
    public List<Type> UiContributions { get; } = [];
}

/// <summary>Records one module's complete candidate contribution graph.</summary>
public sealed class SharpClawModuleBuilder : ISharpClawModuleBuilder
{
    private readonly ModuleBuilderState _state;

    /// <summary>Initializes a builder for one module identity.</summary>
    public SharpClawModuleBuilder(ModuleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _state = new ModuleBuilderState(identity);
        Services = _state.Services;
        Contracts = new ModuleContractBuilder(_state);
        Storage = new ModuleStorageBuilder(_state);
        Actions = new ModuleActionDefinitionBuilder(_state);
        Hooks = new ModuleActionHookBuilder(_state);
        Events = new ModuleEventDefinitionBuilder(_state);
        Tools = new ModuleToolContributionBuilder(_state);
        Chat = new ModuleChatLifecycleBuilder(_state);
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IModuleContractBuilder Contracts { get; }

    /// <inheritdoc />
    public IModuleStorageBuilder Storage { get; }

    /// <inheritdoc />
    public IActionDefinitionBuilder Actions { get; }

    /// <inheritdoc />
    public IActionHookBuilder Hooks { get; }

    /// <inheritdoc />
    public IEventDefinitionBuilder Events { get; }

    /// <inheritdoc />
    public IToolContributionBuilder Tools { get; }

    /// <inheritdoc />
    public IChatLifecycleBuilder Chat { get; }

    internal ModuleBuilderState State => _state;

    internal void AddActionEntry<TAction, TResult, TTerminal>(
        ActionDescriptor<TAction, TResult> descriptor,
        Guid terminalId)
        where TTerminal : class, IHostActionEntryTerminal<TAction, TResult>
    {
        var input = descriptor.InputSchema
            ?? throw new ArgumentException(
                "The action descriptor must declare an input schema.",
                nameof(descriptor));
        var result = descriptor.ResultSchema
            ?? throw new ArgumentException(
                "The action descriptor must declare a result schema.",
                nameof(descriptor));
        var identity = new SidecarActionDescriptorIdentity(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            TypeIdentity(typeof(TAction)),
            input.ContentHash,
            input.Version,
            TypeIdentity(typeof(TResult)),
            result.ContentHash,
            result.Version,
            HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor));
        var invoker = new ModuleActionEntryInvoker<TAction, TResult, TTerminal>(
            identity,
            terminalId);
        _state.ActionEntries.Add(new ModuleActionEntryRegistration(
            _state.Identity.Id,
            identity,
            typeof(TAction),
            typeof(TResult),
            typeof(TTerminal),
            terminalId,
            invoker));
        _state.Services.AddTransient<TTerminal>();
    }

    private static string TypeIdentity(Type type) =>
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
}

/// <summary>Records application contributions for one module.</summary>
public sealed class SharpClawApplicationBuilder : ISharpClawApplicationBuilder
{
    /// <summary>Initializes an application builder that uses the module builder state.</summary>
    public SharpClawApplicationBuilder(SharpClawModuleBuilder moduleBuilder)
    {
        ArgumentNullException.ThrowIfNull(moduleBuilder);
        var state = moduleBuilder.State;
        Endpoints = new ModuleEndpointContributionBuilder(state);
        Cli = new ModuleCliContributionBuilder(state);
        Ui = new ModuleUiContributionBuilder(state);
    }

    /// <inheritdoc />
    public IEndpointContributionBuilder Endpoints { get; }

    /// <inheritdoc />
    public ICliContributionBuilder Cli { get; }

    /// <inheritdoc />
    public IUiContributionBuilder Ui { get; }
}

internal sealed class ModuleContractBuilder(ModuleBuilderState state) : IModuleContractBuilder
{
    public void Export<T>(string contractName, int schemaVersion = 1, int maxBytes = 65_536) =>
        state.Contracts.Add(new ModuleContractContribution(
            state.Identity.Id,
            contractName,
            typeof(T),
            schemaVersion,
            maxBytes,
            IsExport: true,
            Optional: false));

    public void Require<T>(string contractName, int minimumSchemaVersion = 1, bool optional = false) =>
        state.Contracts.Add(new ModuleContractContribution(
            state.Identity.Id,
            contractName,
            typeof(T),
            minimumSchemaVersion,
            0,
            IsExport: false,
            optional));
}

internal sealed class ModuleStorageBuilder(ModuleBuilderState state) : IModuleStorageBuilder
{
    public void Add(ModuleStorageContractDescriptor contract) => state.Storage.Add(contract);
}

internal sealed class ModuleActionDefinitionBuilder(ModuleBuilderState state) : IActionDefinitionBuilder
{
    public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var protocol = descriptor.ProtocolVersionRange ?? ContractVersionRange.Exact(1);
        var safePoints = descriptor.SafePoints ?? [];
        state.Actions.Add(new ModuleActionDefinition(
            state.Identity.Id,
            new UntypedActionDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                descriptor.Capabilities,
                ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(TAction)),
                ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(TResult)),
                descriptor.ContainsSensitiveData)
            {
                ProtocolVersionRange = protocol,
            },
            typeof(TAction),
            typeof(TResult),
            descriptor,
            descriptor.HasIrreversibleEffects,
            descriptor.RepeatPolicy,
            descriptor.ContinuationPolicy,
            descriptor.DefaultTimeout,
            Array.AsReadOnly(safePoints.ToArray())));
    }
}

internal interface IModuleActionHookRegistrationSink
{
    void Add(
        Type handlerType,
        bool isUntyped,
        HookOrdering ordering,
        ActionInterceptionCapabilities? requestedCapabilities);
}

internal sealed class ModuleActionHookBuilder(ModuleBuilderState state) : IActionHookBuilder
{
    public IActionHookRegistrationBuilder For(SharpClawActionKey key) =>
        new ModuleActionHookRegistrationBuilder(state, SidecarHookTargetKind.Exact, key, null);

    public IActionHookRegistrationBuilder Category(string category) =>
        new ModuleActionHookRegistrationBuilder(state, SidecarHookTargetKind.Category, null, category);

    public IActionHookRegistrationBuilder AnyAction() =>
        new ModuleActionHookRegistrationBuilder(state, SidecarHookTargetKind.Wildcard, null, null);

    internal IActionHookRegistrationBuilder ForDescriptor<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor) =>
        new ModuleActionHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Exact,
            descriptor.Key,
            null,
            ContractVersionRange.Exact(descriptor.Version),
            ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(TAction)),
            ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(TResult)),
            descriptor.Category,
            descriptor.Version,
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData);

    internal IActionHookRegistrationBuilder ForCategory(
        string category,
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool acceptUnknownNonSensitiveSchemas) =>
        new ModuleActionHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Category,
            null,
            category,
            versions,
            inputSchema,
            resultSchema,
            category,
            acceptUnknownNonSensitiveSchemas: acceptUnknownNonSensitiveSchemas);

    internal IActionHookRegistrationBuilder ForWildcard(
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool sensitiveApprovalRequired,
        bool acceptUnknownNonSensitiveSchemas) =>
        new ModuleActionHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Wildcard,
            null,
            null,
            versions,
            inputSchema,
            resultSchema,
            null,
            sensitiveApprovalRequired: sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas: acceptUnknownNonSensitiveSchemas);
}

internal sealed class ModuleActionHookRegistrationBuilder(
    ModuleBuilderState state,
    SidecarHookTargetKind targetKind,
    SharpClawActionKey? actionKey,
    string? category,
    ContractVersionRange? versionRange = null,
    JsonSchemaReference? inputSchema = null,
    JsonSchemaReference? resultSchema = null,
    string? descriptorCategory = null,
    int? descriptorVersion = null,
    ActionInterceptionCapabilities? descriptorCapabilities = null,
    bool? descriptorContainsSensitiveData = null,
    bool sensitiveApprovalRequired = false,
    bool acceptUnknownNonSensitiveSchemas = false)
    : IActionHookRegistrationBuilder, IModuleActionHookRegistrationSink
{
    public void Use<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), isUntyped: false, ordering, null);

    public void UseAny<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), isUntyped: true, ordering, null);

    public void Add(
        Type handlerType,
        bool isUntyped,
        HookOrdering ordering,
        ActionInterceptionCapabilities? requestedCapabilities) =>
        state.ActionHooks.Add(new PendingActionHook(
            targetKind,
            actionKey,
            category,
            handlerType,
            isUntyped,
            ordering,
            requestedCapabilities,
            versionRange,
            inputSchema,
            resultSchema,
            descriptorCategory,
            descriptorVersion,
            descriptorCapabilities,
            descriptorContainsSensitiveData,
            sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas));
}

internal sealed class ModuleEventDefinitionBuilder(ModuleBuilderState state)
    : IEventDefinitionBuilder
{
    public void Add<TEvent>(EventDescriptor<TEvent> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var protocol = descriptor.ProtocolVersionRange ?? ContractVersionRange.Exact(1);
        var delivery = descriptor.DeliveryClasses ?? [];
        state.Events.Add(new ModuleEventDefinition(
            state.Identity.Id,
            new UntypedEventDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                descriptor.Capabilities,
                ModuleSchemaIdentity.EventPayload(descriptor.Key, descriptor.Version, typeof(TEvent)),
                descriptor.ContainsSensitiveData)
            {
                ProtocolVersionRange = protocol,
            },
            typeof(TEvent),
            descriptor,
            descriptor.DurableByDefault,
            Array.AsReadOnly(delivery.ToArray())));
    }

    public IEventHookRegistrationBuilder For(SharpClawEventKey key) =>
        new ModuleEventHookRegistrationBuilder(state, SidecarHookTargetKind.Exact, key, null);

    public IEventHookRegistrationBuilder Category(string category) =>
        new ModuleEventHookRegistrationBuilder(state, SidecarHookTargetKind.Category, null, category);

    public IEventHookRegistrationBuilder AnyEvent() =>
        new ModuleEventHookRegistrationBuilder(state, SidecarHookTargetKind.Wildcard, null, null);

    internal IEventHookRegistrationBuilder ForDescriptor<TEvent>(EventDescriptor<TEvent> descriptor) =>
        new ModuleEventHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Exact,
            descriptor.Key,
            null,
            descriptor.ProtocolVersionRange ?? ContractVersionRange.Exact(descriptor.Version),
            ModuleSchemaIdentity.EventPayload(descriptor.Key, descriptor.Version, typeof(TEvent)),
            descriptor.Category,
            descriptor.Version,
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData);

    internal IEventHookRegistrationBuilder ForCategory(
        string category,
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool acceptUnknownNonSensitiveSchemas) =>
        new ModuleEventHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Category,
            null,
            category,
            versions,
            payloadSchema,
            category,
            acceptUnknownNonSensitiveSchemas: acceptUnknownNonSensitiveSchemas);

    internal IEventHookRegistrationBuilder ForWildcard(
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool sensitiveApprovalRequired,
        bool acceptUnknownNonSensitiveSchemas) =>
        new ModuleEventHookRegistrationBuilder(
            state,
            SidecarHookTargetKind.Wildcard,
            null,
            null,
            versions,
            payloadSchema,
            null,
            sensitiveApprovalRequired: sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas: acceptUnknownNonSensitiveSchemas);
}

internal interface IModuleEventHookRegistrationSink
{
    void Add(
        Type handlerType,
        bool isUntyped,
        ModuleEventHookKind kind,
        EventDelivery delivery,
        HookOrdering ordering,
        EventInterceptionCapabilities? requestedCapabilities);
}

internal sealed class ModuleEventHookRegistrationBuilder(
    ModuleBuilderState state,
    SidecarHookTargetKind targetKind,
    SharpClawEventKey? eventKey,
    string? category,
    ContractVersionRange? versionRange = null,
    JsonSchemaReference? payloadSchema = null,
    string? descriptorCategory = null,
    int? descriptorVersion = null,
    EventInterceptionCapabilities? descriptorCapabilities = null,
    bool? descriptorContainsSensitiveData = null,
    bool sensitiveApprovalRequired = false,
    bool acceptUnknownNonSensitiveSchemas = false)
    : IEventHookRegistrationBuilder, IModuleEventHookRegistrationSink
{
    public void Intercept<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), false, ModuleEventHookKind.Interceptor, EventDelivery.Inline, ordering, null);

    public void InterceptAny<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), true, ModuleEventHookKind.Interceptor, EventDelivery.Inline, ordering, null);

    public void Listen<TListener>(EventDelivery delivery, HookOrdering ordering) =>
        Add(typeof(TListener), false, ModuleEventHookKind.Listener, delivery, ordering, EventInterceptionCapabilities.Observe);

    public void ListenAny<TListener>(EventDelivery delivery, HookOrdering ordering) =>
        Add(typeof(TListener), true, ModuleEventHookKind.Listener, delivery, ordering, EventInterceptionCapabilities.Observe);

    public void Add(
        Type handlerType,
        bool isUntyped,
        ModuleEventHookKind kind,
        EventDelivery delivery,
        HookOrdering ordering,
        EventInterceptionCapabilities? requestedCapabilities) =>
        state.EventHooks.Add(new PendingEventHook(
            targetKind,
            eventKey,
            category,
            handlerType,
            isUntyped,
            kind,
            delivery,
            ordering,
            requestedCapabilities,
            versionRange,
            payloadSchema,
            descriptorCategory,
            descriptorVersion,
            descriptorCapabilities,
            descriptorContainsSensitiveData,
            sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas));
}

internal sealed class ModuleToolContributionBuilder(ModuleBuilderState state) : IToolContributionBuilder
{
    public void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        state.Tools.Add(new ModuleToolRegistration(
            state.Identity.Id,
            descriptor,
            typeof(THandler),
            $"{state.Identity.Id}:tool:{descriptor.Name}",
            ModuleSchemaIdentity.ToolInput(descriptor),
            ModuleSchemaIdentity.ToolResult(descriptor)));
    }
}

internal sealed class ModuleChatLifecycleBuilder(ModuleBuilderState state) : IChatLifecycleBuilder
{
    public void UseConversationResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IConversationResolver
    {
        state.ConversationResolvers.Add(typeof(TResolver));
        state.ConversationResolverRegistrations.Add(registration);
    }

    public void UseChatProfileResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IChatProfileResolver
    {
        state.ProfileResolvers.Add(typeof(TResolver));
        state.ProfileResolverRegistrations.Add(registration);
    }

    public void AddContextContributor<TContributor>() where TContributor : IChatContextContributor =>
        state.ContextContributors.Add(typeof(TContributor));
}

internal sealed class ModuleEndpointContributionBuilder(ModuleBuilderState state) : IEndpointContributionBuilder
{
    public void AddHttp<THandler>(ModuleEndpointRouteDescriptor descriptor)
        where THandler : class, IModuleHttpEndpointHandler =>
        state.Endpoints.Add(new ModuleEndpointContribution(descriptor, typeof(THandler)));

    public void AddWebSocket<THandler>(ModuleEndpointRouteDescriptor descriptor)
        where THandler : class, IModuleWebSocketEndpointHandler =>
        state.Endpoints.Add(new ModuleEndpointContribution(descriptor, typeof(THandler)));
}

internal sealed class ModuleCliContributionBuilder(ModuleBuilderState state) : ICliContributionBuilder
{
    public void Add<THandler>(ModuleCliCommandDescriptor descriptor) where THandler : IModuleCliHandler =>
        state.CliCommands.Add(new ModuleCliContribution(descriptor, typeof(THandler)));
}

internal sealed class ModuleUiContributionBuilder(ModuleBuilderState state) : IUiContributionBuilder
{
    public void Add<TContribution>() => state.UiContributions.Add(typeof(TContribution));
}
