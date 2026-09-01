using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

[NonParallelizable]
public sealed class OutOfProcessCapabilityTransportLifecycleTests
{
    private string _moduleDirectory = null!;
    private OutOfProcessModuleServer _server = null!;
    private Uri _controlAddress = null!;
    private string _controlToken = null!;

    [SetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        _moduleDirectory = Path.Combine(
            root,
            "transport-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_moduleDirectory);
        var moduleAssemblyName = Path.GetFileName(
            typeof(LifecycleSmokeModule).Assembly.Location);
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
        _controlToken = "transport-lifecycle-" + Guid.NewGuid().ToString("N");
        _server = await OutOfProcessModuleServer.CreateAsync(
            _moduleDirectory,
            _controlAddress,
            _controlToken);
        await _server.StartAsync();
    }

    [TearDown]
    public async Task StopServer()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Test, CancelAfter(15000)]
    public async Task DisposedTransportRejectsWaitingBind()
    {
        await using var first = await CreateClientAsync();
        await using var second = await CreateClientAsync();
        await first.ConnectCapabilitiesAsync(CreateOptions(first));

        var waitingBind = second.ConnectCapabilitiesAsync(CreateOptions(second));
        await _server.CapabilityTransport.ConnectionWaitObserved
            .WaitAsync(TimeSpan.FromSeconds(5));

        await _server.CapabilityTransport.DisposeAsync();

        var act = async () => await waitingBind;
        await act.Should().ThrowAsync<Exception>();
        waitingBind.IsCompletedSuccessfully.Should().BeFalse();
    }

    [Test, CancelAfter(15000)]
    public async Task ReleasedConnectionAcceptsNextBindAndKeepsSessionUsable()
    {
        await using var first = await CreateClientAsync();
        await using var second = await CreateClientAsync();
        await first.ConnectCapabilitiesAsync(CreateOptions(first));

        var waitingBind = second.ConnectCapabilitiesAsync(CreateOptions(second));
        await _server.CapabilityTransport.ConnectionWaitObserved
            .WaitAsync(TimeSpan.FromSeconds(5));
        await first.DisposeAsync();
        await waitingBind;

        var start = CreateStart(second);
        var result = await second.InvokeActionAsync(
            start.Start,
            (request, _) => ValueTask.FromResult(
                CreateContinuation(
                    start.HandleId,
                    start.Start.Header.Deadline,
                    request.Header.Sequence + 1)));

        result.Completion.Kind.Should().Be(ActionOutcomeKind.Completed);
    }

