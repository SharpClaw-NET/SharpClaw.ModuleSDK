using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
                  "effects": ["inspect", "wrap", "replaceResult"]
                },
                {
                  "target": "smoke.*",
                  "effects": ["inspect", "wrap"]
                },
                {
                  "target": "*",
                  "effects": ["inspect", "wrap"]
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
        DisposeServer();
        if (Directory.Exists(_moduleDirectory))
            await DeleteDirectoryAsync(_moduleDirectory);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisposeServer()
    {
        OutOfProcessModuleServer? server = _server;
        _server = null!;
        if (server is not null)
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        result.Kind.Should().Be(ActionOutcomeKind.Completed);
        result.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Value.Should().Be("sidecar:value");
    }

    [Test, CancelAfter(15000)]
    public async Task DirectFailReturnsBeforeContinuation()
    {
        await using var client = await CreateClientAsync();

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, "fail", typed: true),
            (_, _) => throw new AssertionException("The direct failure used the continuation."));

        result.Kind.Should().Be(ActionOutcomeKind.Failed);
        result.Error!.Code.Should().Be("smoke_failed");
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
        result.Kind.Should().Be(ActionOutcomeKind.Completed);
        result.Result!.Value.Deserialize<SmokeResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Value.Should().Be("host:value");
    }

    [Test, CancelAfter(15000)]
    public async Task SecondContinuationUseFailsTheHook()
    {
        await using var client = await CreateClientAsync();

        var result = await client.InvokeActionAsync(
            CreateStart(client, LifecycleSmokeModule.ExactHookId, "double", typed: true),
            (request, ct) => ValueTask.FromResult(CreateContinuation(request, "host")));

        result.Kind.Should().Be(ActionOutcomeKind.Failed);
        result.Error!.Code.Should().Be(SidecarProtocolErrors.ContinuationAlreadyUsed);
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
        var capabilities = hookId == LifecycleSmokeModule.ExactHookId
            ? ActionInterceptionCapabilities.Inspect
              | ActionInterceptionCapabilities.Wrap
              | ActionInterceptionCapabilities.ReplaceResult
            : ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap;
        var grant = client.Authorization.ActionGrants.Single(item =>
            item.ActionKey == baseDescriptor.Key && item.Capabilities == capabilities);
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
        var outcome = SidecarMessageHeaderFactory.CreateMeasured(
            request.Header.ProtocolVersion,
            request.Header.Sequence + 2,
            request.Header.Deadline,
            limits.ActionResultBytes,
            header => new ContinuationOutcome(
                header,
                request.ContinuationHandleId,
                ActionOutcomeKind.Completed,
                ActionOutcomeCertainty.Certain,
                ActionSafePoint.BeforeTerminal,
                JsonSerializer.SerializeToElement(
                    new SmokeResult(value),
                    OutOfProcessProtocolCodec.JsonOptions)));
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

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(25);
            }
        }
    }
}
