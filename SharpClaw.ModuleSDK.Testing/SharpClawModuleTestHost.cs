using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

/// <summary>Runs module actions and events through the production Core graph.</summary>
public sealed class SharpClawModuleTestHost : IAsyncDisposable
{
    private readonly IReadOnlyList<ISharpClawModule> _modules;
    private readonly ServiceProvider _services;
    private readonly KernelActionExecutionContext _execution;
    private readonly KernelActionDispatcher _actions;
    private readonly KernelEventDispatcher _events;
    private bool _started;

    internal SharpClawModuleTestHost(
        IReadOnlyList<ISharpClawModule> modules,
        ServiceProvider services,
        KernelGraph coreGraph,
        KernelActionExecutionContext execution,
        IReadOnlyList<ModuleContributionGraph> moduleGraphs)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _services = services ?? throw new ArgumentNullException(nameof(services));
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
        var started = 0;
        try
        {
            foreach (var module in _modules)
            {
                var startContext = new ServiceStartContext(
                    hostVersion,
                    CoreGraph.ActionSnapshot.ContractHash,
                    _execution.Features);
                var terminalCompleted = false;
                await _actions.RunRequiredAsync(
                    ModuleLifecycleActions.Start,
                    startContext,
                    async (_, cancellationToken) =>
                    {
                        await module.StartAsync(startContext, cancellationToken);
                        terminalCompleted = true;
                        return true;
                    },
                    CoreGraph.ActionSnapshot,
                    ct);
                if (!terminalCompleted)
                {
                    throw new KernelActionExecutionException(
                        "The registration start action did not run its lifecycle terminal.");
                }
                started++;
            }
        }
        catch
        {
            await StopStartedAsync(started, CancellationToken.None);
            throw;
        }
        _started = true;
    }

    /// <summary>Stops all modules through Core lifecycle actions.</summary>
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (!_started)
            return;
        try
        {
            await StopStartedAsync(_modules.Count, ct);
        }
        finally
        {
            _started = false;
        }
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

    private async ValueTask StopStartedAsync(int count, CancellationToken ct)
    {
        Exception? firstFailure = null;
        for (var index = count - 1; index >= 0; index--)
        {
            var module = _modules[index];
            var terminalCompleted = false;
            try
            {
                await _actions.RunRequiredAsync(
                    ModuleLifecycleActions.Stop,
                    module.Identity,
                    async (_, cancellationToken) =>
                    {
                        await module.StopAsync(cancellationToken);
                        terminalCompleted = true;
                        return true;
                    },
                    CoreGraph.ActionSnapshot,
                    ct);
                if (!terminalCompleted)
                {
                    throw new KernelActionExecutionException(
                        "The registration stop action did not run its lifecycle terminal.");
                }
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }
        if (firstFailure is not null)
            throw firstFailure;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await _services.DisposeAsync();
        }
        if (failure is not null)
            throw failure;
    }
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
