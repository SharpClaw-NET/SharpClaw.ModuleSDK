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

    /// <summary>Creates a test for one module-owned registered action terminal.</summary>
    public ModuleTestActionEntryBuilder<TAction, TResult> ActionEntry<TAction, TResult>(
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

    internal ValueTask<IActionOutcome<TResult>> RunActionEntryAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        CancellationToken ct)
    {
        var resolved = ResolveActionEntry(descriptor);
        return _actions.RunAsync(
            resolved.Descriptor,
            action,
            CreateActionEntryTerminal<TAction, TResult>(resolved.Entry),
            CoreGraph.ActionSnapshot,
            ct);
    }

    internal ValueTask<TResult> RunRequiredActionEntryAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        CancellationToken ct)
    {
        var resolved = ResolveActionEntry(descriptor);
        return _actions.RunRequiredAsync(
            resolved.Descriptor,
            action,
            CreateActionEntryTerminal<TAction, TResult>(resolved.Entry),
            CoreGraph.ActionSnapshot,
            ct);
    }

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

    private (
        ActionDescriptor<TAction, TResult> Descriptor,
        ModuleActionEntryRegistration Entry) ResolveActionEntry<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var definitions = ModuleGraphs
            .SelectMany(graph => graph.Actions)
            .Where(definition =>
                definition.Descriptor.Key == descriptor.Key
                && definition.Descriptor.Version == descriptor.Version
                && definition.ActionType == typeof(TAction)
                && definition.ResultType == typeof(TResult))
            .ToArray();
        if (definitions.Length != 1
            || definitions[0].TypedDescriptor is not ActionDescriptor<TAction, TResult> compiledDescriptor)
        {
            throw new InvalidOperationException(
                $"Action '{descriptor.Key.Value}' has no unique typed definition in the module test graph.");
        }

        var definition = definitions[0];
        var descriptorHash = HostActionEntryAuthorityValidator.ComputeDescriptorHash(compiledDescriptor);
        var entries = ModuleGraphs
            .SelectMany(graph => graph.ActionEntries)
            .Where(entry =>
                string.Equals(entry.OwnerId, definition.OwnerId, StringComparison.Ordinal)
                && entry.Descriptor.Key == descriptor.Key
                && entry.Descriptor.Version == descriptor.Version
                && string.Equals(entry.Descriptor.DescriptorHash, descriptorHash, StringComparison.Ordinal)
                && entry.ActionType == typeof(TAction)
                && entry.ResultType == typeof(TResult))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidOperationException(
                $"Action '{descriptor.Key.Value}' has no unique registered terminal in the module test graph.");
        }

        return (compiledDescriptor, entries[0]);
    }

    private Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>>
        CreateActionEntryTerminal<TAction, TResult>(ModuleActionEntryRegistration entry) =>
        async (context, cancellationToken) =>
        {
            await using var scope = _services.CreateAsyncScope();
            var terminal = scope.ServiceProvider.GetRequiredService(entry.TerminalType);
            if (terminal is not IHostActionEntryTerminal<TAction, TResult> typedTerminal)
            {
                throw new InvalidOperationException(
                    $"Registered terminal '{entry.TerminalType.FullName}' does not match action '{context.ActionKey.Value}'.");
            }

            return await typedTerminal.InvokeAsync(context, cancellationToken);
        };

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

/// <summary>Builds one module-owned action-entry execution.</summary>
public sealed class ModuleTestActionEntryBuilder<TAction, TResult>(
    SharpClawModuleTestHost host,
    ActionDescriptor<TAction, TResult> descriptor,
    TAction action)
{
    /// <summary>Runs the registered terminal and returns every outcome kind.</summary>
    public ValueTask<IActionOutcome<TResult>> RunAsync(CancellationToken ct = default) =>
        host.RunActionEntryAsync(descriptor, action, ct);

    /// <summary>Runs the registered terminal and requires a completed result.</summary>
    public ValueTask<TResult> RunRequiredAsync(CancellationToken ct = default) =>
        host.RunRequiredActionEntryAsync(descriptor, action, ct);
}
