using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.InProcess;

/// <summary>Creates host-issued authority for one compiled behavior graph.</summary>
public sealed class CompiledBehaviorAuthority : IExternalBehaviorAuthority
{
    private CompiledBehaviorAuthority(
        SidecarDiscoveryEnvelope discovery,
        SidecarHostAuthorization authorization)
    {
        Discovery = discovery;
        Authorization = authorization;
    }

    /// <summary>Gets the compiled behavior source identifier.</summary>
    public string SourceId => Discovery.SourceId;

    /// <summary>Gets the exact host-issued action and event grants.</summary>
    public SidecarHostAuthorization Authorization { get; }

    /// <summary>Gets the measured descriptor envelope used to issue the grants.</summary>
    public SidecarDiscoveryEnvelope Discovery { get; }

    /// <summary>Creates one measured descriptor envelope for any supported host mode.</summary>
    public static SidecarDiscoveryEnvelope Describe(
        ModuleContributionGraph graph,
        int protocolVersion,
        long sequence,
        DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.ProtocolVersionRange.Contains(protocolVersion))
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));

        return SidecarDiscoveryFactory.CreateCompiledGraph(
            graph,
            protocolVersion,
            sequence,
            deadline);
    }

    /// <summary>Validates one descriptor envelope and creates its exact host authority.</summary>
    public static CompiledBehaviorAuthority Create(
        ModuleContributionGraph graph,
        SidecarDiscoveryEnvelope discovery,
        SidecarHostDescriptorCatalog hostCatalog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(hostCatalog);
        if (!string.Equals(graph.Identity.Id, discovery.SourceId, StringComparison.Ordinal)
            || !string.Equals(graph.ContractHash, discovery.ContractHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The descriptor envelope does not match the compiled behavior graph.",
                nameof(discovery));
        }

        var authorization = AddActionEntryGrants(
            graph,
            SidecarAuthorizationFactory.Create(discovery, hostCatalog));
        return new CompiledBehaviorAuthority(discovery, authorization);
    }

    private static SidecarHostAuthorization AddActionEntryGrants(
        ModuleContributionGraph graph,
        SidecarHostAuthorization authorization)
    {
        var grants = authorization.ActionGrants.ToList();
        foreach (var entry in graph.ActionEntries)
        {
            var definitions = graph.Actions.Where(item =>
                item.Descriptor.Key == entry.Descriptor.Key
                && item.Descriptor.Version == entry.Descriptor.Version).ToArray();
            if (definitions.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Application action '{entry.Descriptor.Key.Value}' has no unique compiled definition.");
            }

            var definition = definitions[0];
            var grant = new ActionCapabilityGrant(
                definition.Descriptor.Key,
                definition.Descriptor.Version,
                definition.Descriptor.Capabilities,
                SensitiveApproved: definition.Descriptor.ContainsSensitiveData,
                AcceptUnknownSchemas: false);
            var existing = grants.Where(item =>
                item.ActionKey == grant.ActionKey
                && item.ActionVersion == grant.ActionVersion).ToArray();
            if (existing.Any(item => item != grant))
            {
                throw new InvalidOperationException(
                    $"Application action '{entry.Descriptor.Key.Value}' conflicts with compiled authorization.");
            }
            if (existing.Length == 0)
                grants.Add(grant);
        }

        return authorization with
        {
            ActionGrants = Array.AsReadOnly(grants
                .OrderBy(item => item.ActionKey.Value, StringComparer.Ordinal)
                .ThenBy(item => item.ActionVersion)
                .ToArray()),
        };
    }
}
