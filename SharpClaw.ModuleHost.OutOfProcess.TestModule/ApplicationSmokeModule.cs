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

    public static ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> HostAction { get; } =
        new(
            new SharpClawActionKey("host.application.smoke"),
            1,
            "application",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
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
        module.Hooks.For(HostAction).Use<AuthorizationHook>(
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
            new HookOrdering(HostActionHookId));
        module.Hooks.For(OwnedAction).Use<AuthorizationHook>(
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
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
        ApplicationSmokeModule module,
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
}
