using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessModuleRuntime : IAsyncDisposable
{
    private readonly ModuleLoadContext _loadContext;
    private readonly ServiceProvider _services;
    private bool _started;
    private bool _disposed;

    private OutOfProcessModuleRuntime(
        string moduleDirectory,
        ModuleLoadContext loadContext,
        ServiceProvider services,
        ISharpClawModule module,
        ModuleManifest manifest,
        ModuleContributionGraph graph)
    {
        ModuleDirectory = moduleDirectory;
        _loadContext = loadContext;
        _services = services;
        Module = module;
        Manifest = manifest;
        Graph = graph;
    }

    public string ModuleDirectory { get; }

    public ISharpClawModule Module { get; }

    public ModuleManifest Manifest { get; }

    public ModuleContributionGraph Graph { get; }

    public IServiceProvider Services => _services;

    public static async Task<OutOfProcessModuleRuntime> LoadAsync(
        string moduleDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
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
            IServiceCollection services = new ServiceCollection();
            foreach (var descriptor in graph.Services)
                services.Add(descriptor);
            services.AddSingleton(module);
            services.AddSingleton(graph);
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            return new OutOfProcessModuleRuntime(
                root,
                loadContext,
                provider,
                module,
                manifest,
                graph);
        }
        catch
        {
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
        if (_started)
            await Module.StopAsync(CancellationToken.None);
        _started = false;
        _disposed = true;
        await _services.DisposeAsync();
        _loadContext.Unload();
    }
}
