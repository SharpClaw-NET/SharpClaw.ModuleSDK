using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

[NonParallelizable]
public sealed class OutOfProcessCrossSidecarProtocolTests
{
    private string _root = null!;
    private OutOfProcessModuleServer _sourceServer = null!;
    private OutOfProcessModuleServer _targetServer = null!;
    private OutOfProcessModuleClient _targetClient = null!;
    private Uri _sourceAddress = null!;
    private string _sourceToken = null!;
    private Uri _targetAddress = null!;
    private string _targetToken = null!;
    private CountingActionDispatcher _targetDispatcher = null!;

    [OneTimeSetUp]
    public async Task StartSidecars()
    {
        _root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");

        _sourceServer = await StartServerAsync(
            "cross-source",
            ApplicationSmokeModule.Id,
            typeof(ApplicationSmokeModule));
        _targetServer = await StartServerAsync(
            "cross-target",
            CrossSidecarModule.Id,
            typeof(CrossSidecarModule));

        var targetCatalog = new SidecarHostDescriptorCatalog(
            [],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());

        _targetClient = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            _targetAddress,
            _targetToken,
            targetCatalog);

        _targetDispatcher = new CountingActionDispatcher();
        var targetDescriptors = new OutOfProcessActionDescriptorCatalog();
        targetDescriptors.Add(CrossSidecarModule.OwnedAction);
        await _targetClient.ConnectCapabilitiesAsync(
            CreateOptions(
                _targetClient,
                _targetDispatcher,
                descriptors: targetDescriptors));

    }

    [OneTimeTearDown]
    public async Task StopSidecars()
    {
        if (_targetClient is not null)
            await _targetClient.DisposeAsync();
        if (_sourceServer is not null)
            await _sourceServer.DisposeAsync();
        if (_targetServer is not null)
            await _targetServer.DisposeAsync();
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarActionUsesTargetDescriptorAndTerminal()
    {
        await using var client = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            _sourceAddress,
            _sourceToken,
            new SidecarHostDescriptorCatalog(
                [
                    ToDescriptor(ApplicationSmokeModule.HostAction),
                    ToChildDescriptor(),
                ],
                [],
                OutOfProcessModuleHostProtocol.Version,
                new SidecarPayloadLimits()));
        var dispatcher = new CountingActionDispatcher();
        var sourceEntries = new OutOfProcessCrossSidecarActionEntryCatalog();
        sourceEntries.Add(_targetClient);
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.ChildAction);
        await client.ConnectCapabilitiesAsync(
            CreateOptions(client, dispatcher, sourceEntries, descriptors));

        client.Application.ActionEntries.Should().NotBeEmpty();
        _targetClient.Application.ActionEntries.Should().ContainSingle(entry =>
            entry.ModuleId == CrossSidecarModule.Id
            && entry.Descriptor.Key == CrossSidecarModule.OwnedAction.Key
            && entry.TerminalId == CrossSidecarModule.TerminalId);

        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            ApplicationSmokeModule.HostEntryCliName,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("cross-sidecar-root", "action"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            ApplicationSmokeModule.HostEntryTraceId,
            ApplicationSmokeModule.HostEntryIdempotencyKey,
            DateTimeOffset.UtcNow.AddMinutes(1));
        dispatcher.HostContextFactory = () => context;
        SidecarCliExecutionResponse result;
        try
        {
            result = await client.InvokeCliAsync(
                ApplicationSmokeModule.HostEntryCliName,
                ["cross-sidecar"],
                context);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cross-sidecar invocation failed: {ex}; "
                + $"sourceFailure={client.CapabilitySession.RunFailure}; "
                + $"targetFailure={_targetClient.CapabilitySession.RunFailure}; "
                + $"sourceHandledFailure={client.CapabilitySession.LastHandledFailure}; "
                + $"targetHandledFailure={_targetClient.CapabilitySession.LastHandledFailure}; "
                + $"sourceServerFailure={_sourceServer.CapabilityFailure}; "
                + $"targetServerFailure={_targetServer.CapabilityFailure}; "
                + $"dispatcher={dispatcher.LastException}",
                ex);
        }

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text))
            + $"; dispatcher={dispatcher.LastException}"
            + $"; sourceFailure={client.CapabilitySession.RunFailure}"
            + $"; targetFailure={_targetClient.CapabilitySession.RunFailure}"
            + $"; sourceHandledFailure={client.CapabilitySession.LastHandledFailure}"
            + $"; targetHandledFailure={_targetClient.CapabilitySession.LastHandledFailure}"
            + $"; sourceServerFailure={_sourceServer.CapabilityFailure}"
            + $"; targetServerFailure={_targetServer.CapabilityFailure}");
        result.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:cross-sidecar:"
            + "cross_sidecar_target_module|target|action|depth=1|parent=True|"
            + "caller=module-agent|trace=11111111-1111-4111-8111-111111111111|"
            + "idempotency=22222222-2222-4222-8222-222222222222|scope=active");
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.ExternalRunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        _targetDispatcher.RunCalls.Should().Be(1);
        _targetDispatcher.ExternalRunCalls.Should().Be(1);
        _targetDispatcher.TerminalCalls.Should().Be(1);
    }

    [Test, CancelAfter(30000)]
    public async Task RealCoreDispatcherExecutesCrossSidecarTargetThroughRegisteredSession()
    {
        var (targetServer, targetAddress, targetToken) =
            await StartStandaloneServerAsync(
                "real-core-cross-target",
                CrossSidecarModule.Id,
                typeof(CrossSidecarModule));
        await using var server = targetServer;
        var targetCatalog = new SidecarHostDescriptorCatalog(
            [],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());
        await using var targetClient = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            targetAddress,
            targetToken,
            targetCatalog);
        var registry = new KernelExternalAuthoritySessionRegistry();
        var graph = BuildRealCoreCrossTargetGraph();
        var targetDescriptors = new OutOfProcessActionDescriptorCatalog();
        targetDescriptors.Add(CrossSidecarModule.OwnedAction);
        var targetDispatcher = CreateRealCoreDispatcher(graph, registry);
        await targetClient.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            new EmptyStorageGateway(),
            targetDispatcher,
            targetClient.CreateCapabilityGrant(),
            ["unused"],
            targetDescriptors,
            graph.ActionSnapshot,
            new OutOfProcessHostActionEntryContextRegistry(),
            registry));

        var (sourceClient, sourceDispatcher) = await CreateSourceClientAsync(targetClient);
        await using (sourceClient)
        {
            var result = await InvokeSourceAsync(
                sourceClient,
                sourceDispatcher,
                "cross-sidecar");

            result.Result.Succeeded.Should().BeTrue(
                $"Real Core cross-sidecar failed with "
                + $"{result.Result.Error?.Code}: {result.Result.Error?.Message}; "
                + string.Join(" | ", result.Result.Output.Select(item => item.Text))
                + $"; targetFailure={targetClient.CapabilitySession.RunFailure}"
                + $"; targetHandledFailure={targetClient.CapabilitySession.LastHandledFailure}"
                + $"; targetServerFailure={server.CapabilityFailure}");
            result.Result.Output.Single().Text.Should().Contain(
                "host-entry:Completed:cross-sidecar:"
                + "cross_sidecar_target_module|target|action|depth=1|parent=True|"
                + "caller=module-agent|");
            result.Result.Output.Single().Text.Should().EndWith("|scope=active");
        }
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarUnknownTargetDoesNotDispatchTarget()
    {
        await using var unknownServer = await StartServerAsync(
            "cross-unknown-target",
            CrossSidecarModule.Id,
            typeof(CrossSidecarModule));
        var targetCatalog = new SidecarHostDescriptorCatalog(
            [],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());
        await using var unauthorizedTarget = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            _targetAddress,
            _targetToken,
            targetCatalog);
        var unauthorizedDispatcher = new CountingActionDispatcher();
        await unauthorizedTarget.ConnectCapabilitiesAsync(
            CreateOptions(unauthorizedTarget, unauthorizedDispatcher));

        var (client, dispatcher) = await CreateSourceClientAsync(unauthorizedTarget);
        await using (client)
        {
        var result = await InvokeSourceAsync(client, dispatcher, "cross-sidecar");

        result.Result.Succeeded.Should().BeFalse();
        unauthorizedDispatcher.RunCalls.Should().Be(0);
        unauthorizedDispatcher.TerminalCalls.Should().Be(0);
        result.Result.Output.Should().NotContain(item =>
            item.Text.Contains(CrossSidecarModule.Id, StringComparison.Ordinal));
        }
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarFailedTargetReturnsSignedOutcomeAndKeepsSession()
    {
        _targetDispatcher.Reset();
        var (client, dispatcher) = await CreateSourceClientAsync(_targetClient);
        await using (client)
        {

        var failed = await InvokeSourceAsync(client, dispatcher, "cross-sidecar-fail-observe");
        failed.Result.Succeeded.Should().BeTrue();
        failed.Result.Output.Single().Text.Should().Contain(
            "cross-sidecar-fail-observe:outcome=Failed;error=");
        failed.Result.Output.Single().Text.Should().Contain(";result=none");
        _targetDispatcher.RunCalls.Should().Be(1);

        var succeeded = await InvokeSourceAsync(client, dispatcher, "cross-sidecar");
        succeeded.Result.Succeeded.Should().BeTrue();
        _targetDispatcher.RunCalls.Should().Be(2);
        _targetDispatcher.TerminalCalls.Should().Be(1);
        }
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarCancelledTargetReturnsSignedOutcomeAndKeepsSession()
    {
        _targetDispatcher.Reset();
        _targetDispatcher.CancelOperations = true;
        try
        {
            var (client, dispatcher) = await CreateSourceClientAsync(_targetClient);
            await using (client)
            {

            var targetGenerationBefore = _targetClient.CapabilitySession.BindingGeneration;
            var cancelled = await InvokeSourceAsync(client, dispatcher, "cross-sidecar-cancel-observe");
            cancelled.Result.Succeeded.Should().BeTrue();
            cancelled.Result.Output.Single().Text.Should().Contain(
                "cross-sidecar-cancel-observe:outcome=Cancelled;error=none;result=none");
            _targetDispatcher.RunCalls.Should().Be(1);
            _targetDispatcher.TerminalCalls.Should().Be(0);

            _targetDispatcher.CancelOperations = false;
            var succeeded = await InvokeSourceAsync(client, dispatcher, "cross-sidecar");
            succeeded.Result.Succeeded.Should().BeTrue();
            _targetClient.CapabilitySession.BindingGeneration.Should().BeGreaterThan(targetGenerationBefore);
            _targetDispatcher.RunCalls.Should().Be(2);
            _targetDispatcher.TerminalCalls.Should().Be(1);
            }
        }
        finally
        {
            _targetDispatcher.CancelOperations = false;
        }
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarPreTerminalCancellationWaitsForBlockedTargetRotation()
    {
        TestContext.Progress.WriteLine("stage:begin");
        var (targetServer, targetAddress, targetToken) =
            await StartStandaloneServerAsync(
                "cross-blocked-target",
                CrossSidecarModule.Id,
                typeof(CrossSidecarModule));
        await using var server = targetServer;
        await using var targetClient = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            targetAddress,
            targetToken,
            new SidecarHostDescriptorCatalog(
                [],
                [],
                OutOfProcessModuleHostProtocol.Version,
                new SidecarPayloadLimits()));

        var targetDispatcher = new CountingActionDispatcher();
        var targetStorage = new BlockingStorageGateway();
        var targetDescriptors = new OutOfProcessActionDescriptorCatalog();
        targetDescriptors.Add(CrossSidecarModule.OwnedAction);
        await targetClient.ConnectCapabilitiesAsync(
            CreateOptions(
                targetClient,
                targetDispatcher,
                descriptors: targetDescriptors,
                storageGateway: targetStorage));
        TestContext.Progress.WriteLine("stage:target-connected");

        var (sourceClient, sourceDispatcher) = await CreateSourceClientAsync(targetClient);
        await using (sourceClient)
        {
            TestContext.Progress.WriteLine("stage:source-clients-connected");
            var generationBefore = targetClient.CapabilitySession.BindingGeneration;
            var blocked = InvokeSourceAsync(
                sourceClient,
                sourceDispatcher,
                "cross-sidecar",
                "block");
            TestContext.Progress.WriteLine("stage:block-started");
            try
            {
                await targetStorage.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                TestContext.Progress.WriteLine(
                    $"blocked-status: completed={blocked.IsCompleted}; faulted={blocked.IsFaulted}; "
                    + $"canceled={blocked.IsCanceled}");
                if (blocked.Status == TaskStatus.RanToCompletion)
                {
                    var result = blocked.Result;
                    TestContext.Progress.WriteLine(
                        $"blocked-result: succeeded={result.Result.Succeeded}; "
                        + $"error={result.Result.Error?.Code}:{result.Result.Error?.Message}; "
                        + $"output={string.Join('|', result.Result.Output.Select(item => item.Text))}");
                }
                if (blocked.Exception is not null)
                    TestContext.Progress.WriteLine(blocked.Exception.ToString());
                throw;
            }
            TestContext.Progress.WriteLine("stage:block-storage-started");

            targetDispatcher.CancelOperations = true;
            var cancelled = InvokeSourceAsync(
                sourceClient,
                sourceDispatcher,
                "cross-sidecar-cancel-observe");
            TestContext.Progress.WriteLine("stage:cancel-started");
            await Task.Delay(250);
            TestContext.Progress.WriteLine("stage:cancel-delayed");
            cancelled.IsCompleted.Should().BeFalse();
            targetClient.CapabilitySession.BindingGeneration.Should().Be(generationBefore);

            targetStorage.Release.TrySetResult();
            TestContext.Progress.WriteLine("stage:block-released");
            var blockedResult = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("stage:block-complete");
            blockedResult.Result.Succeeded.Should().BeTrue(
                $"Blocked target failed with {blockedResult.Result.Error?.Code}: "
                + $"{blockedResult.Result.Error?.Message}");

            var cancelledResult = await cancelled.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("stage:cancel-complete");
            cancelledResult.Result.Succeeded.Should().BeTrue(
                $"Cancelled target failed with {cancelledResult.Result.Error?.Code}: "
                + $"{cancelledResult.Result.Error?.Message}");
            cancelledResult.Result.Output.Single().Text.Should().Contain(
                "cross-sidecar-cancel-observe:outcome=Cancelled;error=none;result=none");

            targetDispatcher.CancelOperations = false;
            targetClient.CapabilitySession.BindingGeneration.Should().BeGreaterThan(generationBefore);

            var later = await InvokeSourceAsync(
                sourceClient,
                sourceDispatcher,
                "cross-sidecar").WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("stage:later-complete");
            later.Result.Succeeded.Should().BeTrue(
                $"Later relay failed with {later.Result.Error?.Code}: "
                + $"{later.Result.Error?.Message}");
            targetDispatcher.RunCalls.Should().Be(3);
            targetDispatcher.TerminalCalls.Should().Be(2);
        }
    }

    [Test, CancelAfter(30000)]
    public async Task CrossSidecarOutcomeMutationIsRejectedAndSessionRemainsUsable()
    {
        _targetDispatcher.Reset();
        var (client, dispatcher) = await CreateSourceClientAsync(_targetClient);
        await using (client)
        {
            client.CapabilitySession.TestCrossSidecarResponseMutator = response =>
            {
                var outcome = response.CrossSidecarOutcome
                    ?? throw new AssertionException("The response has no cross-sidecar outcome.");
                return response with
                {
                    CrossSidecarOutcome = outcome with
                    {
                        Authority = outcome.Authority with { Proof = "mutated-proof" },
                    },
                };
            };

            try
            {
                var rejected = await InvokeSourceAsync(client, dispatcher, "cross-sidecar");
                rejected.Result.Succeeded.Should().BeFalse();
            }
            finally
            {
                client.CapabilitySession.TestCrossSidecarResponseMutator = null;
            }

            _targetDispatcher.RunCalls.Should().Be(1);
            _targetDispatcher.TerminalCalls.Should().Be(1);

            var recovered = await InvokeSourceAsync(client, dispatcher, "cross-sidecar");
            recovered.Result.Succeeded.Should().BeTrue();
            _targetDispatcher.RunCalls.Should().Be(2);
            _targetDispatcher.TerminalCalls.Should().Be(2);
        }
    }

    private async Task<(OutOfProcessModuleClient Client, CountingActionDispatcher Dispatcher)> CreateSourceClientAsync(
        OutOfProcessModuleClient target)
    {
        var client = await OutOfProcessModuleClient.CreateAuthorizedAsync(
            _sourceAddress,
            _sourceToken,
            new SidecarHostDescriptorCatalog(
                [
                    ToDescriptor(ApplicationSmokeModule.HostAction),
                    ToChildDescriptor(),
                ],
                [],
                OutOfProcessModuleHostProtocol.Version,
                new SidecarPayloadLimits()));
        var dispatcher = new CountingActionDispatcher();
        var sourceEntries = new OutOfProcessCrossSidecarActionEntryCatalog();
        sourceEntries.Add(target);
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.ChildAction);
        await client.ConnectCapabilitiesAsync(
            CreateOptions(client, dispatcher, sourceEntries, descriptors));
        return (client, dispatcher);
    }

    private static async Task<SidecarCliExecutionResponse> InvokeSourceAsync(
        OutOfProcessModuleClient client,
        CountingActionDispatcher dispatcher,
        string mode,
        string value = "action")
    {
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            ApplicationSmokeModule.HostEntryCliName,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction($"{mode}-root", value),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            ApplicationSmokeModule.HostEntryTraceId,
            ApplicationSmokeModule.HostEntryIdempotencyKey,
            DateTimeOffset.UtcNow.AddMinutes(1));
        dispatcher.HostContextFactory = () => context;
        return await client.InvokeCliAsync(
            ApplicationSmokeModule.HostEntryCliName,
            [mode, value],
            context);
    }

    private async Task<OutOfProcessModuleServer> StartServerAsync(
        string name,
        string moduleId,
        Type moduleType)
    {
        var directory = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assemblyName = Path.GetFileName(typeof(ApplicationSmokeModule).Assembly.Location);
        var displayName = moduleType == typeof(ApplicationSmokeModule)
            ? "Application Smoke"
            : "Cross Sidecar Target";
        var toolPrefix = moduleType == typeof(ApplicationSmokeModule)
            ? "appsmoke"
            : "cross-target";
        var requestedHooks = moduleType == typeof(ApplicationSmokeModule)
            ? """
              [
                {
                  "target": "host.application.smoke",
                  "effects": ["inspect", "wrap", "cancel"]
                },
                {
                  "target": "host.application.child",
                  "effects": ["inspect", "wrap", "cancel"]
                },
                {
                  "target": "module.application.smoke",
                  "effects": ["inspect", "wrap", "cancel"]
                }
              ]
              """
            : "[]";
        File.Copy(
            typeof(ApplicationSmokeModule).Assembly.Location,
            Path.Combine(directory, assemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "module.json"),
            $$"""
            {
              "id": "{{moduleId}}",
              "displayName": "{{displayName}}",
              "version": "0.5.0-beta.15",
              "toolPrefix": "{{toolPrefix}}",
              "entryAssembly": "{{assemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{moduleType.FullName}}",
              "requestedHooks": {{requestedHooks}}
            }
            """,
            Encoding.UTF8);
        var address = await FindFreeAddressAsync();
        var token = name + "-token-" + Guid.NewGuid().ToString("N");
        var server = await OutOfProcessModuleServer.CreateAsync(directory, address, token);
        await server.StartAsync();
        if (name == "cross-source")
        {
            _sourceAddress = address;
            _sourceToken = token;
        }
        else
        {
            _targetAddress = address;
            _targetToken = token;
        }

        return server;
    }

    private async Task<(
        OutOfProcessModuleServer Server,
        Uri Address,
        string Token)> StartStandaloneServerAsync(
        string name,
        string moduleId,
        Type moduleType)
    {
        var directory = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assemblyName = Path.GetFileName(typeof(ApplicationSmokeModule).Assembly.Location);
        var displayName = moduleType == typeof(ApplicationSmokeModule)
            ? "Application Smoke"
            : "Cross Sidecar Target";
        var toolPrefix = moduleType == typeof(ApplicationSmokeModule)
            ? "appsmoke"
            : "cross-target";
        File.Copy(
            typeof(ApplicationSmokeModule).Assembly.Location,
            Path.Combine(directory, assemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "module.json"),
            $$"""
            {
              "id": "{{moduleId}}",
              "displayName": "{{displayName}}",
              "version": "0.5.0-beta.15",
              "toolPrefix": "{{toolPrefix}}",
              "entryAssembly": "{{assemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{moduleType.FullName}}",
              "requestedHooks": []
            }
            """,
            Encoding.UTF8);
        var address = await FindFreeAddressAsync();
        var token = name + "-token-" + Guid.NewGuid().ToString("N");
        var server = await OutOfProcessModuleServer.CreateAsync(directory, address, token);
        await server.StartAsync();
        return (server, address, token);
    }

    private static OutOfProcessCapabilityHostOptions CreateOptions(
        OutOfProcessModuleClient client,
        CountingActionDispatcher dispatcher,
        OutOfProcessCrossSidecarActionEntryCatalog? targetEntries = null,
        OutOfProcessActionDescriptorCatalog? descriptors = null,
        IModuleStorageGateway? storageGateway = null) =>
        new(
            storageGateway ?? new EmptyStorageGateway(),
            dispatcher,
            client.CreateCapabilityGrant(DateTimeOffset.UtcNow.AddMinutes(2)),
            ["unused"],
            descriptors ?? new OutOfProcessActionDescriptorCatalog(),
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry(),
            targetEntries);

    private static KernelGraph BuildRealCoreCrossTargetGraph()
    {
        var builder = new KernelGraphBuilder(false);
        using var services = new ServiceCollection().BuildServiceProvider();
        return builder.Compile(
            services,
            new KernelGraphCompileOptions
            {
                SupportedActionCapabilities = CrossSidecarModule.OwnedAction.Capabilities,
                ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [CrossSidecarModule.OwnedAction.Key.Value] =
                        CrossSidecarModule.OwnedAction.Capabilities,
                },
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [CrossSidecarModule.Id] = new Dictionary<
                        string,
                        ActionInterceptionCapabilities>
                    {
                        [CrossSidecarModule.OwnedAction.Key.Value] =
                            CrossSidecarModule.OwnedAction.Capabilities,
                    },
                },
            });
    }

    private static KernelActionDispatcher CreateRealCoreDispatcher(
        KernelGraph graph,
        KernelExternalAuthoritySessionRegistry registry)
    {
        var hostContext = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "real-core-cross-target",
            HostActionEntryIngress.Cli,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow.AddMinutes(1));
        return new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                hostContext),
            new InMemoryContinuationHost(),
            new NoOpCommittedEventWriter(),
            new IdentityResultSnapshotter(),
            new NoOpRepeatEvidenceAuthority(),
            registry);
    }

    private sealed class NoOpCommittedEventWriter : ICommittedEventWriter
    {
        public ValueTask PublishAsync<TEvent>(
            EventDescriptor<TEvent> descriptor,
            TEvent value,
            CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class IdentityResultSnapshotter : IKernelActionResultSnapshotter
    {
        public TResult Snapshot<TResult>(TResult result) => result;
    }

    private sealed class NoOpRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
    {
        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException("The real Core cross-sidecar graph has no repeat actions.");
    }

    private static SidecarHostActionDescriptor ToDescriptor(
        ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            ModuleSchemaIdentity.ActionInput(
                descriptor.Key,
                descriptor.Version,
                typeof(ApplicationSmokeAction)),
            ModuleSchemaIdentity.ActionResult(
                descriptor.Key,
                descriptor.Version,
                typeof(ApplicationSmokeResult)),
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.ProtocolVersionRange);

    private static SidecarHostActionDescriptor ToChildDescriptor() =>
        new(
            ApplicationSmokeModule.ChildAction.Key,
            ApplicationSmokeModule.ChildAction.Version,
            ApplicationSmokeModule.ChildAction.Category,
            ModuleSchemaIdentity.ActionInput(
                ApplicationSmokeModule.ChildAction.Key,
                ApplicationSmokeModule.ChildAction.Version,
                typeof(ApplicationChildAction)),
            ModuleSchemaIdentity.ActionResult(
                ApplicationSmokeModule.ChildAction.Key,
                ApplicationSmokeModule.ChildAction.Version,
                typeof(ApplicationChildResult)),
            ApplicationSmokeModule.ChildAction.Capabilities,
            ApplicationSmokeModule.ChildAction.ContainsSensitiveData,
            ApplicationSmokeModule.ChildAction.ProtocolVersionRange);

    private static async Task<Uri> FindFreeAddressAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        }
        finally
        {
            listener.Stop();
            await Task.CompletedTask;
        }
    }

    private sealed class EmptyStorageGateway : IModuleStorageGateway
    {
        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string moduleId,
            string storageName,
            ModuleStorageMutationAndOutboxRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRenewalRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRecoveryRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingStorageGateway : IModuleStorageGateway
    {
        public TaskCompletionSource InvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
        [
            new ModuleStorageContractDescriptor(
                CrossSidecarModule.Id,
                "target-store",
                [new ModuleStorageOperationDescriptor("echo")]),
        ];

        public async Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct)
        {
            moduleId.Should().Be(CrossSidecarModule.Id);
            storageName.Should().Be("target-store");
            operation.Should().Be("echo");
            InvocationStarted.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return JsonSerializer.SerializeToElement(new { value = "released" });
        }

        public Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string moduleId,
            string storageName,
            ModuleStorageMutationAndOutboxRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRenewalRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRecoveryRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CountingActionDispatcher : IActionDispatcher
    {
        public int RunCalls { get; private set; }

        public int ExternalRunCalls { get; private set; }

        public int TerminalCalls { get; private set; }

        public Func<HostActionEntryRequestContext?>? HostContextFactory { get; set; }

        public Exception? LastException { get; private set; }

        public bool CancelOperations { get; set; }

        public void Reset()
        {
            RunCalls = 0;
            TerminalCalls = 0;
            LastException = null;
        }

        public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            RunCalls++;
            if (CancelOperations
                && action is CrossSidecarAction { Operation: "cancel" })
            {
                return new CountingActionOutcome<TResult>(
                    ActionOutcomeKind.Cancelled,
                    default!,
                    new ExecutionError(
                        SidecarCapabilityErrors.Cancelled,
                        "The target action was cancelled."));
            }
            var hostContext = HostContextFactory?.Invoke();
            TResult result;
            try
            {
                result = await terminal(
                    new ActionContext<TAction>(
                        hostContext?.InvocationId ?? Guid.NewGuid(),
                        hostContext?.ParentInvocationId,
                        hostContext?.TraceId ?? Guid.NewGuid(),
                        hostContext?.IdempotencyKey ?? Guid.NewGuid(),
                        hostContext?.Depth ?? 0,
                        hostContext?.Attempt ?? 1,
                        hostContext?.Deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
                        descriptor.Key,
                        ApplicationSmokeModule.Id,
                        hostContext?.Caller ?? ApplicationSmokeModule.HostEntryCaller,
                        action,
                        hostContext?.Features ?? ExtensionFeatureSet.Empty,
                        snapshot),
                    ct);
            }
            catch (Exception ex)
            {
                LastException = ex;
                throw;
            }
            TerminalCalls++;
            return new CountingActionOutcome<TResult>(
                ActionOutcomeKind.Completed,
                result,
                null);
        }

        public ValueTask<IActionOutcome<TResult>> RunExternalAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken ct)
        {
            ExternalRunCalls++;
            return RunAsync(descriptor, action, terminal, snapshot, ct);
        }

        public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct) =>
            (await RunAsync(descriptor, action, terminal, snapshot, ct)).Result;

        public ValueTask<TResult> RunExternalRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken ct)
        {
            ExternalRunCalls++;
            return RunRequiredAsync(descriptor, action, terminal, snapshot, ct);
        }
    }

    private sealed class CountingActionOutcome<TResult>(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => kind;

        public TResult Result => result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => error;

        public ActionUncertainty? Uncertainty => null;
    }
}
