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

    public static RequestPrincipal HostEntryCaller { get; } =
        new(
            "module-agent",
            "Module Agent",
            new HashSet<string>(["module-agent"], StringComparer.Ordinal),
            IsAuthenticated: true);

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
                var caller = HostEntryCaller;
                var features = HostEntryFeatures;
                var traceId = HostEntryTraceId;
                var idempotencyKey = HostEntryIdempotencyKey;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                switch (invocation.Arguments.FirstOrDefault())
                {
                    case "caller":
                        caller = new RequestPrincipal(
                            "spoofed-agent",
                            HostEntryCaller.DisplayName,
                            HostEntryCaller.Roles,
                            HostEntryCaller.IsAuthenticated);
                        break;
                    case "roles":
                        caller = new RequestPrincipal(
                            HostEntryCaller.SubjectId,
                            HostEntryCaller.DisplayName,
                            new HashSet<string>(["spoofed-role"], StringComparer.Ordinal),
                            HostEntryCaller.IsAuthenticated);
                        break;
                    case "authentication":
                        caller = new RequestPrincipal(
                            HostEntryCaller.SubjectId,
                            HostEntryCaller.DisplayName,
                            HostEntryCaller.Roles,
                            IsAuthenticated: false);
                        break;
                    case "features":
                        features = new ExtensionFeatureSet(
                        [
                            new ExtensionFeature(
                                "application.test-feature",
                                1,
                                Id,
                                128,
                                JsonSerializer.SerializeToElement(true)),
                        ]);
                        break;
                    case "trace":
                        traceId = Guid.NewGuid();
                        break;
                    case "idempotency":
                        idempotencyKey = Guid.NewGuid();
                        break;
                    case "expiry":
                        deadline = DateTimeOffset.UtcNow.AddMinutes(5);
                        break;
                }
                var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                    new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                        HostAction,
                        new ApplicationSmokeAction("host-entry", "action"),
                        caller,
                        features,
                        traceId,
                        idempotencyKey,
                        deadline),
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
