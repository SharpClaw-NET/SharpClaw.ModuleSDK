using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK.Testing.Tests;

public sealed class AuthorizationAuthoringJourneyTests
{
    [Test]
    public async Task ProviderAndRestrictionRunThroughTheirRegisteredProductionGraph()
    {
        var capture = new LifetimeCapture();
        await using var host = new SharpClawModuleTestBuilder()
            .ConfigureServices(services => services.AddSingleton(capture))
            .AddRegistration(new SampleAuthorizationModule(), ProviderManifest())
            .AddRegistration(new TenantRestrictionModule(), RestrictionManifest())
            .ApproveSensitiveContributions("sample_authorization")
            .ApproveSensitiveContributions("tenant_restriction")
            .UseExecutionContext(AuthenticatedCaller(), AllowedFeatures())
            .Build();

        var first = await host.ActionEntry(
                AuthorizationProtocol.Evaluate,
                Request("document-a"))
            .RunRequiredAsync();
        var second = await host.ActionEntry(
                AuthorizationProtocol.Evaluate,
                Request("document-b"))
            .RunRequiredAsync();

        first.Allowed.Should().BeTrue();
        second.Allowed.Should().BeTrue();
        capture.PolicyEvaluations.Should().Be(2);
        capture.PolicyInstances.Should().Be(2);
        capture.PolicyDisposals.Should().Be(2);
        capture.RestrictionEvaluations.Should().Be(2);
        capture.RestrictionInstances.Should().Be(2);
        capture.RestrictionDisposals.Should().Be(2);
    }

    [Test]
    public async Task RestrictionDenialStopsTheProviderTerminal()
    {
        var capture = new LifetimeCapture();
        await using var host = new SharpClawModuleTestBuilder()
            .ConfigureServices(services => services.AddSingleton(capture))
            .AddRegistration(new SampleAuthorizationModule(), ProviderManifest())
            .AddRegistration(new TenantRestrictionModule(), RestrictionManifest())
            .ApproveSensitiveContributions("sample_authorization")
            .ApproveSensitiveContributions("tenant_restriction")
            .UseExecutionContext(AuthenticatedCaller(), ExtensionFeatureSet.Empty)
            .Build();

        var outcome = await host.ActionEntry(
                AuthorizationProtocol.Evaluate,
                Request("document-a"))
            .RunAsync();

        outcome.Kind.Should().Be(ActionOutcomeKind.Failed);
        outcome.Error.Should().Be(new ExecutionError(
            "authorization_restricted:tenant_denied",
            "The caller cannot access this tenant."));
        capture.PolicyInstances.Should().Be(0);
        capture.PolicyEvaluations.Should().Be(0);
        capture.RestrictionInstances.Should().Be(1);
        capture.RestrictionDisposals.Should().Be(1);
    }

    [Test]
    public async Task PreCancelledEntryDoesNotConstructScopedPolicyOrRestriction()
    {
        var capture = new LifetimeCapture();
        await using var host = new SharpClawModuleTestBuilder()
            .ConfigureServices(services => services.AddSingleton(capture))
            .AddRegistration(new SampleAuthorizationModule(), ProviderManifest())
            .AddRegistration(new TenantRestrictionModule(), RestrictionManifest())
            .ApproveSensitiveContributions("sample_authorization")
            .ApproveSensitiveContributions("tenant_restriction")
            .UseExecutionContext(AuthenticatedCaller(), AllowedFeatures())
            .Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await host.ActionEntry(
                AuthorizationProtocol.Evaluate,
                Request("document-a"))
            .RunAsync(cancellation.Token);

        outcome.Kind.Should().Be(ActionOutcomeKind.Cancelled);
        capture.PolicyInstances.Should().Be(0);
        capture.RestrictionInstances.Should().Be(0);
    }

