using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Registers typed module-owned action terminals.</summary>
public static class ModuleActionEntryBuilderExtensions
{
    /// <summary>Registers one typed terminal for an action defined by this module.</summary>
    public static void AddActionEntry<TAction, TResult, TTerminal>(
        this ISharpClawModuleBuilder builder,
        ActionDescriptor<TAction, TResult> descriptor,
        Guid terminalId)
        where TTerminal : class, IHostActionEntryTerminal<TAction, TResult>
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (builder is not SharpClawModuleBuilder moduleBuilder)
        {
            throw new ArgumentException(
                "The action-entry extension requires the SharpClaw module builder.",
                nameof(builder));
        }

        moduleBuilder.AddActionEntry<TAction, TResult, TTerminal>(descriptor, terminalId);
    }
}
