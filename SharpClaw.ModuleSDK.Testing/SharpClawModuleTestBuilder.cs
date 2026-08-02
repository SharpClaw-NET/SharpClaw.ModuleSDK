using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

/// <summary>Builds one Core-backed module test host.</summary>
public sealed class SharpClawModuleTestBuilder
{
    private readonly List<(ISharpClawModule Module, ModuleManifest Manifest)> _modules = [];
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
                new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess }))
            .ToArray();
        var registry = new KernelModuleRegistry();
        foreach (var item in _modules)
            registry.Add(item.Module);
        var coreGraph = registry.Compile(_hostServices, _coreOptions);
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
