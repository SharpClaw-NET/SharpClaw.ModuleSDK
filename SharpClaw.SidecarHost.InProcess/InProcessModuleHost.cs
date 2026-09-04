using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.InProcess;

/// <summary>Owns one loaded in-process module and its collectible load context.</summary>
public sealed class InProcessRegistrationHost : IAsyncDisposable
{
    private readonly RegistrationLoadContext _loadContext;
    private InProcessModuleInvoker? _invoker;
    private bool _started;
    private bool _disposed;

    private InProcessRegistrationHost(
        RegistrationLoadContext loadContext,
        ISharpClawModule module,
        PackageManifest manifest,
        ModuleContributionGraph graph)
    {
        _loadContext = loadContext;
        Module = module;
        Manifest = manifest;
        Graph = graph;
        ServiceDescriptors = CreateServiceDescriptors(module, graph);
    }

    /// <summary>Gets the loaded module.</summary>
    public ISharpClawModule Module { get; }

    /// <summary>Gets the validated module manifest.</summary>
    public PackageManifest Manifest { get; }

    /// <summary>Gets the compiled contribution graph.</summary>
    public ModuleContributionGraph Graph { get; }

    /// <summary>Gets the handler invocation adapter.</summary>
    public InProcessModuleInvoker Invoker => _invoker
        ?? throw new InvalidOperationException("The in-process service graph is not bound.");

    public IReadOnlyList<ServiceDescriptor> ServiceDescriptors { get; }

    /// <summary>Loads and compiles one explicitly in-process module.</summary>
    public static async Task<InProcessRegistrationHost> LoadAsync(
        string registrationDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationDirectory);
        var root = Path.GetFullPath(registrationDirectory);
        var manifestPath = EnsureContained(Path.Combine(root, "package.json"), root);
        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, ManifestJsonOptions)
            ?? throw new InvalidOperationException($"Module manifest '{manifestPath}' is invalid.");
        var runtime = PackageRuntimeInfo.FromJson(manifestJson);
        runtime.EnsureDotNetEntryAssembly(manifest);
        if (!runtime.IsInProcessHostMode)
        {
            throw new InvalidOperationException(
                $"Module '{manifest.Id}' must set hostMode to '{PackageRuntimeInfo.HostModeInProcess}'.");
        }

        var entryPath = EnsureContained(Path.Combine(root, manifest.EntryAssembly), root);
        if (!File.Exists(entryPath))
            throw new FileNotFoundException($"Module entry assembly '{manifest.EntryAssembly}' was not found.", entryPath);

        var loadContext = new RegistrationLoadContext(entryPath);
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
            return new InProcessRegistrationHost(loadContext, module, manifest, graph);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    public void Bind(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_invoker is not null)
            throw new InvalidOperationException("The in-process service graph is already bound.");
        _invoker = new InProcessModuleInvoker(Graph, services);
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
            new ServiceStartContext(
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
        _loadContext.Unload();
    }

    private static IReadOnlyList<ServiceDescriptor> CreateServiceDescriptors(
        ISharpClawModule module,
        ModuleContributionGraph graph)
    {
        var descriptors = graph.Services.ToList();
        descriptors.Add(ServiceDescriptor.Singleton(typeof(ISharpClawModule), module));
        descriptors.Add(ServiceDescriptor.Singleton(typeof(IServiceLifecycle), module));
        descriptors.Add(ServiceDescriptor.Singleton(typeof(ModuleContributionGraph), graph));
        return Array.AsReadOnly(descriptors.ToArray());
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
