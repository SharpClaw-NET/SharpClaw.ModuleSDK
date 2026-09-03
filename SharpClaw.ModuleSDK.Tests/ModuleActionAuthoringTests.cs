using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class ModuleActionAuthoringTests
{
    private static readonly Guid TerminalId = Guid.Parse("1d26b294-c89a-4b62-b0b0-0942a95e561b");

    [Test]
    public void DefineActionAddsCanonicalSchemasAndOneTypedTerminal()
    {
        var graph = Compile(new FluentModule(DefaultDescriptor()));
        var action = graph.Actions.Should().ContainSingle().Subject;
        var terminal = graph.ActionEntries.Should().ContainSingle().Subject;

        action.Descriptor.InputSchema.Should().Be(
            ModuleSchemaIdentity.ActionInput(action.Descriptor.Key, action.Descriptor.Version, typeof(SampleAction)));
        action.Descriptor.ResultSchema.Should().Be(
            ModuleSchemaIdentity.ActionResult(action.Descriptor.Key, action.Descriptor.Version, typeof(SampleResult)));
        terminal.TerminalId.Should().Be(TerminalId);
        terminal.TerminalType.Should().Be(typeof(SampleTerminal));
        terminal.ActionType.Should().Be(typeof(SampleAction));
        terminal.ResultType.Should().Be(typeof(SampleResult));
    }

    [Test]
    public void DefineActionPreservesExplicitSchemaAuthority()
    {
        var inputSchema = new JsonSchemaReference("sample.custom.input", 3, "CUSTOM-INPUT");
        var resultSchema = new JsonSchemaReference("sample.custom.result", 4, "CUSTOM-RESULT");
        var descriptor = DefaultDescriptor() with
        {
            InputSchema = inputSchema,
            ResultSchema = resultSchema,
        };

        var graph = Compile(new FluentModule(descriptor));
        var action = graph.Actions.Should().ContainSingle().Subject;
        var terminal = graph.ActionEntries.Should().ContainSingle().Subject;

        action.TypedDescriptor.Should().BeSameAs(descriptor);
        action.Descriptor.InputSchema.Should().Be(inputSchema);
        action.Descriptor.ResultSchema.Should().Be(resultSchema);
        terminal.Descriptor.InputSchemaHash.Should().Be(inputSchema.ContentHash);
        terminal.Descriptor.ResultSchemaHash.Should().Be(resultSchema.ContentHash);
    }

    [Test]
    public void DefineActionCanRecordAnActionWithoutATerminal()
    {
        var graph = Compile(new DefinitionOnlyModule());

        graph.Actions.Should().ContainSingle();
        graph.ActionEntries.Should().BeEmpty();
    }

    [Test]
    public void RawActionAndTerminalRegistrationRemainAvailable()
    {
        var graph = Compile(new RawModule());

        graph.Actions.Should().ContainSingle();
        graph.ActionEntries.Should().ContainSingle(entry =>
            entry.TerminalId == TerminalId
            && entry.TerminalType == typeof(SampleTerminal));
    }

    private static ModuleContributionGraph Compile(ISharpClawModule module) =>
        SharpClawModuleCompiler.Compile(
            module,
            options: new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.InProcess,
            });

    private static ActionDescriptor<SampleAction, SampleResult> DefaultDescriptor() =>
        new(
            new SharpClawActionKey("sample.authoring"),
            1,
            "sample",
            ActionInterceptionCapabilities.Inspect,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "sample.authoring"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            SafePoints = [ActionSafePoint.BeforeTerminal],
        };

    private sealed record SampleAction(string Value);

    private sealed record SampleResult(string Value);

    private sealed class SampleTerminal : IHostActionEntryTerminal<SampleAction, SampleResult>
    {
        public Guid TerminalId => ModuleActionAuthoringTests.TerminalId;

        public ValueTask<SampleResult> InvokeAsync(
            ActionContext<SampleAction> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SampleResult(context.Action.Value));
    }

    private sealed class FluentModule(ActionDescriptor<SampleAction, SampleResult> descriptor)
        : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("fluent_module", "Fluent Module", "fluent");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.DefineAction(descriptor).UseTerminal<SampleTerminal>(TerminalId);
    }

    private sealed class DefinitionOnlyModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("definition_module", "Definition Module", "definition");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.DefineAction(DefaultDescriptor());
    }

    private sealed class RawModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("raw_module", "Raw Module", "raw");

        public void Configure(ISharpClawModuleBuilder module)
        {
            var descriptor = DefaultDescriptor() with
            {
                InputSchema = ModuleSchemaIdentity.ActionInput(
                    DefaultDescriptor().Key,
                    DefaultDescriptor().Version,
                    typeof(SampleAction)),
                ResultSchema = ModuleSchemaIdentity.ActionResult(
                    DefaultDescriptor().Key,
                    DefaultDescriptor().Version,
                    typeof(SampleResult)),
            };
            module.Actions.Add(descriptor);
            module.AddActionEntry<SampleAction, SampleResult, SampleTerminal>(descriptor, TerminalId);
        }
    }
}
