using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
public sealed class OutOfProcessApplicationProtocolTests
{
    private string _moduleDirectory = null!;
    private Uri _controlAddress = null!;
    private string _controlToken = null!;
    private OutOfProcessModuleServer _server = null!;
    private SidecarHostDescriptorCatalog _catalog = null!;
    private string _scopeProbePath = null!;
    private string _terminalScopeProbePath = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        _moduleDirectory = Path.Combine(root, "application-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_moduleDirectory);
        _scopeProbePath = Path.Combine(_moduleDirectory, "scoped-endpoint-disposed.txt");
        _terminalScopeProbePath = Path.Combine(_moduleDirectory, "scoped-terminal-disposed.txt");
        Environment.SetEnvironmentVariable(
            ApplicationSmokeModule.ScopedEndpointProbeEnvironmentVariable,
            _scopeProbePath);
        Environment.SetEnvironmentVariable(
            ApplicationSmokeModule.ScopedTerminalProbeEnvironmentVariable,
            _terminalScopeProbePath);
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
                  "target": "host.application.child",
                  "effects": ["inspect", "wrap", "cancel"]
                },
                {
                  "target": "module.application.smoke",
                  "effects": ["inspect", "wrap", "cancel"]
                },
                {
                  "target": "permission.policy.read",
                  "effects": ["inspect"]
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
            [HostDescriptor(), ChildHostDescriptor()],
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
        Environment.SetEnvironmentVariable(
            ApplicationSmokeModule.ScopedEndpointProbeEnvironmentVariable,
            null);
        Environment.SetEnvironmentVariable(
            ApplicationSmokeModule.ScopedTerminalProbeEnvironmentVariable,
            null);
    }

    [Test, CancelAfter(15000)]
    public async Task ApplicationDiscoveryAndCliUseTheSameModuleGraph()
    {
        await using var client = await CreateClientAsync();

        client.Application.ModuleId.Should().Be(client.Discovery.ModuleId);
        client.Application.ContractHash.Should().Be(client.Discovery.ContractHash);
        client.Discovery.ActionDefinitions.Should().Contain(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        client.Discovery.Actions.Should().Contain(item =>
            item.ActionKey == ApplicationSmokeModule.OwnedAction.Key);
        client.Application.Endpoints.Should().Contain(endpoint =>
            endpoint.TypeName == typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName);
        client.Application.CliCommands.Should().Contain(command =>
            command.Descriptor.Name == ApplicationSmokeModule.CliName);

        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.CliName,
            ["identity"],
            IssueCliContext(client, ApplicationSmokeModule.CliName, "test-user"));

        result.ModuleId.Should().Be(client.Discovery.ModuleId);
        result.ContractHash.Should().Be(client.Discovery.ContractHash);
        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Be(
            $"{ApplicationSmokeModule.Id}|{ApplicationSmokeModule.Id}|{client.Discovery.ContractHash}|{ApplicationSmokeModule.CliName}");
    }

