using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class ModuleCompilerTests
{
    [Test]
    public void CompileBuildsExactCategoryAndWildcardMapsInStableOrder()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.InProcess);
        var selectedActions = graph.ActionDispatch.Select(UntypedAction(CompleteModule.EchoAction));
        var selectedEvents = graph.EventDispatch.SelectInterceptors(UntypedEvent(CompleteModule.ChangedEvent));

        selectedActions.Select(hook => hook.HookId).Should().Equal(
            "sample.action.exact",
            "sample.action.category",
            "sample.action.wildcard");
        selectedEvents.Select(hook => hook.HookId).Should().Equal(
            "sample.event.exact",
            "sample.event.category");
        graph.EventDispatch
            .SelectListeners(UntypedEvent(CompleteModule.ChangedEvent), EventDelivery.Queued)
            .Select(hook => hook.HookId)
            .Should()
            .Equal("sample.event.wildcard");
    }

    [Test]
    public async Task ToolDispatchInvokesRegisteredHandlerWithoutNameSwitch()
    {
        var graph = Compile(new CompleteModule(), ModuleHostingMode.InProcess);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var invocation = new ToolInvocation(
            Guid.NewGuid(),
            null,
            "call-1",
            "sample.echo",
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty);

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
            [HostAction(CompleteModule.EchoAction)],
            [HostEvent(CompleteModule.ChangedEvent)],
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
    public void OutOfProcessCompilationRejectsApplicationTypeContributions()
    {
        var act = () => Compile(new ApplicationModule(), ModuleHostingMode.OutOfProcess);

        act.Should().Throw<ModuleGraphCompilationException>()
            .Which.Errors.Should().Contain(error =>
                error.Code == "unsupported_transport" && error.Target == "application");
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
                HostActions = [HostAction(CompleteModule.EchoAction)],
                HostEvents = [HostEvent(CompleteModule.ChangedEvent)],
            });

    private static ModuleManifest Manifest(ModuleIdentity identity, bool includeCompleteRequests) =>
        new(
            identity.Id,
            identity.DisplayName,
            "0.5.0-beta.2",
            identity.ToolPrefix,
            "Sample.dll",
            "0.5.0-beta.2",
            Runtime: ModuleManifestRuntimeInfo.DotNet,
            HostMode: ModuleManifestRuntimeInfo.HostModeSidecar,
            RequestedHooks: includeCompleteRequests
                ?
                [
                    new ModuleManifestHookRequest(
                        CompleteModule.EchoAction.Key.Value,
                        ["inspect", "wrap"]),
                    new ModuleManifestHookRequest("sample.*", ["inspect", "wrap"]),
                    new ModuleManifestHookRequest("*", ["inspect", "wrap"]),
                ]
                : RequestedHooksFor(identity.Id),
            RequestedEvents: includeCompleteRequests
                ?
                [
                    new ModuleManifestEventRequest(
                        CompleteModule.ChangedEvent.Key.Value,
                        "Inline",
                        ["inspect", "replace"]),
                    new ModuleManifestEventRequest(
                        "sample.*",
                        "Inline",
                        ["inspect", "replace"]),
                    new ModuleManifestEventRequest("*", "Queued", ["observe"]),
                ]
                : []);

    private static ModuleManifestHookRequest[] RequestedHooksFor(string id) =>
        id switch
        {
            "unsupported_effect" =>
            [
                new ModuleManifestHookRequest(
                    CompleteModule.EchoAction.Key.Value,
                    ["cancel"]),
            ],
            "typed_category" =>
            [
                new ModuleManifestHookRequest("sample.*", ["inspect", "wrap"]),
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

    private sealed class CompleteModule : ISharpClawModule
    {
        public static ActionDescriptor<EchoAction, EchoResult> EchoAction { get; } =
            new(
                new SharpClawActionKey("sample.echo"),
                1,
                "sample",
                ActionInterceptionCapabilities.Inspect
                | ActionInterceptionCapabilities.Wrap
                | ActionInterceptionCapabilities.ReplaceInput
                | ActionInterceptionCapabilities.ReplaceResult
                | ActionInterceptionCapabilities.Cancel,
                ContainsSensitiveData: false,
                HasIrreversibleEffects: false,
                new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "sample.echo"),
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

        public static EventDescriptor<ChangedEvent> ChangedEvent { get; } =
            new(
                new SharpClawEventKey("sample.changed"),
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

        public ModuleIdentity Identity { get; } = new("sample_module", "Sample Module", "sample");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Actions.Add(EchoAction);
            module.Events.Add(ChangedEvent);
            module.Hooks.For(EchoAction).Use<EchoHook>(
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                new HookOrdering("sample.action.exact", Before: ["sample.action.category"]));
            module.Hooks.Category(
                    "sample",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "sample.*"),
                    ModuleSchemaIdentity.UntypedAction("result", "sample.*"),
                    acceptUnknownNonSensitiveSchemas: true)
                .UseAny<AnyActionHook>(
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("sample.action.category"));
            module.Hooks.AnyAction(
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedAction("input", "*"),
                    ModuleSchemaIdentity.UntypedAction("result", "*"))
                .UseAny<AnyActionHook>(
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                    new HookOrdering("sample.action.wildcard", HookPriority.Low));

            module.Events.For(ChangedEvent).Intercept<ChangedEventHook>(
                EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
                new HookOrdering("sample.event.exact", Before: ["sample.event.category"]));
            module.Events.Category(
                    "sample",
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedEvent("sample.*"),
                    acceptUnknownNonSensitiveSchemas: true)
                .InterceptAny<AnyEventHook>(
                    EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
                    new HookOrdering("sample.event.category"));
            module.Events.AnyEvent(
                    ContractVersionRange.Exact(1),
                    ModuleSchemaIdentity.UntypedEvent("*"))
                .ListenAny<AnyEventListener>(
                    EventDelivery.Queued,
                    new HookOrdering("sample.event.wildcard", HookPriority.Low));

            module.Tools.Add<EchoTool>(new ToolDescriptor(
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

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Actions.Add(CompleteModule.EchoAction with
            {
                Capabilities = ActionInterceptionCapabilities.Inspect,
            });
            module.Hooks.For(CompleteModule.EchoAction with
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

        public void Configure(ISharpClawModuleBuilder module)
        {
            var descriptor = new ToolDescriptor(
                "duplicate.echo",
                "Echoes input.",
                JsonSerializer.SerializeToElement(new { type = "object" }));
            module.Tools.Add<EchoTool>(descriptor);
            module.Tools.Add<EchoTool>(descriptor);
        }
    }

    private sealed class TypedCategoryModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("typed_category", "Typed Category", "typed");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Hooks.Category("sample").Use<EchoHook>(new HookOrdering("typed.category"));
    }

    private sealed class ApplicationModule : ISharpClawModule, ISharpClawApplicationModule
    {
        public ModuleIdentity Identity { get; } = new("application_module", "Application Module", "application");

        public void Configure(ISharpClawModuleBuilder module)
        {
        }

        public void ConfigureApplication(ISharpClawApplicationBuilder application) =>
            application.Endpoints.Add<SampleEndpoints>();
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

    private sealed class SampleEndpoints;
}
