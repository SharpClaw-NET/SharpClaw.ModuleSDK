using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

/// <summary>Runs module actions and events through the production Core graph.</summary>
public sealed class SharpClawModuleTestHost : IAsyncDisposable
{
    private readonly KernelModuleRegistry _registry;
    private readonly KernelActionExecutionContext _execution;
    private readonly KernelActionDispatcher _actions;
    private readonly KernelEventDispatcher _events;
    private bool _started;

    internal SharpClawModuleTestHost(
        KernelModuleRegistry registry,
        KernelGraph coreGraph,
        KernelActionExecutionContext execution,
        IReadOnlyList<ModuleContributionGraph> moduleGraphs)
    {
        _registry = registry;
        _execution = execution;
        CoreGraph = coreGraph;
        ModuleGraphs = moduleGraphs;
        _actions = new KernelActionDispatcher(coreGraph, execution);
        _events = new KernelEventDispatcher(coreGraph);
    }

    /// <summary>Gets the compiled production Core graph.</summary>
    public KernelGraph CoreGraph { get; }

    /// <summary>Gets the matching ModuleSDK graphs.</summary>
    public IReadOnlyList<ModuleContributionGraph> ModuleGraphs { get; }

    /// <summary>Creates a fluent action test.</summary>
    public ModuleTestActionBuilder<TAction, TResult> Action<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action) =>
        new(this, descriptor, action);

    /// <summary>Creates a fluent event test.</summary>
    public ModuleTestEventBuilder<TEvent> Event<TEvent>(
        EventDescriptor<TEvent> descriptor,
        TEvent payload) =>
        new(this, descriptor, payload);

    /// <summary>Starts all modules through Core lifecycle actions.</summary>
    public async ValueTask StartAsync(
        string hostVersion = "module-test-host",
        CancellationToken ct = default)
    {
        if (_started)
            throw new InvalidOperationException("The module test host is already started.");
        await _registry.StartAsync(
            CoreGraph,
            _execution,
            hostVersion,
            _execution.Features,
            ct);
        _started = true;
    }

    /// <summary>Stops all modules through Core lifecycle actions.</summary>
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (!_started)
            return;
        await _registry.StopAsync(_execution, ct);
        _started = false;
    }

    internal ValueTask<IActionOutcome<TResult>> RunActionAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct) =>
        _actions.RunAsync(descriptor, action, terminal, CoreGraph.ActionSnapshot, ct);

    internal ValueTask<TResult> RunRequiredActionAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken ct) =>
        _actions.RunRequiredAsync(descriptor, action, terminal, CoreGraph.ActionSnapshot, ct);

    internal ValueTask<IEventInterception<TEvent>> DispatchEventAsync<TEvent>(
        EventDescriptor<TEvent> descriptor,
        TEvent payload,
        CancellationToken ct) =>
        _events.DispatchAsync(
            descriptor,
            payload,
            CoreGraph.ActionSnapshot,
            _execution.Caller,
            _execution.Features,
            ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}

/// <summary>Builds one action execution for a module test.</summary>
public sealed class ModuleTestActionBuilder<TAction, TResult>(
    SharpClawModuleTestHost host,
    ActionDescriptor<TAction, TResult> descriptor,
    TAction action)
{
    private Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>>? _terminal;

    /// <summary>Sets the guarded terminal implementation.</summary>
    public ModuleTestActionBuilder<TAction, TResult> WithTerminal(
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        return this;
    }

    /// <summary>Runs the action and returns every outcome kind.</summary>
    public ValueTask<IActionOutcome<TResult>> RunAsync(CancellationToken ct = default) =>
        host.RunActionAsync(descriptor, action, RequiredTerminal(), ct);

    /// <summary>Runs the action and requires a completed result.</summary>
    public ValueTask<TResult> RunRequiredAsync(CancellationToken ct = default) =>
        host.RunRequiredActionAsync(descriptor, action, RequiredTerminal(), ct);

    private Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> RequiredTerminal() =>
        _terminal ?? throw new InvalidOperationException("The action test requires a terminal implementation.");
}

/// <summary>Builds one event dispatch for a module test.</summary>
public sealed class ModuleTestEventBuilder<TEvent>(
    SharpClawModuleTestHost host,
    EventDescriptor<TEvent> descriptor,
    TEvent payload)
{
    /// <summary>Dispatches the event through the compiled Core graph.</summary>
    public ValueTask<IEventInterception<TEvent>> DispatchAsync(CancellationToken ct = default) =>
        host.DispatchEventAsync(descriptor, payload, ct);
}
