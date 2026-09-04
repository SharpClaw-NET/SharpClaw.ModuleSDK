using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Registers typed module-owned action terminals.</summary>
public static class ModuleActionEntryBuilderExtensions
{
    /// <summary>Registers one typed terminal for an action defined by this module.</summary>
    public static void AddActionEntry<TAction, TResult, TTerminal>(
        this IServiceCollection services,
        ActionDescriptor<TAction, TResult> descriptor,
        Guid terminalId)
        where TTerminal : class, IHostActionEntryTerminal<TAction, TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        SharpClawServiceCollection.Require(services)
            .AddActionEntry<TAction, TResult, TTerminal>(descriptor, terminalId);
    }
}
