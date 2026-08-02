using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

public sealed record SmokeAction(string Mode, string Value);

public sealed record SmokeResult(string Value);

public sealed class LifecycleSmokeModule : ISharpClawModule
{
    public const string Id = "lifecycle_smoke_module";
    public const string ExactHookId = "smoke.action.exact";
    public const string CategoryHookId = "smoke.action.category";
    public const string WildcardHookId = "smoke.action.wildcard";

    public static ActionDescriptor<SmokeAction, SmokeResult> HostAction { get; } = new(
        new SharpClawActionKey("host.smoke"),
        1,
        "smoke",
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.Wrap
        | ActionInterceptionCapabilities.ReplaceResult,
        ContainsSensitiveData: false,
        HasIrreversibleEffects: false,
        new ActionRepeatPolicy(
            ActionRepeatKind.None,
            1,
            TimeSpan.Zero,
            "host.smoke"),
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

    public ModuleIdentity Identity { get; } = new(Id, "Lifecycle Smoke", "smoke");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Hooks.For(HostAction).Use<SmokeTypedHook>(
            ActionInterceptionCapabilities.Inspect
            | ActionInterceptionCapabilities.Wrap
            | ActionInterceptionCapabilities.ReplaceResult,
            new HookOrdering(ExactHookId, Before: [CategoryHookId]));
        module.Hooks.Category(
                "smoke",
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedAction("input", "smoke.*"),
                ModuleSchemaIdentity.UntypedAction("result", "smoke.*"),
                acceptUnknownNonSensitiveSchemas: true)
            .UseAny<SmokeUntypedHook>(
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                new HookOrdering(CategoryHookId, Before: [WildcardHookId]));
        module.Hooks.AnyAction(
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedAction("input", "*"),
                ModuleSchemaIdentity.UntypedAction("result", "*"),
                acceptUnknownNonSensitiveSchemas: true)
            .UseAny<SmokeUntypedHook>(
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
                new HookOrdering(WildcardHookId));
    }

    public sealed class SmokeTypedHook : IActionInterceptor<SmokeAction, SmokeResult>
    {
        public async ValueTask<IActionOutcome<SmokeResult>> InvokeAsync(
            ActionContext<SmokeAction> context,
            IActionControl<SmokeAction, SmokeResult> control,
            CancellationToken ct) =>
            context.Action.Mode switch
            {
                "replace" => control.ReplaceResult(
                    new SmokeResult("sidecar:" + context.Action.Value),
                    "smoke replacement"),
                "fail" => control.Fail(new ExecutionError(
                    "smoke_failed",
                    "The smoke hook failed.")),
                "double" => await UseTwiceAsync(control, ct),
                _ => await control.ProceedAsync(ct),
            };

        private static async ValueTask<IActionOutcome<SmokeResult>> UseTwiceAsync(
            IActionControl<SmokeAction, SmokeResult> control,
            CancellationToken ct)
        {
            await control.ProceedAsync(ct);
            return await control.ProceedAsync(ct);
        }
    }

    public sealed class SmokeUntypedHook : IAnyActionInterceptor
    {
        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct) => control.ProceedAsync(ct);
    }
}