    [Test]
    public async Task BuilderLoadsTheAuthoritativeManifestAndHostingModeFromDisk()
    {
        var capture = new LifetimeCapture();
        var manifestPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sample-authorization-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                ProviderManifest() with { HostMode = PackageRuntimeInfo.HostModeSidecar },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        try
        {
            await using var host = new SharpClawModuleTestBuilder()
                .ConfigureServices(services => services.AddSingleton(capture))
                .AddRegistration(new SampleAuthorizationModule(), manifestPath)
                .ApproveSensitiveContributions("sample_authorization")
                .UseExecutionContext(AuthenticatedCaller(), ExtensionFeatureSet.Empty)
                .Build();

            host.ModuleGraphs.Should().ContainSingle()
                .Which.HostingMode.Should().Be(ModuleHostingMode.OutOfProcess);

            var decision = await host.ActionEntry(
                    AuthorizationProtocol.Evaluate,
                    Request("document-a"))
                .RunRequiredAsync();

            decision.Allowed.Should().BeTrue();
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static AuthorizationRequest Request(string id) =>
        new("documents.read", new AuthorizationResource("document", id));

    private static RequestPrincipal AuthenticatedCaller() =>
        new(
            "sample-user",
            "Sample User",
            new HashSet<string>(["document-reader"], StringComparer.Ordinal),
            IsAuthenticated: true);

    private static ExtensionFeatureSet AllowedFeatures()
    {
        using var value = JsonDocument.Parse("true");
        return new ExtensionFeatureSet(
        [
            new ExtensionFeature(
                "tenant.allowed",
                1,
                "tenant_restriction",
                16,
                value.RootElement.Clone()),
        ]);
    }

    private static PackageManifest ProviderManifest() =>
        new(
            "sample_authorization",
            "Sample Authorization",
            "1.0.0",
            "sample_auth",
            "Sample.Authorization.dll",
            "0.5.0",
            Runtime: PackageRuntimeInfo.DotNet,
            EntryType: typeof(SampleAuthorizationModule).FullName,
            HostMode: PackageRuntimeInfo.HostModeInProcess,
            Exports:
            [
                new PackageContractReference(
                    AuthorizationProtocol.ContractName,
                    typeof(AuthorizationContract).FullName),
            ]);

    private static PackageManifest RestrictionManifest() =>
        new(
            "tenant_restriction",
            "Tenant Restriction",
            "1.0.0",
            "tenant",
            "Tenant.Restriction.dll",
            "0.5.0",
            Runtime: PackageRuntimeInfo.DotNet,
            EntryType: typeof(TenantRestrictionModule).FullName,
            HostMode: PackageRuntimeInfo.HostModeInProcess,
            Requires:
            [
                new PackageContractReference(
                    AuthorizationProtocol.ContractName,
                    typeof(AuthorizationContract).FullName),
            ],
            RequestedHooks:
            [
                new PackageHookRequest(
                    AuthorizationProtocol.Evaluate.Key.Value,
                    ["Inspect", "Wrap", "Observe"]),
            ]);

    private sealed class SampleAuthorizationModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "sample_authorization",
            "Sample Authorization",
            "sample_auth");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddAuthorizationPolicy<SampleAuthorizationPolicy>();
    }

    private sealed class TenantRestrictionModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "tenant_restriction",
            "Tenant Restriction",
            "tenant");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddAuthorizationRestriction<TenantRestriction>("tenant");
    }

    private sealed class SampleAuthorizationPolicy : IAuthorizationPolicy, IDisposable
    {
        private readonly LifetimeCapture _capture;

        public SampleAuthorizationPolicy(LifetimeCapture capture)
        {
            _capture = capture;
            _capture.PolicyInstances++;
        }

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            ActionContext<AuthorizationRequest> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _capture.PolicyEvaluations++;
            return ValueTask.FromResult(
                context.Caller.Roles?.Contains("document-reader") == true
                    ? AuthorizationDecision.Allow("document_reader")
                    : AuthorizationDecision.Deny(
                        "document_denied",
                        "The caller cannot read this document."));
        }

        public void Dispose() => _capture.PolicyDisposals++;
    }

    private sealed class TenantRestriction : IAuthorizationRestriction, IDisposable
    {
        private readonly LifetimeCapture _capture;

        public TenantRestriction(LifetimeCapture capture)
        {
            _capture = capture;
            _capture.RestrictionInstances++;
        }

        public ValueTask<AuthorizationRestriction> EvaluateAsync(
            AuthorizationRestrictionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _capture.RestrictionEvaluations++;
            return ValueTask.FromResult(
                context.Features.Contains("tenant.allowed")
                    ? AuthorizationRestriction.Preserve()
                    : AuthorizationRestriction.Deny(
                        "tenant_denied",
                        "The caller cannot access this tenant."));
        }

        public void Dispose() => _capture.RestrictionDisposals++;
    }

    private sealed class LifetimeCapture
    {
        public int PolicyInstances { get; set; }

        public int PolicyEvaluations { get; set; }

        public int PolicyDisposals { get; set; }

        public int RestrictionInstances { get; set; }

        public int RestrictionEvaluations { get; set; }

        public int RestrictionDisposals { get; set; }
    }
}
