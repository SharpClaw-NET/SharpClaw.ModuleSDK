using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Selects compiled action hooks by exact key, category, and wildcard.</summary>
public sealed class ModuleActionDispatchMap
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ModuleActionHook>> _exact;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ModuleActionHook>> _categories;
    private readonly IReadOnlyList<ModuleActionHook> _wildcard;
    private readonly IReadOnlyDictionary<string, int> _rank;

    internal ModuleActionDispatchMap(IReadOnlyList<ModuleActionHook> orderedHooks)
    {
        _rank = new ReadOnlyDictionary<string, int>(orderedHooks
            .Select((hook, index) => (hook.HookId, index))
            .ToDictionary(item => item.HookId, item => item.index, StringComparer.Ordinal));
        _exact = Group(orderedHooks.Where(hook => hook.TargetKind == SidecarHookTargetKind.Exact),
            hook => hook.ActionKey!.Value.Value);
        _categories = Group(
            orderedHooks.Where(hook => hook.TargetKind == SidecarHookTargetKind.Category),
            hook => hook.Category!);
        _wildcard = Array.AsReadOnly(orderedHooks
            .Where(hook => hook.TargetKind == SidecarHookTargetKind.Wildcard)
            .ToArray());
    }

    /// <summary>Gets hooks for one action descriptor in compiled order.</summary>
    public IReadOnlyList<ModuleActionHook> Select(UntypedActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var selected = new List<ModuleActionHook>();
        Add(selected, _exact, descriptor.Key.Value);
        Add(selected, _categories, descriptor.Category);
        selected.AddRange(_wildcard);
        return Array.AsReadOnly(selected
            .DistinctBy(hook => hook.HookId, StringComparer.Ordinal)
            .OrderBy(hook => _rank[hook.HookId])
            .ToArray());
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ModuleActionHook>> Group(
        IEnumerable<ModuleActionHook> hooks,
        Func<ModuleActionHook, string> keySelector) =>
        new ReadOnlyDictionary<string, IReadOnlyList<ModuleActionHook>>(hooks
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ModuleActionHook>)Array.AsReadOnly(group.ToArray()),
                StringComparer.Ordinal));

    private static void Add(
        ICollection<ModuleActionHook> target,
        IReadOnlyDictionary<string, IReadOnlyList<ModuleActionHook>> source,
        string key)
    {
        if (source.TryGetValue(key, out var hooks))
        {
            foreach (var hook in hooks)
                target.Add(hook);
        }
    }
}

/// <summary>Selects compiled event hooks by exact key, category, and wildcard.</summary>
public sealed class ModuleEventDispatchMap
{
    private readonly IReadOnlyList<ModuleEventHook> _orderedHooks;

    internal ModuleEventDispatchMap(IReadOnlyList<ModuleEventHook> orderedHooks)
    {
        _orderedHooks = orderedHooks;
    }

    /// <summary>Gets event interceptors for one descriptor.</summary>
    public IReadOnlyList<ModuleEventHook> SelectInterceptors(UntypedEventDescriptor descriptor) =>
        Select(descriptor, ModuleEventHookKind.Interceptor);

    /// <summary>Gets event listeners for one descriptor and delivery class.</summary>
    public IReadOnlyList<ModuleEventHook> SelectListeners(
        UntypedEventDescriptor descriptor,
        EventDelivery delivery) =>
        Array.AsReadOnly(Select(descriptor, ModuleEventHookKind.Listener)
            .Where(hook => hook.Delivery == delivery)
            .ToArray());

    private IReadOnlyList<ModuleEventHook> Select(
        UntypedEventDescriptor descriptor,
        ModuleEventHookKind kind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Array.AsReadOnly(_orderedHooks
            .Where(hook => hook.Kind == kind && Matches(hook, descriptor))
            .ToArray());
    }

    private static bool Matches(ModuleEventHook hook, UntypedEventDescriptor descriptor) =>
        hook.TargetKind switch
        {
            SidecarHookTargetKind.Exact => hook.EventKey == descriptor.Key,
            SidecarHookTargetKind.Category => string.Equals(
                hook.Category,
                descriptor.Category,
                StringComparison.Ordinal),
            SidecarHookTargetKind.Wildcard => true,
            _ => false,
        };
}

/// <summary>Resolves and invokes module tool handlers without a name switch.</summary>
public sealed class ModuleToolDispatchMap
{
    private readonly IReadOnlyDictionary<string, ModuleToolRegistration> _tools;

    internal ModuleToolDispatchMap(IReadOnlyList<ModuleToolRegistration> tools)
    {
        _tools = new ReadOnlyDictionary<string, ModuleToolRegistration>(tools.ToDictionary(
            tool => tool.Descriptor.Name,
            StringComparer.Ordinal));
    }

    /// <summary>Gets the registration for one tool name.</summary>
    public bool TryGet(string toolName, out ModuleToolRegistration? registration) =>
        _tools.TryGetValue(toolName, out registration);

    /// <summary>Invokes the handler for one tool name.</summary>
    public async ValueTask<ToolResult> InvokeAsync(
        string toolName,
        IServiceProvider services,
        ToolInvocation invocation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        ValidateInvocation(toolName, invocation);

        if (!_tools.TryGetValue(toolName, out var registration))
            throw new KeyNotFoundException($"Tool '{toolName}' is not registered.");

        await using var scope = services.CreateAsyncScope();
        var handler = (IToolHandler)ActivatorUtilities.GetServiceOrCreateInstance(
            scope.ServiceProvider,
            registration.HandlerType);
        return await handler.InvokeAsync(invocation, ct);
    }

    private static void ValidateInvocation(
        string toolName,
        ToolInvocation invocation)
    {
        if (!string.Equals(toolName, invocation.ToolName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The requested tool name does not match the invocation tool name.");

        if (invocation.ConversationId == Guid.Empty)
            throw new InvalidOperationException(
                "The tool conversation identity must not be empty.");

        if (!invocation.IsWellFormed(DateTimeOffset.UtcNow))
            throw new InvalidOperationException(
                "The tool invocation context is not valid for handler execution.");

        var secondaryIdentity = invocation.HostActionContext
            .Contribution!
            .IngressBinding
            .SecondaryIdentity;
        var expectedConversationIdentity = invocation.ConversationId?.ToString("D");
        if (!string.Equals(
                secondaryIdentity,
                expectedConversationIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The tool conversation identity does not match the host action context.");
        }
    }
}
