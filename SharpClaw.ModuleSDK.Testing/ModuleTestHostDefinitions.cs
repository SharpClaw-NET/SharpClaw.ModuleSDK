using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK.Testing;

internal sealed record ModuleTestHostAction(
    UntypedActionDescriptor Descriptor,
    Type ActionType,
    Type ResultType,
    object TypedDescriptor,
    SidecarHostActionDescriptor SidecarDescriptor,
    Action<ISharpClawModuleBuilder> Register)
{
    public static ModuleTestHostAction Create<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor)
    {
        var inputSchema = ModuleSchemaIdentity.ActionInput(
            descriptor.Key,
            descriptor.Version,
            typeof(TAction));
        var resultSchema = ModuleSchemaIdentity.ActionResult(
            descriptor.Key,
            descriptor.Version,
            typeof(TResult));
        return new ModuleTestHostAction(
            new UntypedActionDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                descriptor.Capabilities,
                inputSchema,
                resultSchema,
                descriptor.ContainsSensitiveData)
            {
                ProtocolVersionRange = descriptor.ProtocolVersionRange,
            },
            typeof(TAction),
            typeof(TResult),
            descriptor,
            new SidecarHostActionDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                inputSchema,
                resultSchema,
                descriptor.Capabilities,
                descriptor.ContainsSensitiveData,
                descriptor.ProtocolVersionRange),
            module => module.Actions.Add(descriptor));
    }
}

internal sealed record ModuleTestHostEvent(
    UntypedEventDescriptor Descriptor,
    Type EventType,
    object TypedDescriptor,
    SidecarHostEventDescriptor SidecarDescriptor,
    Action<ISharpClawModuleBuilder> Register)
{
    public static ModuleTestHostEvent Create<TEvent>(EventDescriptor<TEvent> descriptor)
    {
        var payloadSchema = ModuleSchemaIdentity.EventPayload(
            descriptor.Key,
            descriptor.Version,
            typeof(TEvent));
        return new ModuleTestHostEvent(
            new UntypedEventDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                descriptor.Capabilities,
                payloadSchema,
                descriptor.ContainsSensitiveData)
            {
                ProtocolVersionRange = descriptor.ProtocolVersionRange,
            },
            typeof(TEvent),
            descriptor,
            new SidecarHostEventDescriptor(
                descriptor.Key,
                descriptor.Version,
                descriptor.Category,
                payloadSchema,
                descriptor.Capabilities,
                descriptor.ContainsSensitiveData,
                descriptor.ProtocolVersionRange),
            module => module.Events.Add(descriptor));
    }
}

internal sealed class ModuleTestHostDefinitionModule(
    IReadOnlyList<ModuleTestHostAction> actions,
    IReadOnlyList<ModuleTestHostEvent> events) : ISharpClawModule
{
    internal const string ModuleId = "module_sdk_test_host";

    public ModuleIdentity Identity { get; } =
        new(ModuleId, "Module SDK Test Host", "module_sdk_test_host");

    public void Configure(ISharpClawModuleBuilder module)
    {
        foreach (var action in actions)
            action.Register(module);
        foreach (var evt in events)
            evt.Register(module);
    }
}
