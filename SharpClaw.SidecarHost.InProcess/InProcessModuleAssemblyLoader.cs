using System.Reflection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.InProcess;

/// <summary>Creates one module instance from a loaded entry assembly.</summary>
public static class InProcessModuleAssemblyLoader
{
    /// <summary>Creates the module selected by module type or manifest identity.</summary>
    public static ISharpClawModule CreateModuleInstance(
        Assembly assembly,
        PackageManifest manifest,
        PackageRuntimeInfo runtimeInfo,
        string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var moduleTypes = assembly.GetTypes()
            .Where(type => type.IsAssignableTo(typeof(ISharpClawModule)) && !type.IsAbstract)
            .ToArray();
        if (moduleTypes.Length == 0)
        {
            throw new InvalidOperationException(
                $"No ISharpClawModule implementation was found in '{Path.GetFileName(assemblyPath)}'.");
        }

        if (!string.IsNullOrWhiteSpace(runtimeInfo.EntryType))
        {
            var explicitType = moduleTypes.SingleOrDefault(type =>
                string.Equals(type.FullName, runtimeInfo.EntryType, StringComparison.Ordinal)
                || string.Equals(type.AssemblyQualifiedName, runtimeInfo.EntryType, StringComparison.Ordinal)
                || string.Equals(type.Name, runtimeInfo.EntryType, StringComparison.Ordinal));
            if (explicitType is null)
            {
                throw new InvalidOperationException(
                    $"Module '{manifest.Id}' declares entryType '{runtimeInfo.EntryType}', "
                    + $"but that type was not found in '{Path.GetFileName(assemblyPath)}'.");
            }

            return Create(explicitType);
        }

        var matches = moduleTypes
            .Select(Create)
            .Where(module => string.Equals(module.Identity.Id, manifest.Id, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"No ISharpClawModule implementation in '{Path.GetFileName(assemblyPath)}' "
                + $"declares module id '{manifest.Id}'."),
            _ => throw new InvalidOperationException(
                $"More than one ISharpClawModule implementation in '{Path.GetFileName(assemblyPath)}' "
                + $"declares module id '{manifest.Id}'. Add entryType to package.json."),
        };
    }

    private static ISharpClawModule Create(Type type) =>
        (ISharpClawModule)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Module type '{type.FullName}' could not be created."));
}
