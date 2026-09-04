using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

/// <summary>Builds one Core-backed module test host.</summary>
public sealed class SharpClawModuleTestBuilder
{
    private readonly List<(ISharpClawModule Module, PackageManifest Manifest)> _registrations = [];
    private readonly List<ModuleTestHostAction> _hostActions = [];
    private readonly List<ModuleTestHostEvent> _hostEvents = [];
    private readonly HashSet<string> _sensitiveApprovals = new(StringComparer.Ordinal);
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private KernelGraphCompileOptions _coreOptions = new();
    private RequestPrincipal _caller = RequestPrincipal.Anonymous;
    private ExtensionFeatureSet _features = ExtensionFeatureSet.Empty;

    /// <summary>Adds one module and its authoritative manifest.</summary>
    public SharpClawModuleTestBuilder AddRegistration(
        ISharpClawModule module,
        PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(manifest);
        _registrations.Add((module, manifest));
        return this;
    }

    /// <summary>Adds one host-owned action definition for module hook tests.</summary>
    public SharpClawModuleTestBuilder AddHostAction<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _hostActions.Add(ModuleTestHostAction.Create(descriptor));
        return this;
    }

    /// <summary>Adds one host-owned event definition for module hook tests.</summary>
    public SharpClawModuleTestBuilder AddHostEvent<TEvent>(EventDescriptor<TEvent> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _hostEvents.Add(ModuleTestHostEvent.Create(descriptor));
        return this;
    }

    /// <summary>Approves exact sensitive contributions selected by one module.</summary>
    public SharpClawModuleTestBuilder ApproveSensitiveContributions(string SourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceId);
        _sensitiveApprovals.Add(SourceId);
        return this;
    }

    /// <summary>Adds host services that can satisfy declared module dependencies.</summary>
    public SharpClawModuleTestBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurations.Add(
            configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>Sets Core graph compilation controls.</summary>
    public SharpClawModuleTestBuilder UseCoreOptions(KernelGraphCompileOptions options)
    {
        _coreOptions = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>Sets the action caller and extension features.</summary>
    public SharpClawModuleTestBuilder UseExecutionContext(
        RequestPrincipal caller,
        ExtensionFeatureSet features)
    {
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _features = features ?? throw new ArgumentNullException(nameof(features));
        return this;
    }

    /// <summary>Compiles all modules and creates the test host.</summary>
    public SharpClawModuleTestHost Build()
    {
        if (_registrations.Count == 0)
            throw new InvalidOperationException("The module test host requires at least one module.");

        var moduleGraphs = _registrations.Select(item =>
            SharpClawModuleCompiler.Compile(
                item.Module,
                item.Manifest,
                new ModuleCompilationOptions
                {
                    HostingMode = ModuleHostingMode.InProcess,
                    HostActions = _hostActions.Select(action => action.SidecarDescriptor).ToArray(),
                    HostEvents = _hostEvents.Select(evt => evt.SidecarDescriptor).ToArray(),
                }))
            .ToArray();
        var coreOptions = ModuleTestKernelOptions.Create(
            _coreOptions,
            moduleGraphs,
            _hostActions,
            _hostEvents,
            _sensitiveApprovals);
        var services = new ServiceCollection();
        foreach (var configure in _serviceConfigurations)
            configure(services);
        services.AddSingleton<IActionDefinitionBinding>(
            new ActionDefinitionBinding<ServiceStartContext, bool>(
                ModuleLifecycleActions.Identity.Id,
                ModuleLifecycleActions.Start));
        services.AddSingleton<IActionDefinitionBinding>(
            new ActionDefinitionBinding<ModuleIdentity, bool>(
                ModuleLifecycleActions.Identity.Id,
                ModuleLifecycleActions.Stop));
        ModuleTestHostDefinitionSet.AddTo(services, _hostActions, _hostEvents);
        foreach (var graph in moduleGraphs)
        {
            foreach (var descriptor in graph.Services)
                ((ICollection<ServiceDescriptor>)services).Add(descriptor);
        }
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        KernelGraph coreGraph;
        try
        {
            coreGraph = new KernelGraphBuilder().Compile(serviceProvider, coreOptions);
        }
        catch
        {
            serviceProvider.Dispose();
            throw;
        }
        var execution = new KernelActionExecutionContext(
            _caller,
            _features,
            Guid.NewGuid(),
            Guid.NewGuid());
        return new SharpClawModuleTestHost(
            _registrations.Select(item => item.Module).ToArray(),
            serviceProvider,
            coreGraph,
            execution,
            Array.AsReadOnly(moduleGraphs));
    }
}
