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

    public const ActionInterceptionCapabilities HostCapabilities =
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.Wrap
        | ActionInterceptionCapabilities.Cancel;

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
                new ActionPipelineSnapshot(graph.ContractHash, []),
                ct);
            return new ModuleCliResult(
                true,
                [new ModuleCliOutput(
                    "stdout",
                    $"{module.Identity.Id}|{graph.Identity.Id}|{graph.ContractHash}|"
                    + $"contracts:{contracts.Count}|storage:{storageResult.GetRawText()}|"
                    + $"action:{actionResult.Value}")]);
        }
    }
}
