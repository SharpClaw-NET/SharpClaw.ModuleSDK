using System.Reflection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.ModuleSDK.Testing;

internal static class ModuleTestKernelOptions
{
    private static readonly MethodInfo ActionSchemaMethod = typeof(KernelSchemaIdentity)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(KernelSchemaIdentity.Action)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 3);

    private static readonly MethodInfo EventSchemaMethod = typeof(KernelSchemaIdentity)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(KernelSchemaIdentity.Event)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Length == 2);

    public static KernelGraphCompileOptions Create(
        KernelGraphCompileOptions source,
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IReadOnlyList<ModuleTestHostAction> hostActions,
        IReadOnlyList<ModuleTestHostEvent> hostEvents,
        IReadOnlySet<string> approvedSensitiveModules)
    {
        var knownModules = moduleGraphs
            .Select(graph => graph.Identity.Id)
            .ToHashSet(StringComparer.Ordinal);
        var unknownApprovals = approvedSensitiveModules
            .Where(moduleId => !knownModules.Contains(moduleId))
            .ToArray();
        if (unknownApprovals.Length > 0)
        {
            throw new InvalidOperationException(
                $"Sensitive approval names unknown modules: {string.Join(", ", unknownApprovals)}.");
        }

        var actionCandidates = CreateActionCandidates(moduleGraphs, hostActions);
        var eventCandidates = CreateEventCandidates(moduleGraphs, hostEvents);
        var actionGrants = CreateActionGrants(moduleGraphs, hostActions, actionCandidates);
        var eventGrants = CreateEventGrants(moduleGraphs, hostEvents, eventCandidates);
        ApplyActionLimits(actionGrants, source.ActionModuleCapabilityGrants);
        ApplyEventLimits(eventGrants, source.EventModuleCapabilityGrants);

        var sensitiveModules = approvedSensitiveModules.ToHashSet(StringComparer.Ordinal);
        if (hostActions.Count > 0 || hostEvents.Count > 0)
            sensitiveModules.Add(ModuleTestHostDefinitionModule.ModuleId);
        var actionApprovals = source.SensitiveActionApprovals
            .Concat(CreateActionApprovals(moduleGraphs, actionCandidates, sensitiveModules))
            .Distinct()
            .ToArray();
        var eventApprovals = source.SensitiveEventApprovals
            .Concat(CreateEventApprovals(moduleGraphs, eventCandidates, sensitiveModules))
            .Distinct()
            .ToArray();

        return new KernelGraphCompileOptions
        {
            SupportedActionCapabilities = source.SupportedActionCapabilities,
            SupportedEventCapabilities = source.SupportedEventCapabilities,
            ActionCapabilityGrants = source.ActionCapabilityGrants,
            ActionModuleCapabilityGrants = actionGrants.ToDictionary(
                item => item.Key,
                item => (IReadOnlyDictionary<string, ActionInterceptionCapabilities>)item.Value,
                StringComparer.Ordinal),
            EventCapabilityGrants = source.EventCapabilityGrants,
            EventModuleCapabilityGrants = eventGrants.ToDictionary(
                item => item.Key,
                item => (IReadOnlyDictionary<string, EventInterceptionCapabilities>)item.Value,
                StringComparer.Ordinal),
            SensitiveActionApprovals = actionApprovals,
            SensitiveEventApprovals = eventApprovals,
            MaximumActionDepth = source.MaximumActionDepth,
        };
    }

    private static IReadOnlyList<ActionCandidate> CreateActionCandidates(
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IReadOnlyList<ModuleTestHostAction> hostActions)
    {
        var candidates = KernelActionCatalog.Descriptors
            .Select(entry => new ActionCandidate(
                "core",
                entry.Key,
                entry.Version,
                entry.Category,
                entry.Capabilities,
                entry.ContainsSensitiveData,
                typeof(KernelActionEnvelope),
                typeof(object),
                entry.ToDescriptor(),
                entry))
            .ToList();
        candidates.AddRange(hostActions.Select(action => new ActionCandidate(
            ModuleTestHostDefinitionModule.ModuleId,
            action.Descriptor.Key,
            action.Descriptor.Version,
            action.Descriptor.Category,
            action.Descriptor.Capabilities,
            action.Descriptor.ContainsSensitiveData,
            action.ActionType,
            action.ResultType,
            action.TypedDescriptor,
            null)));
        candidates.AddRange(moduleGraphs.SelectMany(graph => graph.Actions.Select(action =>
            new ActionCandidate(
                action.OwnerModuleId,
                action.Descriptor.Key,
                action.Descriptor.Version,
                action.Descriptor.Category,
                action.Descriptor.Capabilities,
                action.Descriptor.ContainsSensitiveData,
                action.ActionType,
                action.ResultType,
                action.TypedDescriptor,
                null))));
        return candidates;
    }

    private static IReadOnlyList<EventCandidate> CreateEventCandidates(
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IReadOnlyList<ModuleTestHostEvent> hostEvents)
    {
        var candidates = hostEvents.Select(evt => new EventCandidate(
            ModuleTestHostDefinitionModule.ModuleId,
            evt.Descriptor.Key,
            evt.Descriptor.Version,
            evt.Descriptor.Category,
            evt.Descriptor.Capabilities,
            evt.Descriptor.ContainsSensitiveData,
            evt.EventType,
            evt.TypedDescriptor)).ToList();
        candidates.AddRange(moduleGraphs.SelectMany(graph => graph.Events.Select(evt =>
            new EventCandidate(
                evt.OwnerModuleId,
                evt.Descriptor.Key,
                evt.Descriptor.Version,
                evt.Descriptor.Category,
                evt.Descriptor.Capabilities,
                evt.Descriptor.ContainsSensitiveData,
                evt.EventType,
                evt.TypedDescriptor))));
        return candidates;
    }

    private static Dictionary<string, Dictionary<string, ActionInterceptionCapabilities>>
        CreateActionGrants(
            IReadOnlyList<ModuleContributionGraph> moduleGraphs,
            IReadOnlyList<ModuleTestHostAction> hostActions,
            IReadOnlyList<ActionCandidate> candidates)
    {
        var result = new Dictionary<string, Dictionary<string, ActionInterceptionCapabilities>>(
            StringComparer.Ordinal);
        if (hostActions.Count > 0)
        {
            var hostGrants = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var action in hostActions)
                AddGrant(hostGrants, action.Descriptor.Key.Value, action.Descriptor.Capabilities);
            result.Add(ModuleTestHostDefinitionModule.ModuleId, hostGrants);
        }

        foreach (var graph in moduleGraphs)
        {
            var grants = new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var candidate in candidates.Where(candidate =>
                         string.Equals(candidate.OwnerModuleId, graph.Identity.Id, StringComparison.Ordinal)))
            {
                AddGrant(grants, candidate.Key.Value, candidate.Capabilities);
            }

            foreach (var hook in graph.ActionHooks)
            {
                foreach (var candidate in candidates.Where(candidate => Matches(hook, candidate)))
                {
                    AddGrant(
                        grants,
                        candidate.Key.Value,
                        hook.RequestedCapabilities & candidate.Capabilities);
                }
            }

            result.Add(graph.Identity.Id, grants);
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, EventInterceptionCapabilities>>
        CreateEventGrants(
            IReadOnlyList<ModuleContributionGraph> moduleGraphs,
            IReadOnlyList<ModuleTestHostEvent> hostEvents,
            IReadOnlyList<EventCandidate> candidates)
    {
        var result = new Dictionary<string, Dictionary<string, EventInterceptionCapabilities>>(
            StringComparer.Ordinal);
        if (hostEvents.Count > 0)
        {
            var hostGrants = new Dictionary<string, EventInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var evt in hostEvents)
                AddGrant(hostGrants, evt.Descriptor.Key.Value, evt.Descriptor.Capabilities);
            result.Add(ModuleTestHostDefinitionModule.ModuleId, hostGrants);
        }

        foreach (var graph in moduleGraphs)
        {
            var grants = new Dictionary<string, EventInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var candidate in candidates.Where(candidate =>
                         string.Equals(candidate.OwnerModuleId, graph.Identity.Id, StringComparison.Ordinal)))
            {
                AddGrant(grants, candidate.Key.Value, candidate.Capabilities);
            }

            foreach (var hook in graph.EventHooks)
            {
                foreach (var candidate in candidates.Where(candidate => Matches(hook, candidate)))
                {
                    AddGrant(
                        grants,
                        candidate.Key.Value,
                        hook.RequestedCapabilities & candidate.Capabilities);
                }
            }

            result.Add(graph.Identity.Id, grants);
        }

        return result;
    }

    private static IEnumerable<KernelSensitiveActionApproval> CreateActionApprovals(
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IReadOnlyList<ActionCandidate> candidates,
        IReadOnlySet<string> approvedModules)
    {
        foreach (var moduleId in approvedModules)
        {
            var graph = moduleGraphs.SingleOrDefault(candidate =>
                string.Equals(candidate.Identity.Id, moduleId, StringComparison.Ordinal));
            foreach (var candidate in candidates.Where(candidate => candidate.ContainsSensitiveData))
            {
                var selected = string.Equals(candidate.OwnerModuleId, moduleId, StringComparison.Ordinal)
                    || graph?.ActionHooks.Any(hook => Matches(hook, candidate)) == true;
                if (selected)
                    yield return CreateActionApproval(moduleId, candidate);
            }
        }
    }

    private static IEnumerable<KernelSensitiveEventApproval> CreateEventApprovals(
        IReadOnlyList<ModuleContributionGraph> moduleGraphs,
        IReadOnlyList<EventCandidate> candidates,
        IReadOnlySet<string> approvedModules)
    {
        foreach (var moduleId in approvedModules)
        {
            var graph = moduleGraphs.SingleOrDefault(candidate =>
                string.Equals(candidate.Identity.Id, moduleId, StringComparison.Ordinal));
            foreach (var candidate in candidates.Where(candidate => candidate.ContainsSensitiveData))
            {
                var selected = string.Equals(candidate.OwnerModuleId, moduleId, StringComparison.Ordinal)
                    || graph?.EventHooks.Any(hook => Matches(hook, candidate)) == true;
                if (selected)
                    yield return CreateEventApproval(moduleId, candidate);
            }
        }
    }

    private static KernelSensitiveActionApproval CreateActionApproval(
        string moduleId,
        ActionCandidate candidate)
    {
        if (candidate.StandardEntry is not null)
        {
            var descriptor = candidate.StandardEntry.ToDescriptor();
            var types = KernelSchemaIdentity.ActionTypes(
                descriptor,
                typeof(KernelActionEnvelope),
                typeof(object));
            return new KernelSensitiveActionApproval(
                moduleId,
                candidate.Key,
                candidate.Version,
                TypeName(types.ActionType),
                TypeName(types.ResultType),
                KernelSchemaIdentity.Action(descriptor));
        }

        var schema = (string)(ActionSchemaMethod
            .MakeGenericMethod(candidate.ActionType, candidate.ResultType)
            .Invoke(
                null,
                [candidate.TypedDescriptor, candidate.ActionType, candidate.ResultType])
            ?? throw new InvalidOperationException("Core did not create an action schema identity."));
        return new KernelSensitiveActionApproval(
            moduleId,
            candidate.Key,
            candidate.Version,
            TypeName(candidate.ActionType),
            TypeName(candidate.ResultType),
            schema);
    }

    private static KernelSensitiveEventApproval CreateEventApproval(
        string moduleId,
        EventCandidate candidate)
    {
        var schema = (string)(EventSchemaMethod
            .MakeGenericMethod(candidate.EventType)
            .Invoke(null, [candidate.TypedDescriptor, candidate.EventType])
            ?? throw new InvalidOperationException("Core did not create an event schema identity."));
        return new KernelSensitiveEventApproval(
            moduleId,
            candidate.Key,
            candidate.Version,
            TypeName(candidate.EventType),
            schema);
    }

    private static bool Matches(ModuleActionHook hook, ActionCandidate candidate) =>
        hook.TargetKind switch
        {
            SidecarHookTargetKind.Exact => hook.ActionKey == candidate.Key,
            SidecarHookTargetKind.Category => string.Equals(
                hook.Category,
                candidate.Category,
                StringComparison.Ordinal),
            SidecarHookTargetKind.Wildcard => true,
            _ => false,
        };

    private static bool Matches(ModuleEventHook hook, EventCandidate candidate) =>
        hook.TargetKind switch
        {
            SidecarHookTargetKind.Exact => hook.EventKey == candidate.Key,
            SidecarHookTargetKind.Category => string.Equals(
                hook.Category,
                candidate.Category,
                StringComparison.Ordinal),
            SidecarHookTargetKind.Wildcard => true,
            _ => false,
        };

    private static void ApplyActionLimits(
        Dictionary<string, Dictionary<string, ActionInterceptionCapabilities>> grants,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>? limits)
    {
        if (limits is null)
            return;
        foreach (var module in grants)
        {
            if (!limits.TryGetValue(module.Key, out var moduleLimits))
                continue;
            foreach (var key in module.Value.Keys.ToArray())
            {
                if (moduleLimits.TryGetValue(key, out var limit))
                    module.Value[key] &= limit;
            }
        }
    }

    private static void ApplyEventLimits(
        Dictionary<string, Dictionary<string, EventInterceptionCapabilities>> grants,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, EventInterceptionCapabilities>>? limits)
    {
        if (limits is null)
            return;
        foreach (var module in grants)
        {
            if (!limits.TryGetValue(module.Key, out var moduleLimits))
                continue;
            foreach (var key in module.Value.Keys.ToArray())
            {
                if (moduleLimits.TryGetValue(key, out var limit))
                    module.Value[key] &= limit;
            }
        }
    }

    private static void AddGrant(
        IDictionary<string, ActionInterceptionCapabilities> grants,
        string key,
        ActionInterceptionCapabilities capabilities) =>
        grants[key] = grants.TryGetValue(key, out var current)
            ? current | capabilities
            : capabilities;

    private static void AddGrant(
        IDictionary<string, EventInterceptionCapabilities> grants,
        string key,
        EventInterceptionCapabilities capabilities) =>
        grants[key] = grants.TryGetValue(key, out var current)
            ? current | capabilities
            : capabilities;

    private static string TypeName(Type type) =>
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    private sealed record ActionCandidate(
        string OwnerModuleId,
        SharpClawActionKey Key,
        int Version,
        string Category,
        ActionInterceptionCapabilities Capabilities,
        bool ContainsSensitiveData,
        Type ActionType,
        Type ResultType,
        object TypedDescriptor,
        KernelStandardActionManifestEntry? StandardEntry);

    private sealed record EventCandidate(
        string OwnerModuleId,
        SharpClawEventKey Key,
        int Version,
        string Category,
        EventInterceptionCapabilities Capabilities,
        bool ContainsSensitiveData,
        Type EventType,
        object TypedDescriptor);
}
