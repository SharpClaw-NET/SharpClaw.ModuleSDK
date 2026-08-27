using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK.Testing.Tests;

public sealed class ModuleTestHostTests
{
    [Test]
    public async Task TestHostRunsExactCategoryAndWildcardHooksThroughCore()
    {
        var caller = new RequestPrincipal(
            "module-test-caller",
            "Module Test Caller",
            new HashSet<string>(["operator", "auditor"], StringComparer.Ordinal),
            IsAuthenticated: true);
        using var featureValue = JsonDocument.Parse("{\"mode\":\"test\"}");
        var features = new ExtensionFeatureSet(
        [
            new ExtensionFeature(
                "module-test.feature",
                1,
                "test_module",
                1024,
                featureValue.RootElement.Clone()),
        ]);
        await using var host = new SharpClawModuleTestBuilder()
            .AddModule(new TestModule(), Manifest())
            .AddHostAction(TestModule.Action)
            .ApproveSensitiveContributions("test_module")
            .UseExecutionContext(caller, features)
            .Build();

        ActionContext<TestAction>? terminalContext = null;
        CancellationToken terminalCancellation = default;
        var startedAt = DateTimeOffset.UtcNow;
        var result = await host.Action(TestModule.Action, new TestAction("start"))
            .WithTerminal((context, cancellationToken) =>
            {
                terminalContext = context;
                terminalCancellation = cancellationToken;
                return ValueTask.FromResult(new TestResult(context.Action.Value + ":terminal"));
            })
            .RunRequiredAsync();
        var finishedAt = DateTimeOffset.UtcNow;
        var log = host.CoreGraph.GetRequiredService<InvocationLog>();

        result.Value.Should().Be("typed:terminal");
        log.Entries.Should().Equal("typed-before", "category-before", "wildcard", "category-after", "typed-after");
        terminalContext.Should().NotBeNull();
        terminalContext!.InvocationId.Should().NotBe(Guid.Empty);
        terminalContext.ParentInvocationId.Should().BeNull();
        terminalContext.ActionKey.Should().Be(TestModule.Action.Key);
        terminalContext.Action.Should().Be(new TestAction("typed"));
        terminalContext.Caller.Should().BeEquivalentTo(caller);
        terminalContext.Features.Should().BeEquivalentTo(features);
        terminalContext.Snapshot.Should().BeSameAs(host.CoreGraph.ActionSnapshot);
        terminalContext.Deadline.Should().BeAfter(startedAt);
        terminalContext.Deadline.Should().BeBefore(finishedAt.AddSeconds(6));
        terminalCancellation.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public async Task TestHostDoesNotInvokeTerminalAfterPreCancellation()
    {
        await using var host = new SharpClawModuleTestBuilder()
            .AddModule(new TestModule(), Manifest())
            .AddHostAction(TestModule.Action)
            .ApproveSensitiveContributions("test_module")
            .Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var terminalCalls = 0;

        var outcome = await host.Action(TestModule.Action, new TestAction("cancelled"))
            .WithTerminal((_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new TestResult("unexpected"));
            })
            .RunAsync(cancellation.Token);

        outcome.Kind.Should().Be(ActionOutcomeKind.Cancelled);
        terminalCalls.Should().Be(0);
    }

    private static ModuleManifest Manifest() =>
        new(
            "test_module",
            "Test Module",
            "0.5.0-beta.2",
            "test",
            "TestModule.dll",
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            HostMode: ModuleManifestRuntimeInfo.HostModeInProcess,
            RequestedHooks:
            [
                new ModuleManifestHookRequest("test.action", ["inspect", "replaceInput", "replaceResult", "wrap"]),
                new ModuleManifestHookRequest("test.*", ["inspect", "replaceResult", "wrap"]),
                new ModuleManifestHookRequest("*", ["inspect", "wrap"]),
            ]);

    public sealed record TestAction(string Value);
    public sealed record TestResult(string Value);

    private sealed class TestModule : ISharpClawModule
    {
        public static ActionDescriptor<TestAction, TestResult> Action { get; } =
            new(
                new SharpClawActionKey("test.action"),
                1,
                "test",
                ActionInterceptionCapabilities.Inspect
                | ActionInterceptionCapabilities.ReplaceInput
                | ActionInterceptionCapabilities.ReplaceResult
                | ActionInterceptionCapabilities.Wrap,
                ContainsSensitiveData: false,
                HasIrreversibleEffects: false,
                new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "test.action"),
                ContinuationPolicy: null,
                TimeSpan.FromSeconds(5))
            {
                ProtocolVersionRange = ContractVersionRange.Exact(1),
                SafePoints =
                [
                    ActionSafePoint.BeforeContinuation,
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal,
                ],
            };

        public ModuleIdentity Identity { get; } = new("test_module", "Test Module", "test");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<InvocationLog>();
            module.Services.AddTransient<TypedHook>();
            module.Services.AddTransient<CategoryHook>();
            module.Services.AddTransient<WildcardHook>();
            module.Hooks.For(Action).Use<TypedHook>(
                ActionInterceptionCapabilities.Inspect
                | ActionInterceptionCapabilities.ReplaceInput
                | ActionInterceptionCapabilities.ReplaceResult
                | ActionInterceptionCapabilities.Wrap,
                new HookOrdering("test.typed", Before: ["test.category"]));
            module.Hooks.Category(
                    "test",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "test.*"),
                    ModuleSchemaIdentity.UntypedAction("result", "test.*"),
                    acceptUnknownNonSensitiveSchemas: true)
                .UseAny<CategoryHook>(
                    ActionInterceptionCapabilities.Inspect
                    | ActionInterceptionCapabilities.ReplaceResult
                    | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("test.category"));
            module.Hooks.AnyAction(
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "*"),
                    ModuleSchemaIdentity.UntypedAction("result", "*"),
                    sensitiveApprovalRequired: true)
                .UseAny<WildcardHook>(
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("test.wildcard", HookPriority.Low));
        }
    }

    private sealed class InvocationLog
    {
        public List<string> Entries { get; } = [];
    }

    private sealed class TypedHook(InvocationLog log) : IActionInterceptor<TestAction, TestResult>
    {
        public async ValueTask<IActionOutcome<TestResult>> InvokeAsync(
            ActionContext<TestAction> context,
            IActionControl<TestAction, TestResult> control,
            CancellationToken ct)
        {
            log.Entries.Add("typed-before");
            var outcome = await control.ProceedWithInputAsync(
                new ActionReplacement<TestAction>(new TestAction("typed"), "test replacement"),
                ct);
            log.Entries.Add("typed-after");
            return outcome;
        }
    }

    private sealed class CategoryHook(InvocationLog log) : IAnyActionInterceptor
    {
        public async ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct)
        {
            log.Entries.Add("category-before");
            var outcome = await control.ProceedAsync(ct);
            log.Entries.Add("category-after");
            return outcome;
        }
    }

    private sealed class WildcardHook(InvocationLog log) : IAnyActionInterceptor
    {
        public async ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct)
        {
            log.Entries.Add("wildcard");
            return await control.ProceedAsync(ct);
        }
    }
}
