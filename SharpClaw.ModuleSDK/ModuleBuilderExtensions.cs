using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Adds descriptor-aware action hook registration to the Contracts builder.</summary>
public static class ModuleActionHookBuilderExtensions
{
    /// <summary>Selects one exact typed action descriptor.</summary>
    public static IActionHookRegistrationBuilder For<TAction, TResult>(
        this IActionHookBuilder hooks,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(descriptor);
        return hooks is ModuleActionHookBuilder moduleHooks
            ? moduleHooks.ForDescriptor(descriptor)
            : hooks.For(descriptor.Key);
    }

    /// <summary>Selects one action category with an untyped schema contract.</summary>
    public static IActionHookRegistrationBuilder Category(
        this IActionHookBuilder hooks,
        string category,
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool acceptUnknownNonSensitiveSchemas = false)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return hooks is ModuleActionHookBuilder moduleHooks
            ? moduleHooks.ForCategory(
                category,
                versions,
                inputSchema,
                resultSchema,
                acceptUnknownNonSensitiveSchemas)
            : hooks.Category(category);
    }

    /// <summary>Selects all actions with an untyped schema contract.</summary>
    public static IActionHookRegistrationBuilder AnyAction(
        this IActionHookBuilder hooks,
        ContractVersionRange versions,
        JsonSchemaReference inputSchema,
        JsonSchemaReference resultSchema,
        bool sensitiveApprovalRequired = false,
        bool acceptUnknownNonSensitiveSchemas = true)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return hooks is ModuleActionHookBuilder moduleHooks
            ? moduleHooks.ForWildcard(
                versions,
                inputSchema,
                resultSchema,
                sensitiveApprovalRequired,
                acceptUnknownNonSensitiveSchemas)
            : hooks.AnyAction();
    }

    /// <summary>Registers one typed hook and its requested effects.</summary>
    public static void Use<TInterceptor>(
        this IActionHookRegistrationBuilder registration,
        ActionInterceptionCapabilities requestedCapabilities,
        HookOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration is IModuleActionHookRegistrationSink sink)
        {
            sink.Add(typeof(TInterceptor), false, ordering, requestedCapabilities);
            return;
        }

        registration.Use<TInterceptor>(ordering);
    }

    /// <summary>Registers one untyped hook and its requested effects.</summary>
    public static void UseAny<TInterceptor>(
        this IActionHookRegistrationBuilder registration,
        ActionInterceptionCapabilities requestedCapabilities,
        HookOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration is IModuleActionHookRegistrationSink sink)
        {
            sink.Add(typeof(TInterceptor), true, ordering, requestedCapabilities);
            return;
        }

        registration.UseAny<TInterceptor>(ordering);
    }
}

/// <summary>Adds descriptor-aware event hook registration to the Contracts builder.</summary>
public static class ModuleEventHookBuilderExtensions
{
    /// <summary>Selects one exact typed event descriptor.</summary>
    public static IEventHookRegistrationBuilder For<TEvent>(
        this IEventHookBuilder hooks,
        EventDescriptor<TEvent> descriptor)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(descriptor);
        return hooks is ModuleEventDefinitionBuilder moduleEvents
            ? moduleEvents.ForDescriptor(descriptor)
            : hooks.For(descriptor.Key);
    }

    /// <summary>Selects one event category with an untyped schema contract.</summary>
    public static IEventHookRegistrationBuilder Category(
        this IEventHookBuilder hooks,
        string category,
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool acceptUnknownNonSensitiveSchemas = false)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return hooks is ModuleEventDefinitionBuilder moduleEvents
            ? moduleEvents.ForCategory(
                category,
                versions,
                payloadSchema,
                acceptUnknownNonSensitiveSchemas)
            : hooks.Category(category);
    }

    /// <summary>Selects all events with an untyped schema contract.</summary>
    public static IEventHookRegistrationBuilder AnyEvent(
        this IEventHookBuilder hooks,
        ContractVersionRange versions,
        JsonSchemaReference payloadSchema,
        bool sensitiveApprovalRequired = false,
        bool acceptUnknownNonSensitiveSchemas = true)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        return hooks is ModuleEventDefinitionBuilder moduleEvents
            ? moduleEvents.ForWildcard(
                versions,
                payloadSchema,
                sensitiveApprovalRequired,
                acceptUnknownNonSensitiveSchemas)
            : hooks.AnyEvent();
    }

    /// <summary>Registers one typed event interceptor and its requested effects.</summary>
    public static void Intercept<TInterceptor>(
        this IEventHookRegistrationBuilder registration,
        EventInterceptionCapabilities requestedCapabilities,
        HookOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration is IModuleEventHookRegistrationSink sink)
        {
            sink.Add(
                typeof(TInterceptor),
                false,
                ModuleEventHookKind.Interceptor,
                EventDelivery.Inline,
                ordering,
                requestedCapabilities);
            return;
        }

        registration.Intercept<TInterceptor>(ordering);
    }

    /// <summary>Registers one untyped event interceptor and its requested effects.</summary>
    public static void InterceptAny<TInterceptor>(
        this IEventHookRegistrationBuilder registration,
        EventInterceptionCapabilities requestedCapabilities,
        HookOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration is IModuleEventHookRegistrationSink sink)
        {
            sink.Add(
                typeof(TInterceptor),
                true,
                ModuleEventHookKind.Interceptor,
                EventDelivery.Inline,
                ordering,
                requestedCapabilities);
            return;
        }

        registration.InterceptAny<TInterceptor>(ordering);
    }
}
