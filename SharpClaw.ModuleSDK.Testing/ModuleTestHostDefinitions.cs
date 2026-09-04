using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

internal sealed record ModuleTestHostAction(
    UntypedActionDescriptor Descriptor,
    Type ActionType,
    Type ResultType,
    object TypedDescriptor,
    SidecarHostActionDescriptor SidecarDescriptor,
    Action<IServiceCollection> Register)
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
            services => services.AddSingleton<IActionDefinitionBinding>(
                new ActionDefinitionBinding<TAction, TResult>(
                    ModuleTestHostDefinitionSet.SourceId,
                    descriptor)));
    }
}

internal sealed record ModuleTestHostEvent(
    UntypedEventDescriptor Descriptor,
    Type EventType,
    object TypedDescriptor,
    SidecarHostEventDescriptor SidecarDescriptor,
    Action<IServiceCollection> Register)
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
            services => services.AddSingleton<IEventDefinitionBinding>(
                new EventDefinitionBinding<TEvent>(
                    ModuleTestHostDefinitionSet.SourceId,
                    descriptor)));
    }
}

internal static class ModuleTestHostDefinitionSet
{
    internal const string SourceId = "module_sdk_test_host";

    public static void AddTo(
        IServiceCollection services,
        IReadOnlyList<ModuleTestHostAction> actions,
        IReadOnlyList<ModuleTestHostEvent> events)
    {
        foreach (var action in actions)
            action.Register(services);
        foreach (var evt in events)
            evt.Register(services);
    }
}
