using NUnit.Framework;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class AuthoringDocumentationTests
{
    [Test]
    public void PublicGuidesUseTheCurrentAuthoringSurface()
    {
        var documentationRoot = Path.Combine(AppContext.BaseDirectory, "Documentation");
        var documents = Directory.GetFiles(documentationRoot, "*.md")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToArray();
        var text = string.Join(Environment.NewLine, documents);

        foreach (var required in RequiredSymbols)
            Assert.That(text, Does.Contain(required), required);

        foreach (var retired in RetiredSymbols)
            Assert.That(text, Does.Not.Contain(retired), retired);
    }

    private static readonly string[] RequiredSymbols =
    [
        "ISharpClawModule",
        "ConfigureServices",
        "PackageManifestLoader",
        "SharpClawModuleCompiler",
        "AddTool<THandler>",
        "AddAuthorizationPolicy<TPolicy>",
        "AddAuthorizationRestriction<TRestriction>",
        "SharpClawModuleTestBuilder",
        "ActionEntry",
        "IHostActionEntry",
        "hostMode",
    ];

    private static readonly string[] RetiredSymbols =
    [
        "IKernelRegistrationSource",
        "IApplicationRegistrationSource",
        "RegistrationToolDefinition",
        "RegistrationInlineToolDefinition",
        "RegistrationToolPermission",
        "GetToolDefinitions",
        "ExecuteToolAsync",
        "ExecuteInlineToolAsync",
        "SeedDataAsync",
        "InitializeAsync",
        "ShutdownAsync",
        "ExportedContracts",
        "RequiredContracts",
        "AgentJobContext",
        "ModuleToolDefinition",
        "ModuleCliCommand",
        "SharpClaw.Runtime.BLL",
        "SharpClaw.Runtime.INF",
    ];
}
