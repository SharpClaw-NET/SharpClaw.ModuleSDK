using System.Reflection;
using System.Runtime.Loader;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for external .NET modules.
/// Each external module directory gets its own context, enabling assembly
/// unloading when the module is removed or replaced at runtime.
/// <para>
/// The resolver prefers the module's own dependencies next to its DLL,
/// falling back to the default context for shared types
/// (<c>SharpClaw.Contracts</c>, <c>Microsoft.Extensions.*</c>, and similar
/// host-owned assemblies).
/// </para>
/// </summary>
internal sealed class ModuleLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Names and prefixes that must always resolve from the default load context.
    /// This keeps host and module contract types identical.
    /// </summary>
    private static readonly string[] HostSharedPrefixes =
    {
        "SharpClaw.Contracts",
        "SharpClaw.Utils",
        "SharpClaw.Application.Core",
        "SharpClaw.Application.Infrastructure",
        "SharpClaw.Gateway.Abstractions",
        "Microsoft.Extensions.",
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore",
        "System.",
        "netstandard",
        "mscorlib",
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// Creates a collectible load context anchored at the module DLL path.
    /// </summary>
    public ModuleLoadContext(string mainDllPath)
        : base(name: Path.GetFileNameWithoutExtension(mainDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainDllPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is { Length: > 0 } shortName)
        {
            for (var i = 0; i < HostSharedPrefixes.Length; i++)
            {
                var prefix = HostSharedPrefixes[i];
                if (prefix.EndsWith('.')
                    ? shortName.StartsWith(prefix, StringComparison.Ordinal)
                    : shortName.Equals(prefix, StringComparison.Ordinal)
                      || shortName.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        var path = _resolver.ResolveAssemblyToPath(name);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : 0;
    }
}
