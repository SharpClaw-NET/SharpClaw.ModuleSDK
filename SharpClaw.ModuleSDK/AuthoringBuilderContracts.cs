using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

public interface IContractBuilder
{
    void Export<T>(string contractName, int schemaVersion = 1, int maxBytes = 65_536);

    void Require<T>(string contractName, int minimumSchemaVersion = 1, bool optional = false);
}

public interface IStorageContractBuilder
{
    void Add(ScopedStorageContractDescriptor contract);
}

public interface IActionDefinitionBuilder
{
    void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor);
}

public interface IEventDefinitionBuilder : IEventHookBuilder
{
    void Add<TEvent>(EventDescriptor<TEvent> descriptor);
}

public interface IActionHookBuilder
{
    IActionHookRegistrationBuilder For(SharpClawActionKey key);

    IActionHookRegistrationBuilder Category(string category);

    IActionHookRegistrationBuilder AnyAction();
}

public interface IActionHookRegistrationBuilder
{
    void Use<TInterceptor>(HookOrdering ordering);

    void UseAny<TInterceptor>(HookOrdering ordering);
}

public interface IEventHookBuilder
{
    IEventHookRegistrationBuilder For(SharpClawEventKey key);

    IEventHookRegistrationBuilder Category(string category);

    IEventHookRegistrationBuilder AnyEvent();
}

public interface IEventHookRegistrationBuilder
{
    void Intercept<TInterceptor>(HookOrdering ordering);

    void InterceptAny<TInterceptor>(HookOrdering ordering);

    void Listen<TListener>(EventDelivery delivery, HookOrdering ordering);

    void ListenAny<TListener>(EventDelivery delivery, HookOrdering ordering);
}

public interface IToolContributionBuilder
{
    void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler;
}

public interface IChatLifecycleBuilder
{
    void UseConversationResolver<TResolver>(ExclusiveClaim claim)
        where TResolver : IConversationResolver;

    void UseChatProfileResolver<TResolver>(ExclusiveClaim claim)
        where TResolver : IChatProfileResolver;

    void AddContextContributor<TContributor>() where TContributor : IChatContextContributor;
}

public interface IEndpointContributionBuilder
{
    void AddHttp<THandler>(EndpointRouteDescriptor descriptor)
        where THandler : class, IHttpEndpointHandler;

    void AddWebSocket<THandler>(EndpointRouteDescriptor descriptor)
        where THandler : class, IWebSocketEndpointHandler;
}

public interface ICliContributionBuilder
{
    void Add<THandler>(CliCommandDescriptor descriptor) where THandler : ICliHandler;
}

public interface IUiContributionBuilder
{
    void Add<TContribution>();
}
