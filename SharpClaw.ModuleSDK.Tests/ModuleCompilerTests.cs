using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class ModuleCompilerTests
{
    [Test]
    public void CompileBuildsExactCategoryAndWildcardMapsInStableOrder()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.InProcess);
        var selectedActions = graph.ActionDispatch.Select(UntypedAction(CompleteModule.HostAction));
        var selectedEvents = graph.EventDispatch.SelectInterceptors(UntypedEvent(CompleteModule.HostEvent));

        selectedActions.Select(hook => hook.HookId).Should().Equal(
            "sample.action.exact",
            "sample.action.category",
            "sample.action.wildcard");
        selectedEvents.Select(hook => hook.HookId).Should().Equal(
            "sample.event.exact",
            "sample.event.category");
        graph.EventDispatch
            .SelectListeners(UntypedEvent(CompleteModule.HostEvent), EventDelivery.Queued)
            .Select(hook => hook.HookId)
            .Should()
            .Equal("sample.event.wildcard");
    }

    [Test]
    public async Task ToolDispatchInvokesRegisteredHandlerWithoutNameSwitch()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.InProcess);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var invocationId = Guid.NewGuid();
        var invocation = new ToolInvocation(
            invocationId,
            null,
            "call-1",
            "sample.echo",
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            CreateToolContext(invocationId));

        var result = await graph.ToolDispatch.InvokeAsync(
            "sample.echo",
            services,
            invocation,
            CancellationToken.None);

        result.Content.Should().Be("hello");
    }

    [Test]
    public void OutOfProcessCompilationCreatesValidTypedAndUntypedDiscovery()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.OutOfProcess);
        var discovery = graph.CreateSidecarDiscovery(
            protocolVersion: 1,
            sequence: 1,
            deadline: DateTimeOffset.UtcNow.AddMinutes(1));
        var catalog = new SidecarHostDescriptorCatalog(
            [HostAction(CompleteModule.HostAction)],
            [HostEvent(CompleteModule.HostEvent)],
            negotiatedProtocolVersion: 1,
            graph.PayloadLimits);

        var result = SidecarDiscoveryValidator.Validate(discovery, catalog);

        result.Accepted.Should().BeTrue(result.ErrorMessage);
        discovery.Actions.Should().ContainSingle(hook => hook.PayloadMode == SidecarPayloadMode.Typed);
        discovery.Actions.Count(hook => hook.PayloadMode == SidecarPayloadMode.Untyped).Should().Be(2);
        discovery.Events.Should().ContainSingle(hook => hook.PayloadMode == SidecarPayloadMode.Typed);
        discovery.Events.Count(hook => hook.PayloadMode == SidecarPayloadMode.Untyped).Should().Be(2);
        discovery.Header.Size.PayloadBytes.Should().BeGreaterThan(0);
    }

    [Test]
    public void OutOfProcessCompilationPublishesExactToolMetadata()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.OutOfProcess);
        var tool = SidecarDiscoveryFactory.CreateDocument(
                graph,
                protocolVersion: 1,
                sequence: 1,
                DateTimeOffset.UtcNow.AddMinutes(1))
            .ToolHandlers.Single();

        tool.ToolName.Should().Be("sample.echo");
        tool.Version.Should().Be(1);
        tool.ContainsSensitiveData.Should().BeFalse();
        tool.ParametersSchema.GetProperty("type").GetString().Should().Be("object");
        tool.ParametersSchema.GetProperty("required")[0].GetString().Should().Be("text");
    }

    [Test]
    public void OutOfProcessCompilationRetainsTypedDescriptorBeforeHostAuthorization()
    {
        var module = new CompleteModule();
        var graph = SharpClawModuleCompiler.Compile(
            module,
            Manifest(module.Identity, includeCompleteRequests: true),
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
            });

        graph.ActionHooks.Single(hook =>
                hook.TargetKind == SidecarHookTargetKind.Exact)
            .InputSchema.Should().Be(HostAction(CompleteModule.HostAction).InputSchema);
        graph.EventHooks.Single(hook =>
                hook.TargetKind == SidecarHookTargetKind.Exact)
            .PayloadSchema.Should().Be(HostEvent(CompleteModule.HostEvent).PayloadSchema);
    }

    [Test]
    public void CompileRejectsAnEffectOutsideTheActionDescriptor()
    {
        var act = () => Compile(new UnsupportedEffectModule(), ModuleHostingMode.InProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error => error.Code == "unsupported_effect");
    }

    [Test]
    public void CompileRejectsDuplicateToolNames()
    {
        var act = () => Compile(new DuplicateToolModule(), ModuleHostingMode.InProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error => error.Code == "duplicate_tool");
    }

    [Test]
    public void CompileRejectsTypedCategoryHook()
    {
        var act = () => Compile(new TypedCategoryModule(), ModuleHostingMode.InProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error => error.Code == "invalid_handler");
    }

    [Test]
    public void OutOfProcessCompilationTransportsEndpointAndCliContributions()
    {
        var graph = Compile(new ApplicationModule(), ModuleHostingMode.OutOfProcess);

        graph.Application.Endpoints.Should().ContainSingle(value =>
            value.HandlerType == typeof(SampleEndpoints)
            && value.Descriptor.Id == "sample.endpoint"
            && value.Descriptor.Path == "/sample"
            && value.Descriptor.Method == "GET"
            && value.Descriptor.Transport == HostEndpointTransport.Http);
        graph.Application.CliCommands.Should().ContainSingle(item =>
            item.Descriptor.Name == "sample.inspect"
            && item.HandlerType == typeof(SampleCli));
        graph.CreateSidecarApplicationDiscovery().Should().Match<SidecarApplicationDiscovery>(
            discovery => discovery.SourceId == graph.Identity.Id
                && discovery.ContractHash == graph.ContractHash
                && discovery.Endpoints.Single().TypeName == typeof(SampleEndpoints).FullName
                && discovery.Endpoints.Single().Descriptor.Id == "sample.endpoint"
                && discovery.CliCommands.Single().Descriptor.Name == "sample.inspect");
        SidecarDiscoveryFactory.CreateDocument(
                graph,
                protocolVersion: 1,
                sequence: 1,
                DateTimeOffset.UtcNow.AddMinutes(1))
            .StorageContracts.Should().ContainSingle(contract =>
                contract.SourceId == graph.Identity.Id
                && contract.StorageName == "application-store");
    }

    [Test]
    public void OutOfProcessCompilationAcceptsSelfSubscription()
    {
        var graph = Compile(new SelfSubscriptionModule(), ModuleHostingMode.OutOfProcess);

        graph.Actions.Should().ContainSingle(action =>
            action.Descriptor.Key == CompleteModule.HostAction.Key);
        graph.ActionHooks.Should().ContainSingle(hook =>
            hook.ActionKey == CompleteModule.HostAction.Key);
    }

    [Test]
    public void OutOfProcessCompilationStillRejectsUiContributions()
    {
        var act = () => Compile(new UiApplicationModule(), ModuleHostingMode.OutOfProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Code == "unsupported_transport" && error.Target == "application");
    }

    [Test]
    public void OutOfProcessCompilationRejectsDuplicateSubscriptions()
    {
        var act = () => Compile(new DuplicateSubscriptionModule(), ModuleHostingMode.OutOfProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error => error.Code == "unsupported_transport");
    }

    [Test]
    public void OutOfProcessCompilationRejectsMissingManifestRequests()
    {
        var act = () => SharpClawModuleCompiler.Compile(
            new SelfSubscriptionModule(),
            Manifest(new ModuleIdentity("missing_request", "Missing Request", "missing"), false),
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.OutOfProcess,
                HostActions = [HostAction(CompleteModule.HostAction)],
            });

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error => error.Code == "missing_manifest_request");
    }

    [Test]
    public void CompiledCollectionsDoNotExposeMutableLists()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.InProcess);

        var act = () => ((IList<ModuleActionDefinition>)graph.Actions).Clear();

        act.Should().Throw<NotSupportedException>();
    }

    private static ModuleContributionGraph Compile(ISharpClawModule module, ModuleHostingMode mode) =>
        SharpClawModuleCompiler.Compile(
            module,
            Manifest(module.Identity, includeCompleteRequests: module is CompleteModule),
            new ModuleCompilationOptions
            {
                HostingMode = mode,
                HostActions = [HostAction(CompleteModule.HostAction)],
                HostEvents = [HostEvent(CompleteModule.HostEvent)],
            });

    private static PackageManifest Manifest(ModuleIdentity identity, bool includeCompleteRequests) =>
        new(
            identity.Id,
            identity.DisplayName,
            "0.5.0-beta.2",
            identity.ToolPrefix,
            "Sample.dll",
            "0.5.0-beta.2",
            Runtime: PackageRuntimeInfo.DotNet,
            HostMode: PackageRuntimeInfo.HostModeSidecar,
            RequestedHooks: includeCompleteRequests
                ?
                [
                    new PackageHookRequest(
                        CompleteModule.HostAction.Key.Value,
                        ["inspect", "wrap"]),
                    new PackageHookRequest("sample.*", ["inspect", "wrap"]),
                    new PackageHookRequest("*", ["inspect", "wrap"]),
                ]
                : RequestedHooksFor(identity.Id),
            RequestedEvents: includeCompleteRequests
                ?
                [
                    new PackageEventRequest(
                        CompleteModule.HostEvent.Key.Value,
                        "Inline",
                        ["inspect", "replace"]),
                    new PackageEventRequest(
                        "sample.*",
                        "Inline",
                        ["inspect", "replace"]),
                    new PackageEventRequest("*", "Queued", ["observe"]),
                ]
                : []);

    private static PackageHookRequest[] RequestedHooksFor(string id) =>
        id switch
        {
            "unsupported_effect" =>
            [
                new PackageHookRequest(
                    CompleteModule.HostAction.Key.Value,
                    ["cancel"]),
            ],
            "typed_category" =>
            [
                new PackageHookRequest("sample.*", ["inspect", "wrap"]),
            ],
            "self_subscription" =>
            [
                new PackageHookRequest(
                    CompleteModule.HostAction.Key.Value,
                    ["inspect", "wrap"]),
            ],
            _ => [],
        };

    private static UntypedActionDescriptor UntypedAction<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            descriptor.Capabilities,
            ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(TAction)),
            ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(TResult)),
            descriptor.ContainsSensitiveData)
        {
            ProtocolVersionRange = descriptor.ProtocolVersionRange,
        };

    private static UntypedEventDescriptor UntypedEvent<TEvent>(EventDescriptor<TEvent> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            descriptor.Capabilities,
            ModuleSchemaIdentity.EventPayload(descriptor.Key, descriptor.Version, typeof(TEvent)),
            descriptor.ContainsSensitiveData)
        {
            ProtocolVersionRange = descriptor.ProtocolVersionRange,
        };

    private static SidecarHostActionDescriptor HostAction<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(TAction)),
            ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(TResult)),
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.ProtocolVersionRange);

    private static SidecarHostEventDescriptor HostEvent<TEvent>(EventDescriptor<TEvent> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            ModuleSchemaIdentity.EventPayload(descriptor.Key, descriptor.Version, typeof(TEvent)),
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.ProtocolVersionRange);

    public sealed record EchoAction(string Text);
    public sealed record EchoResult(string Text);
    public sealed record ChangedEvent(string Text);

    private static HostActionEntryRequestContext CreateToolContext(Guid invocationId)
    {
        var descriptor = CompleteModule.HostAction;
        var inputSchema = ModuleSchemaIdentity.ActionInput(
            descriptor.Key,
            descriptor.Version,
            typeof(EchoAction));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            HostActionEntryIngress.Tool,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    "sample.echo"),
                new HostActionEntryLineage(
                    descriptor.Key,
                    descriptor.Version,
                    "descriptor-hash",
                    typeof(EchoAction).AssemblyQualifiedName!,
                    inputSchema.Version,
                    inputSchema.ContentHash!,
                    null,
                    null)),
        };
    }

    private sealed class CompleteModule : ISharpClawModule
    {
        public static ActionDescriptor<EchoAction, EchoResult> HostAction { get; } =
            new(
                new SharpClawActionKey("host.echo"),
                1,
                "sample",
                ActionInterceptionCapabilities.Inspect
                | ActionInterceptionCapabilities.Wrap
                | ActionInterceptionCapabilities.ReplaceInput
                | ActionInterceptionCapabilities.ReplaceResult
                | ActionInterceptionCapabilities.Cancel,
                ContainsSensitiveData: false,
                HasIrreversibleEffects: false,
                new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "host.echo"),
                ContinuationPolicy: null,
                TimeSpan.FromSeconds(5))
            {
                ProtocolVersionRange = ContractVersionRange.Exact(1),
                SafePoints =
                [
                    ActionSafePoint.BeforeContinuation,
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal,
                ],
            };

        public static ActionDescriptor<EchoAction, EchoResult> OwnedAction { get; } =
            HostAction with
            {
                Key = new SharpClawActionKey("sample.owned"),
                RepeatPolicy = new ActionRepeatPolicy(
                    ActionRepeatKind.None,
                    1,
                    TimeSpan.Zero,
                    "sample.owned"),
            };

        public static EventDescriptor<ChangedEvent> HostEvent { get; } =
            new(
                new SharpClawEventKey("host.changed"),
                1,
                "sample",
                EventInterceptionCapabilities.Inspect
                | EventInterceptionCapabilities.Replace
                | EventInterceptionCapabilities.Observe,
                DurableByDefault: false,
                ContainsSensitiveData: false)
            {
                ProtocolVersionRange = ContractVersionRange.Exact(1),
                DeliveryClasses = [EventDelivery.Inline, EventDelivery.Queued],
            };

        public static EventDescriptor<ChangedEvent> OwnedEvent { get; } =
            HostEvent with { Key = new SharpClawEventKey("sample.owned.changed") };

        public ModuleIdentity Identity { get; } = new("sample_registration", "Sample Module", "sample");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAction(OwnedAction);
            services.AddEvent(OwnedEvent);
            services.OnAction(HostAction).Use<EchoHook>(
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                new HookOrdering("sample.action.exact", Before: ["sample.action.category"]));
            services.OnActionCategory(
                    "sample",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "sample.*"),
                    ModuleSchemaIdentity.UntypedAction("result", "sample.*"),
                    acceptUnknownNonSensitiveSchemas: true)
                .UseAny<AnyActionHook>(
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("sample.action.category"));
            services.OnAnyAction(
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "*"),
                    ModuleSchemaIdentity.UntypedAction("result", "*"))
                .UseAny<AnyActionHook>(
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("sample.action.wildcard", HookPriority.Low));

            services.OnEvent(HostEvent).Intercept<ChangedEventHook>(
                EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
                new HookOrdering("sample.event.exact", Before: ["sample.event.category"]));
            services.OnEventCategory(
                    "sample",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedEvent("sample.*"),
                    acceptUnknownNonSensitiveSchemas: true)
                .InterceptAny<AnyEventHook>(
                    EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
                    new HookOrdering("sample.event.category"));
            services.OnAnyEvent(
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedEvent("*"))
                .ListenAny<AnyEventListener>(
                    EventDelivery.Queued,
                    new HookOrdering("sample.event.wildcard", HookPriority.Low));

            services.AddTool<EchoTool>(new ToolDescriptor(
                "sample.echo",
                "Returns the supplied text.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { text = new { type = "string" } },
                    required = new[] { "text" },
                })));
        }
    }

    private sealed class UnsupportedEffectModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("unsupported_effect", "Unsupported Effect", "unsupported");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAction(CompleteModule.HostAction with
            {
                Capabilities = ActionInterceptionCapabilities.Inspect,
            });
            services.OnAction(CompleteModule.HostAction with
                {
                    Capabilities = ActionInterceptionCapabilities.Inspect,
                })
                .Use<EchoHook>(
                    ActionInterceptionCapabilities.Cancel,
                    new HookOrdering("unsupported.effect"));
        }
    }

    private sealed class DuplicateToolModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("duplicate_tool", "Duplicate Tool", "duplicate");

        public void ConfigureServices(IServiceCollection services)
        {
            var descriptor = new ToolDescriptor(
                "duplicate.echo",
                "Echoes input.",
                JsonSerializer.SerializeToElement(new { type = "object" }));
            services.AddTool<EchoTool>(descriptor);
            services.AddTool<EchoTool>(descriptor);
        }
    }

    private sealed class SelfSubscriptionModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("self_subscription", "Self Subscription", "self");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddAction(CompleteModule.HostAction);
            services.OnAction(CompleteModule.HostAction).Use<EchoHook>(
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                new HookOrdering("self.subscription"));
        }
    }

    private sealed class TypedCategoryModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("typed_category", "Typed Category", "typed");

        public void ConfigureServices(IServiceCollection services) =>
            services.OnActionCategory(
                    "sample",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "sample.*"),
                    ModuleSchemaIdentity.UntypedAction("result", "sample.*"))
                .Use<EchoHook>(new HookOrdering("typed.category"));
    }

    private sealed class ApplicationModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("application_module", "Application Module", "application");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddStorage(new ScopedStorageContractDescriptor(
                Identity.Id,
                "application-store",
                [new ScopedStorageOperationDescriptor("get")],
                "Application test storage."));
            services.AddHttpEndpoint<SampleEndpoints>(new EndpointRouteDescriptor(
                "sample.endpoint",
                "/sample",
                "GET",
                HostEndpointTransport.Http));
            services.AddCliCommand<SampleCli>(new CliCommandDescriptor(
                "sample.inspect",
                ["sample-i"],
                "Inspects the sample module.",
                new JsonSchemaReference("sample.inspect.input", 1, "sample-input"),
                new JsonSchemaReference("sample.inspect.result", 1, "sample-result")));
        }
    }

    private sealed class UiApplicationModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("ui_application", "UI Application", "ui");

        public void ConfigureServices(IServiceCollection services) =>
            services.AddUi<SampleUi>();
    }

    private sealed class DuplicateSubscriptionModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("duplicate_subscription", "Duplicate Subscription", "duplicate");

        public void ConfigureServices(IServiceCollection services)
        {
            services.OnAction(CompleteModule.HostAction).Use<EchoHook>(
                ActionInterceptionCapabilities.Inspect,
                new HookOrdering("duplicate.one"));
            services.OnAction(CompleteModule.HostAction).Use<EchoHook>(
                ActionInterceptionCapabilities.Inspect,
                new HookOrdering("duplicate.two"));
        }
    }

    private sealed class EchoHook : IActionInterceptor<EchoAction, EchoResult>
    {
        public ValueTask<IActionOutcome<EchoResult>> InvokeAsync(
            ActionContext<EchoAction> context,
            IActionControl<EchoAction, EchoResult> control,
            CancellationToken ct) => control.ProceedAsync(ct);
    }

    private sealed class AnyActionHook : IAnyActionInterceptor
    {
        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct) => control.ProceedAsync(ct);
    }

    private sealed class ChangedEventHook : IEventInterceptor<ChangedEvent>
    {
        public ValueTask<IEventInterception<ChangedEvent>> InterceptAsync(
            EventContext<ChangedEvent> context,
            IEventControl<ChangedEvent> control,
            CancellationToken ct) => ValueTask.FromResult(control.Continue());
    }

    private sealed class AnyEventHook : IAnyEventInterceptor
    {
        public ValueTask<IUntypedEventInterception> InterceptAsync(
            UntypedEventContext context,
            IUntypedEventControl control,
            CancellationToken ct) => ValueTask.FromResult(control.Continue());
    }

    private sealed class AnyEventListener : IAnyEventListener
    {
        public ValueTask OnEventAsync(UntypedEventEnvelope evt, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }

    private sealed class EchoTool : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(ToolResult.Text(invocation.Arguments.GetProperty("text").GetString()!));
    }

    private sealed class SampleEndpoints : IHttpEndpointHandler
    {
        public ValueTask<HttpEndpointResponse> InvokeAsync(
            HostEndpointRouteRequest request,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(HttpEndpointResponse.Empty(204));
    }

    private sealed class SampleUi;

    private sealed class SampleCli : ICliHandler
    {
        public ValueTask<CliResult> ExecuteAsync(
            CliInvocation invocation,
            CancellationToken ct) =>
            ValueTask.FromResult(new CliResult(
                true,
                [new CliOutput("stdout", invocation.Command)]));
    }
}
