using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Provides typed module action registration.</summary>
public static class ModuleActionBuilderExtensions
{
    /// <summary>Defines one typed action and supplies deterministic schemas when the descriptor omits them.</summary>
    public static ActionRegistration<TAction, TResult> DefineAction<TAction, TResult>(
        this IServiceCollection services,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        var builder = SharpClawServiceCollection.Require(services);

        var registeredDescriptor = descriptor;
        if (descriptor.InputSchema is null || descriptor.ResultSchema is null)
        {
            registeredDescriptor = descriptor with
            {
                InputSchema = descriptor.InputSchema
                    ?? ModuleSchemaIdentity.ActionInput(
                        descriptor.Key,
                        descriptor.Version,
                        typeof(TAction)),
                ResultSchema = descriptor.ResultSchema
                    ?? ModuleSchemaIdentity.ActionResult(
                        descriptor.Key,
                        descriptor.Version,
                        typeof(TResult)),
            };
        }

        builder.Actions.Add(registeredDescriptor);
        return new ActionRegistration<TAction, TResult>(builder, registeredDescriptor);
    }
}

/// <summary>Continues the registration of one typed module action.</summary>
public sealed class ActionRegistration<TAction, TResult>
{
    private readonly SharpClawModuleBuilder _builder;

    internal ActionRegistration(
        SharpClawModuleBuilder builder,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        _builder = builder;
        Descriptor = descriptor;
    }

    /// <summary>Gets the exact descriptor recorded by the module builder.</summary>
    public ActionDescriptor<TAction, TResult> Descriptor { get; }

    /// <summary>Adds one typed terminal with its stable identifier.</summary>
    public void UseTerminal<TTerminal>(Guid terminalId)
        where TTerminal : class, IHostActionEntryTerminal<TAction, TResult> =>
        _builder.AddActionEntry<TAction, TResult, TTerminal>(Descriptor, terminalId);
}
