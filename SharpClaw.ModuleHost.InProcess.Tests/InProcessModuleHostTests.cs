using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.InProcess.Tests;

public sealed class InProcessModuleHostTests
{
    [Test]
    public void AssemblyLoaderUsesTheExplicitModuleType()
    {
        var manifest = Manifest(
            typeof(LifecycleModule).Assembly.Location,
            typeof(LifecycleModule).FullName);
        var runtime = new ModuleManifestRuntimeInfo(
            ModuleManifestRuntimeInfo.DotNet,
            typeof(LifecycleModule).FullName,
            ModuleManifestRuntimeInfo.HostModeInProcess);

        var module = InProcessModuleAssemblyLoader.CreateModuleInstance(
            typeof(LifecycleModule).Assembly,
            manifest,
            runtime,
            typeof(LifecycleModule).Assembly.Location);

        module.Should().BeOfType<LifecycleModule>();
    }

    [Test]
    public async Task InvokerPassesTheHostIssuedControlWithoutReplacement()
    {
        var module = new ControlModule();
        var graph = SharpClawModuleCompiler.Compile(
            module,
            ControlManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
        IServiceCollection serviceCollection = new ServiceCollection();
        foreach (var descriptor in graph.Services)
            serviceCollection.Add(descriptor);
        await using var services = serviceCollection.BuildServiceProvider();
        var invoker = new InProcessModuleInvoker(graph, services);
        var control = new StubActionControl();

        var outcome = await invoker.InvokeActionAsync<TestAction, TestResult>(
            graph.ActionHooks.Single(),
            Context(),
            control,
            CancellationToken.None);

        services.GetRequiredService<ControlCapture>().Control.Should().BeSameAs(control);
        outcome.Kind.Should().Be(ActionOutcomeKind.Completed);
        outcome.Result.Should().Be(new TestResult("captured"));
    }

    [Test]
    public async Task ToolInvokerAcceptsNullConversationIdentity()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateToolInvocation(null);

        var result = await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            CancellationToken.None);

