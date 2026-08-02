using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
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
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        OutOfProcessModuleServer? server = _server;
        _server = null!;
        if (server is not null)
            await server.DisposeAsync();
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

}
