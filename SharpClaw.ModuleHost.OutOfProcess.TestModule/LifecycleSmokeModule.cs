using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.TestModule;

public sealed record SmokeAction(string Mode, string Value);

public sealed record SmokeResult(string Value);

public sealed class LifecycleSmokeModule : ISharpClawModule
{
    public const string Id = "lifecycle_smoke_module";
    public const string ExactHookId = "smoke.action.exact";
    public const string CategoryHookId = "smoke.action.category";
    public const string WildcardHookId = "smoke.action.wildcard";

    public const ActionInterceptionCapabilities HostCapabilities =
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.ReplaceInput
        | ActionInterceptionCapabilities.Cancel
        | ActionInterceptionCapabilities.ReplaceResult
        | ActionInterceptionCapabilities.Defer
        | ActionInterceptionCapabilities.Repeat
        | ActionInterceptionCapabilities.Wrap;

    public static ActionDescriptor<SmokeAction, SmokeResult> HostAction { get; } = new(
        new SharpClawActionKey("host.smoke"),
        1,
        "smoke",
        HostCapabilities,
        ContainsSensitiveData: false,
        HasIrreversibleEffects: false,
        new ActionRepeatPolicy(
            ActionRepeatKind.Idempotent,
            3,
            TimeSpan.Zero,
            "host.smoke"),
        new ActionContinuationPolicy(
            TimeSpan.FromMinutes(5),
            Durable: true,
            SingleClaim: true),
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
            HostCapabilities,
            new HookOrdering(ExactHookId, Before: [CategoryHookId]));
        module.Hooks.Category(
                "smoke",
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedAction("input", "smoke.*"),
                ModuleSchemaIdentity.UntypedAction("result", "smoke.*"),
                acceptUnknownNonSensitiveSchemas: true)
            .UseAny<SmokeUntypedHook>(
                HostCapabilities,
                new HookOrdering(CategoryHookId, Before: [WildcardHookId]));
        module.Hooks.AnyAction(
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedAction("input", "*"),
                ModuleSchemaIdentity.UntypedAction("result", "*"),
                acceptUnknownNonSensitiveSchemas: true)
            .UseAny<SmokeUntypedHook>(
                HostCapabilities,
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
                "input" => await control.ProceedWithInputAsync(
                    new ActionReplacement<SmokeAction>(
                        new SmokeAction("proceed", "replacement"),
                        "smoke input replacement"),
                    ct),
                "cancel" => control.Cancel(
                    "smoke_cancelled",
                    "The smoke action was cancelled."),
                "defer" => await control.DeferAsync(
                    new ActionDeferRequest(
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        "smoke deferment"),
                    ct),
                "repeat" => await control.RepeatAsync(
                    new ActionRepeatRequest<SmokeAction>(
                        new SmokeAction("proceed", "repeat"),
                        "smoke repetition"),
                    ct),
                "wrap" => await WrapAsync(control, ct),
                "double" => await UseTwiceAsync(control, ct),
                _ => await control.ProceedAsync(ct),
            };

        private static async ValueTask<IActionOutcome<SmokeResult>> WrapAsync(
            IActionControl<SmokeAction, SmokeResult> control,
            CancellationToken ct)
        {
            var outcome = await control.ProceedAsync(ct);
            return outcome.Kind == ActionOutcomeKind.Completed && outcome.Result is not null
                ? control.ReplaceResult(
                    outcome.Result with { Value = "wrapped:" + outcome.Result.Value },
                    "smoke wrapping")
                : outcome;
        }

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
        public async ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct)
        {
            var mode = context.Input.GetProperty("mode").GetString();
            return mode switch
            {
                "replace" => control.ReplaceResult(
                    JsonSerializer.SerializeToElement(new { value = "sidecar:untyped" }),
                    "untyped replacement"),
                "cancel" => control.Cancel(
                    "smoke_cancelled",
                    "The smoke action was cancelled."),
                "input" => await control.ProceedWithInputAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        mode = "proceed",
                        value = "replacement",
                    }),
                    "untyped input replacement",
                    ct),
                "defer" => await control.DeferAsync(
                    new ActionDeferRequest(
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        "untyped deferment"),
                    ct),
                "repeat" => await control.RepeatAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        mode = "proceed",
                        value = "repeat",
                    }),
                    "untyped repetition",
                    backoff: null,
                    ct),
                _ => await control.ProceedAsync(ct),
            };
        }
    }
}
