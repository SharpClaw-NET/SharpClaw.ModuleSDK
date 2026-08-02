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
public sealed class OutOfProcessToolLifecycleProtocolTests
{
    private Uri _controlAddress = null!;
    private string _controlToken = null!;
    private OutOfProcessModuleServer _server = null!;
    private SidecarHostDescriptorCatalog _catalog = null!;
    private ToolLifecycleSmokeModule _inProcessModule = null!;
    private ServiceProvider _inProcessServices = null!;
    private ModuleContributionGraph _inProcessGraph = null!;
    private InProcessModuleInvoker _inProcessInvoker = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        var moduleDirectory = Path.Combine(root, "tool-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDirectory);
        var moduleAssemblyName = Path.GetFileName(typeof(ToolLifecycleSmokeModule).Assembly.Location);
        File.Copy(
            typeof(ToolLifecycleSmokeModule).Assembly.Location,
            Path.Combine(moduleDirectory, moduleAssemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(moduleDirectory, "module.json"),
            $$"""
            {
              "id": "{{ToolLifecycleSmokeModule.Id}}",
              "displayName": "Tool Lifecycle Smoke",
              "version": "0.5.0-beta.2",
              "toolPrefix": "smoke",
              "entryAssembly": "{{moduleAssemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{typeof(ToolLifecycleSmokeModule).FullName}}"
            }
            """,
            Encoding.UTF8);
        _controlAddress = await FindFreeAddressAsync();
        _controlToken = "tool-token-" + Guid.NewGuid().ToString("N");
        _server = await OutOfProcessModuleServer.CreateAsync(
            moduleDirectory,
            _controlAddress,
            _controlToken);
        await _server.StartAsync();
        _catalog = new SidecarHostDescriptorCatalog(
            [],
            [],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());

        _inProcessModule = new ToolLifecycleSmokeModule();
        _inProcessGraph = SharpClawModuleCompiler.Compile(
            _inProcessModule,
            InProcessManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in _inProcessGraph.Services)
            services.Add(descriptor);
        services.AddSingleton<ISharpClawModule>(_inProcessModule);
        services.AddSingleton(_inProcessModule);
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
    public async Task ToolHandlerReturnsCompleteToolResult()
    {
        await using var client = await CreateClientAsync();

        var terminal = await client.InvokeToolAsync(CreateToolStart(
            client,
            "echo",
            "hello"));

        terminal.Result.Deserialize<ToolResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Content.Should().Be("hello");
        terminal.ResultSchema.Should().Be(client.Discovery.ToolHandlers.Single().ResultSchema);
    }

    [TestCase("fail", "module_tool_failed")]
    [TestCase("cancel", "tool_cancelled")]
    [CancelAfter(15000)]
    public async Task ToolTerminalFailuresPreserveStableCodes(string mode, string code)
    {
        await using var client = await CreateClientAsync();

        var act = async () => await client.InvokeToolAsync(CreateToolStart(client, mode, null));

        (await act.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(code);
    }

    [Test, CancelAfter(15000)]
    public async Task LifecycleStartAndStopChangeToolVisibleState()
    {
        await using var client = await CreateClientAsync();

        await client.InvokeLifecycleAsync(CreateLifecycleStart(
            client,
            SidecarLifecycleCallKind.Start));
        var started = await client.InvokeToolAsync(CreateToolStart(client, "state", null));
        await client.InvokeLifecycleAsync(CreateLifecycleStart(
            client,
            SidecarLifecycleCallKind.Stop));
        var stopped = await client.InvokeToolAsync(CreateToolStart(client, "state", null));

        started.Result.Deserialize<ToolResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Content.Should().Be("started");
        stopped.Result.Deserialize<ToolResult>(OutOfProcessProtocolCodec.JsonOptions)!
            .Content.Should().Be("stopped");
    }

    [TestCase(ModuleHostingMode.InProcess)]
    [TestCase(ModuleHostingMode.OutOfProcess)]
    [Category("ModuleHostHandlerConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsReturnTheSameToolResult(ModuleHostingMode hostingMode)
    {
        var result = await InvokeToolConformanceAsync(hostingMode, "echo", "hello");

        result.Should().Be("hello");
    }

    [TestCase(ModuleHostingMode.InProcess)]
    [TestCase(ModuleHostingMode.OutOfProcess)]
    [Category("ModuleHostHandlerConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsExposeTheSameLifecycleState(ModuleHostingMode hostingMode)
    {
        await InvokeLifecycleConformanceAsync(hostingMode, SidecarLifecycleCallKind.Start);
        var started = await InvokeToolConformanceAsync(hostingMode, "state", null);
        await InvokeLifecycleConformanceAsync(hostingMode, SidecarLifecycleCallKind.Stop);
        var stopped = await InvokeToolConformanceAsync(hostingMode, "state", null);

        started.Should().Be("started");
        stopped.Should().Be("stopped");
    }

    [Test, CancelAfter(15000)]
    public async Task ForgedLifecycleHandlerFailsBeforeModuleCodeRuns()
    {
        await using var client = await CreateClientAsync();
        var start = CreateLifecycleStart(
            client,
            SidecarLifecycleCallKind.Start,
            handlerId: "forged:lifecycle:start");

        var act = async () => await client.InvokeLifecycleAsync(start);

        (await act.Should().ThrowAsync<OutOfProcessProtocolException>())
            .Which.Code.Should().Be(SidecarProtocolErrors.UnknownHostDescriptor);
    }

    private async ValueTask<string> InvokeToolConformanceAsync(
        ModuleHostingMode hostingMode,
        string mode,
        string? text)
    {
        if (hostingMode == ModuleHostingMode.OutOfProcess)
        {
            await using var client = await CreateClientAsync();
            var terminal = await client.InvokeToolAsync(CreateToolStart(client, mode, text));
            return terminal.Result.Deserialize<ToolResult>(OutOfProcessProtocolCodec.JsonOptions)!
                .Content;
        }

        var invocationId = Guid.NewGuid();
        var result = await _inProcessInvoker.InvokeToolAsync(
            ToolLifecycleSmokeModule.ToolName,
            new ToolInvocation(
                invocationId,
                null,
                invocationId.ToString("D"),
                ToolLifecycleSmokeModule.ToolName,
                JsonSerializer.SerializeToElement(
                    new { mode, text },
                    OutOfProcessProtocolCodec.JsonOptions),
                new RequestPrincipal("test-user"),
                ExtensionFeatureSet.Empty),
            CancellationToken.None);
        return result.Content;
    }

    private async ValueTask InvokeLifecycleConformanceAsync(
        ModuleHostingMode hostingMode,
        SidecarLifecycleCallKind call)
    {
        if (hostingMode == ModuleHostingMode.OutOfProcess)
        {
            await using var client = await CreateClientAsync();
            await client.InvokeLifecycleAsync(CreateLifecycleStart(client, call));
            return;
        }

        if (call == SidecarLifecycleCallKind.Start)
        {
            await _inProcessModule.StartAsync(
                new ModuleStartContext(
                    _inProcessGraph.Identity,
                    "test-host",
                    _inProcessGraph.ContractHash,
                    ExtensionFeatureSet.Empty),
                CancellationToken.None);
            return;
        }

        await _inProcessModule.StopAsync(CancellationToken.None);
    }

    private Task<OutOfProcessModuleClient> CreateClientAsync() =>
        OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private static SidecarToolHandlerInvokeStart CreateToolStart(
        OutOfProcessModuleClient client,
        string mode,
        string? text)
    {
        var definition = client.Discovery.ToolHandlers.Single();
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence: 1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            client.HostLimits.ActionInputBytes,
            header => new SidecarToolHandlerInvokeStart(
                header,
                Guid.NewGuid(),
                definition.ToolName,
                definition.HandlerId,
                JsonSerializer.SerializeToElement(
                    new { mode, text },
                    OutOfProcessProtocolCodec.JsonOptions),
                definition.InputSchema,
                new RequestPrincipal("test-user")));
    }

    private static SidecarLifecycleHandlerInvokeStart CreateLifecycleStart(
        OutOfProcessModuleClient client,
        SidecarLifecycleCallKind call,
        string? handlerId = null)
    {
        var definition = client.Discovery.LifecycleHandlers.Single(item => item.Call == call);
        JsonElement? input = call == SidecarLifecycleCallKind.Start
            ? JsonSerializer.SerializeToElement(
                new ModuleStartContext(
                    new ModuleIdentity(
                        ToolLifecycleSmokeModule.Id,
                        "Tool Lifecycle Smoke",
                        "smoke"),
                    "test-host",
                    client.Discovery.ContractHash,
                    ExtensionFeatureSet.Empty),
                OutOfProcessProtocolCodec.JsonOptions)
            : null;
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence: 1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            client.HostLimits.ActionInputBytes,
            header => new SidecarLifecycleHandlerInvokeStart(
                header,
                Guid.NewGuid(),
                call,
                handlerId ?? definition.HandlerId,
                input));
    }

    private static ModuleManifest InProcessManifest() =>
        new(
            ToolLifecycleSmokeModule.Id,
            "Tool Lifecycle Smoke",
            "0.5.0-beta.2",
            "smoke",
            "ToolLifecycleSmokeModule.dll",
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            ModuleType: typeof(ToolLifecycleSmokeModule).FullName,
            HostMode: ModuleManifestRuntimeInfo.HostModeInProcess);

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
