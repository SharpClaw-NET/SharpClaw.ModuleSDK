using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

internal static class SharpClawServiceCollection
{
    public static SharpClawModuleBuilder Require(IServiceCollection services) =>
        services as SharpClawModuleBuilder
        ?? throw new ArgumentException(
            "These registrations require the service collection supplied by SharpClaw.",
            nameof(services));
}

/// <summary>Adds SharpClaw behavior to the same service collection used for normal dependencies.</summary>
public static class SharpClawServiceCollectionExtensions
{
    public static void ExportContract<TService>(
        this IServiceCollection services,
        string contractName,
        int schemaVersion = 1,
        int maxBytes = 65_536) =>
        SharpClawServiceCollection.Require(services)
            .Contracts.Export<TService>(contractName, schemaVersion, maxBytes);

    public static void RequireContract<TService>(
        this IServiceCollection services,
        string contractName,
        int minimumSchemaVersion = 1,
        bool optional = false) =>
        SharpClawServiceCollection.Require(services)
            .Contracts.Require<TService>(contractName, minimumSchemaVersion, optional);

    public static void AddStorage(
        this IServiceCollection services,
        ScopedStorageContractDescriptor descriptor) =>
        SharpClawServiceCollection.Require(services).Storage.Add(descriptor);

    public static ActionRegistration<TAction, TResult> AddAction<TAction, TResult>(
        this IServiceCollection services,
        ActionDescriptor<TAction, TResult> descriptor) =>
        ModuleActionBuilderExtensions.DefineAction(services, descriptor);

    public static void AddEvent<TEvent>(
        this IServiceCollection services,
        EventDescriptor<TEvent> descriptor) =>
        SharpClawServiceCollection.Require(services).Events.Add(descriptor);

    public static IActionHookRegistrationBuilder OnAction(
        this IServiceCollection services,
        SharpClawActionKey key) =>
        SharpClawServiceCollection.Require(services).Hooks.For(key);

    public static IActionHookRegistrationBuilder OnAction<TAction, TResult>(
        this IServiceCollection services,
        ActionDescriptor<TAction, TResult> descriptor) =>
        SharpClawServiceCollection.Require(services).Hooks.For(descriptor);

    public static IActionHookRegistrationBuilder OnActionCategory(
        this IServiceCollection services,
        string category,
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool acceptUnknownNonSensitiveSchemas = false) =>
        SharpClawServiceCollection.Require(services).Hooks.Category(
            category,
            versions,
            inputSchema,
            resultSchema,
            acceptUnknownNonSensitiveSchemas);

    public static IActionHookRegistrationBuilder OnAnyAction(
        this IServiceCollection services,
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool sensitiveApprovalRequired = false,
        bool acceptUnknownNonSensitiveSchemas = true) =>
        SharpClawServiceCollection.Require(services).Hooks.AnyAction(
            versions,
            inputSchema,
            resultSchema,
            sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas);

    public static IEventHookRegistrationBuilder OnEvent(
        this IServiceCollection services,
        SharpClawEventKey key) =>
        SharpClawServiceCollection.Require(services).Events.For(key);

    public static IEventHookRegistrationBuilder OnEvent<TEvent>(
        this IServiceCollection services,
        EventDescriptor<TEvent> descriptor) =>
        SharpClawServiceCollection.Require(services).Events.For(descriptor);

    public static IEventHookRegistrationBuilder OnEventCategory(
        this IServiceCollection services,
        string category,
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool acceptUnknownNonSensitiveSchemas = false) =>
        SharpClawServiceCollection.Require(services).Events.Category(
            category,
            versions,
            payloadSchema,
            acceptUnknownNonSensitiveSchemas);

    public static IEventHookRegistrationBuilder OnAnyEvent(
        this IServiceCollection services,
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool sensitiveApprovalRequired = false,
        bool acceptUnknownNonSensitiveSchemas = true) =>
        SharpClawServiceCollection.Require(services).Events.AnyEvent(
            versions,
            payloadSchema,
            sensitiveApprovalRequired,
            acceptUnknownNonSensitiveSchemas);

    public static void AddTool<THandler>(
        this IServiceCollection services,
        ToolDescriptor descriptor)
        where THandler : class, IToolHandler
    {
        services.TryAddScoped<THandler>();
        SharpClawServiceCollection.Require(services).Tools.Add<THandler>(descriptor);
    }

    public static void UseConversationResolver<TResolver>(
        this IServiceCollection services,
        ExclusiveClaim claim)
        where TResolver : class, IConversationResolver
    {
        services.AddScoped<IConversationResolver, TResolver>();
        SharpClawServiceCollection.Require(services).Chat.UseConversationResolver<TResolver>(claim);
    }

    public static void UseChatProfileResolver<TResolver>(
        this IServiceCollection services,
        ExclusiveClaim claim)
        where TResolver : class, IChatProfileResolver
    {
        services.AddScoped<IChatProfileResolver, TResolver>();
        SharpClawServiceCollection.Require(services).Chat.UseChatProfileResolver<TResolver>(claim);
    }

    public static void UseConversationStore<TStore>(this IServiceCollection services)
        where TStore : class, IConversationStore
    {
        services.AddScoped<IConversationStore, TStore>();
    }

    public static void AddChatContext<TContributor>(this IServiceCollection services)
        where TContributor : class, IChatContextContributor
    {
        services.AddScoped<IChatContextContributor, TContributor>();
        SharpClawServiceCollection.Require(services).Chat.AddContextContributor<TContributor>();
    }

    public static void AddHttpEndpoint<THandler>(
        this IServiceCollection services,
        EndpointRouteDescriptor descriptor)
        where THandler : class, IHttpEndpointHandler
    {
        services.TryAddScoped<THandler>();
        new ModuleEndpointContributionBuilder(SharpClawServiceCollection.Require(services).State)
            .AddHttp<THandler>(descriptor);
    }

    public static void AddWebSocketEndpoint<THandler>(
        this IServiceCollection services,
        EndpointRouteDescriptor descriptor)
        where THandler : class, IWebSocketEndpointHandler
    {
        services.TryAddScoped<THandler>();
        new ModuleEndpointContributionBuilder(SharpClawServiceCollection.Require(services).State)
            .AddWebSocket<THandler>(descriptor);
    }

    public static void AddCliCommand<THandler>(
        this IServiceCollection services,
        CliCommandDescriptor descriptor)
        where THandler : class, ICliHandler
    {
        services.TryAddScoped<THandler>();
        new ModuleCliContributionBuilder(SharpClawServiceCollection.Require(services).State)
            .Add<THandler>(descriptor);
    }

    public static void AddUi<TContribution>(this IServiceCollection services) =>
        new ModuleUiContributionBuilder(SharpClawServiceCollection.Require(services).State)
            .Add<TContribution>();
}
