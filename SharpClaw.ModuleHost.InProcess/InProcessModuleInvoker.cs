using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.InProcess;

/// <summary>Invokes handlers from one in-process module graph.</summary>
public sealed class InProcessModuleInvoker(
    ModuleContributionGraph graph,
    IServiceProvider services)
{
    /// <summary>Invokes one typed action hook with the host-issued control.</summary>
    public ValueTask<IActionOutcome<TResult>> InvokeActionAsync<TAction, TResult>(
        ModuleActionHook hook,
        ActionContext<TAction> context,
        IActionControl<TAction, TResult> control,
        CancellationToken ct)
    {
        RequireHook(hook, isUntyped: false);
        return Resolve<IActionInterceptor<TAction, TResult>>(hook.HandlerType)
            .InvokeAsync(context, control, ct);
    }

    /// <summary>Invokes one untyped action hook with the host-issued control.</summary>
    public ValueTask<IUntypedActionOutcome> InvokeAnyActionAsync(
        ModuleActionHook hook,
        UntypedActionContext context,
        IUntypedActionControl control,
        CancellationToken ct)
    {
        RequireHook(hook, isUntyped: true);
        return Resolve<IAnyActionInterceptor>(hook.HandlerType).InvokeAsync(context, control, ct);
    }

    /// <summary>Invokes one typed event interceptor with the host-issued control.</summary>
    public ValueTask<IEventInterception<TEvent>> InvokeEventAsync<TEvent>(
        ModuleEventHook hook,
        EventContext<TEvent> context,
        IEventControl<TEvent> control,
        CancellationToken ct)
    {
        RequireEventHook(hook, ModuleEventHookKind.Interceptor, isUntyped: false);
        return Resolve<IEventInterceptor<TEvent>>(hook.HandlerType).InterceptAsync(context, control, ct);
    }

    /// <summary>Invokes one untyped event interceptor with the host-issued control.</summary>
    public ValueTask<IUntypedEventInterception> InvokeAnyEventAsync(
        ModuleEventHook hook,
        UntypedEventContext context,
        IUntypedEventControl control,
        CancellationToken ct)
    {
        RequireEventHook(hook, ModuleEventHookKind.Interceptor, isUntyped: true);
        return Resolve<IAnyEventInterceptor>(hook.HandlerType).InterceptAsync(context, control, ct);
    }

    /// <summary>Invokes one typed event listener.</summary>
    public ValueTask InvokeEventListenerAsync<TEvent>(
        ModuleEventHook hook,
        EventEnvelope<TEvent> evt,
        CancellationToken ct)
    {
        RequireEventHook(hook, ModuleEventHookKind.Listener, isUntyped: false);
        return Resolve<IEventListener<TEvent>>(hook.HandlerType).OnEventAsync(evt, ct);
    }

    /// <summary>Invokes one untyped event listener.</summary>
    public ValueTask InvokeAnyEventListenerAsync(
        ModuleEventHook hook,
        UntypedEventEnvelope evt,
        CancellationToken ct)
    {
        RequireEventHook(hook, ModuleEventHookKind.Listener, isUntyped: true);
        return Resolve<IAnyEventListener>(hook.HandlerType).OnEventAsync(evt, ct);
    }

    /// <summary>Invokes one registered tool.</summary>
    public ValueTask<ToolResult> InvokeToolAsync(
        string toolName,
        ToolInvocation invocation,
        CancellationToken ct) =>
        graph.ToolDispatch.InvokeAsync(toolName, services, invocation, ct);

    /// <summary>Invokes one declared HTTP endpoint with host-owned action authority.</summary>
    public async ValueTask<ModuleHttpEndpointResponse> InvokeHttpEndpointAsync(
        HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        ct.ThrowIfCancellationRequested();
        var endpoint = RequireEndpoint(request, HostEndpointTransport.Http);

        await using var scope = services.CreateAsyncScope();
        var handler = Resolve<IModuleHttpEndpointHandler>(
            scope.ServiceProvider,
            endpoint.HandlerType);
        var response = await handler.InvokeAsync(request, hostActionEntry, ct);
        if (!response.IsWellFormed)
            throw new InvalidOperationException("The endpoint handler returned an invalid response.");
        return response;
    }

    /// <summary>Invokes one declared WebSocket endpoint with host-owned action authority.</summary>
    public async ValueTask InvokeWebSocketEndpointAsync(
        HostEndpointRouteRequest request,
        IModuleWebSocketChannel channel,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        ct.ThrowIfCancellationRequested();
        var endpoint = RequireEndpoint(request, HostEndpointTransport.WebSocket);

        await using var scope = services.CreateAsyncScope();
        var handler = Resolve<IModuleWebSocketEndpointHandler>(
            scope.ServiceProvider,
            endpoint.HandlerType);
        await handler.InvokeAsync(request, channel, hostActionEntry, ct);
    }

    private THandler Resolve<THandler>(Type handlerType) where THandler : class =>
        Resolve<THandler>(services, handlerType);

    private static THandler Resolve<THandler>(
        IServiceProvider serviceProvider,
        Type handlerType)
        where THandler : class =>
        ActivatorUtilities.GetServiceOrCreateInstance(serviceProvider, handlerType) as THandler
        ?? throw new InvalidOperationException(
            $"Handler '{handlerType.FullName}' does not implement '{typeof(THandler).FullName}'.");

    private ModuleEndpointContribution RequireEndpoint(
        HostEndpointRouteRequest request,
        HostEndpointTransport transport)
    {
        if (!request.IsWellFormed(DateTimeOffset.UtcNow) || request.Route.Transport != transport)
            throw new InvalidOperationException("The endpoint route request is invalid.");

        return graph.Application.Endpoints.SingleOrDefault(endpoint =>
                endpoint.Descriptor.ToRouteIdentity() == request.Route)
            ?? throw new InvalidOperationException(
                $"Endpoint route '{request.Route.Method} {request.Route.Path}' is not declared.");
    }

    private static void RequireHook(ModuleActionHook hook, bool isUntyped)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (hook.IsUntyped != isUntyped)
            throw new InvalidOperationException($"Action hook '{hook.HookId}' uses a different payload mode.");
    }

    private static void RequireEventHook(
        ModuleEventHook hook,
        ModuleEventHookKind kind,
        bool isUntyped)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (hook.Kind != kind || hook.IsUntyped != isUntyped)
            throw new InvalidOperationException($"Event hook '{hook.HookId}' uses a different handler mode.");
    }
}
