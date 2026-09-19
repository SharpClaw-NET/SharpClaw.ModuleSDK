using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class ModulePackagingTargetsTests
{
    [Test]
    public void BuildTransitiveTargetAddsOneOptInModulePayload()
    {
        var targetPath = Path.Combine(
            AppContext.BaseDirectory,
            "BuildTransitive",
            "SharpClaw.ModuleSDK.targets");
        var document = XDocument.Load(targetPath);
        document.Root.Should().NotBeNull();
        var root = document.Root!;
        var propertyGroup = root.Elements("PropertyGroup").Single();
        var target = root.Elements("Target").Single();

        propertyGroup.Attribute("Condition")!.Value.Should().Contain(
            "$(IsSharpClawModulePackage)");
        propertyGroup.Element("SuppressDependenciesWhenPacking")!.Value.Should().Be("true");
        propertyGroup.Element("GenerateDependencyFile")!.Value.Should().Be("true");
        propertyGroup.Element("IncludeContentInPack").Should().BeNull();
        propertyGroup.Element("NoWarn")!.Value.Should().Contain("NU5100").And.Contain("NU5128");
        propertyGroup.Element("TargetsForTfmSpecificContentInPackage")!.Value.Should().Contain(
            "AddSharpClawModulePayloadToPackage");
        target.Attribute("Condition")!.Value.Should().Contain("package.json");

        var files = target.Descendants("TfmSpecificPackageFile").ToArray();
        files.Should().ContainSingle(file =>
            file.Attribute("Include")!.Value == "@(_SharpClawModulePayload)"
            && file.Attribute("PackagePath")!.Value == "sharpclaw\\");
        files.Should().ContainSingle(file =>
            file.Attribute("Include")!.Value.EndsWith("package.json", StringComparison.Ordinal)
            && file.Attribute("PackagePath")!.Value == "sharpclaw\\");
        target.Descendants("_SharpClawModulePayload").Single()
            .Attribute("Exclude")!.Value.Should().Contain("$(TargetDir)package.json");
    }
}
