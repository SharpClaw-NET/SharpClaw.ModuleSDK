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

    private THandler Resolve<THandler>(Type handlerType) where THandler : class =>
        ActivatorUtilities.GetServiceOrCreateInstance(services, handlerType) as THandler
        ?? throw new InvalidOperationException(
            $"Handler '{handlerType.FullName}' does not implement '{typeof(THandler).FullName}'.");

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
