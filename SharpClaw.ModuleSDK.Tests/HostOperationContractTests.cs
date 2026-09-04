using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK.HostOperations;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class HostOperationContractTests
{
    [Test]
    public void DescriptorsUseStableTypedNonrepeatableMutationBoundaries()
    {
        HostOperationActionDescriptors.ModuleList.Key.Value.Should().Be("host.module.list");
        HostOperationActionDescriptors.ModuleLifecycle.Key.Value.Should().Be("host.module.lifecycle");
        HostOperationActionDescriptors.ToolInvoke.Key.Value.Should().Be("host.tool.invoke");
        HostOperationActionDescriptors.ModuleLifecycle.HasIrreversibleEffects.Should().BeTrue();
        HostOperationActionDescriptors.ToolInvoke.HasIrreversibleEffects.Should().BeTrue();
        HostOperationActionDescriptors.ModuleLifecycle.RepeatPolicy.Kind.Should().Be(ActionRepeatKind.None);
        HostOperationActionDescriptors.ToolInvoke.RepeatPolicy.Kind.Should().Be(ActionRepeatKind.None);
        HostOperationActionDescriptors.ModuleLifecycle.Capabilities
            .Should().NotHaveFlag(ActionInterceptionCapabilities.Repeat);
        HostOperationActionDescriptors.ToolInvoke.Capabilities
            .Should().NotHaveFlag(ActionInterceptionCapabilities.ReplaceInput);
    }

    [Test]
    public void RequestsRejectChangedOrEmptyHostOperationIdentity()
    {
        new HostModuleLifecycleAction(HostModuleLifecycleOperation.Load, "sample_registration")
            .IsWellFormed.Should().BeTrue();
        new HostModuleLifecycleAction(HostModuleLifecycleOperation.Load, " sample_registration")
            .IsWellFormed.Should().BeFalse();

        var valid = new HostToolInvokeAction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "call-1",
            "sample_tool",
            JsonSerializer.SerializeToElement(new { value = 1 }));
        valid.IsWellFormed.Should().BeTrue();
        (valid with { ConversationId = Guid.Empty }).IsWellFormed.Should().BeFalse();
        (valid with { ToolName = "sample/tool" }).IsWellFormed.Should().BeFalse();
    }

    [Test]
    public void ModuleSummaryPreservesCanonicalExportedContractNames()
    {
        var state = new RegistrationStateResponse(
            "sample_registration",
            "Sample Module",
            "sm",
            true,
            "0.1.0",
            true,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        new HostModuleSummary(state, ["sample.contract"])
            .IsWellFormed.Should().BeTrue();
        new HostModuleSummary(state, ["sample.contract", "sample.contract"])
            .IsWellFormed.Should().BeFalse();
    }
}
