using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.InProcess.Tests;

public sealed class InProcessModuleHostTests
{
    [Test]
    public void AssemblyLoaderUsesTheExplicitModuleType()
    {
        var manifest = Manifest(
            typeof(LifecycleRegistration).Assembly.Location,
            typeof(LifecycleRegistration).FullName);
        var runtime = new PackageRuntimeInfo(
            PackageRuntimeInfo.DotNet,
            typeof(LifecycleRegistration).FullName,
            PackageRuntimeInfo.HostModeInProcess);

        var module = InProcessModuleAssemblyLoader.CreateModuleInstance(
            typeof(LifecycleRegistration).Assembly,
            manifest,
            runtime,
            typeof(LifecycleRegistration).Assembly.Location);

        module.Should().BeOfType<LifecycleRegistration>();
    }

    [Test]
    public async Task InvokerPassesTheHostIssuedControlWithoutReplacement()
    {
        var module = new ControlModule();
        var graph = SharpClawModuleCompiler.Compile(
            module,
            ControlManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
        IServiceCollection serviceCollection = new ServiceCollection();
        foreach (var descriptor in graph.Services)
            serviceCollection.Add(descriptor);
        await using var services = serviceCollection.BuildServiceProvider();
        var invoker = new InProcessModuleInvoker(graph, services);
        var control = new StubActionControl();

        var outcome = await invoker.InvokeActionAsync<TestAction, TestResult>(
            graph.ActionHooks.Single(),
            Context(),
            control,
            CancellationToken.None);

        services.GetRequiredService<ControlCapture>().Control.Should().BeSameAs(control);
        outcome.Kind.Should().Be(ActionOutcomeKind.Completed);
        outcome.Result.Should().Be(new TestResult("captured"));
    }

    [Test]
    public async Task ScopedContributionsUseDistinctInstancesAndDisposeAfterEachInvocation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var actionHook = fixture.Graph.ActionHooks.Single();
        var eventHook = fixture.Graph.EventHooks.Single();

        for (var index = 0; index < 2; index++)
        {
            await fixture.Invoker.InvokeActionAsync<TestAction, TestResult>(
                actionHook,
                Context(),
                new StubActionControl(),
                CancellationToken.None);
            await fixture.Invoker.InvokeEventListenerAsync(
                eventHook,
                new EventEnvelope<TestEvent>(
                    Guid.NewGuid(),
                    null,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    "in_process_control",
                    new TestEvent(index)),
                CancellationToken.None);
            await fixture.Invoker.InvokeToolAsync(
                ControlModule.ToolName,
                CreateToolInvocation(null),
                CancellationToken.None);
            var cliContext = CreateCliContext();
            var cli = await fixture.Invoker.InvokeCliAsync(
                new CliInvocation(
                    cliContext.InvocationId,
                    ControlModule.Cli.Name,
                    [index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    cliContext),
                CancellationToken.None);
            cli.Succeeded.Should().BeTrue();
        }

        var capture = services.GetRequiredService<ScopedInvocationCapture>();
        capture.AssertCategory("action", 2);
        capture.AssertCategory("event", 2);
        capture.AssertCategory("tool", 2);
        capture.AssertCategory("cli", 2);
    }

    [Test]
    public async Task ScopedAuthorizationServicesUseDistinctInstancesAndDisposeAfterEachEvaluation()
    {
        var policyGraph = SharpClawModuleCompiler.Compile(new ScopedAuthorizationPolicyModule());
        await using var policyServices = BuildValidatedProvider(policyGraph);
        for (var index = 0; index < 2; index++)
        {
            await using var scope = policyServices.CreateAsyncScope();
            var decision = await scope.ServiceProvider
                .GetRequiredService<AuthorizationPolicyTerminal>()
                .InvokeAsync(CreateAuthorizationContext());
            decision.Allowed.Should().BeTrue();
        }
        policyServices.GetRequiredService<ScopedInvocationCapture>()
            .AssertCategory("authorization-policy", 2);

        var restrictionGraph = SharpClawModuleCompiler.Compile(
            new ScopedAuthorizationRestrictionModule(),
            ScopedAuthorizationRestrictionManifest());
        await using var restrictionServices = BuildValidatedProvider(restrictionGraph);
        for (var index = 0; index < 2; index++)
        {
            await using var scope = restrictionServices.CreateAsyncScope();
            var outcome = await scope.ServiceProvider
                .GetRequiredService<AuthorizationRestrictionHook<ScopedAuthorizationRestriction>>()
                .InvokeAsync(
                    CreateAuthorizationContext(),
                    new AuthorizationActionControl(),
                    CancellationToken.None);
            outcome.Kind.Should().Be(ActionOutcomeKind.Completed);
            outcome.Result!.Allowed.Should().BeTrue();
        }
        restrictionServices.GetRequiredService<ScopedInvocationCapture>()
            .AssertCategory("authorization-restriction", 2);
    }

    [Test]
    public async Task ToolInvokerAcceptsNullConversationIdentity()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateToolInvocation(null);

        var result = await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            CancellationToken.None);

