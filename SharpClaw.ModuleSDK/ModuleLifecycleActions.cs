using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Defines lifecycle actions owned by the module host.</summary>
public static class ModuleLifecycleActions
{
    public static readonly ModuleIdentity Identity =
        new("module-sdk.lifecycle", "Module lifecycle", "module_lifecycle");

    public static readonly ActionDescriptor<ServiceStartContext, bool> Start =
        Create<ServiceStartContext>(
            new SharpClawActionKey("module.lifecycle.start"),
            typeof(ServiceStartContext));

    public static readonly ActionDescriptor<ModuleIdentity, bool> Stop =
        Create<ModuleIdentity>(
            new SharpClawActionKey("module.lifecycle.stop"),
            typeof(ModuleIdentity));

    public static void AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAction(Start);
        services.AddAction(Stop);
    }

    private static ActionDescriptor<TAction, bool> Create<TAction>(
        SharpClawActionKey key,
        Type actionType) =>
        new(
            key,
            1,
            "module.lifecycle",
            ActionInterceptionCapabilities.Inspect |
            ActionInterceptionCapabilities.Cancel |
            ActionInterceptionCapabilities.Defer |
            ActionInterceptionCapabilities.Wrap |
            ActionInterceptionCapabilities.Observe,
            false,
            true,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, key.Value),
            new ActionContinuationPolicy(TimeSpan.FromMinutes(5), true, true),
            TimeSpan.FromMinutes(2))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(key, 1, actionType),
            ResultSchema = ModuleSchemaIdentity.ActionResult(key, 1, typeof(bool)),
            SafePoints =
            [
                ActionSafePoint.BeforeContinuation,
                ActionSafePoint.BeforeTerminal,
                ActionSafePoint.AfterTerminal,
                ActionSafePoint.BeforeCommit,
                ActionSafePoint.AfterCommit,
            ],
        };
}