        result.Content.Should().Be("captured");
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(1);
        capture.Invocations.Should().Be(1);
        capture.LastInvocation.Should().BeSameAs(invocation);
    }

    [TestCase("empty-conversation")]
    [TestCase("missing-conversation")]
    [TestCase("changed-secondary-identity")]
    [TestCase("noncanonical-secondary-identity")]
    [TestCase("mismatched-tool-name")]
    [TestCase("expired-context")]
    [TestCase("invalid-caller-context")]
    [TestCase("empty-invocation-identity")]
    public async Task ToolInvokerRejectsInvalidInvocationBeforeHandlerCreation(string mutation)
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateInvalidToolInvocation(mutation);

        var act = async () => await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(0);
        capture.Invocations.Should().Be(0);
    }

    [Test]
    public async Task ToolInvokerRejectsPreCancellationBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateToolInvocation(null);

        var act = async () => await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(0);
        capture.Invocations.Should().Be(0);
    }

    [Test]
    public async Task HostLoadsStartsAndStopsOneInProcessModule()
    {
        var moduleDirectory = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.ModuleSDK",
            "in-process-host",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDirectory);
        try
        {
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                File.Copy(
                    source,
                    Path.Combine(moduleDirectory, Path.GetFileName(source)),
                    overwrite: true);
            }

            var manifest = Manifest(
                typeof(LifecycleModule).Assembly.Location,
                typeof(LifecycleModule).FullName);
            await File.WriteAllTextAsync(
                Path.Combine(moduleDirectory, "module.json"),
                JsonSerializer.Serialize(manifest));

            await RunLifecycleAsync(moduleDirectory);
        }
        finally
        {
            await DeleteModuleDirectoryAsync(moduleDirectory);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunLifecycleAsync(string moduleDirectory)
    {
        await using var host = await InProcessModuleHost.LoadAsync(moduleDirectory);
        host.Graph.HostingMode.Should().Be(ModuleHostingMode.InProcess);
        host.Module.Should().BeAssignableTo<ISharpClawModule>();
        GetStarted(host.Module).Should().BeFalse();

        await host.StartAsync("test-host");
        GetStarted(host.Module).Should().BeTrue();

        await host.StopAsync();
        GetStarted(host.Module).Should().BeFalse();
    }

    private static async Task DeleteModuleDirectoryAsync(string moduleDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                Directory.Delete(moduleDirectory, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(25);
            }
        }
    }

    private static bool GetStarted(ISharpClawModule module) =>
        (bool)(module.GetType().GetProperty(nameof(LifecycleModule.Started))?.GetValue(module)
            ?? throw new InvalidOperationException("The loaded test module has no Started property."));

    private static ModuleManifest Manifest(string assemblyPath, string? moduleType) =>
        new(
            "in_process_lifecycle",
            "In-process Lifecycle",
            "0.5.0-beta.2",
            "inprocess",
            Path.GetFileName(assemblyPath),
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            ModuleType: moduleType,
            HostMode: ModuleManifestRuntimeInfo.HostModeInProcess);

    private static ModuleManifest ControlManifest() =>
        new(
            "in_process_control",
            "In-process Control",
            "0.5.0-beta.2",
            "inprocess",
            "Control.dll",
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            HostMode: ModuleManifestRuntimeInfo.HostModeInProcess,
            RequestedHooks:
            [
                new ModuleManifestHookRequest("inprocess.control", ["replaceResult"]),
            ]);

    private static ToolFixture CreateToolFixture()
    {
        var graph = SharpClawModuleCompiler.Compile(
            new ControlModule(),
            ControlManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
        IServiceCollection serviceCollection = new ServiceCollection();
        foreach (var descriptor in graph.Services)
            serviceCollection.Add(descriptor);
        var services = serviceCollection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return new ToolFixture(graph, services, new InProcessModuleInvoker(graph, services));
    }

    private static ToolInvocation CreateToolInvocation(Guid? conversationId)
    {
        var invocationId = Guid.NewGuid();
        return new ToolInvocation(
            invocationId,
            conversationId,
            "tool-call",
            ControlModule.ToolName,
            JsonSerializer.SerializeToElement(new { value = "input" }),
            CreateToolContext(invocationId, conversationId));
    }

    private static ToolInvocation CreateInvalidToolInvocation(string mutation)
    {
        var valid = CreateToolInvocation(Guid.NewGuid());
        return mutation switch
        {
            "empty-conversation" => valid with { ConversationId = Guid.Empty },
            "missing-conversation" => valid with { ConversationId = null },
            "changed-secondary-identity" => valid with
            {
                HostActionContext = WithConversationIdentity(
                    valid.HostActionContext,
                    Guid.NewGuid().ToString("D")),
            },
            "noncanonical-secondary-identity" => valid with
            {
                HostActionContext = WithConversationIdentity(
                    valid.HostActionContext,
                    $"{{{valid.ConversationId!.Value:D}}}"),
            },
            "mismatched-tool-name" => valid with { ToolName = "inprocess.other" },
            "expired-context" => valid with
            {
                HostActionContext = valid.HostActionContext with
                {
                    Deadline = DateTimeOffset.UtcNow.AddSeconds(-2),
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                },
            },
            "invalid-caller-context" => valid with
            {
                HostActionContext = valid.HostActionContext with { Caller = null! },
            },
            "empty-invocation-identity" => valid with { InvocationId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
    }

    private static HostActionEntryRequestContext CreateToolContext(
        Guid invocationId,
        Guid? conversationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "tool-capability",
            HostActionEntryIngress.Tool,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RequestPrincipal("tool-user"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    ControlModule.ToolName,
                    conversationId?.ToString("D")),
                new HostActionEntryLineage(
                    new SharpClawActionKey("inprocess.tool"),
                    1,
                    "tool-descriptor-hash",
                    typeof(ToolInvocation).AssemblyQualifiedName!,
                    1,
                    "tool-input-schema-hash",
                    null,
                    null)),
        };
    }

    private static HostActionEntryRequestContext WithConversationIdentity(
        HostActionEntryRequestContext context,
        string? conversationIdentity) =>
        context with
        {
            Contribution = context.Contribution! with
            {
                IngressBinding = context.Contribution.IngressBinding with
                {
                    SecondaryIdentity = conversationIdentity,
                },
            },
        };

    private static ActionContext<TestAction> Context() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ControlModule.Action.Key,
            "host",
            RequestPrincipal.Anonymous,
            new TestAction("input"),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("test", []));

    public sealed class LifecycleModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("in_process_lifecycle", "In-process Lifecycle", "inprocess");

        public bool Started { get; private set; }

        public void Configure(ISharpClawModuleBuilder module)
        {
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken ct)
        {
            Started = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlModule : ISharpClawModule
    {
        public const string ToolName = "inprocess.tool";

        public static ToolDescriptor Tool { get; } =
            new(ToolName, "Captures one tool invocation.", ToolSchemas.EmptyObject);

        public static ActionDescriptor<TestAction, TestResult> Action { get; } =
            new(
                new SharpClawActionKey("inprocess.control"),
                1,
                "inprocess",
                ActionInterceptionCapabilities.ReplaceResult,
                ContainsSensitiveData: false,
                HasIrreversibleEffects: false,
                new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "inprocess.control"),
                ContinuationPolicy: null,
                TimeSpan.FromSeconds(5))
            {
                ProtocolVersionRange = ContractVersionRange.Exact(1),
                SafePoints = [ActionSafePoint.BeforeContinuation],
            };

        public ModuleIdentity Identity { get; } =
            new("in_process_control", "In-process Control", "inprocess");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<ControlCapture>();
            module.Services.AddSingleton<ToolInvocationCapture>();
            module.Services.AddTransient<CapturingActionHook>();
            module.Actions.Add(Action);
            module.Tools.Add<CapturingTool>(Tool);
            module.Hooks.For(Action).Use<CapturingActionHook>(
                ActionInterceptionCapabilities.ReplaceResult,
                new HookOrdering("inprocess.control.capture"));
        }
    }

    private sealed class CapturingActionHook(ControlCapture capture)
        : IActionInterceptor<TestAction, TestResult>
    {
        public ValueTask<IActionOutcome<TestResult>> InvokeAsync(
            ActionContext<TestAction> context,
            IActionControl<TestAction, TestResult> control,
            CancellationToken ct)
        {
            capture.Control = control;
            return ValueTask.FromResult(control.ReplaceResult(new TestResult("captured"), "test"));
        }
    }

    private sealed class ControlCapture
    {
        public IActionControl<TestAction, TestResult>? Control { get; set; }
    }

    private sealed class CapturingTool : IToolHandler
    {
        private readonly ToolInvocationCapture _capture;

        public CapturingTool(ToolInvocationCapture capture)
        {
            _capture = capture;
            _capture.Constructions++;
        }

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            _capture.Invocations++;
            _capture.LastInvocation = invocation;
            return ValueTask.FromResult(ToolResult.Text("captured"));
        }
    }

    private sealed class ToolInvocationCapture
    {
        public int Constructions { get; set; }

        public int Invocations { get; set; }

        public ToolInvocation? LastInvocation { get; set; }
    }

    private sealed record ToolFixture(
        ModuleContributionGraph Graph,
        ServiceProvider Services,
        InProcessModuleInvoker Invoker);

    private sealed class StubActionControl : IActionControl<TestAction, TestResult>
    {
        public ValueTask<IActionOutcome<TestResult>> ProceedAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> ProceedWithInputAsync(
            ActionReplacement<TestAction> replacement,
            CancellationToken ct) => throw new NotSupportedException();

        public IActionOutcome<TestResult> ReplaceResult(TestResult result, string reason) =>
            new StubActionOutcome(result);

        public IActionOutcome<TestResult> Cancel(string code, string message) =>
            throw new NotSupportedException();

        public IActionOutcome<TestResult> Fail(ExecutionError error) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> RepeatAsync(
            ActionRepeatRequest<TestAction> request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record StubActionOutcome(TestResult Result) : IActionOutcome<TestResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;

        TestResult? IActionOutcome<TestResult>.Result => Result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => null;

        public ActionUncertainty? Uncertainty => null;
    }

    private sealed record TestAction(string Value);

    private sealed record TestResult(string Value);
}
