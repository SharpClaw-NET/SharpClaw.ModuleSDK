using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Identifies one typed endpoint contribution across the sidecar boundary.</summary>
public sealed record SidecarApplicationEndpoint(
    ModuleEndpointRouteDescriptor Descriptor,
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
    IReadOnlyList<SidecarApplicationActionEntry> ActionEntries,
    IReadOnlyList<SidecarChatContributionDefinition> Chat)
{
    /// <summary>Gets whether the graph has no application contribution.</summary>
    public bool IsEmpty =>
        Endpoints.Count == 0 && CliCommands.Count == 0 && ActionEntries.Count == 0 && Chat.Count == 0;
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

/// <summary>Returns one endpoint result with graph identity.</summary>
public sealed record SidecarEndpointExecutionResponse(
    string ModuleId,
    string ContractHash,
    ModuleHttpEndpointResponse Response);

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
    IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts,
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
            Array.AsReadOnly(graph.Application.Endpoints
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
                .ToArray()),
            CreateChat(graph));
    }

    private static IReadOnlyList<SidecarChatContributionDefinition> CreateChat(
        ModuleContributionGraph graph)
    {
        var contributions = new List<SidecarChatContributionDefinition>();
        if (graph.Chat.ConversationResolver is not null)
        {
            contributions.Add(CreateChatContribution(
                graph,
                SidecarChatContributionKind.ConversationResolver,
                graph.Chat.ConversationResolverRegistration?.Id
                    ?? throw new InvalidOperationException(
                        "A sidecar conversation resolver requires one registration identity."),
                SidecarChatActionDescriptors.ConversationResolver,
                SidecarChatActionDescriptors.ConversationResolverTerminalId));
        }
        if (graph.Chat.ProfileResolver is not null)
        {
            contributions.Add(CreateChatContribution(
                graph,
                SidecarChatContributionKind.ProfileResolver,
                graph.Chat.ProfileResolverRegistration?.Id
                    ?? throw new InvalidOperationException(
                        "A sidecar profile resolver requires one registration identity."),
                SidecarChatActionDescriptors.ProfileResolver,
                SidecarChatActionDescriptors.ProfileResolverTerminalId));
        }
        if (graph.Services.Any(item => item.ServiceType == typeof(IConversationStore)))
        {
            var registrationId = $"{graph.Identity.Id}.conversation-store";
            contributions.Add(CreateChatContribution(
                graph,
                SidecarChatContributionKind.HistoryLoad,
                registrationId,
                SidecarChatActionDescriptors.HistoryLoad,
                SidecarChatActionDescriptors.HistoryLoadTerminalId));
            contributions.Add(CreateChatContribution(
                graph,
                SidecarChatContributionKind.ExchangeCommit,
                registrationId,
                SidecarChatActionDescriptors.ExchangeCommit,
                SidecarChatActionDescriptors.ExchangeCommitTerminalId));
        }
        if (graph.Chat.ContextContributors.Count > 0)
        {
            contributions.Add(CreateChatContribution(
                graph,
                SidecarChatContributionKind.ContextContributor,
                $"{graph.Identity.Id}.context-contributor",
                SidecarChatActionDescriptors.ContextContributor,
                SidecarChatActionDescriptors.ContextContributorTerminalId));
        }
        return Array.AsReadOnly(contributions.ToArray());
    }

    private static SidecarChatContributionDefinition CreateChatContribution<TAction, TResult>(
        ModuleContributionGraph graph,
        SidecarChatContributionKind kind,
        string registrationId,
        ActionDescriptor<TAction, TResult> descriptor,
        Guid terminalId)
    {
        var entry = graph.ActionEntries.SingleOrDefault(item => item.TerminalId == terminalId)
            ?? throw new InvalidOperationException(
                $"The sidecar chat contribution '{kind}' has no action entry.");
        return new SidecarChatContributionDefinition(
            kind,
            registrationId,
            entry.Descriptor,
            terminalId);
    }

    private static SidecarApplicationEndpoint CreateEndpoint(
        ModuleEndpointContribution contribution) =>
        new(
            contribution.Descriptor,
            contribution.HandlerType.FullName
                ?? throw new InvalidOperationException(
                    "An endpoint handler type must have a full name."),
            contribution.HandlerType.Assembly.GetName().Name
                ?? throw new InvalidOperationException(
                    "An endpoint handler type must have an assembly name."));

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
