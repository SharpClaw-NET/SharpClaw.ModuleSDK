using System.Net;
using System.Net.Http.Headers;
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
public sealed class OutOfProcessApplicationProtocolTests
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
        _moduleDirectory = Path.Combine(root, "application-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_moduleDirectory);
        var moduleAssemblyName = Path.GetFileName(typeof(ApplicationSmokeModule).Assembly.Location);
        File.Copy(
            typeof(ApplicationSmokeModule).Assembly.Location,
            Path.Combine(_moduleDirectory, moduleAssemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(_moduleDirectory, "module.json"),
            $$"""
            {
              "id": "{{ApplicationSmokeModule.Id}}",
              "displayName": "Application Smoke",
              "version": "0.5.0-beta.3",
              "toolPrefix": "appsmoke",
              "entryAssembly": "{{moduleAssemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{typeof(ApplicationSmokeModule).FullName}}",
              "requestedHooks": [
                {
                  "target": "host.application.smoke",
                  "effects": ["inspect", "wrap", "cancel"]
                },
                {
                  "target": "module.application.smoke",
                  "effects": ["inspect", "wrap", "cancel"]
                }
              ]
            }
            """,
            Encoding.UTF8);
        _controlAddress = await FindFreeAddressAsync();
        _controlToken = "application-token-" + Guid.NewGuid().ToString("N");
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
        var server = _server;
        _server = null!;
        if (server is not null)
            await server.DisposeAsync();
    }

    [Test, CancelAfter(15000)]
    public async Task ApplicationDiscoveryAndCliUseTheSameModuleGraph()
    {
        await using var client = await CreateClientAsync();

        client.Application.ModuleId.Should().Be(client.Discovery.ModuleId);
        client.Application.ContractHash.Should().Be(client.Discovery.ContractHash);
        client.Discovery.ActionDefinitions.Should().ContainSingle(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        client.Discovery.Actions.Should().ContainSingle(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        client.Application.Endpoints.Should().ContainSingle(endpoint =>
            endpoint.TypeName == typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName);
        client.Application.CliCommands.Should().ContainSingle(command =>
            command.Descriptor.Name == ApplicationSmokeModule.CliName);

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.CliName,
            ["identity"],
            new RequestPrincipal("test-user"));

        result.ModuleId.Should().Be(client.Discovery.ModuleId);
        result.ContractHash.Should().Be(client.Discovery.ContractHash);
        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}");
        result.Result.Output.Single().Text.Should().Be(
            $"{ApplicationSmokeModule.Id}|{ApplicationSmokeModule.Id}|{client.Discovery.ContractHash}|{ApplicationSmokeModule.CliName}");
    }

    [Test, CancelAfter(15000)]
    public async Task CapabilityChannelDelegatesToTheExactHostSingletons()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors));

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            [],
            new RequestPrincipal("capability-test"));

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}");
        result.Result.Output.Single().Text.Should().Contain("contracts:1");
        result.Result.Output.Single().Text.Should().Contain("storage:{\"value\":\"storage\"}");
        result.Result.Output.Single().Text.Should().Contain("action:terminal:action");
        storage.ListContractsCalls.Should().Be(1);
        storage.InvokeCalls.Should().Be(1);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
    }

    [Test, CancelAfter(15000)]
    public async Task AuthorizationHookAllowsOneTerminalCallAndDeniesBeforeTheTerminalCall()
    {
        await using var client = await CreateClientAsync();
        var allowedTerminalCalls = 0;
        var allowed = await client.InvokeActionAsync(
            CreateStart(client, "allow"),
            (request, ct) =>
            {
                if (request.Command == SidecarContinuationCommand.ContinueOriginal)
                    allowedTerminalCalls++;
                return ValueTask.FromResult(CreateContinuation(request, "allowed"));
            });

        allowed.Completion.Kind.Should().Be(
            ActionOutcomeKind.Completed,
            $"action error {allowed.Completion.Error?.Code}: {allowed.Completion.Error?.Message}");
        allowedTerminalCalls.Should().Be(1);

        var deniedTerminalCalls = 0;
        var denied = await client.InvokeActionAsync(
            CreateStart(client, "deny"),
            (request, ct) =>
            {
                if (request.Command == SidecarContinuationCommand.ContinueOriginal)
                    deniedTerminalCalls++;
                return ValueTask.FromResult(CreateContinuation(request, "denied"));
            });

        denied.Completion.Kind.Should().Be(ActionOutcomeKind.Cancelled);
        deniedTerminalCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task SelfOwnedActionGrantAllowsOneTerminalCallAndDeniesBeforeTheTerminalCall()
    {
        await using var client = await CreateClientAsync();
        var grant = client.Authorization.ActionGrants.Single(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        grant.ActionVersion.Should().Be(ApplicationSmokeModule.OwnedAction.Version);
        grant.Capabilities.Should().Be(ApplicationSmokeModule.HostCapabilities);
        grant.SensitiveApproved.Should().BeFalse();

        var allowedTerminalCalls = 0;
        var allowed = await client.InvokeActionAsync(
            CreateStart(
                client,
                ApplicationSmokeModule.OwnedAction,
                ApplicationSmokeModule.OwnedActionHookId,
                "allow"),
            (request, ct) =>
            {
                if (request.Command == SidecarContinuationCommand.ContinueOriginal)
                    allowedTerminalCalls++;
                return ValueTask.FromResult(CreateContinuation(request, "allowed"));
            });

        allowed.Completion.Kind.Should().Be(
            ActionOutcomeKind.Completed,
            $"action error {allowed.Completion.Error?.Code}: {allowed.Completion.Error?.Message}");
        allowedTerminalCalls.Should().Be(1);

        var deniedTerminalCalls = 0;
        var denied = await client.InvokeActionAsync(
            CreateStart(
                client,
                ApplicationSmokeModule.OwnedAction,
                ApplicationSmokeModule.OwnedActionHookId,
                "deny"),
            (request, ct) =>
            {
                if (request.Command == SidecarContinuationCommand.ContinueOriginal)
                    deniedTerminalCalls++;
                return ValueTask.FromResult(CreateContinuation(request, "denied"));
            });

        denied.Completion.Kind.Should().Be(ActionOutcomeKind.Cancelled);
        deniedTerminalCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task SelfOwnedDefinitionCannotShadowAHostActionKey()
    {
        await using var client = await CreateClientAsync();
        var definition = client.Discovery.ActionDefinitions.Single(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key) with
        {
            ActionKey = ApplicationSmokeModule.HostAction.Key,
        };
        var discovery = client.Discovery with
        {
            ActionDefinitions = client.Discovery.ActionDefinitions
                .Select(item => item.ActionKey == ApplicationSmokeModule.OwnedAction.Key
                    ? definition
                    : item)
                .ToArray(),
        };

        AssertDiscoveryRejected(discovery, SidecarProtocolErrors.ShadowedHostKey);
    }

    [Test, CancelAfter(15000)]
    public async Task DuplicateSelfOwnedDefinitionsAreRejectedBeforeGrantExtraction()
    {
        await using var client = await CreateClientAsync();
        var definition = client.Discovery.ActionDefinitions.Single(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        var discovery = client.Discovery with
        {
            ActionDefinitions = client.Discovery.ActionDefinitions
                .Append(definition)
                .ToArray(),
        };

        AssertDiscoveryRejected(discovery, SidecarProtocolErrors.DuplicateDescriptor);
    }

    [Test, CancelAfter(15000)]
    public async Task DuplicateSelfOwnedSubscriptionsAreRejectedBeforeGrantExtraction()
    {
        await using var client = await CreateClientAsync();
        var subscription = client.Discovery.Actions.Single(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        var discovery = client.Discovery with
        {
            Actions = client.Discovery.Actions
                .Append(subscription)
                .ToArray(),
        };

        AssertDiscoveryRejected(discovery, SidecarProtocolErrors.DuplicateDescriptor);
    }

    [Test, CancelAfter(15000)]
    public async Task OversizedFullDiscoveryIsRejectedBeforeGrantExtraction()
    {
        await using var client = await CreateClientAsync();
        var discovery = client.Discovery with
        {
            ContractHash = new string(
                'x',
                client.HostLimits.ProtocolMessageBytes + 1024),
        };

        AssertDiscoveryRejected(discovery, SidecarProtocolErrors.ModulePayloadTooLarge);
    }

    [Test, CancelAfter(15000)]
    public async Task SelfOwnedDefinitionMustSupportTheNegotiatedProtocol()
    {
        await using var client = await CreateClientAsync();
        var definition = client.Discovery.ActionDefinitions.Single(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key) with
        {
            ProtocolVersionRange = ContractVersionRange.Exact(2),
        };
        var discovery = client.Discovery with
        {
            ActionDefinitions = client.Discovery.ActionDefinitions
                .Select(item => item.ActionKey == ApplicationSmokeModule.OwnedAction.Key
                    ? definition
                    : item)
                .ToArray(),
        };

        AssertDiscoveryRejected(discovery, SidecarProtocolErrors.UnsupportedVersion);
    }

    private Task<OutOfProcessModuleClient> CreateClientAsync() =>
        OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private void AssertDiscoveryRejected(
        SidecarDiscoveryEnvelope discovery,
        string expectedCode)
    {
        var validation = SidecarDiscoveryValidator.Validate(discovery, _catalog);
        validation.Accepted.Should().BeFalse(
            $"Validator accepted {discovery.Actions.Count} actions and {discovery.ActionDefinitions.Count} definitions.");
        var act = () => SidecarAuthorizationFactory.Create(discovery, _catalog);

        act.Should().Throw<SidecarDiscoveryAuthorizationException>()
            .Which.Code.Should().Be(expectedCode);
    }

    private static HookInvokeStart CreateStart(
        OutOfProcessModuleClient client,
        string mode)
        => CreateStart(
            client,
            ApplicationSmokeModule.HostAction,
            ApplicationSmokeModule.HostActionHookId,
            mode);

    private static HookInvokeStart CreateStart(
        OutOfProcessModuleClient client,
        ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> action,
        string hookId,
        string mode)
    {
        var descriptor = ToDescriptor(action);
        var grant = client.Authorization.ActionGrants.Single(item =>
            item.ActionKey == descriptor.ActionKey);
        var invocationId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence: 1,
            deadline,
            client.HostLimits.ActionInputBytes,
            header => new HookInvokeStart(
                header,
                invocationId,
                null,
                Guid.NewGuid(),
                hookId,
                descriptor.ActionKey,
                descriptor.Version,
                SidecarPayloadMode.Typed,
                JsonSerializer.SerializeToElement(
                    new ApplicationSmokeAction(mode, "value"),
                    OutOfProcessProtocolCodec.JsonOptions),
                new UntypedActionDescriptor(
                    descriptor.ActionKey,
                    descriptor.Version,
                    descriptor.Category,
                    descriptor.Capabilities,
                    descriptor.InputSchema,
                    descriptor.ResultSchema,
                    descriptor.ContainsSensitiveData)
                {
                    ProtocolVersionRange = descriptor.ProtocolVersionRange,
                },
                grant,
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                new ContinuationHandle(
                    Guid.NewGuid(),
                    invocationId,
                    hookId,
                    deadline,
                    1)));
    }

    private static SidecarHostActionDescriptor HostDescriptor() =>
        ToDescriptor(ApplicationSmokeModule.HostAction);

    private static SidecarHostActionDescriptor ToDescriptor(
        ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> descriptor)
    {
        return new SidecarHostActionDescriptor(
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
    }

    private static (ContinuationAccepted Accepted, ContinuationOutcome Outcome) CreateContinuation(
        SidecarEffectRequest request,
        string value)
    {
        var accepted = SidecarMessageHeaderFactory.CreateMeasured(
            request.Header.ProtocolVersion,
            request.Header.Sequence + 1,
            request.Header.Deadline,
            new SidecarPayloadLimits().ProtocolMessageBytes,
            header => new ContinuationAccepted(
                header,
                request.ContinuationHandleId,
                request.Command,
                ActionSafePoint.BeforeContinuation,
                ContinuationState.Claimed));
        var kind = request.Command == SidecarContinuationCommand.Cancel
            ? ActionOutcomeKind.Cancelled
            : ActionOutcomeKind.Completed;
        var outcome = SidecarMessageHeaderFactory.CreateMeasured(
            request.Header.ProtocolVersion,
            request.Header.Sequence + 2,
            request.Header.Deadline,
            new SidecarPayloadLimits().ActionResultBytes,
            header => new ContinuationOutcome(
                header,
                request.ContinuationHandleId,
                kind,
                ActionOutcomeCertainty.Certain,
                ActionSafePoint.BeforeTerminal,
                kind == ActionOutcomeKind.Completed
                    ? JsonSerializer.SerializeToElement(
                        new ApplicationSmokeResult(value),
                        OutOfProcessProtocolCodec.JsonOptions)
                    : null,
                Error: kind == ActionOutcomeKind.Cancelled
                    ? new ExecutionError(
                        request.Code ?? "application_denied",
                        request.Message ?? "The request was denied.")
                    : null,
                Continuation: null));
        return (accepted, outcome);
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

    private sealed class CountingStorageGateway : IModuleStorageGateway
    {
        public int ListContractsCalls { get; private set; }

        public int InvokeCalls { get; private set; }

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts()
        {
            ListContractsCalls++;
            return
            [
                new ModuleStorageContractDescriptor(
                    ApplicationSmokeModule.Id,
                    "application-store",
                    [new ModuleStorageOperationDescriptor("echo")]),
            ];
        }

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct)
        {
            InvokeCalls++;
            moduleId.Should().Be(ApplicationSmokeModule.Id);
            storageName.Should().Be("application-store");
            operation.Should().Be("echo");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { value = "storage" }));
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

        public int TerminalCalls { get; private set; }

        public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            RunCalls++;
            var result = await terminal(action, ct);
            TerminalCalls++;
            return new CountingActionOutcome<TResult>(result);
        }

        public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            var outcome = await RunAsync(descriptor, action, terminal, snapshot, ct);
            return outcome.Result;
        }
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
