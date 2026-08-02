using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

/// <summary>Builds one Core-backed module test host.</summary>
public sealed class SharpClawModuleTestBuilder
{
    private readonly List<(ISharpClawModule Module, ModuleManifest Manifest)> _modules = [];
    private readonly List<ModuleTestHostAction> _hostActions = [];
    private readonly List<ModuleTestHostEvent> _hostEvents = [];
    private readonly HashSet<string> _sensitiveApprovals = new(StringComparer.Ordinal);
    private IServiceProvider? _hostServices;
    private KernelGraphCompileOptions _coreOptions = new();
    private RequestPrincipal _caller = RequestPrincipal.Anonymous;
    private ExtensionFeatureSet _features = ExtensionFeatureSet.Empty;

    /// <summary>Adds one module and its authoritative manifest.</summary>
    public SharpClawModuleTestBuilder AddModule(
        ISharpClawModule module,
        ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(manifest);
        _modules.Add((module, manifest));
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
    public SharpClawModuleTestBuilder ApproveSensitiveContributions(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _sensitiveApprovals.Add(moduleId);
        return this;
    }

    /// <summary>Sets host services that can satisfy declared module dependencies.</summary>
    public SharpClawModuleTestBuilder UseHostServices(IServiceProvider services)
    {
        _hostServices = services ?? throw new ArgumentNullException(nameof(services));
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
        if (_modules.Count == 0)
            throw new InvalidOperationException("The module test host requires at least one module.");

        var moduleGraphs = _modules.Select(item =>
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
        var registry = new KernelModuleRegistry();
        if (_hostActions.Count > 0 || _hostEvents.Count > 0)
            registry.Add(new ModuleTestHostDefinitionModule(_hostActions, _hostEvents));
        foreach (var item in _modules)
            registry.Add(item.Module);
        var coreOptions = ModuleTestKernelOptions.Create(
            _coreOptions,
            moduleGraphs,
            _hostActions,
            _hostEvents,
            _sensitiveApprovals);
        var coreGraph = registry.Compile(_hostServices, coreOptions);
        var execution = new KernelActionExecutionContext(
            _caller,
            _features,
            Guid.NewGuid(),
            Guid.NewGuid());
        return new SharpClawModuleTestHost(
            registry,
            coreGraph,
            execution,
            Array.AsReadOnly(moduleGraphs));
    }
}
