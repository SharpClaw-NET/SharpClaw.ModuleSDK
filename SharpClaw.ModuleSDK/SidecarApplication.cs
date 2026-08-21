using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Identifies one typed endpoint contribution across the sidecar boundary.</summary>
public sealed record SidecarApplicationEndpoint(
    string TypeName,
    string AssemblyName);

/// <summary>Describes one CLI handler contribution across the sidecar boundary.</summary>
public sealed record SidecarApplicationCliCommand(
    string HandlerTypeName,
    string AssemblyName,
    ModuleCliCommandDescriptor Descriptor);

/// <summary>Describes one module-owned action terminal across the sidecar boundary.</summary>
public sealed record SidecarApplicationActionEntry(
    string ModuleId,
    string ContractHash,
    SidecarActionDescriptorIdentity Descriptor,
    Guid TerminalId,
    string TerminalTypeName,
    string AssemblyName);

/// <summary>Describes application contributions from one compiled sidecar graph.</summary>
public sealed record SidecarApplicationDiscovery(
    string ModuleId,
    string ContractHash,
    IReadOnlyList<SidecarApplicationEndpoint> Endpoints,
    IReadOnlyList<SidecarApplicationCliCommand> CliCommands,
    IReadOnlyList<SidecarApplicationActionEntry> ActionEntries)
{
    /// <summary>Gets whether the graph has no application contribution.</summary>
    public bool IsEmpty => Endpoints.Count == 0 && CliCommands.Count == 0 && ActionEntries.Count == 0;
}

/// <summary>Invokes one discovered module CLI command.</summary>
public sealed record SidecarCliInvocation(
    Guid InvocationId,
    string ModuleId,
    string ContractHash,
    string Command,
    IReadOnlyList<string> Arguments,
    HostActionEntryRequestContext HostActionContext);

/// <summary>Returns one module CLI result with graph identity.</summary>
public sealed record SidecarCliExecutionResponse(
    string ModuleId,
    string ContractHash,
    ModuleCliResult Result);

/// <summary>Extends the flat sidecar discovery document with application metadata.</summary>
public sealed record SidecarDiscoveryDocument(
    SidecarMessageHeader Header,
    string ModuleId,
    string ContractHash,
    SidecarProtocolOffer Protocol,
    IReadOnlyList<SidecarActionSubscription> Actions,
    IReadOnlyList<SidecarEventSubscription> Events,
    IReadOnlyList<SidecarActionDefinition> ActionDefinitions,
    IReadOnlyList<SidecarEventDefinition> EventDefinitions,
    IReadOnlyList<SidecarToolHandlerDefinition> ToolHandlers,
    IReadOnlyList<SidecarLifecycleHandlerDefinition> LifecycleHandlers,
    IReadOnlyList<ModuleFeatureDescriptor> Features,
    SidecarApplicationDiscovery Application) : ISidecarProtocolMessage
{
    /// <summary>Gets the discovery message kind used by the base sidecar protocol.</summary>
    public SidecarProtocolMessageKind MessageKind => SidecarProtocolMessageKind.Discovery;

    /// <summary>Gets the base Contracts discovery envelope without application metadata.</summary>
    public SidecarDiscoveryEnvelope ToDiscovery() => new(
        Header,
        ModuleId,
        ContractHash,
        Protocol,
        Actions,
        Events,
        ActionDefinitions,
        EventDefinitions,
        ToolHandlers,
        LifecycleHandlers,
        Features);
}

/// <summary>Builds application metadata from the compiled module graph.</summary>
public static class ModuleContributionGraphApplicationExtensions
{
    /// <summary>Creates application metadata with stable module type identities.</summary>
    public static SidecarApplicationDiscovery CreateSidecarApplicationDiscovery(
        this ModuleContributionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new SidecarApplicationDiscovery(
            graph.Identity.Id,
            graph.ContractHash,
            Array.AsReadOnly(graph.Application.EndpointTypes
                .Select(CreateEndpoint)
                .ToArray()),
            Array.AsReadOnly(graph.Application.CliCommands
                .Select(CreateCliCommand)
                .ToArray()),
            Array.AsReadOnly(graph.ActionEntries
                .Select(entry => new SidecarApplicationActionEntry(
                    graph.Identity.Id,
                    graph.ContractHash,
                    entry.Descriptor,
                    entry.TerminalId,
                    entry.TerminalType.FullName
                        ?? throw new InvalidOperationException(
                            "An action entry terminal type must have a full name."),
                    entry.TerminalType.Assembly.GetName().Name
                        ?? throw new InvalidOperationException(
                            "An action entry terminal type must have an assembly name.")))
                .ToArray()));
    }

    private static SidecarApplicationEndpoint CreateEndpoint(Type type) =>
        new(
            type.FullName
                ?? throw new InvalidOperationException(
                    "An endpoint contribution type must have a full name."),
            type.Assembly.GetName().Name
                ?? throw new InvalidOperationException(
                    "An endpoint contribution type must have an assembly name."));

    private static SidecarApplicationCliCommand CreateCliCommand(
        ModuleCliContribution contribution) =>
        new(
            contribution.HandlerType.FullName
                ?? throw new InvalidOperationException(
                    "A CLI contribution handler type must have a full name."),
            contribution.HandlerType.Assembly.GetName().Name
                ?? throw new InvalidOperationException(
                    "A CLI contribution handler type must have an assembly name."),
            contribution.Descriptor);
}
