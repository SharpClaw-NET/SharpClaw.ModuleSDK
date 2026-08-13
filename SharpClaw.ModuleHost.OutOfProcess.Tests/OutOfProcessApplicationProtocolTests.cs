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
                  "effects": ["inspect", "cancel"]
                },
                {
                  "target": "module.application.smoke",
                  "effects": ["inspect", "cancel"]
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
        if (Directory.Exists(_moduleDirectory))
            Directory.Delete(_moduleDirectory, recursive: true);
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
        result.Result.Succeeded.Should().BeTrue();
        result.Result.Output.Single().Text.Should().Be(
            $"{ApplicationSmokeModule.Id}|{ApplicationSmokeModule.Id}|{client.Discovery.ContractHash}|{ApplicationSmokeModule.CliName}");
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

        allowed.Completion.Kind.Should().Be(ActionOutcomeKind.Completed);
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

    private Task<OutOfProcessModuleClient> CreateClientAsync() =>
        OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private static HookInvokeStart CreateStart(
        OutOfProcessModuleClient client,
        string mode)
    {
        var descriptor = HostDescriptor();
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
                ApplicationSmokeModule.HostActionHookId,
                descriptor.ActionKey,
                descriptor.Version,
                SidecarPayloadMode.Typed,
                JsonSerializer.SerializeToElement(
                    new ApplicationSmokeAction(mode, "value"),
                    OutOfProcessProtocolCodec.JsonOptions),
                descriptor,
                grant,
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                new ContinuationHandle(
                    Guid.NewGuid(),
                    invocationId,
                    ApplicationSmokeModule.HostActionHookId,
                    deadline,
                    1)));
    }

    private static SidecarHostActionDescriptor HostDescriptor()
    {
        var descriptor = ApplicationSmokeModule.HostAction;
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
}