        result.Content.Should().Be("captured");
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(1);
        capture.Invocations.Should().Be(1);
        capture.LastInvocation.Should().BeSameAs(invocation);
    }

    [Test]
    public async Task ToolInvokerAcceptsNonNullConversationIdentity()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateToolInvocation(Guid.NewGuid());

        var result = await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            CancellationToken.None);

        result.Content.Should().Be("captured");
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(1);
        capture.Invocations.Should().Be(1);
        capture.LastInvocation.Should().BeSameAs(invocation);
    }

    [TestCase("empty-conversation")]
    [TestCase("missing-conversation")]
    [TestCase("changed-secondary-identity")]
    [TestCase("noncanonical-secondary-identity")]
    [TestCase("mismatched-tool-name")]
    [TestCase("expired-context")]
    [TestCase("invalid-caller-context")]
    [TestCase("empty-invocation-identity")]
    public async Task ToolInvokerRejectsInvalidInvocationBeforeHandlerCreation(string mutation)
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateInvalidToolInvocation(mutation);

        var act = async () => await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(0);
        capture.Invocations.Should().Be(0);
    }

    [Test]
    public async Task ToolInvokerRejectsPreCancellationBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var invocation = CreateToolInvocation(null);

        var act = async () => await fixture.Invoker.InvokeToolAsync(
            ControlModule.ToolName,
            invocation,
            new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        var capture = services.GetRequiredService<ToolInvocationCapture>();
        capture.Constructions.Should().Be(0);
        capture.Invocations.Should().Be(0);
    }

    [Test]
    public async Task HttpEndpointInvokerUsesTheDeclaredRouteAndOneInvocationScope()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.EndpointRoute);

        var response = await fixture.Invoker.InvokeHttpEndpointAsync(
            request,
            new StubHostActionEntry(),
            CancellationToken.None);

        response.StatusCode.Should().Be(200);
        using var payload = JsonDocument.Parse(response.Body);
        payload.RootElement.GetProperty("path").GetString().Should().Be("/inprocess/control");
        payload.RootElement.GetProperty("routeValue").GetString().Should().Be("inprocess-route-value");
        var capture = services.GetRequiredService<EndpointInvocationCapture>();
        capture.Constructions.Should().Be(1);
        capture.Invocations.Should().Be(1);
        capture.RouteValue.Should().Be("inprocess-route-value");
        capture.Disposals.Should().Be(1);
    }

    [Test]
    public async Task HttpEndpointInvokerRejectsChangedRouteBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.EndpointRoute) with
        {
            Route = ControlModule.EndpointRoute.ToRouteIdentity() with
            {
                Path = "/inprocess/changed",
            },
        };

        var act = async () => await fixture.Invoker.InvokeHttpEndpointAsync(
            request,
            new StubHostActionEntry(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        services.GetRequiredService<EndpointInvocationCapture>().Constructions.Should().Be(0);
    }

    [Test]
    public async Task HttpEndpointInvokerRejectsCancellationBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.EndpointRoute);

        var act = async () => await fixture.Invoker.InvokeHttpEndpointAsync(
            request,
            new StubHostActionEntry(),
            new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        services.GetRequiredService<EndpointInvocationCapture>().Constructions.Should().Be(0);
    }

    [Test]
    public async Task WebSocketEndpointInvokerUsesTheDeclaredRouteAndOneInvocationScope()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.WebSocketEndpointRoute);
        var text = new WebSocketMessage(
            WebSocketMessageType.Text,
            "in-process"u8.ToArray());
        var channel = new RecordingWebSocketChannel(
        [
            text,
            new WebSocketMessage(
                WebSocketMessageType.Close,
                [],
                1000,
                "complete"),
        ]);

        await fixture.Invoker.InvokeWebSocketEndpointAsync(
            request,
            channel,
            new StubHostActionEntry(),
            CancellationToken.None);

        channel.Sent.Should().ContainSingle().Which.Should().Be(text);
        channel.CloseStatus.Should().Be(1000);
        channel.CloseDescription.Should().Be("complete");
        var capture = services.GetRequiredService<WebSocketInvocationCapture>();
        capture.Constructions.Should().Be(1);
        capture.Invocations.Should().Be(1);
        capture.Disposals.Should().Be(1);
    }

    [Test]
    public async Task WebSocketEndpointInvokerRejectsChangedRouteBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.WebSocketEndpointRoute) with
        {
            Route = ControlModule.WebSocketEndpointRoute.ToRouteIdentity() with
            {
                Path = "/inprocess/changed-websocket",
            },
        };

        var act = async () => await fixture.Invoker.InvokeWebSocketEndpointAsync(
            request,
            new RecordingWebSocketChannel([]),
            new StubHostActionEntry(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        services.GetRequiredService<WebSocketInvocationCapture>().Constructions.Should().Be(0);
    }

    [Test]
    public async Task WebSocketEndpointInvokerRejectsCancellationBeforeHandlerCreation()
    {
        var fixture = CreateToolFixture();
        await using var services = fixture.Services;
        var request = CreateEndpointRequest(ControlModule.WebSocketEndpointRoute);

        var act = async () => await fixture.Invoker.InvokeWebSocketEndpointAsync(
            request,
            new RecordingWebSocketChannel([]),
            new StubHostActionEntry(),
            new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        services.GetRequiredService<WebSocketInvocationCapture>().Constructions.Should().Be(0);
    }

    [Test]
    public async Task HostLoadsStartsAndStopsOneInProcessModule()
    {
        var registrationDirectory = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.ModuleSDK",
            "in-process-host",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registrationDirectory);
        try
        {
            foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                File.Copy(
                    source,
                    Path.Combine(registrationDirectory, Path.GetFileName(source)),
                    overwrite: true);
            }

            var manifest = Manifest(
                typeof(LifecycleRegistration).Assembly.Location,
                typeof(LifecycleRegistration).FullName);
            await File.WriteAllTextAsync(
                Path.Combine(registrationDirectory, "package.json"),
                JsonSerializer.Serialize(manifest));

            await RunLifecycleAsync(registrationDirectory);
        }
        finally
        {
            await DeleteModuleDirectoryAsync(registrationDirectory);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunLifecycleAsync(string registrationDirectory)
    {
        await using var host = await InProcessRegistrationHost.LoadAsync(registrationDirectory);
        host.Graph.HostingMode.Should().Be(ModuleHostingMode.InProcess);
        host.Module.Should().BeAssignableTo<ISharpClawModule>();
        GetStarted(host.Module).Should().BeFalse();

        await host.StartAsync("test-host");
        GetStarted(host.Module).Should().BeTrue();

        await host.StopAsync();
        GetStarted(host.Module).Should().BeFalse();
    }

    private static async Task DeleteModuleDirectoryAsync(string registrationDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                Directory.Delete(registrationDirectory, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(25);
            }
        }
    }

    private static bool GetStarted(ISharpClawModule module) =>
        (bool)(module.GetType().GetProperty(nameof(LifecycleRegistration.Started))?.GetValue(module)
            ?? throw new InvalidOperationException("The loaded test module has no Started property."));

    private static PackageManifest Manifest(string assemblyPath, string? entryType) =>
        new(
            "in_process_lifecycle",
            "In-process Lifecycle",
            "0.5.0-beta.2",
            "inprocess",
            Path.GetFileName(assemblyPath),
            "0.5.0-beta.2",
            Runtime: PackageRuntimeInfo.DotNet,
            EntryType: entryType,
            HostMode: PackageRuntimeInfo.HostModeInProcess);

    private static PackageManifest ControlManifest() =>
        new(
            "in_process_control",
            "In-process Control",
            "0.5.0-beta.2",
            "inprocess",
            "Control.dll",
            "0.5.0-beta.2",
            Runtime: PackageRuntimeInfo.DotNet,
            HostMode: PackageRuntimeInfo.HostModeInProcess,
            RequestedHooks:
            [
                new PackageHookRequest("inprocess.control", ["replaceResult"]),
            ],
            RequestedEvents:
            [
                new PackageEventRequest("inprocess.control.event", "Inline", ["observe"]),
            ]);

    private static PackageManifest ScopedAuthorizationRestrictionManifest() =>
        new(
            "scoped_authorization_restriction",
            "Scoped Authorization Restriction",
            "0.5.0-dev",
            "scoped_authorization_restriction",
            "ScopedAuthorizationRestriction.dll",
            "0.5.0-dev",
            Runtime: PackageRuntimeInfo.DotNet,
            HostMode: PackageRuntimeInfo.HostModeInProcess,
            RequestedHooks:
            [
                new PackageHookRequest(
                    AuthorizationProtocol.Evaluate.Key.Value,
                    ["inspect", "wrap"]),
            ]);

    private static ToolFixture CreateToolFixture()
    {
        var graph = SharpClawModuleCompiler.Compile(
            new ControlModule(),
            ControlManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.InProcess });
        IServiceCollection serviceCollection = new ServiceCollection();
        foreach (var descriptor in graph.Services)
            serviceCollection.Add(descriptor);
        var services = serviceCollection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return new ToolFixture(graph, services, new InProcessModuleInvoker(graph, services));
    }

    private static ServiceProvider BuildValidatedProvider(ModuleContributionGraph graph)
    {
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in graph.Services)
            services.Add(descriptor);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ActionContext<AuthorizationRequest> CreateAuthorizationContext() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            AuthorizationProtocol.Evaluate.Key,
            "scoped-authorization",
            new RequestPrincipal("authorization-user", IsAuthenticated: true),
            new AuthorizationRequest(
                "scope.evaluate",
                new AuthorizationResource("scope", "resource")),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("scoped-authorization", []));

    private static ToolInvocation CreateToolInvocation(Guid? conversationId)
    {
        var invocationId = Guid.NewGuid();
        return new ToolInvocation(
            invocationId,
            conversationId,
            "tool-call",
            ControlModule.ToolName,
            JsonSerializer.SerializeToElement(new { value = "input" }),
            CreateToolContext(invocationId, conversationId));
    }

    private static ToolInvocation CreateInvalidToolInvocation(string mutation)
    {
        var valid = CreateToolInvocation(Guid.NewGuid());
        return mutation switch
        {
            "empty-conversation" => valid with { ConversationId = Guid.Empty },
            "missing-conversation" => valid with { ConversationId = null },
            "changed-secondary-identity" => valid with
            {
                HostActionContext = WithConversationIdentity(
                    valid.HostActionContext,
                    Guid.NewGuid().ToString("D")),
            },
            "noncanonical-secondary-identity" => valid with
            {
                HostActionContext = WithConversationIdentity(
                    valid.HostActionContext,
                    $"{{{valid.ConversationId!.Value:D}}}"),
            },
            "mismatched-tool-name" => valid with { ToolName = "inprocess.other" },
            "expired-context" => valid with
            {
                HostActionContext = valid.HostActionContext with
                {
                    Deadline = DateTimeOffset.UtcNow.AddSeconds(-2),
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                },
            },
            "invalid-caller-context" => valid with
            {
                HostActionContext = valid.HostActionContext with { Caller = null! },
            },
            "empty-invocation-identity" => valid with { InvocationId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
    }

    private static HostActionEntryRequestContext CreateToolContext(
        Guid invocationId,
        Guid? conversationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "tool-capability",
            HostActionEntryIngress.Tool,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RequestPrincipal("tool-user"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    ControlModule.ToolName,
                    conversationId?.ToString("D")),
                new HostActionEntryLineage(
                    new SharpClawActionKey("inprocess.tool"),
                    1,
                    "tool-descriptor-hash",
                    typeof(ToolInvocation).AssemblyQualifiedName!,
                    1,
                    "tool-input-schema-hash",
                    null,
                    null)),
        };
    }

    private static HostActionEntryRequestContext CreateCliContext()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "cli-capability",
            HostActionEntryIngress.Cli,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RequestPrincipal("cli-user"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Cli,
                    ControlModule.Cli.Name),
                new HostActionEntryLineage(
                    ControlModule.Action.Key,
                    ControlModule.Action.Version,
                    "inprocess-control-descriptor-hash",
                    typeof(TestAction).AssemblyQualifiedName!,
                    1,
                    "inprocess-control-input-schema-hash",
                    null,
                    null)),
        };
    }

    private static HostEndpointRouteRequest CreateEndpointRequest(
        EndpointRouteDescriptor descriptor)
    {
        var invocationId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var context = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "endpoint-capability",
            HostActionEntryIngress.Endpoint,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new RequestPrincipal("endpoint-user"),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Endpoint,
                    descriptor.Id),
                new HostActionEntryLineage(
                    ControlModule.Action.Key,
                    ControlModule.Action.Version,
                    "inprocess-control-descriptor-hash",
                    typeof(TestAction).AssemblyQualifiedName!,
                    1,
                    "inprocess-control-input-schema-hash",
                    null,
                    null)),
        };
        return new HostEndpointRouteRequest(
            new HostEndpointInvocation(invocationId, descriptor.Id, context),
            descriptor.ToRouteIdentity(),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            new Dictionary<string, string[]>(StringComparer.Ordinal),
            [])
        {
            RouteValues = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["id"] = ["inprocess-route-value"],
            },
        };
    }

    private static HostActionEntryRequestContext WithConversationIdentity(
        HostActionEntryRequestContext context,
        string? conversationIdentity) =>
        context with
        {
            Contribution = context.Contribution! with
            {
                IngressBinding = context.Contribution.IngressBinding with
                {
                    SecondaryIdentity = conversationIdentity,
                },
            },
        };

    private static ActionContext<TestAction> Context() =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ControlModule.Action.Key,
            "host",
            RequestPrincipal.Anonymous,
            new TestAction("input"),
            ExtensionFeatureSet.Empty,
            new ActionPipelineSnapshot("test", []));

    public sealed class LifecycleRegistration : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("in_process_lifecycle", "In-process Lifecycle", "inprocess");

        public bool Started { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken ct)
        {
            Started = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlModule : ISharpClawModule
    {
        public const string ToolName = "inprocess.tool";

        public static EndpointRouteDescriptor EndpointRoute { get; } = new(
            "inprocess.control.endpoint",
            "/inprocess/control",
            "GET",
            HostEndpointTransport.Http);

        public static EndpointRouteDescriptor WebSocketEndpointRoute { get; } = new(
            "inprocess.control.websocket",
            "/inprocess/control/ws",
            "GET",
            HostEndpointTransport.WebSocket);

        public static ToolDescriptor Tool { get; } =
            new(ToolName, "Captures one tool invocation.", ToolSchemas.EmptyObject);

        public static CliCommandDescriptor Cli { get; } = new(
            "inprocess.control.cli",
            ["inprocess-control"],
            "Captures one CLI invocation.",
            new JsonSchemaReference("inprocess.control.cli.input", 1, "inprocess-control-cli-input"),
            new JsonSchemaReference("inprocess.control.cli.result", 1, "inprocess-control-cli-result"));

        public static EventDescriptor<TestEvent> Event { get; } = new(
            new SharpClawEventKey("inprocess.control.event"),
            1,
            "inprocess",
            EventInterceptionCapabilities.Observe,
            DurableByDefault: false,
            ContainsSensitiveData: false)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            DeliveryClasses = [EventDelivery.Inline],
        };

        public static ActionDescriptor<TestAction, TestResult> Action { get; } =
            new(
                new SharpClawActionKey("inprocess.control"),
                1,
                "inprocess",
                ActionInterceptionCapabilities.ReplaceResult,
                ContainsSensitiveData: false,
                HasIrreversibleEffects: false,
                new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "inprocess.control"),
                ContinuationPolicy: null,
                TimeSpan.FromSeconds(5))
            {
                ProtocolVersionRange = ContractVersionRange.Exact(1),
                SafePoints = [ActionSafePoint.BeforeContinuation],
            };

        public ModuleIdentity Identity { get; } =
            new("in_process_control", "In-process Control", "inprocess");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ControlCapture>();
            services.AddSingleton<ToolInvocationCapture>();
            services.AddSingleton<EndpointInvocationCapture>();
            services.AddSingleton<WebSocketInvocationCapture>();
            services.AddSingleton<ScopedInvocationCapture>();
            services.AddAction(Action);
            services.AddEvent(Event);
            services.AddTool<CapturingTool>(Tool);
            services.OnAction(Action).Use<CapturingActionHook>(
                ActionInterceptionCapabilities.ReplaceResult,
                new HookOrdering("inprocess.control.capture"));
            services.OnEvent(Event).Listen<CapturingEventListener>(
                EventDelivery.Inline,
                new HookOrdering("inprocess.control.event.capture"));
            services.AddCliCommand<CapturingCliHandler>(Cli);
            services.AddHttpEndpoint<CapturingEndpoint>(EndpointRoute);
            services.AddWebSocketEndpoint<CapturingWebSocketEndpoint>(
                WebSocketEndpointRoute);
        }
    }

    private sealed class CapturingActionHook(
        ControlCapture capture,
        ScopedInvocationCapture lifetime)
        : ScopedContribution(lifetime, "action"), IActionInterceptor<TestAction, TestResult>
    {
        public ValueTask<IActionOutcome<TestResult>> InvokeAsync(
            ActionContext<TestAction> context,
            IActionControl<TestAction, TestResult> control,
            CancellationToken ct)
        {
            RecordInvocation();
            capture.Control = control;
            return ValueTask.FromResult(control.ReplaceResult(new TestResult("captured"), "test"));
        }
    }

    private sealed class ControlCapture
    {
        public IActionControl<TestAction, TestResult>? Control { get; set; }
    }

    private sealed class CapturingTool(
        ToolInvocationCapture capture,
        ScopedInvocationCapture lifetime)
        : ScopedContribution(lifetime, "tool"), IToolHandler
    {
        private readonly ToolInvocationCapture _capture = Register(capture);

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            RecordInvocation();
            _capture.Invocations++;
            _capture.LastInvocation = invocation;
            return ValueTask.FromResult(ToolResult.Text("captured"));
        }

        private static ToolInvocationCapture Register(ToolInvocationCapture capture)
        {
            capture.Constructions++;
            return capture;
        }
    }

    private sealed class CapturingEventListener(ScopedInvocationCapture lifetime)
        : ScopedContribution(lifetime, "event"), IEventListener<TestEvent>
    {
        public ValueTask OnEventAsync(
            EventEnvelope<TestEvent> envelope,
            CancellationToken cancellationToken)
        {
            RecordInvocation();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingCliHandler(ScopedInvocationCapture lifetime)
        : ScopedContribution(lifetime, "cli"), ICliHandler
    {
        public ValueTask<CliResult> ExecuteAsync(
            CliInvocation invocation,
            CancellationToken cancellationToken)
        {
            RecordInvocation();
            return ValueTask.FromResult(new CliResult(
                true,
                [new CliOutput("stdout", invocation.Command)]));
        }
    }

    private abstract class ScopedContribution : IDisposable
    {
        private readonly ScopedInvocationCapture _capture;
        private readonly string _category;
        private readonly Guid _instanceId = Guid.NewGuid();

        protected ScopedContribution(ScopedInvocationCapture capture, string category)
        {
            _capture = capture;
            _category = category;
            _capture.RecordConstruction(category, _instanceId);
        }

        protected void RecordInvocation() =>
            _capture.RecordInvocation(_category, _instanceId);

        public void Dispose() => _capture.RecordDisposal(_category, _instanceId);
    }

    private sealed class ScopedInvocationCapture
    {
        private readonly Dictionary<string, HashSet<Guid>> _constructed = [];
        private readonly Dictionary<string, HashSet<Guid>> _invoked = [];
        private readonly Dictionary<string, HashSet<Guid>> _disposed = [];

        public void RecordConstruction(string category, Guid instanceId) =>
            Record(_constructed, category, instanceId);

        public void RecordInvocation(string category, Guid instanceId) =>
            Record(_invoked, category, instanceId);

        public void RecordDisposal(string category, Guid instanceId) =>
            Record(_disposed, category, instanceId);

        public void AssertCategory(string category, int expected)
        {
            _constructed[category].Should().HaveCount(expected);
            _invoked[category].Should().BeEquivalentTo(_constructed[category]);
            _disposed[category].Should().BeEquivalentTo(_constructed[category]);
        }

        private static void Record(
            IDictionary<string, HashSet<Guid>> values,
            string category,
            Guid instanceId)
        {
            if (!values.TryGetValue(category, out var items))
            {
                items = [];
                values.Add(category, items);
            }

            items.Add(instanceId);
        }
    }

    private sealed class ScopedAuthorizationPolicyModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "scoped_authorization_policy",
            "Scoped Authorization Policy",
            "scoped_authorization_policy");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ScopedInvocationCapture>();
            services.AddAuthorizationPolicy<ScopedAuthorizationPolicy>();
        }
    }

    private sealed class ScopedAuthorizationRestrictionModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(
            "scoped_authorization_restriction",
            "Scoped Authorization Restriction",
            "scoped_authorization_restriction");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ScopedInvocationCapture>();
            services.AddAuthorizationRestriction<ScopedAuthorizationRestriction>("scope");
        }
    }

    private sealed class ScopedAuthorizationPolicy(ScopedInvocationCapture capture)
        : ScopedContribution(capture, "authorization-policy"), IAuthorizationPolicy
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            ActionContext<AuthorizationRequest> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordInvocation();
            return ValueTask.FromResult(AuthorizationDecision.Allow("scope_allowed"));
        }
    }

    private sealed class ScopedAuthorizationRestriction(ScopedInvocationCapture capture)
        : ScopedContribution(capture, "authorization-restriction"), IAuthorizationRestriction
    {
        public ValueTask<AuthorizationRestriction> EvaluateAsync(
            ActionContext<AuthorizationRequest> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordInvocation();
            return ValueTask.FromResult(default(AuthorizationRestriction));
        }
    }

    private sealed class AuthorizationActionControl
        : IActionControl<AuthorizationRequest, AuthorizationDecision>
    {
        public ValueTask<IActionOutcome<AuthorizationDecision>> ProceedAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<AuthorizationDecision>>(
                new AuthorizationActionOutcome(AuthorizationDecision.Allow("scope_allowed")));

        public ValueTask<IActionOutcome<AuthorizationDecision>> ProceedWithInputAsync(
            ActionReplacement<AuthorizationRequest> replacement,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IActionOutcome<AuthorizationDecision> ReplaceResult(
            AuthorizationDecision result,
            string reason) => throw new NotSupportedException();

        public IActionOutcome<AuthorizationDecision> Cancel(string code, string message) =>
            throw new NotSupportedException();

        public IActionOutcome<AuthorizationDecision> Fail(ExecutionError error) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<AuthorizationDecision>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<AuthorizationDecision>> RepeatAsync(
            ActionRepeatRequest<AuthorizationRequest> request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record AuthorizationActionOutcome(AuthorizationDecision Value)
        : IActionOutcome<AuthorizationDecision>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;
        public AuthorizationDecision? Result => Value;
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error => null;
        public ActionUncertainty? Uncertainty => null;
    }

    private sealed class ToolInvocationCapture
    {
        public int Constructions { get; set; }

        public int Invocations { get; set; }

        public ToolInvocation? LastInvocation { get; set; }
    }

    private sealed class CapturingEndpoint : IHttpEndpointHandler, IDisposable
    {
        private readonly EndpointInvocationCapture _capture;

        public CapturingEndpoint(EndpointInvocationCapture capture)
        {
            _capture = capture;
            _capture.Constructions++;
        }

        public ValueTask<HttpEndpointResponse> InvokeAsync(
            HostEndpointRouteRequest request,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken)
        {
            _capture.Invocations++;
            _capture.RouteValue = request.RouteValues["id"].Single();
            return ValueTask.FromResult(HttpEndpointResponse.Json(
                200,
                JsonSerializer.SerializeToElement(new
                {
                    path = request.Route.Path,
                    routeValue = request.RouteValues["id"].Single(),
                })));
        }

        public void Dispose() => _capture.Disposals++;
    }

    private sealed class EndpointInvocationCapture
    {
        public int Constructions { get; set; }

        public int Invocations { get; set; }

        public string? RouteValue { get; set; }

        public int Disposals { get; set; }
    }

    private sealed class CapturingWebSocketEndpoint :
        IWebSocketEndpointHandler,
        IDisposable
    {
        private readonly WebSocketInvocationCapture _capture;

        public CapturingWebSocketEndpoint(WebSocketInvocationCapture capture)
        {
            _capture = capture;
            _capture.Constructions++;
        }

        public async ValueTask InvokeAsync(
            HostEndpointRouteRequest request,
            IWebSocketChannel channel,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken)
        {
            _capture.Invocations++;
            _capture.RouteValue = request.RouteValues["id"].Single();
            while (true)
            {
                var message = await channel.ReceiveAsync(cancellationToken);
                if (message is null)
                    return;
                if (message.Type == WebSocketMessageType.Close)
                {
                    await channel.CloseAsync(
                        message.CloseStatus!.Value,
                        message.CloseDescription,
                        cancellationToken);
                    return;
                }

                await channel.SendAsync(message, cancellationToken);
            }
        }

        public void Dispose() => _capture.Disposals++;
    }

    private sealed class WebSocketInvocationCapture
    {
        public int Constructions { get; set; }

        public int Invocations { get; set; }

        public int Disposals { get; set; }

        public string? RouteValue { get; set; }
    }

    private sealed class RecordingWebSocketChannel(
        IEnumerable<WebSocketMessage> incoming) : IWebSocketChannel
    {
        private readonly Queue<WebSocketMessage> _incoming = new(incoming);

        public List<WebSocketMessage> Sent { get; } = [];

        public int? CloseStatus { get; private set; }

        public string? CloseDescription { get; private set; }

        public ValueTask<WebSocketMessage?> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<WebSocketMessage?>(
                _incoming.Count == 0 ? null : _incoming.Dequeue());
        }

        public ValueTask SendAsync(
            WebSocketMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(
            int closeStatus,
            string? description,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseStatus = closeStatus;
            CloseDescription = description;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHostActionEntry : IHostActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record ToolFixture(
        ModuleContributionGraph Graph,
        ServiceProvider Services,
        InProcessModuleInvoker Invoker);

    private sealed class StubActionControl : IActionControl<TestAction, TestResult>
    {
        public ValueTask<IActionOutcome<TestResult>> ProceedAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> ProceedWithInputAsync(
            ActionReplacement<TestAction> replacement,
            CancellationToken ct) => throw new NotSupportedException();

        public IActionOutcome<TestResult> ReplaceResult(TestResult result, string reason) =>
            new StubActionOutcome(result);

        public IActionOutcome<TestResult> Cancel(string code, string message) =>
            throw new NotSupportedException();

        public IActionOutcome<TestResult> Fail(ExecutionError error) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> DeferAsync(
            ActionDeferRequest request,
            CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IActionOutcome<TestResult>> RepeatAsync(
            ActionRepeatRequest<TestAction> request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record StubActionOutcome(TestResult Result) : IActionOutcome<TestResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;

        TestResult? IActionOutcome<TestResult>.Result => Result;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => null;

        public ActionUncertainty? Uncertainty => null;
    }

    private sealed record TestAction(string Value);

    private sealed record TestResult(string Value);

    private sealed record TestEvent(int Value);
}
