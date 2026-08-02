using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

[NonParallelizable]
public sealed class OutOfProcessActionProtocolTests
{
    private string _moduleDirectory = null!;
    private Uri _controlAddress = null!;
    private string _controlToken = null!;
    private OutOfProcessModuleServer _server = null!;
    private SidecarHostDescriptorCatalog _catalog = null!;
    private ServiceProvider _inProcessServices = null!;
    private ModuleContributionGraph _inProcessGraph = null!;
    private InProcessModuleInvoker _inProcessInvoker = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        _moduleDirectory = Path.Combine(root, "action-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_moduleDirectory);
        var moduleAssemblyName = Path.GetFileName(typeof(LifecycleSmokeModule).Assembly.Location);
        File.Copy(
            typeof(LifecycleSmokeModule).Assembly.Location,
            Path.Combine(_moduleDirectory, moduleAssemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(_moduleDirectory, "module.json"),
            $$"""
            {
              "id": "{{LifecycleSmokeModule.Id}}",
              "displayName": "Lifecycle Smoke",
              "version": "0.5.0-beta.2",
              "toolPrefix": "smoke",
              "entryAssembly": "{{moduleAssemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{typeof(LifecycleSmokeModule).FullName}}",
              "requestedHooks": [
                {
                  "target": "host.smoke",
                  "effects": ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]
                },
                {
                  "target": "smoke.*",
                  "effects": ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]
                },
                {
                  "target": "*",
                  "effects": ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]
                }
              ]
            }
            """,
            Encoding.UTF8);
        _controlAddress = await FindFreeAddressAsync();
        _controlToken = "smoke-token-" + Guid.NewGuid().ToString("N");
        _server = await OutOfProcessModuleServer.CreateAsync(
            _moduleDirectory,
            _controlAddress,
            _controlToken);
        await _server.StartAsync();
        _catalog = new SidecarHostDescriptorCatalog(
            [HostDescriptor()],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());

        var inProcessModule = new LifecycleSmokeModule();
        _inProcessGraph = SharpClawModuleCompiler.Compile(
            inProcessModule,
            InProcessManifest(),
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.InProcess,
                HostActions = [HostDescriptor()],
            });
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in _inProcessGraph.Services)
            services.Add(descriptor);
        services.AddSingleton(inProcessModule);
        services.AddSingleton(_inProcessGraph);
        _inProcessServices = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _inProcessInvoker = new InProcessModuleInvoker(_inProcessGraph, _inProcessServices);
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        OutOfProcessModuleServer? server = _server;
        _server = null!;
        if (server is not null)
            await server.DisposeAsync();
        if (_inProcessServices is not null)
            await _inProcessServices.DisposeAsync();
    }

    [Test, CancelAfter(15000)]
    public async Task DirectReplaceReturnsBeforeContinuation()
    {
        await using var client = await CreateClientAsync();
        var continuationCalled = false;

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, "replace", typed: true),
            (request, ct) =>
            {
                continuationCalled = true;
                return ValueTask.FromResult(CreateContinuation(request, "host"));
            });

        continuationCalled.Should().BeFalse();
        result.Completion.Kind.Should().Be(ActionOutcomeKind.Completed);
        result.Completion.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Value.Should().Be("sidecar:value");
    }

    [Test, CancelAfter(15000)]
    public async Task DirectFailReturnsBeforeContinuation()
    {
        await using var client = await CreateClientAsync();

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, "fail", typed: true),
            (_, _) => throw new AssertionException("The direct failure used the continuation."));

        result.Completion.Kind.Should().Be(ActionOutcomeKind.Failed);
        result.Completion.Error!.Code.Should().Be("smoke_failed");
    }

    [TestCase(LifecycleSmokeModule.ExactHookId, true)]
    [TestCase(LifecycleSmokeModule.CategoryHookId, false)]
    [TestCase(LifecycleSmokeModule.WildcardHookId, false)]
    [CancelAfter(15000)]
    public async Task ExactCategoryAndWildcardHooksUseOneContinuation(
        string hookId,
        bool typed)
    {
        await using var client = await CreateClientAsync();
        var calls = 0;

        var result = await client.InvokeActionAsync(
            CreateStart(client, hookId, "proceed", typed),
            (request, ct) =>
            {
                calls++;
                return ValueTask.FromResult(CreateContinuation(request, "host:value"));
            });

        calls.Should().Be(1);
        result.Completion.Kind.Should().Be(ActionOutcomeKind.Completed);
        result.Completion.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Value.Should().Be("host:value");
    }

    [Test, CancelAfter(15000)]
    public async Task SecondContinuationUseFailsTheHook()
    {
        await using var client = await CreateClientAsync();

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, "double", typed: true),
            (request, ct) => ValueTask.FromResult(CreateContinuation(request, "host")));

        result.Completion.Kind.Should().Be(ActionOutcomeKind.Failed);
        result.Completion.Error!.Code.Should().Be(SidecarProtocolErrors.ContinuationAlreadyUsed);
    }

    [TestCase("input", SidecarContinuationCommand.ContinueReplacement, ActionOutcomeKind.Completed, "replacement")]
    [TestCase("cancel", SidecarContinuationCommand.Cancel, ActionOutcomeKind.Cancelled, null)]
    [TestCase("defer", SidecarContinuationCommand.Defer, ActionOutcomeKind.Deferred, null)]
    [TestCase("repeat", SidecarContinuationCommand.Repeat, ActionOutcomeKind.Completed, "repeat")]
    [CancelAfter(15000)]
    public async Task TypedEffectsUseOneExactContinuationCommand(
        string mode,
        SidecarContinuationCommand expectedCommand,
        ActionOutcomeKind expectedKind,
        string? expectedValue)
    {
        await using var client = await CreateClientAsync();
        SidecarEffectRequest? observed = null;

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, mode, typed: true),
            (request, ct) =>
            {
                observed = request;
                return ValueTask.FromResult(CreateContinuation(request, "host"));
            });

        observed.Should().NotBeNull();
        observed!.Command.Should().Be(expectedCommand);
        result.Completion.Kind.Should().Be(expectedKind);
        if (expectedValue is not null)
        {
            result.Completion.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
                .Value.Should().Be(expectedValue);
        }
        if (expectedKind == ActionOutcomeKind.Cancelled)
            result.Completion.Error!.Code.Should().Be("smoke_cancelled");
        if (expectedKind == ActionOutcomeKind.Deferred)
            result.Continuation.Should().NotBeNull();
    }

    [Test, CancelAfter(15000)]
    public async Task UntypedWildcardReplacesWithoutContinuation()
    {
        await using var client = await CreateClientAsync();
        var continuationCalled = false;

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.WildcardHookId, "replace", typed: false),
            (request, ct) =>
            {
                continuationCalled = true;
                return ValueTask.FromResult(CreateContinuation(request, "host"));
            });

        continuationCalled.Should().BeFalse();
        result.Completion.Kind.Should().Be(ActionOutcomeKind.Completed);
        result.Completion.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Value.Should().Be("sidecar:untyped");
    }

    [Test, CancelAfter(15000)]
    public async Task UntypedWildcardCancellationUsesHostContinuation()
    {
        await using var client = await CreateClientAsync();
        SidecarEffectRequest? observed = null;

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.WildcardHookId, "cancel", typed: false),
            (request, ct) =>
            {
                observed = request;
                return ValueTask.FromResult(CreateContinuation(request, "host"));
            });

        observed!.Command.Should().Be(SidecarContinuationCommand.Cancel);
        result.Completion.Kind.Should().Be(ActionOutcomeKind.Cancelled);
        result.Completion.Error!.Code.Should().Be("smoke_cancelled");
    }

    [TestCaseSource(nameof(ActionSemanticsCases))]
    [Category("ModuleHostActionConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsReturnTheSameActionSemantics(
        ModuleHostingMode hostingMode,
        string mode,
        ActionConformanceResult expected)
    {
        var result = await InvokeConformanceAsync(
            hostingMode,
            LifecycleSmokeModule.ExactHookId,
            mode,
            typed: true);

        result.Should().Be(expected);
    }

    [TestCaseSource(nameof(SelectorConformanceCases))]
    [Category("ModuleHostActionConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsUseTheSameTypedAndUntypedSelectors(
        ModuleHostingMode hostingMode,
        string hookId,
        bool typed,
        string expectedValue)
    {
        var result = await InvokeConformanceAsync(
            hostingMode,
            hookId,
            "replace",
            typed);

        result.Should().Be(new ActionConformanceResult(
            ActionOutcomeKind.Completed,
            expectedValue,
            ErrorCode: null,
            HasContinuation: false));
    }

    [Test]
    public async Task InvalidSequenceAndExpiredDeadlineFailBeforeTransport()
    {
        await using var client = await CreateClientAsync();
        var invalidSequence = CreateStart(
            client,
            LifecycleSmokeModule.ExactHookId,
            "proceed",
            typed: true,
            sequence: 0);
        var expired = CreateStart(
            client,
            LifecycleSmokeModule.ExactHookId,
            "proceed",
            typed: true,
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1));

        var sequenceAct = async () => await client.InvokeActionAsync(
            invalidSequence,
            (request, ct) => ValueTask.FromResult(CreateContinuation(request, "host")));
        var deadlineAct = async () => await client.InvokeActionAsync(
            expired,
            (request, ct) => ValueTask.FromResult(CreateContinuation(request, "host")));

        (await sequenceAct.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(SidecarProtocolErrors.InvalidSequence);
        (await deadlineAct.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(SidecarProtocolErrors.DeadlineExceeded);
    }

    [Test, CancelAfter(5000)]
    public async Task BoundedQueueRejectsWorkAfterItsCapacity()
    {
        await using var queue = new BoundedExecutionQueue(capacity: 1, concurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TrySchedule(
            async _ =>
            {
                started.TrySetResult();
                await release.Task;
            },
            CancellationToken.None,
            out var first).Should().BeTrue();
        await started.Task;
        queue.TrySchedule(_ => Task.CompletedTask, CancellationToken.None, out var second)
            .Should().BeTrue();

        var accepted = queue.TrySchedule(
            _ => Task.CompletedTask,
            CancellationToken.None,
            out var rejected);

        accepted.Should().BeFalse();
        var rejectedAct = async () => await rejected;
        (await rejectedAct.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(SidecarProtocolErrors.ModuleBusy);
        release.TrySetResult();
        await Task.WhenAll(first, second);
    }

    private static IEnumerable<TestCaseData> ActionSemanticsCases()
    {
        var scenarios = new (string Mode, ActionConformanceResult Expected)[]
        {
            ("replace", new(ActionOutcomeKind.Completed, "sidecar:value", null, false)),
            ("fail", new(ActionOutcomeKind.Failed, null, "smoke_failed", false)),
            ("input", new(ActionOutcomeKind.Completed, "replacement", null, false)),
            ("cancel", new(ActionOutcomeKind.Cancelled, null, "smoke_cancelled", false)),
            ("defer", new(ActionOutcomeKind.Deferred, null, null, true)),
            ("repeat", new(ActionOutcomeKind.Completed, "repeat", null, false)),
            ("wrap", new(ActionOutcomeKind.Completed, "wrapped:host:value", null, false)),
            ("proceed", new(ActionOutcomeKind.Completed, "host:value", null, false)),
            ("double", new(
                ActionOutcomeKind.Failed,
                null,
                SidecarProtocolErrors.ContinuationAlreadyUsed,
                false)),
        };
        foreach (var hostingMode in Enum.GetValues<ModuleHostingMode>())
        {
            foreach (var scenario in scenarios)
            {
                yield return new TestCaseData(hostingMode, scenario.Mode, scenario.Expected)
                    .SetName($"Action_{hostingMode}_{scenario.Mode}");
            }
        }
    }

    private static IEnumerable<TestCaseData> SelectorConformanceCases()
    {
        var selectors = new (string HookId, bool Typed, string ExpectedValue)[]
        {
            (LifecycleSmokeModule.ExactHookId, true, "sidecar:value"),
            (LifecycleSmokeModule.CategoryHookId, false, "sidecar:untyped"),
            (LifecycleSmokeModule.WildcardHookId, false, "sidecar:untyped"),
        };
        foreach (var hostingMode in Enum.GetValues<ModuleHostingMode>())
        {
            foreach (var selector in selectors)
            {
                yield return new TestCaseData(
                        hostingMode,
                        selector.HookId,
                        selector.Typed,
                        selector.ExpectedValue)
                    .SetName($"Selector_{hostingMode}_{selector.HookId}");
            }
        }
    }

    private async ValueTask<ActionConformanceResult> InvokeConformanceAsync(
        ModuleHostingMode hostingMode,
        string hookId,
        string mode,
        bool typed)
    {
        if (hostingMode == ModuleHostingMode.OutOfProcess)
        {
            await using var client = await CreateClientAsync();
            var result = await client.InvokeActionAsync(
                CreateStart(client, hookId, mode, typed),
                (request, ct) => ValueTask.FromResult(CreateContinuation(request, "host:value")));
            return Normalize(result.Completion, result.Continuation);
        }

        var hook = _inProcessGraph.ActionHooks.Single(item =>
            string.Equals(item.HookId, hookId, StringComparison.Ordinal));
        if (typed)
        {
            var outcome = await _inProcessInvoker.InvokeActionAsync<SmokeAction, SmokeResult>(
                hook,
                TypedContext(mode),
                new ConformanceActionControl(),
                CancellationToken.None);
            return Normalize(outcome);
        }

        var untypedOutcome = await _inProcessInvoker.InvokeAnyActionAsync(
            hook,
            UntypedContext(mode, hookId),
            new ConformanceUntypedActionControl(),
            CancellationToken.None);
        return Normalize(untypedOutcome);
    }

    private ActionContext<SmokeAction> TypedContext(string mode)
    {
        var invocationId = Guid.NewGuid();
        return new ActionContext<SmokeAction>(
            invocationId,
            null,
            Guid.NewGuid(),
            invocationId,
            0,
            1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            LifecycleSmokeModule.HostAction.Key,
            LifecycleSmokeModule.HostAction.Key.Value,
            RequestPrincipal.Anonymous,
            new SmokeAction(mode, "value"),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot(_inProcessGraph.ContractHash, []));
    }

    private UntypedActionContext UntypedContext(string mode, string hookId)
    {
        var invocationId = Guid.NewGuid();
        var descriptor = UntypedDescriptor() with
        {
            AcceptsUnknownNonSensitiveSchemas =
                hookId != LifecycleSmokeModule.ExactHookId,
        };
        return new UntypedActionContext(
            invocationId,
            null,
            Guid.NewGuid(),
            invocationId,
            0,
            1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            descriptor.Key.Value,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            _inProcessGraph.ContractHash,
            descriptor,
            JsonSerializer.SerializeToElement(
                new SmokeAction(mode, "value"),
                OutOfProcessProtocolCodec.JsonOptions));
    }

    private static ActionConformanceResult Normalize(
        HookCompleted outcome,
        ContinuationToken? continuation) =>
        new(
            outcome.Kind,
            ReadResult(outcome.Result),
            outcome.Error?.Code,
            continuation is not null);

    private static ActionConformanceResult Normalize(IActionOutcome<SmokeResult> outcome) =>
        new(
            outcome.Kind,
            outcome.Result?.Value,
            outcome.Error?.Code,
            outcome.Continuation is not null);

    private static ActionConformanceResult Normalize(IUntypedActionOutcome outcome) =>
        new(
            outcome.Kind,
            ReadResult(outcome.Result),
            outcome.Error?.Code,
            outcome.Continuation is not null);

    private static string? ReadResult(JsonElement? result) =>
        result is { } value && value.TryGetProperty("value", out var property)
            ? property.GetString()
            : null;

    private Task<OutOfProcessModuleClient> CreateClientAsync() =>
        OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private static HookInvokeStart CreateStart(
        OutOfProcessModuleClient client,
        string hookId,
        string mode,
        bool typed,
        long sequence = 1,
        DateTimeOffset? deadline = null)
    {
        var expires = deadline ?? DateTimeOffset.UtcNow.AddSeconds(10);
        var invocationId = Guid.NewGuid();
        var baseDescriptor = UntypedDescriptor();
        var acceptsUnknown = hookId != LifecycleSmokeModule.ExactHookId;
        var grant = client.Authorization.ActionGrants.Single(item =>
            item.ActionKey == baseDescriptor.Key
            && item.Capabilities == LifecycleSmokeModule.HostCapabilities
            && item.AcceptUnknownSchemas == acceptsUnknown);
        var descriptor = baseDescriptor with
        {
            AcceptsUnknownNonSensitiveSchemas = grant.AcceptUnknownSchemas,
        };
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence,
            expires,
            client.HostLimits.ActionInputBytes,
            header => new HookInvokeStart(
                header,
                invocationId,
                null,
                Guid.NewGuid(),
                hookId,
                descriptor.Key,
                descriptor.Version,
                typed ? SidecarPayloadMode.Typed : SidecarPayloadMode.Untyped,
                JsonSerializer.SerializeToElement(
                    new SmokeAction(mode, "value"),
                    OutOfProcessProtocolCodec.JsonOptions),
                descriptor,
                grant,
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                new ContinuationHandle(
                    Guid.NewGuid(),
                    invocationId,
                    hookId,
                    expires,
                    sequence)));
    }

    private static (ContinuationAccepted Accepted, ContinuationOutcome Outcome) CreateContinuation(
        SidecarEffectRequest request,
        string value)
    {
        var limits = new SidecarPayloadLimits();
        var accepted = SidecarMessageHeaderFactory.CreateMeasured(
            request.Header.ProtocolVersion,
            request.Header.Sequence + 1,
            request.Header.Deadline,
            limits.ProtocolMessageBytes,
            header => new ContinuationAccepted(
                header,
                request.ContinuationHandleId,
                request.Command,
                ActionSafePoint.BeforeContinuation,
                ContinuationState.Claimed));
        var kind = request.Command switch
        {
            SidecarContinuationCommand.Cancel => ActionOutcomeKind.Cancelled,
            SidecarContinuationCommand.Defer => ActionOutcomeKind.Deferred,
            _ => ActionOutcomeKind.Completed,
        };
        var resultValue = request.Command switch
        {
            SidecarContinuationCommand.ContinueReplacement or SidecarContinuationCommand.Repeat =>
                request.Value is { } replacement
                    ? replacement.GetProperty("value").GetString()
                    : null,
            _ => value,
        };
        var outcome = SidecarMessageHeaderFactory.CreateMeasured(
            request.Header.ProtocolVersion,
            request.Header.Sequence + 2,
            request.Header.Deadline,
            limits.ActionResultBytes,
            header => new ContinuationOutcome(
                header,
                request.ContinuationHandleId,
                kind,
                ActionOutcomeCertainty.Certain,
                ActionSafePoint.BeforeTerminal,
                kind == ActionOutcomeKind.Completed
                    ? JsonSerializer.SerializeToElement(
                        new SmokeResult(resultValue!),
                        OutOfProcessProtocolCodec.JsonOptions)
                    : null,
                Error: kind == ActionOutcomeKind.Cancelled
                    ? new ExecutionError(
                        request.Code ?? "cancelled",
                        request.Message ?? "The action was cancelled.")
                    : null,
                Continuation: kind == ActionOutcomeKind.Deferred
                    ? new ContinuationToken(Guid.NewGuid(), "test-secret")
                    : null));
        return (accepted, outcome);
    }

    private static ModuleManifest InProcessManifest() =>
        new(
            LifecycleSmokeModule.Id,
            "Lifecycle Smoke",
            "0.5.0-beta.2",
            "smoke",
            "LifecycleSmokeModule.dll",
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            ModuleType: typeof(LifecycleSmokeModule).FullName,
            HostMode: ModuleManifestRuntimeInfo.HostModeInProcess,
            RequestedHooks:
            [
                new ModuleManifestHookRequest(
                    "host.smoke",
                    ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]),
                new ModuleManifestHookRequest(
                    "smoke.*",
                    ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]),
                new ModuleManifestHookRequest(
                    "*",
                    ["inspect", "replaceInput", "cancel", "replaceResult", "defer", "repeat", "wrap"]),
            ]);

    private static SidecarHostActionDescriptor HostDescriptor()
    {
        var descriptor = LifecycleSmokeModule.HostAction;
        return new SidecarHostActionDescriptor(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(SmokeAction)),
            ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(SmokeResult)),
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.ProtocolVersionRange!);
    }

    private static UntypedActionDescriptor UntypedDescriptor()
    {
        var descriptor = HostDescriptor();
        return new UntypedActionDescriptor(
            descriptor.ActionKey,
            descriptor.Version,
            descriptor.Category,
            descriptor.Capabilities,
            descriptor.InputSchema,
            descriptor.ResultSchema,
            descriptor.ContainsSensitiveData)
        {
            ProtocolVersionRange = descriptor.ProtocolVersionRange,
        };
    }

    private static async Task<Uri> FindFreeAddressAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new Uri($"http://127.0.0.1:{port}/");
        }
        finally
        {
            listener.Stop();
            await Task.CompletedTask;
        }
    }

    public sealed record ActionConformanceResult(
        ActionOutcomeKind Kind,
        string? Value,
        string? ErrorCode,
        bool HasContinuation);

    private sealed record ConformanceActionOutcome(
        ActionOutcomeKind Kind,
        SmokeResult? Result,
        ContinuationToken? Continuation,
        ExecutionError? Error,
        ActionUncertainty? Uncertainty = null) : IActionOutcome<SmokeResult>;

    private sealed record ConformanceUntypedActionOutcome(
        ActionOutcomeKind Kind,
        JsonElement? Result,
        ContinuationToken? Continuation,
        ExecutionError? Error,
        ActionUncertainty? Uncertainty = null) : IUntypedActionOutcome;

    private sealed class ConformanceActionControl : IActionControl<SmokeAction, SmokeResult>
    {
        private bool _continued;

        public ValueTask<IActionOutcome<SmokeResult>> ProceedAsync(CancellationToken ct) =>
            ContinueAsync(new SmokeResult("host:value"), ct);

        public ValueTask<IActionOutcome<SmokeResult>> ProceedWithInputAsync(
            ActionReplacement<SmokeAction> replacement,
            CancellationToken ct) =>
            ContinueAsync(new SmokeResult(replacement.Value.Value), ct);

        public IActionOutcome<SmokeResult> ReplaceResult(SmokeResult result, string reason) =>
            new ConformanceActionOutcome(
                ActionOutcomeKind.Completed,
                result,
                Continuation: null,
                Error: null);

        public IActionOutcome<SmokeResult> Cancel(string code, string message) =>
            new ConformanceActionOutcome(
                ActionOutcomeKind.Cancelled,
                Result: null,
                Continuation: null,
                Error: new ExecutionError(code, message));

        public IActionOutcome<SmokeResult> Fail(ExecutionError error) =>
            new ConformanceActionOutcome(
                ActionOutcomeKind.Failed,
                Result: null,
                Continuation: null,
                Error: error);

        public ValueTask<IActionOutcome<SmokeResult>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Continue(
                ActionOutcomeKind.Deferred,
                Result: null,
                new ContinuationToken(Guid.NewGuid(), "test-secret"),
                Error: null));
        }

        public ValueTask<IActionOutcome<SmokeResult>> RepeatAsync(
            ActionRepeatRequest<SmokeAction> request,
            CancellationToken ct) =>
            ContinueAsync(new SmokeResult(request.Value.Value), ct);

        private ValueTask<IActionOutcome<SmokeResult>> ContinueAsync(
            SmokeResult result,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Continue(
                ActionOutcomeKind.Completed,
                result,
                Continuation: null,
                Error: null));
        }

        private IActionOutcome<SmokeResult> Continue(
            ActionOutcomeKind kind,
            SmokeResult? Result,
            ContinuationToken? Continuation,
            ExecutionError? Error)
        {
            if (_continued)
            {
                return new ConformanceActionOutcome(
                    ActionOutcomeKind.Failed,
                    Result: null,
                    Continuation: null,
                    Error: new ExecutionError(
                        SidecarProtocolErrors.ContinuationAlreadyUsed,
                        "The continuation was already used."));
            }

            _continued = true;
            return new ConformanceActionOutcome(kind, Result, Continuation, Error);
        }
    }

    private sealed class ConformanceUntypedActionControl : IUntypedActionControl
    {
        private bool _continued;

        public ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken ct) =>
            ContinueAsync(Result("host:value"), ct);

        public ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
            JsonElement replacement,
            string reason,
            CancellationToken ct) =>
            ContinueAsync(Result(replacement.GetProperty("value").GetString()!), ct);

        public IUntypedActionOutcome ReplaceResult(JsonElement result, string reason) =>
            new ConformanceUntypedActionOutcome(
                ActionOutcomeKind.Completed,
                result,
                Continuation: null,
                Error: null);

        public IUntypedActionOutcome Cancel(string code, string message) =>
            new ConformanceUntypedActionOutcome(
                ActionOutcomeKind.Cancelled,
                Result: null,
                Continuation: null,
                Error: new ExecutionError(code, message));

        public IUntypedActionOutcome Fail(ExecutionError error) =>
            new ConformanceUntypedActionOutcome(
                ActionOutcomeKind.Failed,
                Result: null,
                Continuation: null,
                Error: error);

        public ValueTask<IUntypedActionOutcome> DeferAsync(
            ActionDeferRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Continue(
                ActionOutcomeKind.Deferred,
                Result: null,
                new ContinuationToken(Guid.NewGuid(), "test-secret"),
                Error: null));
        }

        public ValueTask<IUntypedActionOutcome> RepeatAsync(
            JsonElement replacement,
            string reason,
            TimeSpan? backoff,
            CancellationToken ct) =>
            ContinueAsync(Result(replacement.GetProperty("value").GetString()!), ct);

        private ValueTask<IUntypedActionOutcome> ContinueAsync(
            JsonElement result,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Continue(
                ActionOutcomeKind.Completed,
                result,
                Continuation: null,
                Error: null));
        }

        private IUntypedActionOutcome Continue(
            ActionOutcomeKind kind,
            JsonElement? Result,
            ContinuationToken? Continuation,
            ExecutionError? Error)
        {
            if (_continued)
            {
                return new ConformanceUntypedActionOutcome(
                    ActionOutcomeKind.Failed,
                    Result: null,
                    Continuation: null,
                    Error: new ExecutionError(
                        SidecarProtocolErrors.ContinuationAlreadyUsed,
                        "The continuation was already used."));
            }

            _continued = true;
            return new ConformanceUntypedActionOutcome(kind, Result, Continuation, Error);
        }

        private static JsonElement Result(string value) =>
            JsonSerializer.SerializeToElement(
                new SmokeResult(value),
                OutOfProcessProtocolCodec.JsonOptions);
    }

}