    [Test, CancelAfter(30000)]
    public async Task RealCoreDispatcherExecutesExternalEndpointAndTypedEntryThroughSessionVerifier()
    {
        await using var client = await CreateClientAsync();
        var registry = new KernelExternalAuthoritySessionRegistry();
        var graph = BuildRealCoreHostGraph();
        graph.ActionSnapshot.ContractHash.Should().NotBe(client.Discovery.ContractHash);

        var storage = new CountingStorageGateway();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var dispatcher = CreateRealCoreDispatcher(graph, registry);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            graph.ActionSnapshot,
            new OutOfProcessHostActionEntryContextRegistry(),
            registry));

        var endpoint = await client.InvokeEndpointAsync(
            typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName!,
            client.IssueHostActionContext(
                HostActionEntryIngress.Endpoint,
                typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName!,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.HostAction,
                new ApplicationSmokeAction("endpoint", "action"),
                ApplicationSmokeModule.HostEntryCaller,
                ApplicationSmokeModule.HostEntryFeatures,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        endpoint.Succeeded.Should().BeTrue();
        endpoint.Payload.Should().NotBeNull();
        endpoint.Payload!.Value.GetProperty("outcome").GetString().Should().Be(
            ActionOutcomeKind.Completed.ToString());
        endpoint.Payload.Value.GetProperty("value").GetString().Should().Be("entry-terminal:action");

        var action = new AgentsJobImportAction("real-core");
        var typed = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            action,
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                action,
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        typed.Kind.Should().Be(
            ActionOutcomeKind.Completed,
            $"Typed external dispatch failed with {typed.Error?.Code}: {typed.Error?.Message}");
        typed.Result.Value.Should().StartWith("imported:real-core:");
    }

    [Test, CancelAfter(30000)]
    public async Task RealCoreDispatcherExecutesNestedHostEntryThroughSessionVerifier()
    {
        await using var client = await CreateClientAsync();
        var registry = new KernelExternalAuthoritySessionRegistry();
        var graph = BuildRealCoreHostGraph();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        var dispatcher = CreateRealCoreDispatcher(graph, registry);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            new CountingStorageGateway(),
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            graph.ActionSnapshot,
            new OutOfProcessHostActionEntryContextRegistry(),
            registry));

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["nested"],
            IssueHostEntryContext(
                client,
                ApplicationSmokeModule.NestedHostEntryCliName,
                DateTimeOffset.UtcNow.AddMinutes(1),
                "nested-root"));

        result.Result.Succeeded.Should().BeTrue(
            $"Nested CLI failed with {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:nested-root:nested-child:entry-terminal:nested-grandchild");
    }

    [Test, CancelAfter(15000)]
    public async Task EndpointActivationUsesOneInvocationScope()
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
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var endpointTypeName = typeof(ApplicationSmokeModule.ScopedEndpoint).FullName!;
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Endpoint,
            endpointTypeName,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("scoped", "endpoint"),
            new RequestPrincipal("scope-test"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var response = await client.InvokeEndpointAsync(endpointTypeName, context);

        response.Succeeded.Should().BeTrue();
        response.Payload.Should().NotBeNull();
        response.Payload!.Value.GetProperty("state").GetString().Should().Be("active");
        File.Exists(_scopeProbePath).Should().BeTrue();
        File.ReadAllText(_scopeProbePath).Should().Be("disposed");
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
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            [],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "capability-test"));

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Contain("contracts:1");
        result.Result.Output.Single().Text.Should().Contain("storage:{\"value\":\"storage\"}");
        result.Result.Output.Single().Text.Should().Contain("action:terminal:action");
        storage.ListContractsCalls.Should().Be(1);
        storage.InvokeCalls.Should().Be(1);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        dispatcher.LastSnapshotCapabilities.Should().Be(ApplicationSmokeModule.HostCapabilities);
    }

    [Test, CancelAfter(15000)]
    public async Task EndpointAndTypedModuleActionEntryUseTheAuthenticatedCapabilitySession()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        const string hostGraphId = "host-graph-h";
        client.Discovery.ContractHash.Should().NotBe(hostGraphId);
        dispatcher.ExpectedSnapshotContractHash = hostGraphId;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                hostGraphId,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        dispatcher.ReplaceInput = value => value is AgentsJobImportAction import
            ? import with { JobId = "job-replaced" }
            : value;

        var endpointContext = client.IssueHostActionContext(
            HostActionEntryIngress.Endpoint,
            typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName!,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("endpoint", "action"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var endpoint = await client.InvokeEndpointAsync(
            typeof(ApplicationSmokeModule.ApplicationEndpoint).FullName!,
            endpointContext);

        endpoint.Succeeded.Should().BeTrue();
        endpoint.Payload.Should().NotBeNull();
        endpoint.Payload!.Value.GetProperty("value").GetString().Should().Be("entry-terminal:action");

        var importAction = new AgentsJobImportAction("job-123");
        var importContext = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.AgentsJobImportAction,
            importAction,
            new RequestPrincipal("module-agent"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var import = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            importAction,
            importContext);

        import.Kind.Should().Be(
            ActionOutcomeKind.Completed,
            $"Typed action failed with {import.Error?.Code}: {import.Error?.Message}");
        dispatcher.LastSnapshotHash.Should().NotBeNull();
        import.Result.Value.Should().StartWith(
            $"imported:job-replaced:caller=module-agent:snapshot={dispatcher.LastSnapshotHash}:scope=");
        import.Result.Value.Should().Contain(":state=active");
        File.ReadAllText(_terminalScopeProbePath).Should().StartWith("disposed:");
        dispatcher.LastSnapshotContractHash.Should().Be(hostGraphId);
        dispatcher.RunCalls.Should().Be(2);
        dispatcher.ExternalRunCalls.Should().Be(2);
        dispatcher.TerminalCalls.Should().Be(2);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task MutatedHostSnapshotIsRejectedByDispatcherAuthorityBeforeTerminal()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher
        {
            ExpectedSnapshotContractHash = "host-graph-h",
        };
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                "mutated-host-graph",
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var result = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            new AgentsJobImportAction("mutated-host"),
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("mutated-host"),
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        result.Kind.Should().NotBe(ActionOutcomeKind.Completed);
        dispatcher.SnapshotRejectionCalls.Should().Be(1);
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task MutatedSidecarGraphIsRejectedBeforeDispatch()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        var grant = client.CreateCapabilityGrant() with
        {
            GraphId = "mutated-sidecar-graph",
        };

        var act = async () => await client.ConnectCapabilitiesAsync(
            new OutOfProcessCapabilityHostOptions(
                storage,
                dispatcher,
                grant,
                ["application-store"],
                descriptors,
                new ActionPipelineSnapshot(
                    "host-graph-h",
                    client.Authorization.ActionGrants,
                    client.Authorization.EventGrants),
                new OutOfProcessHostActionEntryContextRegistry(),
                new KernelExternalAuthoritySessionRegistry()));

        await act.Should().ThrowAsync<OutOfProcessCapabilityException>();
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task ModuleOwnedHostEntryUsesApplicationRegistrationAndKeepsSequence()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var rejected = await client.InvokeCliAsync(
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            ["unauthorized"],
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.SelfOwnedEntryCliName,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("unauthorized"),
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        rejected.Result.Succeeded.Should().BeFalse();
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);

        var accepted = await client.InvokeCliAsync(
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            ["accepted"],
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.SelfOwnedEntryCliName,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("accepted"),
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        accepted.Result.Succeeded.Should().BeTrue(
            $"CLI error {accepted.Result.Error?.Code}: {accepted.Result.Error?.Message}; "
            + string.Join(" | ", accepted.Result.Output.Select(item => item.Text)));
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(30000)]
    public async Task AgentsJobImportNestedPermissionCompletesBeforeRootCarrierAndKeepsSession()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        descriptors.Add(ApplicationSmokeModule.PermissionPolicyAction);
        var grants = client.Authorization.ActionGrants
            .Where(grant =>
                grant.ActionKey != ApplicationSmokeModule.AgentsJobImportAction.Key
                && grant.ActionKey != ApplicationSmokeModule.PermissionPolicyAction.Key)
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.PermissionPolicyAction.Key,
                ApplicationSmokeModule.PermissionPolicyAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var rootContext = client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.AgentsJobImportAction,
            new AgentsJobImportAction("permission-nested"),
            new RequestPrincipal("module-agent"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        const int priorCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"before-agents-permission-{i}"));

            prior.Result.Succeeded.Should().BeTrue(
                $"Prior CLI failed with {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
        }

        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            ["permission-nested"],
            rootContext);
        result.Result.Succeeded.Should().BeTrue(
            $"Agents import failed with {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Contain(
            "self-owned:Completed:imported:permission-nested:permission=permission:agents-job-import:");
        dispatcher.RunCalls.Should().Be(2);
        dispatcher.TerminalCalls.Should().Be(2);
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        client.HostActionEntryContexts.HasPendingContexts.Should().BeFalse();
        client.CapabilitySession.BindingGeneration.Should().BeGreaterThan(1);
        client.CapabilitySession.RunFailure.Should().BeNull();

        var later = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "after-agents-permission"));

        later.Result.Succeeded.Should().BeTrue(
            $"Later CLI failed with {later.Result.Error?.Code}: {later.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 2);
        client.CapabilitySession.RunFailure.Should().BeNull();
    }

    [Test, CancelAfter(15000)]
    public async Task ModuleOwnedHostEntryRejectsUnregisteredTerminalImplementationBeforeDispatch()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var rejected = await client.InvokeCliAsync(
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            ["bad-terminal"],
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.SelfOwnedEntryCliName,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("bad-terminal"),
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        rejected.Result.Succeeded.Should().BeFalse();
        rejected.Result.Error?.Code.Should().Be(SidecarCapabilityErrors.Unauthorized);
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);

        var accepted = await client.InvokeCliAsync(
            ApplicationSmokeModule.SelfOwnedEntryCliName,
            ["accepted"],
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.SelfOwnedEntryCliName,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("accepted"),
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        accepted.Result.Succeeded.Should().BeTrue(
            $"CLI error {accepted.Result.Error?.Code}: {accepted.Result.Error?.Message}; "
            + string.Join(" | ", accepted.Result.Output.Select(item => item.Text)));
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(15000)]
    public async Task IncomingHostActionAlignsTheNextModuleStorageSequence()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var action = new AgentsJobImportAction("storage");
        var result = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            action,
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                action,
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        result.Kind.Should().Be(ActionOutcomeKind.Completed);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        storage.InvokeCalls.Should().Be(1);

        var followUp = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "after-storage"));

        followUp.Result.Succeeded.Should().BeTrue(
            $"CLI error {followUp.Result.Error?.Code}: {followUp.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(2);
    }

    [Test, CancelAfter(30000)]
    public async Task HostSequenceResetsAfterRotationBeforeScopedModuleAction()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        for (var i = 0; i < 2; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                [],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"host-sequence-rotation-{i}"));

            prior.Result.Succeeded.Should().BeTrue(
                $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
        }

        var action = new AgentsJobImportAction("storage");
        var result = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            action,
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                action,
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        result.Kind.Should().Be(ActionOutcomeKind.Completed);
        dispatcher.RunCalls.Should().Be(3);
        dispatcher.TerminalCalls.Should().Be(3);
        storage.ListContractsCalls.Should().Be(2);
        storage.InvokeCalls.Should().Be(3);

        var followUp = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            [],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "host-sequence-after"));

        followUp.Result.Succeeded.Should().BeTrue(
            $"CLI error {followUp.Result.Error?.Code}: {followUp.Result.Error?.Message}");
        dispatcher.RunCalls.Should().Be(4);
        dispatcher.TerminalCalls.Should().Be(4);
        storage.ListContractsCalls.Should().Be(3);
        storage.InvokeCalls.Should().Be(4);
    }

    [Test, CancelAfter(30000)]
    public async Task DisconnectedHostActionCompletionReleasesOutgoingCallBeforeCarrierCleanup()
    {
        var client = await CreateClientAsync();
        var storage = new CountingStorageGateway { BlockInvoke = true };
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ActionInterceptionCapabilities.Inspect,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var action = new AgentsJobImportAction("storage");
        var invocation = client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            action,
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                action,
                new RequestPrincipal("module-agent"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1))).AsTask();

        await storage.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await storage.InvocationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var completed = await Task.WhenAny(
            invocation,
            Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(invocation);
        Func<Task> awaitInvocation = async () => await invocation;
        await awaitInvocation.Should().ThrowAsync<Exception>();
    }

    [Test, CancelAfter(30000)]
    public async Task NonRetryableCompletionReleasesLiveSessionBeforeCarrierCleanup()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.AgentsJobImportAction);
        var grants = client.Authorization.ActionGrants
            .Append(new ActionCapabilityGrant(
                ApplicationSmokeModule.AgentsJobImportAction.Key,
                ApplicationSmokeModule.AgentsJobImportAction.Version,
                ApplicationSmokeModule.AgentsJobImportAction.Capabilities,
                SensitiveApproved: false,
                AcceptUnknownSchemas: false))
            .ToArray();
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                grants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var transformCalls = 0;
        var observedTerminalCallCount = -1;
        try
        {
            OutOfProcessProtocolTestFixture.ConfigureResponseTerminalCallCountTransform(value =>
            {
                transformCalls++;
                observedTerminalCallCount = value;
                return 2;
            });

            OutOfProcessCapabilityException? failure = null;
            try
            {
                await client.InvokeModuleActionEntryAsync(
                    ApplicationSmokeModule.AgentsJobImportAction,
                    new AgentsJobImportAction("completion-rejection"),
                    client.IssueHostActionContext(
                        HostActionEntryIngress.Cli,
                        ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                        client.Discovery.ModuleId,
                        ApplicationSmokeModule.AgentsJobImportAction,
                        new AgentsJobImportAction("completion-rejection"),
                        new RequestPrincipal("completion-test"),
                        ExtensionFeatureSet.Empty,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow.AddMinutes(1)));
            }
            catch (OutOfProcessCapabilityException exception)
            {
                failure = exception;
            }

            transformCalls.Should().Be(1);
            observedTerminalCallCount.Should().Be(0);
            failure.Should().NotBeNull();
            failure!.Code.Should().Be(SidecarCapabilityErrors.InvalidBinding);
            failure.Message.Should().Be("The terminal call count must be zero or one.");
            client.HostActionEntryContexts.HasActiveContexts.Should().BeFalse();
            dispatcher.RunCalls.Should().Be(1);
            dispatcher.TerminalCalls.Should().Be(1);
            storage.InvokeCalls.Should().Be(0);
        }
        finally
        {
            OutOfProcessProtocolTestFixture.ConfigureResponseTerminalCallCountTransform(null);
        }
        var generationBefore = client.CapabilitySession.BindingGeneration;
        var valid = await client.InvokeModuleActionEntryAsync(
            ApplicationSmokeModule.AgentsJobImportAction,
            new AgentsJobImportAction("after-rejection"),
            client.IssueHostActionContext(
                HostActionEntryIngress.Cli,
                ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.AgentsJobImportAction,
                new AgentsJobImportAction("after-rejection"),
                new RequestPrincipal("completion-test"),
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1)));

        valid.Kind.Should().Be(ActionOutcomeKind.Completed);
        valid.Result.Value.Should().StartWith("imported:after-rejection:");

        for (var i = 0; i < 5; i++)
        {
            var followUpAction = new AgentsJobImportAction($"completion-follow-up-{i}");
            var followUp = await client.InvokeModuleActionEntryAsync(
                ApplicationSmokeModule.AgentsJobImportAction,
                followUpAction,
                client.IssueHostActionContext(
                    HostActionEntryIngress.Cli,
                    ApplicationSmokeModule.AgentsJobImportAction.Key.Value,
                    client.Discovery.ModuleId,
                    ApplicationSmokeModule.AgentsJobImportAction,
                    followUpAction,
                    new RequestPrincipal("completion-test"),
                    ExtensionFeatureSet.Empty,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow.AddMinutes(1)));
            followUp.Kind.Should().Be(ActionOutcomeKind.Completed);
            followUp.Result.Value.Should().StartWith("imported:completion-follow-up-");
        }

        client.CapabilitySession.BindingGeneration.Should().BeGreaterThan(generationBefore);
        client.HostActionEntryContexts.HasActiveContexts.Should().BeFalse();
        dispatcher.RunCalls.Should().Be(7);
        dispatcher.TerminalCalls.Should().Be(7);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(30000)]
    public async Task CapabilitySessionRotatesAfterMaximumCalls()
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
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        for (var i = 0; i < 3; i++)
        {
            var result = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                [],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"capability-rotation-{i}"));

            result.Result.Succeeded.Should().BeTrue(
                $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
                + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        }

        storage.ListContractsCalls.Should().Be(3);
        storage.InvokeCalls.Should().Be(3);
        dispatcher.RunCalls.Should().Be(3);
        dispatcher.TerminalCalls.Should().Be(3);
    }

    [Test, CancelAfter(30000)]
    public async Task StorageHeavyCliThenEndpointRotatesBeforeTheNextWorkflow()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        var rotationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry())
        {
            BeforeRotationStartAsync = async () =>
            {
                rotationStarted.TrySetResult();
                await rotationRelease.Task;
            },
        };
        await client.ConnectCapabilitiesAsync(options);

        var cliTask = client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["storage-heavy"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "permission-policy-list"))
            .AsTask();
        await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cliTask.IsCompleted.Should().BeFalse();
        rotationRelease.TrySetResult();
        var cli = await cliTask;

        cli.Result.Succeeded.Should().BeTrue(
            $"CLI error {cli.Result.Error?.Code}: {cli.Result.Error?.Message}; "
            + string.Join(" | ", cli.Result.Output.Select(item => item.Text)));
        storage.InvokeCalls.Should().Be(5);

        var endpointTask = Task.Run(async () => await client.InvokeEndpointAsync(
            typeof(ApplicationSmokeModule.StorageHeavyEndpoint).FullName!,
            client.IssueHostActionContext(
                HostActionEntryIngress.Endpoint,
                typeof(ApplicationSmokeModule.StorageHeavyEndpoint).FullName!,
                client.Discovery.ModuleId,
                ApplicationSmokeModule.HostAction,
                new ApplicationSmokeAction("storage-heavy", "endpoint"),
                ApplicationSmokeModule.HostEntryCaller,
                ApplicationSmokeModule.HostEntryFeatures,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1))));
        await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        endpointTask.IsCompleted.Should().BeFalse();
        rotationRelease.TrySetResult();
        var endpoint = await endpointTask;

        endpoint.Succeeded.Should().BeTrue(
            $"Endpoint error {endpoint.Error?.Code}: {endpoint.Error?.Message}");
        endpoint.Payload.Should().NotBeNull();
        endpoint.Payload!.Value.GetProperty("storageReads").GetInt32().Should().Be(3);
        endpoint.Payload.Value.GetProperty("outcome").GetString().Should().Be(
            ActionOutcomeKind.Completed.ToString());
        endpoint.Payload.Value.GetProperty("value").GetString().Should().Be("entry-terminal:endpoint");
        storage.InvokeCalls.Should().Be(8);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);

        var followUp = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "permission-after-endpoint"));

        followUp.Result.Succeeded.Should().BeTrue(
            $"Follow-up CLI error {followUp.Result.Error?.Code}: {followUp.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(9);
    }

    [Test, CancelAfter(30000)]
    public async Task RebindReaderDrainsPendingActionResponseWithoutHeadOfLineBlocking()
    {
        var rebindReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actionResponseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actionResponseRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostEntryCallId = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actionReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var storageResponseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var storageFrameReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rebindStates = new ConcurrentQueue<string>();
        OutOfProcessProtocolTestFixture.ConfigureRebindStateObserver(state =>
        {
            rebindStates.Enqueue(state);
            TestContext.Progress.WriteLine("Rebind observer: " + state);
            if (state.StartsWith("rebind-received|", StringComparison.Ordinal))
                rebindReceived.TrySetResult(state);
            if (state.StartsWith("state-released|actions=", StringComparison.Ordinal)
                && hostEntryCallId.Task.IsCompletedSuccessfully
                && state.EndsWith(
                    hostEntryCallId.Task.Result.ToString("N"),
                    StringComparison.Ordinal))
            {
                actionReleased.TrySetResult();
            }
            if (state.StartsWith("storage-frame-received|", StringComparison.Ordinal))
                storageFrameReceived.TrySetResult(state);
        });
        try
        {
            await using var client = await CreateClientAsync();
            var storage = new CountingStorageGateway();
            var dispatcher = new CountingActionDispatcher();
            var descriptors = new OutOfProcessActionDescriptorCatalog();
            descriptors.Add(
                ApplicationSmokeModule.HostAction,
                static (context, _) => ValueTask.FromResult(
                    new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
            await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
                storage,
                dispatcher,
                client.CreateCapabilityGrant(),
                ["application-store"],
                descriptors,
                new ActionPipelineSnapshot(
                    client.Discovery.ContractHash,
                    client.Authorization.ActionGrants,
                    client.Authorization.EventGrants),
                new OutOfProcessHostActionEntryContextRegistry(),
                new KernelExternalAuthoritySessionRegistry()));

            for (var i = 0; i < 2; i++)
            {
                var prior = await client.InvokeCliAsync(
                    ApplicationSmokeModule.CapabilityCliName,
                    ["single"],
                    IssueCliContext(
                        client,
                        ApplicationSmokeModule.CapabilityCliName,
                        $"rebind-reader-prior-{i}"));
                prior.Result.Succeeded.Should().BeTrue(
                    $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
            }

            OutOfProcessProtocolTestFixture.ConfigureBeforeActionResponseForCallAsync(async (call, ct) =>
            {
                if (hostEntryCallId.Task.IsCompletedSuccessfully
                    && call.CallId == hostEntryCallId.Task.Result)
                {
                    actionResponseEntered.TrySetResult();
                    await actionResponseRelease.Task.WaitAsync(ct);
                }
            });
            OutOfProcessProtocolTestFixture.ConfigureBeforeStorageResponseAsync(ct =>
            {
                storageResponseEntered.TrySetResult();
                return Task.CompletedTask;
            });
            OutOfProcessProtocolTestFixture.ConfigureCallCreatedObserver(call =>
            {
                if (call.Capability == SidecarCapabilityKind.Action)
                    hostEntryCallId.TrySetResult(call.CallId);
            });

            var hostEntry = client.InvokeCliAsync(
                ApplicationSmokeModule.HostEntryCliName,
                [],
                IssueHostEntryContext(
                    client,
                    DateTimeOffset.UtcNow.AddMinutes(1))).AsTask();
            await actionResponseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var expectedHostEntryCallId = await hostEntryCallId.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            var endpointTask = Task.Run(async () => await client.InvokeEndpointAsync(
                typeof(ApplicationSmokeModule.StorageHeavyEndpoint).FullName!,
                client.IssueHostActionContext(
                    HostActionEntryIngress.Endpoint,
                    typeof(ApplicationSmokeModule.StorageHeavyEndpoint).FullName!,
                    client.Discovery.ModuleId,
                    ApplicationSmokeModule.HostAction,
                    new ApplicationSmokeAction("storage-heavy", "endpoint"),
                    ApplicationSmokeModule.HostEntryCaller,
                    ApplicationSmokeModule.HostEntryFeatures,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow.AddMinutes(1))));
            await storageResponseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("Storage response send started.");
            var storageFrameCompleted = await Task.WhenAny(
                storageFrameReceived.Task,
                Task.Delay(TimeSpan.FromSeconds(5)));
            storageFrameCompleted.Should().Be(
                storageFrameReceived.Task,
                "the module reader must receive the storage response; states: "
                + string.Join(" | ", rebindStates));
            TestContext.Progress.WriteLine(
                "Storage frame received: " + storageFrameReceived.Task.Result);
            var rebindState = await rebindReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rebindState.Should().Contain(
                $"actions=[{expectedHostEntryCallId:N}]",
                "the gated HostEntry call must cause the first observed rebind");
            rebindState.Should().Contain("incomingActions=[]");
            rebindState.Should().Contain("storage=[]");
            TestContext.Progress.WriteLine(
                "Rebind state evidence: " + string.Join(" | ", rebindStates));

            actionResponseRelease.TrySetResult();
            var result = await hostEntry.WaitAsync(TimeSpan.FromSeconds(5));
            result.Result.Succeeded.Should().BeTrue(
                $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
                + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
            result.Result.Output.Single().Text.Should().Be(
                "host-entry:Completed:entry-terminal:action");
            await actionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var endpoint = await endpointTask.WaitAsync(TimeSpan.FromSeconds(5));
            endpoint.Succeeded.Should().BeTrue(
                $"Endpoint error {endpoint.Error?.Code}: {endpoint.Error?.Message}");
            endpoint.Payload.Should().NotBeNull();
            endpoint.Payload!.Value.GetProperty("storageReads").GetInt32().Should().Be(3);
            endpoint.Payload.Value.GetProperty("outcome").GetString().Should().Be(
                ActionOutcomeKind.Completed.ToString());
            endpoint.Payload.Value.GetProperty("value").GetString().Should().Be(
                "entry-terminal:endpoint");

            var afterRotation = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    "rebind-reader-after"));
            afterRotation.Result.Succeeded.Should().BeTrue(
                $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
            storage.InvokeCalls.Should().Be(6);
            dispatcher.RunCalls.Should().Be(2);
            dispatcher.TerminalCalls.Should().Be(2);
        }
        finally
        {
            actionResponseRelease.TrySetResult();
            OutOfProcessProtocolTestFixture.ConfigureBeforeActionResponseAsync(null);
            OutOfProcessProtocolTestFixture.ConfigureBeforeActionResponseForCallAsync(null);
            OutOfProcessProtocolTestFixture.ConfigureBeforeStorageResponseAsync(null);
            OutOfProcessProtocolTestFixture.ConfigureCallCreatedObserver(null);
            OutOfProcessProtocolTestFixture.ConfigureRebindStateObserver(null);
        }
    }

    [Test, CancelAfter(30000)]
    public async Task RebindAdmissionReservesCallBeforeRequestRegistration()
    {
        var rebindReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rebindDrained = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hostEntryCallId = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestRegistrationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rebindStates = new ConcurrentQueue<string>();
        var callObserverUsed = 0;
        OutOfProcessProtocolTestFixture.ConfigureRebindStateObserver(state =>
        {
            rebindStates.Enqueue(state);
            if (state.StartsWith("rebind-received|", StringComparison.Ordinal))
                rebindReceived.TrySetResult(state);
            if (state.StartsWith("rebind-drained|", StringComparison.Ordinal))
                rebindDrained.TrySetResult(state);
        });
        try
        {
            await using var client = await CreateClientAsync();
            var storage = new CountingStorageGateway();
            var dispatcher = new CountingActionDispatcher();
            var descriptors = new OutOfProcessActionDescriptorCatalog();
            descriptors.Add(
                ApplicationSmokeModule.HostAction,
                static (context, _) => ValueTask.FromResult(
                    new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
            var options = new OutOfProcessCapabilityHostOptions(
                storage,
                dispatcher,
                client.CreateCapabilityGrant(),
                ["application-store"],
                descriptors,
                new ActionPipelineSnapshot(
                    client.Discovery.ContractHash,
                    client.Authorization.ActionGrants,
                    client.Authorization.EventGrants),
                new OutOfProcessHostActionEntryContextRegistry(),
                new KernelExternalAuthoritySessionRegistry())
            {
                BeforeRotationStartAsync = async () =>
                {
                    rotationStarted.TrySetResult();
                    await rotationRelease.Task;
                },
            };
            await client.ConnectCapabilitiesAsync(options);

            for (var i = 0; i < 5; i++)
            {
                var prior = await client.InvokeCliAsync(
                    ApplicationSmokeModule.CapabilityCliName,
                    ["single"],
                    IssueCliContext(
                        client,
                        ApplicationSmokeModule.CapabilityCliName,
                        $"rebind-admission-prior-{i}"));
                prior.Result.Succeeded.Should().BeTrue(
                    $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
            }

            var sixthPrior = client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    "rebind-admission-sixth")).AsTask();
            await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            OutOfProcessProtocolTestFixture.ConfigureCallCreatedObserver(call =>
            {
                if (call.Capability == SidecarCapabilityKind.Action
                    && Interlocked.Exchange(ref callObserverUsed, 1) == 0)
                {
                    hostEntryCallId.TrySetResult(call.CallId);
                    requestRegistrationRelease.Task.GetAwaiter().GetResult();
                }
            });

            var hostEntry = client.InvokeCliAsync(
                ApplicationSmokeModule.HostEntryCliName,
                [],
                IssueHostEntryContext(
                    client,
                    DateTimeOffset.UtcNow.AddMinutes(1))).AsTask();
            var expectedHostEntryCallId = await hostEntryCallId.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            rotationRelease.TrySetResult();
            var rebindState = await rebindReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            rebindState.Should().Contain(
                $"outgoing=[{expectedHostEntryCallId:N}:",
                "the rebind must observe the pre-registration call reservation");
            rebindState.Should().Contain("actions=[]");

            requestRegistrationRelease.TrySetResult();
            var result = await hostEntry.WaitAsync(TimeSpan.FromSeconds(5));
            result.Result.Succeeded.Should().BeTrue(
                $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
                + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
            result.Result.Output.Single().Text.Should().Be(
                "host-entry:Completed:entry-terminal:action");
            var priorResult = await sixthPrior.WaitAsync(TimeSpan.FromSeconds(5));
            priorResult.Result.Succeeded.Should().BeTrue(
                $"CLI error {priorResult.Result.Error?.Code}: {priorResult.Result.Error?.Message}");

            var drainedState = await rebindDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            drainedState.Should().Contain("outgoing=[]");

            var afterRotation = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    "rebind-admission-after"));
            afterRotation.Result.Succeeded.Should().BeTrue(
                $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
            storage.InvokeCalls.Should().Be(7);
            dispatcher.RunCalls.Should().Be(1);
            dispatcher.TerminalCalls.Should().Be(1);
            TestContext.Progress.WriteLine(
                "Pre-registration rebind state evidence: "
                + string.Join(" | ", rebindStates));
        }
        finally
        {
            rotationRelease.TrySetResult();
            requestRegistrationRelease.TrySetResult();
            OutOfProcessProtocolTestFixture.ConfigureCallCreatedObserver(null);
            OutOfProcessProtocolTestFixture.ConfigureRebindStateObserver(null);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(30000)]
    public async Task ContextIssuanceWaitsForBindingRotationAtCallBudget(
        bool usePublicRegistry)
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var rotationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry())
        {
            BeforeRotationStartAsync = async () =>
            {
                rotationEntered.TrySetResult();
                await rotationRelease.Task;
            },
        };
        await client.ConnectCapabilitiesAsync(options);

        var context = usePublicRegistry
            ? IssueHostEntryContextThroughRegistry(client, grantExpiresAt)
            : IssueHostEntryContext(client, grantExpiresAt);
        hostContext = context;

        const int maximumCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest;
        const int priorCalls = maximumCalls - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var result = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"rotation-issuance-{i}"));

            result.Result.Succeeded.Should().BeTrue(
                $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}");
        }

        var hostEntryTask = Task.Run(() => client.InvokeCliAsync(
            ApplicationSmokeModule.HostEntryCliName,
            [],
            context).AsTask());
        await rotationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hostEntryTask.IsCompleted.Should().BeFalse();

        rotationRelease.TrySetResult();
        var hostEntry = await hostEntryTask;

        hostEntry.Result.Succeeded.Should().BeTrue(
            $"CLI error {hostEntry.Result.Error?.Code}: {hostEntry.Result.Error?.Message}; "
            + string.Join(" | ", hostEntry.Result.Output.Select(item => item.Text)));
        hostEntry.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:entry-terminal:action");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "rotation-issuance-after"));

        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
    }

    [Test, CancelAfter(30000)]
    public async Task PendingHostActionCarrierActivationRetriesBindingRotation()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        descriptors.Add(
            ApplicationSmokeModule.ChildAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationChildResult($"host-child:{context.Action.Name}:{context.Action.Count}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var options = new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry());
        await client.ConnectCapabilitiesAsync(options);

        var pendingContext = IssueHostEntryContextThroughRegistry(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "cross-descriptor-root");

        const int maximumCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest;
        const int priorCalls = maximumCalls - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var result = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"rotation-pending-{i}"));

            result.Result.Succeeded.Should().BeTrue(
                $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}");
        }

        hostContext = pendingContext;
        var carrierEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        options.BeforeCarrierSessionBeginAsync = async () =>
        {
            carrierEntered.TrySetResult();
            await carrierRelease.Task;
        };

        var hostEntryTask = Task.Run(async () => await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["cross-descriptor"],
            pendingContext));
        await carrierEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hostEntryTask.IsCompleted.Should().BeFalse();
        carrierRelease.TrySetResult();
        var hostEntry = await hostEntryTask.WaitAsync(TimeSpan.FromSeconds(5));

        hostEntry.Result.Succeeded.Should().BeTrue(
            $"CLI error {hostEntry.Result.Error?.Code}: {hostEntry.Result.Error?.Message}; "
            + string.Join(" | ", hostEntry.Result.Output.Select(item => item.Text)));
        hostEntry.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:cross-descriptor:cross-descriptor-child:7");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "rotation-after"));

        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(2);
        dispatcher.TerminalCalls.Should().Be(2);
    }

    [Test, CancelAfter(15000)]
    public async Task HostActionEntryUsesHostContextSnapshotAndSingletonDispatcher()
    {
        await using var client = await CreateClientAsync();
        var roles = ApplicationSmokeModule.HostEntryCaller.Roles;
        roles.Should().NotBeNull();
        roles!.Should().BeEquivalentTo(["module-agent", "module-operator"]);
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var grant = client.CreateCapabilityGrant(grantExpiresAt);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            grant,
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        hostContext = IssueHostEntryContext(client, grantExpiresAt);
        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.HostEntryCliName,
            [],
            hostContext);

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:entry-terminal:action");
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
        dispatcher.LastSnapshotCapabilities.Should().Be(ApplicationSmokeModule.HostCapabilities);
    }

    [Test, CancelAfter(30000)]
    public async Task NestedHostActionEntryUsesOneAuthenticatedDispatcherRoute()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var pendingContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "nested-root");
        const int priorCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"nested-boundary-{i}"));
            prior.Result.Succeeded.Should().BeTrue(
                $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
        }

        hostContext = pendingContext;
        SidecarCliExecutionResponse nested;
        try
        {
            nested = await client.InvokeCliAsync(
                ApplicationSmokeModule.NestedHostEntryCliName,
                ["nested"],
                hostContext);
        }
        catch (Exception ex)
        {
            throw new AssertionException(
                $"Nested invocation failed: {ex}; "
                + $"hostFailure={client.CapabilitySession.LastHandledFailure}; "
                + $"moduleFailure={_server.CapabilityFailure}",
                ex);
        }

        nested.Result.Succeeded.Should().BeTrue(
            $"CLI error {nested.Result.Error?.Code}: {nested.Result.Error?.Message}; "
            + string.Join(" | ", nested.Result.Output.Select(item => item.Text))
            + $"; hostFailure={client.CapabilitySession.LastHandledFailure}; "
            + $"moduleFailure={_server.CapabilityFailure}");
        nested.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:nested-root:nested-child:entry-terminal:nested-grandchild");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(
                client,
                ApplicationSmokeModule.CapabilityCliName,
                "nested-boundary-after"));
        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");

        hostContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "sequential-root");
        var sequential = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["sequential"],
            hostContext);

        sequential.Result.Succeeded.Should().BeTrue(
            $"CLI error {sequential.Result.Error?.Code}: {sequential.Result.Error?.Message}; "
            + string.Join(" | ", sequential.Result.Output.Select(item => item.Text)));
        sequential.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:sequential-root:entry-terminal:sequential-child-one|entry-terminal:sequential-child-two");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(6);
        dispatcher.TerminalCalls.Should().Be(6);
    }

    [Test, CancelAfter(30000)]
    public async Task NestedHostActionEntrySupportsSequentialChildrenAtSixCallBoundary()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var pendingContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "sequential-root");
        const int priorCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"sequential-boundary-{i}"));
            prior.Result.Succeeded.Should().BeTrue(
                $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
        }

        hostContext = pendingContext;
        var sequential = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["sequential"],
            hostContext);

        sequential.Result.Succeeded.Should().BeTrue(
            $"CLI error {sequential.Result.Error?.Code}: {sequential.Result.Error?.Message}; "
            + string.Join(" | ", sequential.Result.Output.Select(item => item.Text)));
        sequential.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:sequential-root:entry-terminal:sequential-child-one|entry-terminal:sequential-child-two");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(
                client,
                ApplicationSmokeModule.CapabilityCliName,
                "sequential-boundary-after"));
        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(3);
        dispatcher.TerminalCalls.Should().Be(3);
    }

    [Test, CancelAfter(30000)]
    public async Task NestedHostActionEntryRotatesWithTwoPendingContextsAtSixCallBoundary()
    {
        var diagnosticPath = Environment.GetEnvironmentVariable(
            "SHARPCLAW_MODULESDK_ROTATION_DIAGNOSTIC_LOG");
        void Trace(string message)
        {
            if (diagnosticPath is not null)
                File.AppendAllText(
                    diagnosticPath,
                    $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }

        Trace("start");
        await using var client = await CreateClientAsync();
        Trace("client-created");
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var rotationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotationRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry())
        {
            BeforeRotationStartAsync = async () =>
            {
                Trace(
                    $"rotation-start active={client.HostActionEntryContexts.HasActiveContexts}; "
                    + $"pending={client.HostActionEntryContexts.HasPendingContexts}");
                rotationStarted.TrySetResult();
                await rotationRelease.Task;
                Trace("rotation-released");
            },
            BeforeCarrierSessionBeginAsync = () =>
            {
                Trace(
                    $"carrier-session-begin active={client.HostActionEntryContexts.HasActiveContexts}; "
                    + $"pending={client.HostActionEntryContexts.HasPendingContexts}");
                return Task.CompletedTask;
            },
        };
        await client.ConnectCapabilitiesAsync(options);
        Trace("connected");

        var nestedContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "nested-root");
        var sequentialContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "sequential-root");
        Trace(
            $"contexts-issued active={client.HostActionEntryContexts.HasActiveContexts}; "
            + $"pending={client.HostActionEntryContexts.HasPendingContexts}");
        const int priorCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest - 2;
        for (var i = 0; i < priorCalls; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"two-pending-boundary-{i}"));
            prior.Result.Succeeded.Should().BeTrue(
                $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
            Trace(
                $"prior-complete-{i} active={client.HostActionEntryContexts.HasActiveContexts}; "
                + $"pending={client.HostActionEntryContexts.HasPendingContexts}");
        }

        hostContext = nestedContext;
        var nestedTask = Task.Run(async () => await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["nested"],
            nestedContext));
        var peerActivationTask = Task.Run(async () => await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["sequential"],
            sequentialContext));
        Trace(
            $"carrier-tasks-started active={client.HostActionEntryContexts.HasActiveContexts}; "
            + $"pending={client.HostActionEntryContexts.HasPendingContexts}");

        await rotationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Trace("rotation-observed");
        Trace($"before-release nested={nestedTask.Status}; peer={peerActivationTask.Status}");
        rotationRelease.TrySetResult();
        Trace("release-signaled");

        Exception? nestedFailure = null;
        Exception? peerFailure = null;
        try
        {
            await nestedTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            nestedFailure = ex;
        }

        try
        {
            await peerActivationTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            peerFailure = ex;
        }
        Trace($"carrier-tasks-observed nested={nestedFailure}; peer={peerFailure}");

        if (nestedFailure is not null || peerFailure is not null)
        {
            Assert.Fail(
                $"nestedFailure={nestedFailure}; peerFailure={peerFailure}; "
                + $"hostFailure={client.CapabilitySession.LastHandledFailure}; "
                + $"moduleFailure={_server.CapabilityFailure}");
        }

        var nested = await nestedTask;
        var peer = await peerActivationTask;

        nested.Result.Succeeded.Should().BeTrue(
            $"CLI error {nested.Result.Error?.Code}: {nested.Result.Error?.Message}; "
            + string.Join(" | ", nested.Result.Output.Select(item => item.Text)));
        peer.Result.Succeeded.Should().BeTrue(
            $"CLI error {peer.Result.Error?.Code}: {peer.Result.Error?.Message}; "
            + string.Join(" | ", peer.Result.Output.Select(item => item.Text)));
        nested.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:nested-root:nested-child:entry-terminal:nested-grandchild");

        hostContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "sequential-root");
        var sequential = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["sequential"],
            hostContext);
        sequential.Result.Succeeded.Should().BeTrue(
            $"CLI error {sequential.Result.Error?.Code}: {sequential.Result.Error?.Message}; "
            + string.Join(" | ", sequential.Result.Output.Select(item => item.Text)));
        sequential.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:sequential-root:entry-terminal:sequential-child-one|entry-terminal:sequential-child-two");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(
                client,
                ApplicationSmokeModule.CapabilityCliName,
                "two-pending-boundary-after"));
        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(9);
        dispatcher.TerminalCalls.Should().Be(9);
    }

    [Test, CancelAfter(30000)]
    public async Task NestedHostActionEntryResolvesDifferentHostDescriptorFromCatalog()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        descriptors.Add(
            ApplicationSmokeModule.ChildAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationChildResult($"host-child:{context.Action.Name}:{context.Action.Count}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        hostContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "cross-descriptor-root");
        var result = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["cross-descriptor"],
            hostContext);

        result.Result.Succeeded.Should().BeTrue(
            $"CLI error {result.Result.Error?.Code}: {result.Result.Error?.Message}; "
            + string.Join(" | ", result.Result.Output.Select(item => item.Text)));
        result.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:cross-descriptor:cross-descriptor-child:7");
        dispatcher.RunCalls.Should().Be(2);
        dispatcher.TerminalCalls.Should().Be(2);
    }

    [Test, CancelAfter(30000)]
    public async Task NestedHostActionEntryRotatesAfterSevenCallsAndContinuesTheSession()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        descriptors.Add(ApplicationSmokeModule.ChildAction);
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        const int priorCalls = OutOfProcessCapabilityWire.DefaultMaximumCallsPerRequest - 1;
        for (var i = 0; i < priorCalls; i++)
        {
            var prior = await client.InvokeCliAsync(
                ApplicationSmokeModule.CapabilityCliName,
                ["single"],
                IssueCliContext(
                    client,
                    ApplicationSmokeModule.CapabilityCliName,
                    $"nested-rotation-{i}"));
            prior.Result.Succeeded.Should().BeTrue(
                $"CLI error {prior.Result.Error?.Code}: {prior.Result.Error?.Message}");
        }

        hostContext = IssueHostEntryContext(
            client,
            ApplicationSmokeModule.NestedHostEntryCliName,
            grantExpiresAt,
            "rotation-root");
        var nested = await client.InvokeCliAsync(
            ApplicationSmokeModule.NestedHostEntryCliName,
            ["rotation"],
            hostContext);

        nested.Result.Succeeded.Should().BeTrue(
            $"CLI error {nested.Result.Error?.Code}: {nested.Result.Error?.Message}; "
            + string.Join(" | ", nested.Result.Output.Select(item => item.Text)));
        nested.Result.Output.Single().Text.Should().Be(
            "host-entry:Completed:rotation-root:cross-descriptor-child:7");

        var afterRotation = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            ["single"],
            IssueCliContext(
                client,
                ApplicationSmokeModule.CapabilityCliName,
                "nested-rotation-after"));
        afterRotation.Result.Succeeded.Should().BeTrue(
            $"CLI error {afterRotation.Result.Error?.Code}: {afterRotation.Result.Error?.Message}");
        storage.InvokeCalls.Should().Be(priorCalls + 1);
        dispatcher.RunCalls.Should().Be(2);
        dispatcher.TerminalCalls.Should().Be(2);
    }

    [Test, CancelAfter(15000)]
    public async Task ActiveHostActionCarrierSurvivesBindingRotation()
    {
        await using var client = await CreateClientAsync();
        var grant = client.CreateCapabilityGrant(DateTimeOffset.UtcNow.AddMinutes(2));
        var binding = OutOfProcessCapabilitySecurity.CreateBinding(
            client.Discovery.ContractHash,
            client.Discovery.ModuleId,
            OutOfProcessModuleHostProtocol.Version,
            grant,
            client.HostLimits,
            _controlToken);
        var now = DateTimeOffset.UtcNow;
        var session = new SidecarCapabilitySession(
            binding,
            authority => OutOfProcessCapabilitySecurity.Authenticate(authority, _controlToken),
            _ => true,
            now);
        var descriptor = ApplicationSmokeModule.HostAction;
        var inputSchema = descriptor.InputSchema
            ?? throw new AssertionException("The rotation test action has no input schema.");
        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var deadline = binding.ExpiresAt.AddSeconds(-2);
        var contribution = new HostActionEntryContribution(
            new HostActionEntryIngressBinding(
                HostActionEntryIngress.Tool,
                "rotation-tool",
                null),
            new HostActionEntryLineage(
                identity.Key,
                identity.Version,
                identity.DescriptorHash,
                identity.InputTypeIdentity,
                inputSchema.Version,
                inputSchema.ContentHash!,
                null,
                null));
        var request = new HostActionEntryContextRequest(
            HostActionEntryIngress.Tool,
            Guid.NewGuid(),
            binding.RequestId,
            binding.CancellationId,
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            binding.ExpiresAt)
        {
            Contribution = contribution,
        };

        var issued = session.IssueHostActionEntryContext(request, now, out var context);
        issued.Accepted.Should().BeTrue(issued.Message);
        context.Should().NotBeNull();
        var carrier = new HostActionEntryCarrierIdentity(
            request.Ingress,
            request.InvocationId,
            contribution.IngressBinding);
        var started = session.BeginHostActionEntryCarrier(
            context!,
            carrier,
            now,
            out var authority);
        started.Accepted.Should().BeTrue(started.Message);
        authority.Should().NotBeNull();
        var issuedAuthority = authority!;

        var replacement = OutOfProcessCapabilitySecurity.CreateBinding(
            client.Discovery.ContractHash,
            client.Discovery.ModuleId,
            OutOfProcessModuleHostProtocol.Version,
            grant,
            client.HostLimits,
            _controlToken);
        var rotated = session.RotateBinding(replacement, DateTimeOffset.UtcNow);
        rotated.Accepted.Should().BeTrue(rotated.Message);
        session.BindingGeneration.Should().Be(2);
        session.ActiveHostActionEntryCarrierCount.Should().Be(1);
        session.TryGetActiveHostActionEntryCarrier(
            context!.CapabilityId,
            out var preserved).Should().BeTrue();
        preserved.Should().NotBeNull();
        preserved!.ModuleId.Should().Be(issuedAuthority.ModuleId);
        preserved.GraphId.Should().Be(issuedAuthority.GraphId);
        preserved.CapabilityId.Should().Be(issuedAuthority.CapabilityId);
        preserved.Carrier.Should().BeEquivalentTo(issuedAuthority.Carrier);
        preserved.IssuedAt.Should().Be(issuedAuthority.IssuedAt);
        preserved.ExpiresAt.Should().Be(issuedAuthority.ExpiresAt);
        preserved.CapabilityHandleHash.Should().Be(issuedAuthority.CapabilityHandleHash);
        preserved.SessionId.Should().Be(replacement.SessionId);
        preserved.RequestId.Should().Be(replacement.RequestId);
        preserved.CancellationId.Should().Be(replacement.CancellationId);
        preserved.BindingGeneration.Should().Be(session.BindingGeneration);

        var completed = session.CompleteHostActionEntryCarrier(
            preserved,
            HostActionEntryCarrierCompletionKind.Succeeded,
            DateTimeOffset.UtcNow);
        completed.Accepted.Should().BeTrue(completed.Message);
        session.ActiveHostActionEntryCarrierCount.Should().Be(0);
        session.CompletedHostActionEntryTombstoneCount.Should().Be(1);
    }

    [Test, CancelAfter(15000)]
    public async Task ToolHostActionEntryCarriesIssuedContextAndRejectsReplay()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        HostActionEntryRequestContext? hostContext = null;
        dispatcher.HostContextFactory = () => hostContext;
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var definition = client.Discovery.ToolHandlers.Single(item =>
            item.ToolName == ApplicationSmokeModule.HostEntryToolName);
        var invocationId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Tool,
            definition.ToolName,
            definition.HandlerId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("host-tool", "tool-value"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            traceId,
            idempotencyKey,
            deadline,
            invocationId);
        hostContext = context;
        var start = CreateHostEntryToolStart(
            client,
            definition,
            invocationId,
            deadline,
            context,
            ApplicationSmokeModule.HostEntryCaller);

        start.HostActionContext.Should().BeEquivalentTo(context);
        start.HostActionContext!.Caller.Should().BeEquivalentTo(ApplicationSmokeModule.HostEntryCaller);
        start.HostActionContext.Features.Should().BeEquivalentTo(ApplicationSmokeModule.HostEntryFeatures);
        start.HostActionContext.TraceId.Should().Be(traceId);
        start.HostActionContext.IdempotencyKey.Should().Be(idempotencyKey);
        start.HostActionContext.Deadline.Should().Be(deadline);
        start.HostActionContext.Contribution!.Lineage.ActionKey.Should().Be(
            ApplicationSmokeModule.HostAction.Key);
        start.HostActionContext.Contribution.Lineage.IsPayloadBound.Should().BeTrue();
        start.InputSchema.Should().Be(definition.InputSchema);

        var result = await client.InvokeToolAsync(start);
        var tool = result.Result.Deserialize<ToolResult>(OutOfProcessProtocolCodec.JsonOptions)!;
        tool.Content.Should().Contain("host-tool:Completed:entry-terminal:tool-value");
        tool.Content.Should().Contain("caller=module-agent");
        tool.Content.Should().Contain("roles=module-agent,module-operator");
        tool.Content.Should().Contain($"trace={context.TraceId}");
        tool.Content.Should().Contain($"idempotency={context.IdempotencyKey}");
        tool.Content.Should().Contain($"deadline={context.Deadline:O}");
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);

        var replay = async () => await client.InvokeToolAsync(start);
        (await replay.Should().ThrowAsync<OutOfProcessCapabilityException>())
            .Which.Code.Should().Be(SidecarCapabilityErrors.Replay);
        dispatcher.RunCalls.Should().Be(1);
        dispatcher.TerminalCalls.Should().Be(1);
    }

    [Test, CancelAfter(15000)]
    public async Task ToolHostActionEntryRejectsHostileCarrierCallerBeforeDispatch()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var definition = client.Discovery.ToolHandlers.Single(item =>
            item.ToolName == ApplicationSmokeModule.HostEntryToolName);
        var invocationId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var context = client.IssueHostActionContext(
            HostActionEntryIngress.Tool,
            definition.ToolName,
            definition.HandlerId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("host-tool", "tool-value"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            invocationId);
        var hostileCaller = new RequestPrincipal(
            "spoofed-agent",
            ApplicationSmokeModule.HostEntryCaller.DisplayName,
            ApplicationSmokeModule.HostEntryCaller.Roles,
            ApplicationSmokeModule.HostEntryCaller.IsAuthenticated);
        var start = CreateHostEntryToolStart(
            client,
            definition,
            invocationId,
            deadline,
            context,
            hostileCaller);

        var act = async () => await client.InvokeToolAsync(start);
        (await act.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(SidecarProtocolErrors.MalformedMessage);
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);
    }

    [TestCase("caller")]
    [TestCase("roles")]
    [TestCase("authentication")]
    [TestCase("features")]
    [TestCase("trace")]
    [TestCase("idempotency")]
    [TestCase("expiry")]
    [CancelAfter(15000)]
    public async Task HostActionEntryRejectsMismatchedRequestContextBeforeDispatch(string mismatch)
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway();
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(
            ApplicationSmokeModule.HostAction,
            static (context, _) => ValueTask.FromResult(
                new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}")));
        var grantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(grantExpiresAt),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        var rejected = await client.InvokeCliAsync(
            ApplicationSmokeModule.HostEntryCliName,
            [mismatch],
            IssueHostEntryContext(client, grantExpiresAt));

        rejected.Result.Succeeded.Should().BeFalse();
        rejected.Result.Error?.Code.Should().Be("host_entry_failed");
        dispatcher.RunCalls.Should().Be(0);
        dispatcher.TerminalCalls.Should().Be(0);
        storage.InvokeCalls.Should().Be(0);
    }

    [Test, CancelAfter(30000)]
    public async Task CapabilityCancellationStopsHostOperationAndKeepsSessionUsable()
    {
        await using var client = await CreateClientAsync();
        var storage = new CountingStorageGateway { BlockInvoke = true };
        var dispatcher = new CountingActionDispatcher();
        var descriptors = new OutOfProcessActionDescriptorCatalog();
        descriptors.Add(ApplicationSmokeModule.HostAction);
        await client.ConnectCapabilitiesAsync(new OutOfProcessCapabilityHostOptions(
            storage,
            dispatcher,
            client.CreateCapabilityGrant(),
            ["application-store"],
            descriptors,
            new ActionPipelineSnapshot(
                client.Discovery.ContractHash,
                client.Authorization.ActionGrants,
                client.Authorization.EventGrants),
            new OutOfProcessHostActionEntryContextRegistry(),
            new KernelExternalAuthoritySessionRegistry()));

        using var cancellation = new CancellationTokenSource();
        var pending = client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            [],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "capability-cancellation"),
            ct: cancellation.Token).AsTask();
        await storage.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.That(
            async () => await pending,
            Throws.InstanceOf<OperationCanceledException>());
        await storage.InvocationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        storage.BlockInvoke = false;
        var followUp = await client.InvokeCliAsync(
            ApplicationSmokeModule.CapabilityCliName,
            [],
            IssueCliContext(client, ApplicationSmokeModule.CapabilityCliName, "capability-after-cancellation"));

        followUp.Result.Succeeded.Should().BeTrue(
            $"CLI error {followUp.Result.Error?.Code}: {followUp.Result.Error?.Message}; "
            + string.Join(" | ", followUp.Result.Output.Select(item => item.Text)));
        storage.InvokeCalls.Should().Be(2);
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

    private static HostActionEntryRequestContext IssueCliContext(
        OutOfProcessModuleClient client,
        string command,
        string subject,
        DateTimeOffset? deadline = null) =>
        client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            command,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction("cli", command),
            new RequestPrincipal(subject),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(1));

    private static HostActionEntryRequestContext IssueHostEntryContext(
        OutOfProcessModuleClient client,
        DateTimeOffset deadline) =>
        IssueHostEntryContext(
            client,
            ApplicationSmokeModule.HostEntryCliName,
            deadline,
            "host-entry");

    private static HostActionEntryRequestContext IssueHostEntryContext(
        OutOfProcessModuleClient client,
        string command,
        DateTimeOffset deadline,
        string actionMode = "host-entry") =>
        client.IssueHostActionContext(
            HostActionEntryIngress.Cli,
            command,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction(actionMode, "action"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline);

    private static HostActionEntryRequestContext IssueHostEntryContextThroughRegistry(
        OutOfProcessModuleClient client,
        DateTimeOffset deadline) =>
        IssueHostEntryContextThroughRegistry(
            client,
            ApplicationSmokeModule.HostEntryCliName,
            deadline,
            "host-entry");

    private static HostActionEntryRequestContext IssueHostEntryContextThroughRegistry(
        OutOfProcessModuleClient client,
        string command,
        DateTimeOffset deadline,
        string actionMode = "host-entry") =>
        client.HostActionEntryContexts.Issue(
            HostActionEntryIngress.Cli,
            command,
            client.Discovery.ModuleId,
            ApplicationSmokeModule.HostAction,
            new ApplicationSmokeAction(actionMode, "action"),
            ApplicationSmokeModule.HostEntryCaller,
            ApplicationSmokeModule.HostEntryFeatures,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline);

    private static SidecarToolHandlerInvokeStart CreateHostEntryToolStart(
        OutOfProcessModuleClient client,
        SidecarToolHandlerDefinition definition,
        Guid invocationId,
        DateTimeOffset deadline,
        HostActionEntryRequestContext context,
        RequestPrincipal caller) =>
        SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence: 1,
            deadline,
            client.HostLimits.ActionInputBytes,
            header => new SidecarToolHandlerInvokeStart(
                header,
                invocationId,
                definition.ToolName,
                definition.HandlerId,
                JsonSerializer.SerializeToElement(
                    new { value = "tool-value" },
                    OutOfProcessProtocolCodec.JsonOptions),
                definition.InputSchema,
                caller,
                context));

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

    private static SidecarHostActionDescriptor ChildHostDescriptor() =>
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

    private static KernelGraph BuildRealCoreHostGraph()
    {
        var builder = new KernelGraphBuilder(false);
        builder.Add(ApplicationSmokeModule.HostAction, "host-runtime");
        using var services = new ServiceCollection().BuildServiceProvider();
        return builder.Compile(
            services,
            new KernelGraphCompileOptions
            {
                SupportedActionCapabilities = ApplicationSmokeModule.HostCapabilities,
                ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [ApplicationSmokeModule.HostAction.Key.Value] = ApplicationSmokeModule.HostCapabilities,
                },
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["host-runtime"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [ApplicationSmokeModule.HostAction.Key.Value] = ApplicationSmokeModule.HostCapabilities,
                    },
                    [ApplicationSmokeModule.Id] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [ApplicationSmokeModule.HostAction.Key.Value] =
                            ApplicationSmokeModule.HostAction.Capabilities,
                        [ApplicationSmokeModule.AgentsJobImportAction.Key.Value] =
                            ApplicationSmokeModule.AgentsJobImportAction.Capabilities,
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
            "real-core-host",
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
            throw new NotSupportedException("The real Core test graph has no repeat actions.");
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

        public bool BlockInvoke { get; set; }

        public TaskCompletionSource InvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InvocationReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InvocationRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InvocationCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public async Task<JsonElement> InvokeAsync(
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
            if (BlockInvoke)
            {
                InvocationStarted.TrySetResult();
                try
                {
                    await InvocationRelease.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    InvocationCancelled.TrySetResult();
                    throw;
                }
            }

            InvocationReturned.TrySetResult();
            return JsonSerializer.SerializeToElement(new { value = "storage" });
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

        public int SnapshotRejectionCalls { get; private set; }

        public ActionInterceptionCapabilities? LastSnapshotCapabilities { get; private set; }

        public string? LastSnapshotHash { get; private set; }

        public string? LastSnapshotContractHash { get; private set; }

        public string? ExpectedSnapshotContractHash { get; set; }

        public Func<object, object?>? ReplaceInput { get; set; }

        public Func<HostActionEntryRequestContext?>? HostContextFactory { get; set; }

        public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
            ActionDescriptor<TAction, TResult> descriptor,
            TAction action,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            CancellationToken ct)
        {
            if (ExpectedSnapshotContractHash is not null
                && !string.Equals(
                    snapshot.ContractHash,
                    ExpectedSnapshotContractHash,
                    StringComparison.Ordinal))
            {
                SnapshotRejectionCalls++;
                throw new AssertionException(
                    $"The dispatcher received snapshot '{snapshot.ContractHash}', "
                    + $"expected '{ExpectedSnapshotContractHash}'.");
            }

            var matchingGrants = snapshot.ActionGrants.Where(item =>
                item.ActionKey == descriptor.Key
                && item.ActionVersion == descriptor.Version)
                .ToArray();
            if (matchingGrants.Length != 1)
            {
                throw new AssertionException(
                    $"The dispatcher received {matchingGrants.Length} grants for "
                    + $"{descriptor.Key}:{descriptor.Version}: "
                    + string.Join(", ", matchingGrants.Select(item => item.Capabilities)));
            }

            var grant = matchingGrants[0];
            var expectedCapabilities = descriptor.Key == ApplicationSmokeModule.AgentsJobImportAction.Key
                || descriptor.Key == ApplicationSmokeModule.PermissionPolicyAction.Key
                ? ActionInterceptionCapabilities.Inspect
                : ApplicationSmokeModule.HostCapabilities;
            if (grant.Capabilities != expectedCapabilities)
                throw new AssertionException(
                    $"The dispatcher received unexpected capabilities for {descriptor.Key}: {grant.Capabilities}.");

            LastSnapshotCapabilities = grant.Capabilities;
            LastSnapshotContractHash = snapshot.ContractHash;
            LastSnapshotHash = SidecarCapabilityTransportValidation.ComputeSnapshotHash(snapshot);
            RunCalls++;
            var hostContext = HostContextFactory?.Invoke();
            var effectiveAction = ReplaceInput?.Invoke(action!) is { } replacement
                ? (TAction)replacement
                : action;
            var result = await terminal(
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
                    effectiveAction,
                    hostContext?.Features ?? ExtensionFeatureSet.Empty,
                    snapshot),
                ct);
            TerminalCalls++;
            return new CountingActionOutcome<TResult>(result);
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
            CancellationToken ct)
        {
            var outcome = await RunAsync(descriptor, action, terminal, snapshot, ct);
            return outcome.Result;
        }

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

    private sealed class CountingActionOutcome<TResult>(TResult result) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;

        public TResult Result => result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => null;

        public ActionUncertainty? Uncertainty => null;
    }

}
