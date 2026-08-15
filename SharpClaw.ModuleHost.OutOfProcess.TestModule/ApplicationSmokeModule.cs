using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.TestModule;

public sealed record ApplicationSmokeAction(string Mode, string Value);

public sealed record ApplicationSmokeResult(string Value);

public sealed class ApplicationSmokeModule : ISharpClawModule, ISharpClawApplicationModule
{
    public const string Id = "application_smoke_module";
    public const string HostActionHookId = "application.host.authorization";
    public const string OwnedActionHookId = "application.owned.authorization";
    public const string CliName = "application.inspect";
    public const string CapabilityCliName = "application.capabilities";
    public const string HostEntryCliName = "application.host-entry";
    public const string HostEntryToolName = "application.host-entry-tool";

    public static RequestPrincipal HostEntryCaller { get; } =
        new(
            "module-agent",
            "Module Agent",
            new HashSet<string>(["module-agent", "module-operator"], StringComparer.Ordinal));

    public static ExtensionFeatureSet HostEntryFeatures { get; } =
        ExtensionFeatureSet.Empty;

    public static Guid HostEntryTraceId { get; } =
        new("11111111-1111-4111-8111-111111111111");

    public static Guid HostEntryIdempotencyKey { get; } =
        new("22222222-2222-4222-8222-222222222222");

    public const ActionInterceptionCapabilities HostCapabilities =
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.Wrap
        | ActionInterceptionCapabilities.Cancel;

    public const ActionInterceptionCapabilities UnrequestedCapabilities =
        ActionInterceptionCapabilities.ReplaceResult
        | ActionInterceptionCapabilities.Defer
        | ActionInterceptionCapabilities.Repeat;

