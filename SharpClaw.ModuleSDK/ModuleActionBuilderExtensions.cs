using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Provides typed module action registration.</summary>
public static class ModuleActionBuilderExtensions
{
    /// <summary>Defines one typed action and supplies deterministic schemas when the descriptor omits them.</summary>
    public static ModuleActionRegistration<TAction, TResult> DefineAction<TAction, TResult>(
        this ISharpClawModuleBuilder builder,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);

        var registeredDescriptor = descriptor with
        {
            InputSchema = descriptor.InputSchema
                ?? ModuleSchemaIdentity.ActionInput(descriptor.Key, descriptor.Version, typeof(TAction)),
            ResultSchema = descriptor.ResultSchema
                ?? ModuleSchemaIdentity.ActionResult(descriptor.Key, descriptor.Version, typeof(TResult)),
        };

        builder.Actions.Add(registeredDescriptor);
        return new ModuleActionRegistration<TAction, TResult>(builder, registeredDescriptor);
    }
}

/// <summary>Continues the registration of one typed module action.</summary>
public sealed class ModuleActionRegistration<TAction, TResult>
{
    private readonly ISharpClawModuleBuilder _builder;

    internal ModuleActionRegistration(
        ISharpClawModuleBuilder builder,
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