    private async Task<OutOfProcessModuleClient> CreateClientAsync()
    {
        var catalog = new SidecarHostDescriptorCatalog(
            [HostDescriptor()],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());
        return await OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            catalog);
    }

    private static OutOfProcessCapabilityHostOptions CreateOptions(
        OutOfProcessModuleClient client) =>
        new(
            new NoOpStorageGateway(),
            new NoOpActionDispatcher(),
            client.CreateCapabilityGrant(),
            ["lifecycle-store"],
            new OutOfProcessActionDescriptorCatalog(),
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry());

    private static (HookInvokeStart Start, Guid HandleId) CreateStart(
        OutOfProcessModuleClient client) =>
        CreateStartCore(client);

    private static (HookInvokeStart Start, Guid HandleId) CreateStartCore(
        OutOfProcessModuleClient client)
    {
        var invocationId = Guid.NewGuid();
        var handleId = Guid.NewGuid();
        var start = SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            client.HostLimits.ActionInputBytes,
            header => new HookInvokeStart(
                header,
                invocationId,
                null,
                Guid.NewGuid(),
                LifecycleSmokeModule.ExactHookId,
                LifecycleSmokeModule.HostAction.Key,
                LifecycleSmokeModule.HostAction.Version,
                SidecarPayloadMode.Typed,
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new SmokeAction("proceed", "value"),
                    OutOfProcessProtocolCodec.JsonOptions),
                new UntypedActionDescriptor(
                    LifecycleSmokeModule.HostAction.Key,
                    LifecycleSmokeModule.HostAction.Version,
                    LifecycleSmokeModule.HostAction.Category,
                    LifecycleSmokeModule.HostAction.Capabilities,
                    ModuleSchemaIdentity.ActionInput(
                        LifecycleSmokeModule.HostAction.Key,
                        LifecycleSmokeModule.HostAction.Version,
                        typeof(SmokeAction)),
                    ModuleSchemaIdentity.ActionResult(
                        LifecycleSmokeModule.HostAction.Key,
                        LifecycleSmokeModule.HostAction.Version,
                        typeof(SmokeResult)),
                    LifecycleSmokeModule.HostAction.ContainsSensitiveData)
                {
                    ProtocolVersionRange = LifecycleSmokeModule.HostAction.ProtocolVersionRange,
                },
                client.Authorization.ActionGrants.First(item =>
                    item.ActionKey == LifecycleSmokeModule.HostAction.Key),
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                new ContinuationHandle(
                    handleId,
                    invocationId,
                    LifecycleSmokeModule.ExactHookId,
                    header.Deadline,
                    1)));
        return (start, handleId);
    }

    private static (ContinuationAccepted Accepted, ContinuationOutcome Outcome)
        CreateContinuation(
            Guid handleId,
            DateTimeOffset deadline,
            long acceptedSequence)
    {
        var limits = new SidecarPayloadLimits();
        var accepted = SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            acceptedSequence,
            deadline,
            limits.ProtocolMessageBytes,
            header => new ContinuationAccepted(
                header,
                handleId,
                SidecarContinuationCommand.ContinueOriginal,
                ActionSafePoint.BeforeContinuation,
                ContinuationState.Claimed));
        var outcome = SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            acceptedSequence + 1,
            accepted.Header.Deadline,
            limits.ActionResultBytes,
            header => new ContinuationOutcome(
                header,
                handleId,
                ActionOutcomeKind.Completed,
                ActionOutcomeCertainty.Certain,
                ActionSafePoint.BeforeTerminal,
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new SmokeResult("later"),
                    OutOfProcessProtocolCodec.JsonOptions),
                null,
                null));
        return (accepted, outcome);
    }

    private static SidecarHostActionDescriptor HostDescriptor() =>
        new(
            LifecycleSmokeModule.HostAction.Key,
            LifecycleSmokeModule.HostAction.Version,
            LifecycleSmokeModule.HostAction.Category,
            ModuleSchemaIdentity.ActionInput(
                LifecycleSmokeModule.HostAction.Key,
                LifecycleSmokeModule.HostAction.Version,
                typeof(SmokeAction)),
            ModuleSchemaIdentity.ActionResult(
                LifecycleSmokeModule.HostAction.Key,
                LifecycleSmokeModule.HostAction.Version,
                typeof(SmokeResult)),
            LifecycleSmokeModule.HostAction.Capabilities,
            LifecycleSmokeModule.HostAction.ContainsSensitiveData,
            LifecycleSmokeModule.HostAction.ProtocolVersionRange!);

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

    private sealed class NoOpActionDispatcher : IActionDispatcher
    {
        public ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot actionSnapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IActionOutcome<TResult>>(
                new InvalidOperationException("The lifecycle test dispatcher must not execute."));

        public ValueTask<IActionOutcome<TResult>> RunExternalAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot actionSnapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken cancellationToken) =>
            RunAsync(descriptor, action, terminal, actionSnapshot, cancellationToken);

        public ValueTask<IActionOutcome<JsonElement>> RunExternalSerializedAsync(
            SidecarActionDefinition definition,
            SidecarActionDescriptorIdentity identity,
            JsonElement action,
            Func<ActionContext<JsonElement>, CancellationToken, ValueTask<JsonElement>> terminal,
            ActionPipelineSnapshot actionSnapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IActionOutcome<JsonElement>>(
                new InvalidOperationException("The lifecycle test dispatcher must not execute."));

        public ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot actionSnapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<TResult>(
                new InvalidOperationException("The lifecycle test dispatcher must not execute."));

        public ValueTask<TResult> RunExternalRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot actionSnapshot,
            SidecarExternalActionDispatchAuthority authority,
            CancellationToken cancellationToken) =>
            RunRequiredAsync(descriptor, action, terminal, actionSnapshot, cancellationToken);
    }

    private sealed class NoOpStorageGateway : IModuleStorageGateway
    {
        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() => [];

        public Task<System.Text.Json.JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            System.Text.Json.JsonElement parameters,
            CancellationToken cancellationToken = default) =>
            Task.FromException<System.Text.Json.JsonElement>(
                new InvalidOperationException("The lifecycle test storage gateway must not execute."));

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ModuleStorageClaimResult<T>>(
                new InvalidOperationException("The lifecycle test storage gateway must not execute."));

        public Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string moduleId,
            string storageName,
            ModuleStorageMutationAndOutboxRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ModuleStorageMutationAndOutboxResult>(
                new InvalidOperationException("The lifecycle test storage gateway must not execute."));

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRenewalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ModuleStorageClaimRenewalResult>(
                new InvalidOperationException("The lifecycle test storage gateway must not execute."));

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRecoveryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ModuleStorageClaimRecoveryResult>(
                new InvalidOperationException("The lifecycle test storage gateway must not execute."));
    }
}
