using FluentAssertions;
using NUnit.Framework;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class PackageManifestLoaderTests
{
    [Test]
    public void ParseReturnsManifestAndRuntimeMetadata()
    {
        var document = PackageManifestLoader.Parse(ValidManifest, "sample-package.json");

        document.Source.Should().Be("sample-package.json");
        document.Manifest.Id.Should().Be("sample_authorization");
        document.Manifest.EntryAssembly.Should().Be("Sample.Authorization.dll");
        document.Runtime.Runtime.Should().Be("dotnet");
        document.Runtime.HostMode.Should().Be("sidecar");
    }

    [Test]
    public void ParseRejectsIncorrectRequiredPropertyCasing()
    {
        var json = ValidManifest.Replace("\"id\"", "\"Id\"", StringComparison.Ordinal);

        var act = () => PackageManifestLoader.Parse(json, "wrong-case.json");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*missing required metadata*");
    }

    [Test]
    public void ParseRejectsDocumentsAboveTheDepthLimit()
    {
        var nested = string.Concat(Enumerable.Repeat("{\"value\":", 9))
            + "true"
            + string.Concat(Enumerable.Repeat("}", 9));
        var json = ValidManifest[..^1] + ",\"extra\":" + nested + "}";

        var act = () => PackageManifestLoader.Parse(json, "deep.json");

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*not valid JSON*");
    }

    private const string ValidManifest = """
        {
          "id": "sample_authorization",
          "displayName": "Sample Authorization",
          "version": "1.0.0",
          "toolPrefix": "sample_auth",
          "runtime": "dotnet",
          "hostMode": "sidecar",
          "entryAssembly": "Sample.Authorization.dll",
          "entryType": "Sample.Authorization.SampleAuthorizationModule",
          "minHostVersion": "0.5.0"
        }
        """;
}
