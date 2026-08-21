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

        await _targetClient.ConnectCapabilitiesAsync(
            CreateOptions(_targetClient, new CountingActionDispatcher()));

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

        client.Application.ActionEntries.Should().BeEmpty();
        _targetClient.Application.ActionEntries.Should().ContainSingle(entry =>
            entry.ModuleId == CrossSidecarModule.Id
            && entry.Descriptor.Key == CrossSidecarModule.OwnedAction.Key
            && entry.TerminalId == CrossSidecarModule.TerminalId);

        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            ApplicationSmokeModule.HostEntryCliName,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("cross-sidecar", "source-value"),
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
                + $"dispatcher={dispatcher.LastException}",
                ex);
        }

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text))
            + $"; dispatcher={dispatcher.LastException}"
            + $"; sourceFailure={client.CapabilitySession.RunFailure}"
            + $"; targetFailure={_targetClient.CapabilitySession.RunFailure}");
        result.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:cross-sidecar:"
            + "cross_sidecar_target_module|target|action|depth=1|parent=True|"
            + "caller=module-agent|trace=11111111-1111-4111-8111-111111111111|"
            + "idempotency=22222222-2222-4222-8222-222222222222");
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
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

    private static OutOfProcessCapabilityHostOptions CreateOptions(
        OutOfProcessModuleClient client,
        CountingActionDispatcher dispatcher,
        OutOfProcessCrossSidecarActionEntryCatalog? targetEntries = null,
        OutOfProcessActionDescriptorCatalog? descriptors = null) =>
        new(
            new EmptyStorageGateway(),
            dispatcher,
            client.CreateCapabilityGrant(DateTimeOffset.UtcNow.AddMinutes(2)),
            ["unused"],
            descriptors ?? new OutOfProcessActionDescriptorCatalog(),
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            targetEntries);

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

    private sealed class CountingActionDispatcher : IActionDispatcher
    {
        public int RunCalls { get; private set; }

        public int TerminalCalls { get; private set; }

        public Func<HostActionEntryRequestContext?>? HostContextFactory { get; set; }

        public Exception? LastException { get; private set; }

        public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            RunCalls++;
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
            return new CountingActionOutcome<TResult>(result);
        }

        public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct) =>
            (await RunAsync(descriptor, action, terminal, snapshot, ct)).Result;
    }

    private sealed class CountingActionOutcome<TResult>(TResult result) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;

        public TResult Result => result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => null;

        public ActionUncertainty? Uncertainty => null;
    }
}
