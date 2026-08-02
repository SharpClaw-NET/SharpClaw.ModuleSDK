using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.InProcess;

/// <summary>Owns one loaded in-process module and its collectible load context.</summary>
public sealed class InProcessModuleHost : IAsyncDisposable
{
    private readonly ModuleLoadContext _loadContext;
    private readonly ServiceProvider _services;
    private bool _started;
    private bool _disposed;

    private InProcessModuleHost(
        ModuleLoadContext loadContext,
        ServiceProvider services,
        ISharpClawModule module,
        ModuleManifest manifest,
        ModuleContributionGraph graph)
    {
        _loadContext = loadContext;
        _services = services;
        Module = module;
        Manifest = manifest;
        Graph = graph;
        Invoker = new InProcessModuleInvoker(graph, services);
    }

    /// <summary>Gets the loaded module.</summary>
    public ISharpClawModule Module { get; }

    /// <summary>Gets the validated module manifest.</summary>
    public ModuleManifest Manifest { get; }

    /// <summary>Gets the compiled contribution graph.</summary>
    public ModuleContributionGraph Graph { get; }

    /// <summary>Gets the handler invocation adapter.</summary>
    public InProcessModuleInvoker Invoker { get; }

    /// <summary>Loads and compiles one explicitly in-process module.</summary>
    public static async Task<InProcessModuleHost> LoadAsync(
        string moduleDirectory,
        Action<IServiceCollection>? configureHostCapabilities = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        var root = Path.GetFullPath(moduleDirectory);
        var manifestPath = EnsureContained(Path.Combine(root, "module.json"), root);
        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(manifestJson, ManifestJsonOptions)
            ?? throw new InvalidOperationException($"Module manifest '{manifestPath}' is invalid.");
        var runtime = ModuleManifestRuntimeInfo.FromJson(manifestJson);
        runtime.EnsureDotNetEntryAssembly(manifest);
        if (!runtime.IsInProcessHostMode)
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' must set hostMode to '{ModuleManifestRuntimeInfo.HostModeInProcess}'.");
        }

        var entryPath = EnsureContained(Path.Combine(root, manifest.EntryAssembly), root);
        if (!File.Exists(entryPath))
            throw new FileNotFoundException($"Module entry assembly '{manifest.EntryAssembly}' was not found.", entryPath);

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
                new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
            var services = new ServiceCollection();
            configureHostCapabilities?.Invoke(services);
            foreach (var descriptor in graph.Services)
                services.Add(descriptor);
            services.AddSingleton(module);
            services.AddSingleton(graph);
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            return new InProcessModuleHost(loadContext, provider, module, manifest, graph);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>Starts the loaded module once.</summary>
    public async ValueTask StartAsync(
        string hostVersion,
        ExtensionFeatureSet? features = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("The in-process module is already started.");
        await Module.StartAsync(
            new ModuleStartContext(
                Graph.Identity,
                hostVersion,
                Graph.ContractHash,
                features ?? ExtensionFeatureSet.Empty),
            ct);
        _started = true;
    }

    /// <summary>Stops the loaded module once.</summary>
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            return;
        await Module.StopAsync(ct);
        _started = false;
    }

    /// <inheritdoc />
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

    private static string EnsureContained(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{path}' escapes the module directory.");
        return fullPath;
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
    };
}