    public static ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> HostAction { get; } =
        new(
            new SharpClawActionKey("host.application.smoke"),
            1,
            "application",
            HostCapabilities,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "host.application.smoke"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeContinuation, ActionSafePoint.BeforeTerminal],
            InputSchema = new JsonSchemaReference("application.smoke.action", 1, "application-smoke-action"),
            ResultSchema = new JsonSchemaReference("application.smoke.result", 1, "application-smoke-result"),
        };

    public static ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> OwnedAction { get; } =
        HostAction with
        {
            Key = new SharpClawActionKey("module.application.smoke"),
        };

    public ModuleIdentity Identity { get; } = new(Id, "Application Smoke", "appsmoke");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Actions.Add(OwnedAction);
        module.Storage.Add(new ModuleStorageContractDescriptor(
            Id,
            "application-store",
            [new ModuleStorageOperationDescriptor("echo")],
            "Application capability smoke storage."));
        module.Hooks.For(HostAction).Use<AuthorizationHook>(
            HostCapabilities,
            new HookOrdering(HostActionHookId));
        module.Hooks.For(OwnedAction).Use<AuthorizationHook>(
            HostCapabilities,
            new HookOrdering(OwnedActionHookId));
        module.Tools.Add<HostEntryToolHandler>(new ToolDescriptor(
            HostEntryToolName,
            "Invokes the host-owned application action through the tool ingress.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    value = new { type = "string" },
                },
                required = new[] { "value" },
            })));
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        application.Endpoints.Add<ApplicationEndpoint>();
        application.Cli.Add<ApplicationCliHandler>(new ModuleCliCommandDescriptor(
            CliName,
            ["app-inspect"],
            "Returns the module and graph identity.",
            new JsonSchemaReference("application.inspect.input", 1, "application-input"),
            new JsonSchemaReference("application.inspect.result", 1, "application-result")));
        application.Cli.Add<CapabilityCliHandler>(new ModuleCliCommandDescriptor(
            CapabilityCliName,
            ["app-capabilities"],
            "Exercises host-owned action and storage capabilities.",
            new JsonSchemaReference("application.capabilities.input", 1, "application-input"),
            new JsonSchemaReference("application.capabilities.result", 1, "application-result")));
        application.Cli.Add<HostEntryCliHandler>(new ModuleCliCommandDescriptor(
            HostEntryCliName,
            ["app-host-entry"],
            "Exercises the host-owned action entry.",
            new JsonSchemaReference("application.host-entry.input", 1, "application-input"),
            new JsonSchemaReference("application.host-entry.result", 1, "application-result")));
    }

    public sealed class AuthorizationHook : IActionInterceptor<ApplicationSmokeAction, ApplicationSmokeResult>
    {
        public async ValueTask<IActionOutcome<ApplicationSmokeResult>> InvokeAsync(
            ActionContext<ApplicationSmokeAction> context,
            IActionControl<ApplicationSmokeAction, ApplicationSmokeResult> control,
            CancellationToken ct) =>
            string.Equals(context.Action.Mode, "deny", StringComparison.Ordinal)
                ? control.Cancel("application_denied", "The application smoke request was denied.")
                : await control.ProceedAsync(ct);
    }

    public sealed class ApplicationCliHandler(
        ISharpClawModule module,
        ModuleContributionGraph graph) : IModuleCliHandler
    {
        public ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct) =>
            ValueTask.FromResult(new ModuleCliResult(
                true,
                [new ModuleCliOutput(
                    "stdout",
                    $"{module.Identity.Id}|{graph.Identity.Id}|{graph.ContractHash}|{invocation.Command}")]));
    }

    public sealed class ApplicationEndpoint;

    public sealed class HostEntryCliHandler(IHostActionEntry hostActionEntry) : IModuleCliHandler
    {
        public async ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct)
        {
            try
            {
                var hostActionContext = invocation.HostActionContext;
                switch (invocation.Arguments.FirstOrDefault())
                {
                    case "caller":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                            "spoofed-agent",
                                hostActionContext.Caller.DisplayName,
                                hostActionContext.Caller.Roles,
                                hostActionContext.Caller.IsAuthenticated),
                        };
                        break;
                    case "roles":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                                hostActionContext.Caller.SubjectId,
                                hostActionContext.Caller.DisplayName,
                                new HashSet<string>(["spoofed-role"], StringComparer.Ordinal),
                                hostActionContext.Caller.IsAuthenticated),
                        };
                        break;
                    case "authentication":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                                hostActionContext.Caller.SubjectId,
                                hostActionContext.Caller.DisplayName,
                                hostActionContext.Caller.Roles,
                                IsAuthenticated: false),
                        };
                        break;
                    case "features":
                        hostActionContext = hostActionContext with
                        {
                            Features = new ExtensionFeatureSet(
                            [
                                new ExtensionFeature(
                                    "application.test-feature",
                                    1,
                                    Id,
                                    128,
                                    JsonSerializer.SerializeToElement(true)),
                            ]),
                        };
                        break;
                    case "trace":
                        hostActionContext = hostActionContext with { TraceId = Guid.NewGuid() };
                        break;
                    case "idempotency":
                        hostActionContext = hostActionContext with { IdempotencyKey = Guid.NewGuid() };
                        break;
                    case "expiry":
                        hostActionContext = hostActionContext with
                        {
                            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
                        };
                        break;
                }
                var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                    new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                        HostAction,
                        new ApplicationSmokeAction("host-entry", "action"),
                        hostActionContext),
                    ct);
                return new ModuleCliResult(
                    outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred,
                    [new ModuleCliOutput(
                        "stdout",
                        $"host-entry:{outcome.Kind}:{outcome.Result?.Value}")],
                    outcome.Error);
            }
            catch (Exception ex)
            {
                return new ModuleCliResult(
                    false,
                    [new ModuleCliOutput("stderr", $"{ex.GetType().FullName}: {ex.Message}")],
                    new ExecutionError("host_entry_failed", ex.Message));
            }
        }
    }

    public sealed class HostEntryToolHandler(IHostActionEntry hostActionEntry) : IToolHandler
    {
        public async ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            var value = invocation.Arguments.GetProperty("value").GetString() ?? string.Empty;
            var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                    HostAction,
                    new ApplicationSmokeAction("host-tool", value),
                    invocation.HostActionContext),
                ct);
            return ToolResult.Text($"host-tool:{outcome.Kind}:{outcome.Result?.Value}");
        }
    }

    public sealed class CapabilityCliHandler(
        ISharpClawModule module,
        ModuleContributionGraph graph,
        IModuleStorageGateway storage,
        IActionDispatcher dispatcher) : IModuleCliHandler
    {
        public async ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct)
        {
            try
            {
                var contracts = storage.ListContracts();
                var storageResult = await storage.InvokeAsync(
                    module.Identity.Id,
                    "application-store",
                    "echo",
                    JsonSerializer.SerializeToElement(new { value = "storage" }),
                    ct);
                var actionResult = await dispatcher.RunRequiredAsync(
                    HostAction,
                    new ApplicationSmokeAction("capability", "action"),
                    static (action, _) => ValueTask.FromResult(
                        new ApplicationSmokeResult($"terminal:{action.Value}")),
                    new ActionPipelineSnapshot(
                        graph.ContractHash,
                        [new ActionCapabilityGrant(
                            HostAction.Key,
                            HostAction.Version,
                            HostCapabilities | UnrequestedCapabilities,
                            SensitiveApproved: false,
                            AcceptUnknownSchemas: false)],
                        []),
                    ct);
                return new ModuleCliResult(
                    true,
                    [new ModuleCliOutput(
                        "stdout",
                        $"{module.Identity.Id}|{graph.Identity.Id}|{graph.ContractHash}|"
                        + $"contracts:{contracts.Count}|storage:{storageResult.GetRawText()}|"
                        + $"action:{actionResult.Value}")]);
            }
            catch (Exception ex)
            {
                return new ModuleCliResult(
                    false,
                    [new ModuleCliOutput("stderr", $"{ex.GetType().FullName}: {ex.Message}")],
                    new ExecutionError("capability_smoke_failed", ex.Message));
            }
        }
    }
}
