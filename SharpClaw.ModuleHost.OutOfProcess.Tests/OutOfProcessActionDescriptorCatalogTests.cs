using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;

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
}
