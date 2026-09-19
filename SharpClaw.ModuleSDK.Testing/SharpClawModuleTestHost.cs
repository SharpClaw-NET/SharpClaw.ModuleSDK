using System.Text.Json;
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
    private readonly IHostActionEntry _hostActionEntry;
    private readonly KernelActionDispatcher _actions;
    private readonly KernelEventDispatcher _events;
    private bool _started;

    internal SharpClawModuleTestHost(
        IReadOnlyList<ISharpClawModule> modules,
        ServiceProvider services,
        KernelGraph coreGraph,
        KernelActionExecutionContext execution,
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IHostActionEntry hostActionEntry)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _execution = execution;
        _hostActionEntry = hostActionEntry ?? throw new ArgumentNullException(nameof(hostActionEntry));
        CoreGraph = coreGraph;
        ModuleGraphs = moduleGraphs;
        _actions = new KernelActionDispatcher(coreGraph, execution);
        _events = new KernelEventDispatcher(coreGraph);
    }

    /// <summary>Gets the compiled production Core graph.</summary>
    public KernelGraph CoreGraph { get; }

    /// <summary>Gets the matching ModuleSDK graphs.</summary>
    public IReadOnlyList<ModuleContributionGraph> ModuleGraphs { get; }

    /// <summary>Runs one operation in a new asynchronous dependency-injection scope.</summary>
    public async ValueTask<TResult> InScopeAsync<TResult>(
        Func<IServiceProvider, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ct.ThrowIfCancellationRequested();
        await using var scope = _services.CreateAsyncScope();
        return await operation(scope.ServiceProvider, ct);
    }

    /// <summary>Invokes one registered tool through its compiled dispatch map.</summary>
    public ValueTask<ToolResult> InvokeToolAsync(
        string toolName,
        JsonElement arguments,
        Guid? conversationId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (conversationId == Guid.Empty)
            throw new ArgumentException(
                "The tool conversation identity must be null or nonempty.",
                nameof(conversationId));

        var matches = ModuleGraphs
            .Where(graph => graph.ToolDispatch.TryGet(toolName, out _))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' has no unique registration in the module test graph.");
        }

        var invocationId = Guid.NewGuid();
        var invocation = new ToolInvocation(
            invocationId,
            conversationId,
            $"test-{invocationId:N}",
            toolName,
            arguments,
            CreateHostActionContext(
                HostActionEntryIngress.Tool,
                toolName,
                invocationId,
                conversationId?.ToString("D")));
        return matches[0].ToolDispatch.InvokeAsync(
            toolName,
            _services,
            invocation,
            ct);
    }

    /// <summary>Invokes one registered CLI command through a scoped handler.</summary>
    public async ValueTask<CliResult> InvokeCliAsync(
        string command,
        IReadOnlyList<string>? arguments = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ct.ThrowIfCancellationRequested();
        var matches = ModuleGraphs
            .SelectMany(graph => graph.Application.CliCommands)
            .Where(item =>
                string.Equals(item.Descriptor.Name, command, StringComparison.OrdinalIgnoreCase)
                || item.Descriptor.Aliases.Any(alias => string.Equals(
                    alias,
                    command,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"CLI command '{command}' has no unique registration in the module test graph.");
        }

        var invocationId = Guid.NewGuid();
        var invocation = new CliInvocation(
            invocationId,
            command,
            arguments ?? [],
            CreateHostActionContext(
                HostActionEntryIngress.Cli,
                command,
                invocationId));
        await using var scope = _services.CreateAsyncScope();
        var handler = (ICliHandler)ActivatorUtilities.GetServiceOrCreateInstance(
            scope.ServiceProvider,
            matches[0].HandlerType);
        return await handler.ExecuteAsync(invocation, ct);
    }

    /// <summary>Invokes one registered HTTP endpoint through a scoped handler.</summary>
    public async ValueTask<HttpEndpointResponse> InvokeHttpAsync(
        string endpointId,
        byte[]? body = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        IReadOnlyDictionary<string, string[]>? query = null,
        IReadOnlyDictionary<string, string[]>? routeValues = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ct.ThrowIfCancellationRequested();
        var matches = ModuleGraphs
            .SelectMany(graph => graph.Application.Endpoints)
            .Where(item =>
                item.Descriptor.Transport == HostEndpointTransport.Http
                && string.Equals(item.Descriptor.Id, endpointId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"HTTP endpoint '{endpointId}' has no unique registration in the module test graph.");
        }

        var contribution = matches[0];
        var invocationId = Guid.NewGuid();
        var request = new HostEndpointRouteRequest(
            new HostEndpointInvocation(
                invocationId,
                endpointId,
                CreateHostActionContext(
                    HostActionEntryIngress.Endpoint,
                    endpointId,
                    invocationId)),
            contribution.Descriptor.ToRouteIdentity(),
            headers ?? EmptyMetadata,
            query ?? EmptyMetadata,
            body ?? [])
        {
            RouteValues = routeValues ?? EmptyMetadata,
        };

        await using var scope = _services.CreateAsyncScope();
        var handler = (IHttpEndpointHandler)ActivatorUtilities.GetServiceOrCreateInstance(
            scope.ServiceProvider,
            contribution.HandlerType);
        var response = await handler.InvokeAsync(request, _hostActionEntry, ct);
        if (!response.IsWellFormed)
            throw new InvalidOperationException("The endpoint handler returned an invalid response.");
        return response;
    }

    /// <summary>Invokes one registered WebSocket endpoint through a scoped handler.</summary>
    public async ValueTask InvokeWebSocketAsync(
        string endpointId,
        IWebSocketChannel channel,
        IReadOnlyDictionary<string, string[]>? headers = null,
        IReadOnlyDictionary<string, string[]>? query = null,
        IReadOnlyDictionary<string, string[]>? routeValues = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(channel);
        ct.ThrowIfCancellationRequested();
        var matches = ModuleGraphs
            .SelectMany(graph => graph.Application.Endpoints)
            .Where(item =>
                item.Descriptor.Transport == HostEndpointTransport.WebSocket
                && string.Equals(item.Descriptor.Id, endpointId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"WebSocket endpoint '{endpointId}' has no unique registration in the module test graph.");
        }

        var contribution = matches[0];
        var invocationId = Guid.NewGuid();
        var request = new HostEndpointRouteRequest(
            new HostEndpointInvocation(
                invocationId,
                endpointId,
                CreateHostActionContext(
                    HostActionEntryIngress.Endpoint,
                    endpointId,
                    invocationId)),
            contribution.Descriptor.ToRouteIdentity(),
            headers ?? EmptyMetadata,
            query ?? EmptyMetadata,
            [])
        {
            RouteValues = routeValues ?? EmptyMetadata,
        };

        await using var scope = _services.CreateAsyncScope();
        var handler = (IWebSocketEndpointHandler)ActivatorUtilities.GetServiceOrCreateInstance(
            scope.ServiceProvider,
            contribution.HandlerType);
        await handler.InvokeAsync(request, channel, _hostActionEntry, ct);
    }

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

    private HostActionEntryRequestContext CreateHostActionContext(
        HostActionEntryIngress ingress,
        string primaryIdentity,
        Guid invocationId,
        string? secondaryIdentity = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            $"module-test-{Guid.NewGuid():N}",
            ingress,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _execution.Caller,
            _execution.Features,
            _execution.TraceId,
            Guid.NewGuid(),
            deadline,
            deadline.AddMinutes(1))
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    ingress,
                    primaryIdentity,
                    secondaryIdentity),
                new HostActionEntryLineage(
                    new SharpClawActionKey("module.test.ingress"),
                    1,
                    "module-test-descriptor",
                    "module-test-input",
                    1,
                    "module-test-schema",
                    null,
                    null)),
        };
    }

    private static IReadOnlyDictionary<string, string[]> EmptyMetadata { get; } =
        new Dictionary<string, string[]>();

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
