using System.Reflection;
using System.Runtime.Loader;

namespace SharpClaw.SidecarHost.InProcess;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for external modules.
/// Each external module directory gets its own context, enabling assembly
/// unloading when the module is removed or replaced at runtime.
/// <para>
/// The resolver prefers the module's own dependencies next to its DLL,
/// falling back to the default context for shared types
/// (<c>SharpClaw.Contracts</c>, <c>SharpClaw.ModuleSDK</c>, and host assemblies).
/// </para>
/// </summary>
public sealed class RegistrationLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Names/prefixes that must always resolve from the default ALC so the host and
    /// every module share the same <see cref="Type"/> identity. If any of these were
    /// resolved by <see cref="AssemblyDependencyResolver"/> from the module directory,
    /// the runtime would load a second copy and casts like
    /// <c>obj is ISharpClawModule</c> would fail with a type mismatch.
    /// </summary>
    private static readonly HashSet<string> HostSharedAssemblyNames =
        new(StringComparer.Ordinal)
    {
        "SharpClaw.Contracts",
        "SharpClaw.ModuleSDK",
        "SharpClaw.SidecarHost.InProcess",
    };

    private static readonly string[] HostSharedPrefixes =
    {
        "Microsoft.Extensions.",
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore",
        "System.",
        "netstandard",
        "mscorlib",
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// Creates a new collectible load context anchored at the module's main DLL path.
    /// </summary>
    public RegistrationLoadContext(string mainDllPath)
        : base(name: Path.GetFileNameWithoutExtension(mainDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainDllPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName name)
    {
        // Always delegate host-shared assemblies to the default ALC. Without this
        // guard, AssemblyDependencyResolver would happily return a copy that the
        // module ships next to itself, causing type identity mismatches.
        if (name.Name is { Length: > 0 } shortName)
        {
            if (HostSharedAssemblyNames.Contains(shortName))
                return null;

            for (var i = 0; i < HostSharedPrefixes.Length; i++)
            {
                var prefix = HostSharedPrefixes[i];
                if (shortName.Equals(prefix, StringComparison.Ordinal)
                    || shortName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is not null)
            return LoadFromAssemblyPath(path);

        return null;
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}
