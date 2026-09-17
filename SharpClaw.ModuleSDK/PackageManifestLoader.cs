using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Contains one parsed package manifest and its runtime metadata.</summary>
public sealed record LoadedPackageManifest(
    PackageManifest Manifest,
    PackageRuntimeInfo Runtime,
    string Source);

/// <summary>Loads package manifests with the same bounded JSON rules as the production hosts.</summary>
public static class PackageManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Parses one package manifest document.</summary>
    public static LoadedPackageManifest Parse(
        string json,
        string source = "package.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        try
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(json, JsonOptions)
                ?? throw new InvalidDataException($"Package manifest '{source}' is empty.");
            ValidateRequiredMetadata(manifest, source);
            return new LoadedPackageManifest(
                manifest,
                PackageRuntimeInfo.FromJson(json),
                source);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Package manifest '{source}' is not valid JSON.",
                exception);
        }
    }

    private static void ValidateRequiredMetadata(
        PackageManifest manifest,
        string source)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.ToolPrefix)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            throw new InvalidDataException(
                $"Package manifest '{source}' is missing required metadata.");
        }
    }

    /// <summary>Reads and parses one package manifest file.</summary>
    public static LoadedPackageManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Parse(File.ReadAllText(fullPath), fullPath);
    }

    /// <summary>Reads and parses one package manifest file asynchronously.</summary>
    public static async ValueTask<LoadedPackageManifest> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return Parse(json, fullPath);
    }
}
