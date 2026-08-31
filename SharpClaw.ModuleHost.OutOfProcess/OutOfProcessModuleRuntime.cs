using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessModuleRuntime : IAsyncDisposable
{
    private readonly ModuleLoadContext _loadContext;
    private readonly OutOfProcessModuleCapabilityTransport _capabilityTransport;
    private ServiceProvider? _services;
    private ISharpClawModule? _module;
    private ModuleContributionGraph? _graph;
    private bool _started;
    private bool _disposed;

    private OutOfProcessModuleRuntime(
        string moduleDirectory,
        ModuleLoadContext loadContext,
        OutOfProcessModuleCapabilityTransport capabilityTransport,
        ServiceProvider services,
        ISharpClawModule module,
        ModuleManifest manifest,
        ModuleContributionGraph graph)
    {
        ModuleDirectory = moduleDirectory;
        _loadContext = loadContext;
        _capabilityTransport = capabilityTransport;
        _services = services;
        _module = module;
        Manifest = manifest;
        _graph = graph;
    }

    public string ModuleDirectory { get; }

    public ISharpClawModule Module => _module
        ?? throw new ObjectDisposedException(nameof(OutOfProcessModuleRuntime));

    public ModuleManifest Manifest { get; }

    public ModuleContributionGraph Graph => _graph
        ?? throw new ObjectDisposedException(nameof(OutOfProcessModuleRuntime));

    public IServiceProvider Services => _services
        ?? throw new ObjectDisposedException(nameof(OutOfProcessModuleRuntime));

    internal IDisposable PushActiveCarrier(Guid capabilityId) =>
        _capabilityTransport.PushActiveCarrier(capabilityId);

    internal IDisposable PushActiveCarrier(
        Guid capabilityId,
        SidecarCapabilityCallIdentity parentCall) =>
        _capabilityTransport.PushActiveCarrier(capabilityId, parentCall);

    public static Task<OutOfProcessModuleRuntime> LoadAsync(
        string moduleDirectory,
        CancellationToken ct = default) =>
        LoadAsync(
            moduleDirectory,
            new OutOfProcessModuleCapabilityTransport(),
            ct);

    internal static async Task<OutOfProcessModuleRuntime> LoadAsync(
        string moduleDirectory,
        OutOfProcessModuleCapabilityTransport capabilityTransport,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(capabilityTransport);
        var root = Path.GetFullPath(moduleDirectory);
        var manifestPath = OutOfProcessPathGuard.EnsureContainedIn(
            Path.Combine(root, "module.json"),
            root);
        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(
            manifestJson,
            OutOfProcessJsonOptions.Manifest)
            ?? throw new InvalidOperationException(
                $"Module manifest '{manifestPath}' is invalid.");
        var runtime = ModuleManifestRuntimeInfo.FromJson(manifestJson);
        runtime.EnsureDotNetEntryAssembly(manifest);
        if (!runtime.IsSidecarHostMode)
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' must set hostMode to "
                + $"'{ModuleManifestRuntimeInfo.HostModeSidecar}'.");
        }

        var entryPath = OutOfProcessPathGuard.EnsureContainedIn(
            Path.Combine(root, manifest.EntryAssembly),
            root);
        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException(
                $"Module entry assembly '{manifest.EntryAssembly}' was not found.",
                entryPath);
        }

        var loadContext = new ModuleLoadContext(entryPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);
            var module = InProcessModuleAssemblyLoader.CreateModuleInstance(
                assembly,
                manifest,
                runtime,
                entryPath);
            var graph = SharpClawModuleCompiler.Compile(
                module,
                manifest,
                new ModuleCompilationOptions
                {
                    HostingMode = ModuleHostingMode.OutOfProcess,
                });
            capabilityTransport.Initialize(
                graph.Identity.Id,
                graph.ContractHash,
                graph.PayloadLimits,
                graph.ActionHooks,
                graph);
            IServiceCollection services = new ServiceCollection();
            foreach (var descriptor in graph.Services)
            {
                if (descriptor.ServiceType == typeof(IModuleStorageGateway)
                    || descriptor.ServiceType == typeof(IActionDispatcher)
                    || descriptor.ServiceType == typeof(ISidecarCapabilityTransport)
                    || descriptor.ServiceType == typeof(IHostActionEntry))
                {
                    throw new InvalidOperationException(
                        $"The module cannot register host-owned service '{descriptor.ServiceType.FullName}'.");
                }
                services.Add(descriptor);
            }
            services.AddSingleton<ISidecarCapabilityTransport>(capabilityTransport);
            services.AddSingleton<IModuleStorageGateway>(
                new OutOfProcessModuleStorageGateway(
                    capabilityTransport,
                    graph.Identity.Id,
                    graph.Storage.Select(value => value.StorageName)));
            services.AddSingleton<IActionDispatcher>(
                new OutOfProcessActionDispatcher(capabilityTransport));
            services.AddSingleton<IHostActionEntry>(
                new OutOfProcessHostActionEntry(capabilityTransport));
            services.AddSingleton(module);
            services.AddSingleton(graph);
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            capabilityTransport.SetServices(provider);
            return new OutOfProcessModuleRuntime(
                root,
                loadContext,
                capabilityTransport,
                provider,
                module,
                manifest,
                graph);
        }
        catch
        {
            await capabilityTransport.DisposeAsync();
            loadContext.Unload();
            throw;
        }
    }

    public async ValueTask StartAsync(ModuleStartContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("The out-of-process module is already started.");
        await Module.StartAsync(context, ct);
        _started = true;
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            return;
        await Module.StopAsync(ct);
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_started)
                await _module!.StopAsync(CancellationToken.None);
            _started = false;
            await _services!.DisposeAsync();
        }
        finally
        {
            _started = false;
            _services = null;
            _module = null;
            _graph = null;
            await _capabilityTransport.DisposeAsync();
            _loadContext.Unload();
        }
    }
}
