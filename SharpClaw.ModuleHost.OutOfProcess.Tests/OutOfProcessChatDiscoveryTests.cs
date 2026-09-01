using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

public sealed class OutOfProcessChatDiscoveryTests
{
    [Test]
    public void GeneratedChatDiscoveryMatchesAllNeutralActionEntries()
    {
        var document = CreateDocument();

        OutOfProcessModuleClient.ValidateApplicationDiscovery(
            document.ToDiscovery(),
            document.Application);

        document.Application.Chat.Select(item => item.Kind).Should().Equal(
            SidecarChatContributionKind.ConversationResolver,
            SidecarChatContributionKind.ProfileResolver,
            SidecarChatContributionKind.HistoryLoad,
            SidecarChatContributionKind.ExchangeCommit,
            SidecarChatContributionKind.ContextContributor);
        document.Application.Chat.Should().OnlyContain(item =>
            document.Application.ActionEntries.Count(entry =>
                entry.TerminalId == item.TerminalId
                && entry.Descriptor == item.Descriptor) == 1);
        var history = document.Application.Chat.Where(item =>
            item.Kind is SidecarChatContributionKind.HistoryLoad
                or SidecarChatContributionKind.ExchangeCommit).ToArray();
        history.Select(item => item.RegistrationId).Distinct().Should().ContainSingle();
    }

    [Test]
    public void ValidationRejectsAChatKindWithAnotherDescriptor()
    {
        var document = CreateDocument();
        var chat = document.Application.Chat.ToArray();
        chat[0] = chat[0] with
        {
            Descriptor = chat.Single(item =>
                item.Kind == SidecarChatContributionKind.ExchangeCommit).Descriptor,
        };
        var application = document.Application with { Chat = chat };

        var act = () => OutOfProcessModuleClient.ValidateApplicationDiscovery(
            document.ToDiscovery(),
            application);

        act.Should().Throw<OutOfProcessProtocolException>()
            .Which.Code.Should().Be(SidecarProtocolErrors.MalformedMessage);
    }

    [Test]
    public void ValidationRejectsAnIncompleteConversationStorePair()
    {
        var document = CreateDocument();
        var application = document.Application with
        {
            Chat = document.Application.Chat.Where(item =>
                item.Kind != SidecarChatContributionKind.ExchangeCommit).ToArray(),
        };

        var act = () => OutOfProcessModuleClient.ValidateApplicationDiscovery(
            document.ToDiscovery(),
            application);

        act.Should().Throw<OutOfProcessProtocolException>()
            .Which.Code.Should().Be(SidecarProtocolErrors.MalformedMessage);
    }

    private static SidecarDiscoveryDocument CreateDocument()
    {
        var module = new ChatLifecycleSmokeModule();
        var graph = SharpClawModuleCompiler.Compile(
            module,
            new ModuleManifest(
                module.Identity.Id,
                module.Identity.DisplayName,
                "0.5.0-beta.1",
                module.Identity.ToolPrefix,
                "SharpClaw.ModuleHost.OutOfProcess.TestModule.dll",
                "0.5.0-beta.1",
                Runtime: ModuleManifestRuntimeInfo.DotNet,
                HostMode: ModuleManifestRuntimeInfo.HostModeSidecar),
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
            });
        return SidecarDiscoveryFactory.CreateDocument(
            graph,
            protocolVersion: 1,
            sequence: 1,
            DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
