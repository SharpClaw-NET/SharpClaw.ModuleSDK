using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

public sealed class OutOfProcessActionDescriptorCatalogTests
{
    [TestCase("input")]
    [TestCase("result")]
    public void CatalogRejectsTypeIdentityMutationBeforeDispatch(string mutation)
    {
        var catalog = new OutOfProcessActionDescriptorCatalog();
        catalog.Add(ApplicationSmokeModule.HostAction);
        var registered = OutOfProcessActionDescriptorIdentity.Create(
            ApplicationSmokeModule.HostAction);
        var request = mutation switch
        {
            "input" => registered with
            {
                InputTypeIdentity = registered.InputTypeIdentity + ".Spoofed",
            },
            "result" => registered with
            {
                ResultTypeIdentity = registered.ResultTypeIdentity + ".Spoofed",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        request.DescriptorHash.Should().Be(registered.DescriptorHash);
        request.InputSchemaHash.Should().Be(registered.InputSchemaHash);
        request.InputSchemaVersion.Should().Be(registered.InputSchemaVersion);
        request.ResultSchemaHash.Should().Be(registered.ResultSchemaHash);
        request.ResultSchemaVersion.Should().Be(registered.ResultSchemaVersion);

        var dispatcherCalls = 0;
        var terminalCalls = 0;
        catalog.TryGet(request, out var registration).Should().BeFalse();
        dispatcherCalls.Should().Be(0);
        terminalCalls.Should().Be(0);
        registration.Should().BeNull();
    }

    [TestCase("category")]
    [TestCase("input-schema-version")]
    [TestCase("input-schema-hash")]
    [TestCase("result-schema-version")]
    [TestCase("result-schema-hash")]
    [TestCase("input-type")]
    [TestCase("result-type")]
    public void NestedRelayRejectsEveryTypedHookMetadataMutation(string mutation)
    {
        var hook = new ModuleActionHook(
            ApplicationSmokeModule.Id,
            SidecarHookTargetKind.Exact,
            ApplicationSmokeModule.HostAction.Key,
            ApplicationSmokeModule.HostAction.Category,
            typeof(ApplicationSmokeModule.AuthorizationHook),
            IsUntyped: false,
            new HookOrdering(ApplicationSmokeModule.HostActionHookId),
            ApplicationSmokeModule.HostCapabilities,
            ApplicationSmokeModule.HostAction.ProtocolVersionRange,
            ApplicationSmokeModule.HostAction.InputSchema!,
            ApplicationSmokeModule.HostAction.ResultSchema!,
            SensitiveWildcardApprovalRequired: false,
            AcceptUnknownNonSensitiveSchemas: false)
        {
            ActionType = typeof(ApplicationSmokeAction),
            ResultType = typeof(ApplicationSmokeResult),
        };
        var transport = new OutOfProcessModuleCapabilityTransport();
        transport.Initialize(
            ApplicationSmokeModule.Id,
            "graph",
            new SidecarPayloadLimits(),
            [hook]);
        var metadata = transport.ResolveNestedActionMetadata<
            ApplicationSmokeAction,
            ApplicationSmokeResult>(
            ApplicationSmokeModule.HostAction.Key,
            ApplicationSmokeModule.HostAction.Version);
        var registered = OutOfProcessActionDescriptorIdentity.Create(
            ApplicationSmokeModule.HostAction);
        var mutated = mutation switch
        {
            "category" => registered with { Category = registered.Category + ".spoofed" },
            "input-schema-version" => registered with
            {
                InputSchemaVersion = registered.InputSchemaVersion + 1,
            },
            "input-schema-hash" => registered with
            {
                InputSchemaHash = registered.InputSchemaHash + ".spoofed",
            },
            "result-schema-version" => registered with
            {
                ResultSchemaVersion = registered.ResultSchemaVersion + 1,
            },
            "result-schema-hash" => registered with
            {
                ResultSchemaHash = registered.ResultSchemaHash + ".spoofed",
            },
            "input-type" => registered with
            {
                InputTypeIdentity = registered.InputTypeIdentity + ".spoofed",
            },
            "result-type" => registered with
            {
                ResultTypeIdentity = registered.ResultTypeIdentity + ".spoofed",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        OutOfProcessModuleCapabilityTransport.MatchesNestedActionMetadata(
            mutated,
            metadata).Should().BeFalse();
        OutOfProcessModuleCapabilityTransport.MatchesNestedActionMetadata(
            registered,
            metadata).Should().BeTrue();
    }
}
