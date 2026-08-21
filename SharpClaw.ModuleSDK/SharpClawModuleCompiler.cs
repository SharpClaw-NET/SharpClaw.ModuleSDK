using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Compiles one module into an immutable host contribution graph.</summary>
public static class SharpClawModuleCompiler
{
    /// <summary>Compiles and validates one module.</summary>
    public static ModuleContributionGraph Compile(
        ISharpClawModule module,
        ModuleManifest? manifest = null,
        ModuleCompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        options ??= new ModuleCompilationOptions();
        var errors = new List<GraphCompilationError>();
        ValidateIdentity(module.Identity, manifest, errors);
        ValidateOptions(options, module.Identity.Id, errors);

        var builder = new SharpClawModuleBuilder(module.Identity);
        try
        {
            module.Configure(builder);
            if (module is ISharpClawApplicationModule applicationModule)
                applicationModule.ConfigureApplication(new SharpClawApplicationBuilder(builder));
        }
        catch (Exception ex) when (ex is not ModuleGraphCompilationException)
        {
            errors.Add(Error(
                "module_configuration_failed",
                module.Identity.Id,
                module.Identity.Id,
                "configure",
                $"Module configuration failed: {ex.Message}"));
        }

        var state = builder.State;
        ValidateActions(state, errors);
        ValidateEvents(state, errors);
        ValidateTools(state, errors);
        ValidateContracts(state, errors);
        ValidateStorage(state, errors);
        ValidateChat(state, errors);
        ValidateApplication(state, options, errors);
        ValidateActionEntries(state, errors);

        var actionHooks = CompileActionHooks(state, manifest, options, errors);
        var eventHooks = CompileEventHooks(state, manifest, options, errors);
        actionHooks = OrderHooks(actionHooks, hook => hook.Ordering, module.Identity.Id, "action", errors);
        eventHooks = OrderHooks(eventHooks, hook => hook.Ordering, module.Identity.Id, "event", errors);

        if (options.HostingMode == ModuleHostingMode.OutOfProcess)
        {
            ValidateUniqueSidecarTargets(state, actionHooks, eventHooks, module.Identity.Id, errors);
        }

        if (errors.Count > 0)
            throw new ModuleGraphCompilationException(Array.AsReadOnly(errors.ToArray()));

        var services = Array.AsReadOnly(state.Services.ToArray());
        var contracts = Array.AsReadOnly(state.Contracts.ToArray());
        var storage = Array.AsReadOnly(state.Storage.ToArray());
        var actions = Array.AsReadOnly(state.Actions.ToArray());
        var events = Array.AsReadOnly(state.Events.ToArray());
        var tools = Array.AsReadOnly(state.Tools.ToArray());
        var application = new ModuleApplicationContributions(
            Array.AsReadOnly(state.Endpoints.ToArray()),
            Array.AsReadOnly(state.CliCommands.ToArray()),
            Array.AsReadOnly(state.UiContributions.ToArray()),
            Array.AsReadOnly(state.ActionEntries.ToArray()));
        var chat = new ModuleChatContributions(
            state.ConversationResolvers.SingleOrDefault(),
            state.ConversationResolverRegistrations.SingleOrDefault(),
            state.ProfileResolvers.SingleOrDefault(),
            state.ProfileResolverRegistrations.SingleOrDefault(),
            Array.AsReadOnly(state.ContextContributors.ToArray()));
        var features = Array.AsReadOnly((manifest?.Features ?? [])
            .Select(feature => new ModuleFeatureDescriptor(
                feature.ContractName,
                feature.SchemaVersion,
                module.Identity.Id,
                feature.MaxBytes,
                feature.Required))
            .ToArray());
        var contractHash = ComputeHash(
            module.Identity,
            contracts,
            storage,
            actions,
            events,
            actionHooks,
            eventHooks,
            tools,
            chat,
            application,
            state.ActionEntries,
            features);

        return new ModuleContributionGraph(
            module.Identity,
            options.HostingMode,
            services,
            contracts,
            storage,
            actions,
            events,
            actionHooks,
            eventHooks,
            tools,
            state.ActionEntries,
            chat,
            application,
            new ModuleActionDispatchMap(actionHooks),
            new ModuleEventDispatchMap(eventHooks),
            new ModuleToolDispatchMap(tools),
            contractHash,
            options.ProtocolVersionRange,
            options.PayloadLimits,
            features);
    }

    private static void ValidateIdentity(
        ModuleIdentity identity,
        ModuleManifest? manifest,
        ICollection<GraphCompilationError> errors)
    {
        if (identity is null
            || !ValidIdentifier(identity.Id)
            || string.IsNullOrWhiteSpace(identity.DisplayName)
            || !ValidIdentifier(identity.ToolPrefix))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidIdentity,
                identity?.Id ?? "unknown",
                identity?.Id ?? "unknown",
                "identity",
                "The module identity contains an invalid required value."));
            return;
        }

        if (manifest is not null
            && (!string.Equals(manifest.Id, identity.Id, StringComparison.Ordinal)
                || !string.Equals(manifest.DisplayName, identity.DisplayName, StringComparison.Ordinal)
                || !string.Equals(manifest.ToolPrefix, identity.ToolPrefix, StringComparison.Ordinal)))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.ManifestMismatch,
                identity.Id,
                manifest.Id,
                "identity",
                "The module identity does not match module.json."));
        }
    }

    private static void ValidateOptions(
        ModuleCompilationOptions options,
        string moduleId,
        ICollection<GraphCompilationError> errors)
    {
        if (options.ProtocolVersionRange is null || !options.PayloadLimits.IsValid)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidDescriptor,
                moduleId,
                moduleId,
                "protocol",
                "The host protocol versions or payload limits are invalid."));
        }
    }

    private static void ValidateActions(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in state.Actions.GroupBy(action => action.Descriptor.Key.Value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.DuplicateAction,
                state.Identity.Id,
                duplicate.Key,
                "definition",
                $"Action '{duplicate.Key}' is defined more than once."));
        }

        foreach (var action in state.Actions)
        {
            var descriptor = action.Descriptor;
            if (!ValidIdentifier(descriptor.Key.Value)
                || descriptor.Version < 1
                || string.IsNullOrWhiteSpace(descriptor.Category)
                || action.DefaultTimeout <= TimeSpan.Zero
                || action.SafePoints.Count == 0
                || descriptor.ProtocolVersionRange is null)
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidDescriptor,
                    state.Identity.Id,
                    descriptor.Key.Value,
                    "action",
                    $"Action '{descriptor.Key.Value}' has invalid identity, version, category, timeout, protocol, or safe points."));
            }

            if ((descriptor.Capabilities & ActionInterceptionCapabilities.Defer) != 0
                && (action.ContinuationPolicy is null
                    || !action.ContinuationPolicy.Durable
                    || !action.ContinuationPolicy.SingleClaim
                    || action.ContinuationPolicy.MaximumLifetime <= TimeSpan.Zero))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidDescriptor,
                    state.Identity.Id,
                    descriptor.Key.Value,
                    "defer",
                    $"Action '{descriptor.Key.Value}' permits deferment without a durable single-claim continuation policy."));
            }

            if ((descriptor.Capabilities & ActionInterceptionCapabilities.Repeat) != 0
                && (action.RepeatPolicy.Kind == ActionRepeatKind.None
                    || action.RepeatPolicy.MaximumAttempts < 2
                    || action.RepeatPolicy.MinimumBackoff < TimeSpan.Zero
                    || string.IsNullOrWhiteSpace(action.RepeatPolicy.IdempotencyScope)))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidDescriptor,
                    state.Identity.Id,
                    descriptor.Key.Value,
                    "repeat",
                    $"Action '{descriptor.Key.Value}' permits repetition without a bounded repeat policy."));
            }

            if (action.HasIrreversibleEffects
                && (descriptor.Capabilities & ActionInterceptionCapabilities.Repeat) != 0
                && action.RepeatPolicy.Kind != ActionRepeatKind.Receipted)
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidDescriptor,
                    state.Identity.Id,
                    descriptor.Key.Value,
                    "repeat",
                    $"Irreversible action '{descriptor.Key.Value}' requires receipted repetition."));
            }
        }
    }

    private static void ValidateEvents(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in state.Events.GroupBy(evt => evt.Descriptor.Key.Value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.DuplicateEvent,
                state.Identity.Id,
                duplicate.Key,
                "definition",
                $"Event '{duplicate.Key}' is defined more than once."));
        }

        foreach (var evt in state.Events)
        {
            if (!ValidIdentifier(evt.Descriptor.Key.Value)
                || evt.Descriptor.Version < 1
                || string.IsNullOrWhiteSpace(evt.Descriptor.Category)
                || evt.Descriptor.ProtocolVersionRange is null
                || evt.DeliveryClasses.Count == 0)
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidDescriptor,
                    state.Identity.Id,
                    evt.Descriptor.Key.Value,
                    "event",
                    $"Event '{evt.Descriptor.Key.Value}' has invalid identity, version, category, protocol, or delivery classes."));
            }
        }
    }

    private static void ValidateTools(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in state.Tools.GroupBy(tool => tool.Descriptor.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.DuplicateTool,
                state.Identity.Id,
                duplicate.Key,
                "tool",
                $"Tool '{duplicate.Key}' is registered more than once."));
        }

        foreach (var tool in state.Tools)
        {
            if (!ValidIdentifier(tool.Descriptor.Name)
                || string.IsNullOrWhiteSpace(tool.Descriptor.Description)
                || tool.Descriptor.Version < 1
                || !typeof(IToolHandler).IsAssignableFrom(tool.HandlerType))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidHandler,
                    state.Identity.Id,
                    tool.Descriptor.Name,
                    "tool",
                    $"Tool '{tool.Descriptor.Name}' has an invalid descriptor or handler."));
            }
        }
    }

    private static void ValidateContracts(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var contract in state.Contracts)
        {
            if (!ValidIdentifier(contract.ContractName)
                || contract.SchemaVersion < 1
                || (contract.IsExport && contract.MaxBytes < 1)
                || (!contract.IsExport && contract.MaxBytes != 0))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidContract,
                    state.Identity.Id,
                    contract.ContractName,
                    "contract",
                    $"Contract '{contract.ContractName}' has an invalid version or size."));
            }
        }

        foreach (var duplicate in state.Contracts
                     .Where(contract => contract.IsExport)
                     .GroupBy(contract => (contract.ContractName, contract.SchemaVersion))
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidContract,
                state.Identity.Id,
                duplicate.Key.ContractName,
                "contract",
                $"Contract '{duplicate.Key.ContractName}' version {duplicate.Key.SchemaVersion} is exported more than once."));
        }
    }

    private static void ValidateStorage(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var storage in state.Storage)
        {
            if (!string.Equals(storage.ModuleId, state.Identity.Id, StringComparison.Ordinal)
                || !ValidIdentifier(storage.StorageName)
                || storage.MaxDocumentBytes < 1
                || storage.MaxBatchSize < 1
                || storage.Operations.Count == 0)
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidStorage,
                    state.Identity.Id,
                    storage.StorageName,
                    "storage",
                    $"Storage '{storage.StorageName}' has invalid ownership or limits."));
            }
        }
    }

    private static void ValidateChat(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        if (state.ConversationResolvers.Count > 1 || state.ProfileResolvers.Count > 1)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidApplication,
                state.Identity.Id,
                "chat",
                "exclusive",
                "A module can register only one resolver for each exclusive chat slot."));
        }

        if (state.ConversationResolverRegistrations.Any(value => string.IsNullOrWhiteSpace(value?.Id))
            || state.ProfileResolverRegistrations.Any(value => string.IsNullOrWhiteSpace(value?.Id)))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidApplication,
                state.Identity.Id,
                "chat",
                "exclusive",
                "Each exclusive chat registration requires an identifier."));
        }
    }

    private static void ValidateApplication(
        ModuleBuilderState state,
        ModuleCompilationOptions options,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in state.CliCommands
                     .SelectMany(command => command.Descriptor.Aliases.Prepend(command.Descriptor.Name))
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidApplication,
                state.Identity.Id,
                duplicate.Key,
                "cli",
                $"CLI name or alias '{duplicate.Key}' is registered more than once."));
        }

        if (options.HostingMode == ModuleHostingMode.OutOfProcess
            && state.UiContributions.Count > 0)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.UnsupportedTransport,
                state.Identity.Id,
                "application",
                "sidecar",
                "The sidecar protocol does not transport UI contribution types."));
        }
    }

    private static void ValidateActionEntries(
        ModuleBuilderState state,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in state.ActionEntries
                     .GroupBy(entry => $"{entry.Descriptor.Key.Value}:{entry.Descriptor.Version}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidApplication,
                state.Identity.Id,
                duplicate.Key,
                "action-entry",
                "The module declares more than one terminal for the same action descriptor."));
        }

        foreach (var duplicate in state.ActionEntries
                     .GroupBy(entry => entry.TerminalId)
                     .Where(group => group.Key == Guid.Empty || group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidApplication,
                state.Identity.Id,
                duplicate.Key.ToString("D"),
                "action-entry",
                "Each action entry requires one unique non-empty terminal identifier."));
        }

        foreach (var entry in state.ActionEntries)
        {
            var action = state.Actions.SingleOrDefault(candidate =>
                candidate.Descriptor.Key == entry.Descriptor.Key
                && candidate.Descriptor.Version == entry.Descriptor.Version);
            if (action is null
                || action.ActionType != entry.ActionType
                || action.ResultType != entry.ResultType
                || !string.Equals(
                    action.Descriptor.Category,
                    entry.Descriptor.Category,
                    StringComparison.Ordinal)
                || !string.Equals(
                    action.Descriptor.InputSchema.ContentHash,
                    entry.Descriptor.InputSchemaHash,
                    StringComparison.Ordinal)
                || !string.Equals(
                    action.Descriptor.ResultSchema.ContentHash,
                    entry.Descriptor.ResultSchemaHash,
                    StringComparison.Ordinal))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidTarget,
                    state.Identity.Id,
                    $"{entry.Descriptor.Key.Value}:{entry.Descriptor.Version}",
                    "action-entry",
                    "An action entry must target one exact action definition owned by the module."));
            }
        }
    }

    private static IReadOnlyList<ModuleActionHook> CompileActionHooks(
        ModuleBuilderState state,
        ModuleManifest? manifest,
        ModuleCompilationOptions options,
        ICollection<GraphCompilationError> errors)
    {
        var compiled = new List<ModuleActionHook>();
        foreach (var pending in state.ActionHooks)
        {
            ValidateActionHandler(state.Identity.Id, pending, errors);
            var target = Target(pending.TargetKind, pending.ActionKey?.Value, pending.Category);
            var request = FindHookRequest(manifest, target);
            var manifestCapabilities = ParseActionEffects(request?.Effects, state.Identity.Id, target, errors);
            var requested = ResolveActionCapabilities(
                pending.RequestedCapabilities,
                manifestCapabilities,
                request,
                options.RequireManifestRequests,
                state.Identity.Id,
                target,
                errors);
            var descriptor = FindActionDescriptor(state, options, pending);
            var category = pending.DescriptorCategory ?? descriptor?.Category ?? pending.Category;
            var versionRange = pending.VersionRange
                ?? request?.VersionRange
                ?? descriptor?.ProtocolVersionRange
                ?? ContractVersionRange.Exact(descriptor?.Version ?? 1);
            var inputSchema = pending.InputSchema ?? descriptor?.InputSchema;
            var resultSchema = pending.ResultSchema ?? descriptor?.ResultSchema;

            if (pending.TargetKind == SidecarHookTargetKind.Exact
                && (descriptor is null || string.IsNullOrWhiteSpace(category)
                    || inputSchema is null || resultSchema is null))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidTarget,
                    state.Identity.Id,
                    target,
                    "descriptor",
                    $"Exact action hook '{target}' requires a known typed descriptor."));
                continue;
            }

            inputSchema ??= ModuleSchemaIdentity.UntypedAction("input", target);
            resultSchema ??= ModuleSchemaIdentity.UntypedAction("result", target);
            ValidateActionCapabilities(state.Identity.Id, target, requested, descriptor, options, errors);
            var (actionType, resultType) = GetTypedActionTypes(pending);
            compiled.Add(new ModuleActionHook(
                state.Identity.Id,
                pending.TargetKind,
                pending.ActionKey,
                category,
                pending.HandlerType,
                pending.IsUntyped,
                pending.Ordering,
                requested,
                versionRange,
                inputSchema,
                resultSchema,
                pending.SensitiveWildcardApprovalRequired || request?.Sensitive == true,
                pending.AcceptUnknownNonSensitiveSchemas)
            {
                ActionType = actionType,
                ResultType = resultType,
            });
        }

        return Array.AsReadOnly(compiled.ToArray());
    }

    private static IReadOnlyList<ModuleEventHook> CompileEventHooks(
        ModuleBuilderState state,
        ModuleManifest? manifest,
        ModuleCompilationOptions options,
        ICollection<GraphCompilationError> errors)
    {
        var compiled = new List<ModuleEventHook>();
        foreach (var pending in state.EventHooks)
        {
            ValidateEventHandler(state.Identity.Id, pending, errors);
            var target = Target(pending.TargetKind, pending.EventKey?.Value, pending.Category);
            var request = FindEventRequest(manifest, target, pending.Delivery);
            var manifestCapabilities = ParseEventEffects(request?.Effects, state.Identity.Id, target, errors);
            var requested = ResolveEventCapabilities(
                pending.RequestedCapabilities,
                manifestCapabilities,
                request,
                options.RequireManifestRequests,
                state.Identity.Id,
                target,
                pending.Kind,
                errors);
            var descriptor = FindEventDescriptor(state, options, pending);
            var category = pending.DescriptorCategory ?? descriptor?.Category ?? pending.Category;
            var versionRange = pending.VersionRange
                ?? request?.VersionRange
                ?? descriptor?.ProtocolVersionRange
                ?? ContractVersionRange.Exact(descriptor?.Version ?? 1);
            var payloadSchema = pending.PayloadSchema ?? descriptor?.PayloadSchema;

            if (pending.TargetKind == SidecarHookTargetKind.Exact
                && (descriptor is null || string.IsNullOrWhiteSpace(category) || payloadSchema is null))
            {
                errors.Add(Error(
                    ModuleGraphErrorCodes.InvalidTarget,
                    state.Identity.Id,
                    target,
                    "descriptor",
                    $"Exact event hook '{target}' requires a known typed descriptor."));
                continue;
            }

            payloadSchema ??= ModuleSchemaIdentity.UntypedEvent(target);
            ValidateEventCapabilities(state.Identity.Id, target, requested, descriptor, options, errors);
            compiled.Add(new ModuleEventHook(
                state.Identity.Id,
                pending.TargetKind,
                pending.EventKey,
                category,
                pending.HandlerType,
                pending.IsUntyped,
                pending.Kind,
                pending.Delivery,
                pending.Ordering,
                requested,
                versionRange,
                payloadSchema,
                pending.SensitiveWildcardApprovalRequired || request?.Sensitive == true,
                pending.AcceptUnknownNonSensitiveSchemas));
        }

        return Array.AsReadOnly(compiled.ToArray());
    }

    private static void ValidateActionHandler(
        string moduleId,
        PendingActionHook pending,
        ICollection<GraphCompilationError> errors)
    {
        var valid = pending.IsUntyped
            ? typeof(IAnyActionInterceptor).IsAssignableFrom(pending.HandlerType)
            : pending.HandlerType.GetInterfaces().Count(type =>
                type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IActionInterceptor<,>)) == 1;
        if (pending.TargetKind != SidecarHookTargetKind.Exact && !pending.IsUntyped)
            valid = false;

        if (!valid)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidHandler,
                moduleId,
                pending.Ordering.Id,
                "handler",
                $"Action hook '{pending.Ordering.Id}' does not implement the required typed or untyped interface."));
        }
    }

    private static (Type? ActionType, Type? ResultType) GetTypedActionTypes(
        PendingActionHook pending)
    {
        if (pending.IsUntyped)
            return (null, null);

        var contract = pending.HandlerType.GetInterfaces().FirstOrDefault(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IActionInterceptor<,>));
        if (contract is null)
            return (null, null);

        var arguments = contract.GetGenericArguments();
        return arguments.Length == 2
            ? (arguments[0], arguments[1])
            : (null, null);
    }

    private static void ValidateEventHandler(
        string moduleId,
        PendingEventHook pending,
        ICollection<GraphCompilationError> errors)
    {
        var genericInterface = pending.Kind == ModuleEventHookKind.Interceptor
            ? typeof(IEventInterceptor<>)
            : typeof(IEventListener<>);
        var untypedInterface = pending.Kind == ModuleEventHookKind.Interceptor
            ? typeof(IAnyEventInterceptor)
            : typeof(IAnyEventListener);
        var valid = pending.IsUntyped
            ? untypedInterface.IsAssignableFrom(pending.HandlerType)
            : pending.HandlerType.GetInterfaces().Count(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == genericInterface) == 1;
        if (pending.TargetKind != SidecarHookTargetKind.Exact && !pending.IsUntyped)
            valid = false;

        if (!valid)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidHandler,
                moduleId,
                pending.Ordering.Id,
                "handler",
                $"Event hook '{pending.Ordering.Id}' does not implement the required typed or untyped interface."));
        }
    }

    private static UntypedActionDescriptor? FindActionDescriptor(
        ModuleBuilderState state,
        ModuleCompilationOptions options,
        PendingActionHook pending)
    {
        if (pending.TargetKind != SidecarHookTargetKind.Exact || pending.ActionKey is null)
            return null;

        var own = state.Actions.FirstOrDefault(action => action.Descriptor.Key == pending.ActionKey)?.Descriptor;
        if (own is not null)
            return own;

        var host = options.HostActions.FirstOrDefault(action => action.ActionKey == pending.ActionKey);
        if (host is not null)
        {
            return new UntypedActionDescriptor(
                host.ActionKey,
                host.Version,
                host.Category,
                host.Capabilities,
                host.InputSchema,
                host.ResultSchema,
                host.ContainsSensitiveData)
            {
                ProtocolVersionRange = host.ProtocolVersionRange,
            };
        }

        if (pending.DescriptorVersion is not { } version
            || pending.DescriptorCapabilities is not { } capabilities
            || pending.DescriptorContainsSensitiveData is not { } containsSensitiveData
            || string.IsNullOrWhiteSpace(pending.DescriptorCategory)
            || pending.InputSchema is null
            || pending.ResultSchema is null)
        {
            return null;
        }

        return new UntypedActionDescriptor(
            pending.ActionKey.Value,
            version,
            pending.DescriptorCategory,
            capabilities,
            pending.InputSchema,
            pending.ResultSchema,
            containsSensitiveData)
        {
            ProtocolVersionRange = pending.VersionRange ?? ContractVersionRange.Exact(version),
        };
    }

    private static UntypedEventDescriptor? FindEventDescriptor(
        ModuleBuilderState state,
        ModuleCompilationOptions options,
        PendingEventHook pending)
    {
        if (pending.TargetKind != SidecarHookTargetKind.Exact || pending.EventKey is null)
            return null;

        var own = state.Events.FirstOrDefault(evt => evt.Descriptor.Key == pending.EventKey)?.Descriptor;
        if (own is not null)
            return own;

        var host = options.HostEvents.FirstOrDefault(evt => evt.EventKey == pending.EventKey);
        if (host is not null)
        {
            return new UntypedEventDescriptor(
                host.EventKey,
                host.Version,
                host.Category,
                host.Capabilities,
                host.PayloadSchema,
                host.ContainsSensitiveData)
            {
                ProtocolVersionRange = host.ProtocolVersionRange,
            };
        }

        if (pending.DescriptorVersion is not { } version
            || pending.DescriptorCapabilities is not { } capabilities
            || pending.DescriptorContainsSensitiveData is not { } containsSensitiveData
            || string.IsNullOrWhiteSpace(pending.DescriptorCategory)
            || pending.PayloadSchema is null)
        {
            return null;
        }

        return new UntypedEventDescriptor(
            pending.EventKey.Value,
            version,
            pending.DescriptorCategory,
            capabilities,
            pending.PayloadSchema,
            containsSensitiveData)
        {
            ProtocolVersionRange = pending.VersionRange ?? ContractVersionRange.Exact(version),
        };
    }

    private static void ValidateActionCapabilities(
        string moduleId,
        string target,
        ActionInterceptionCapabilities requested,
        UntypedActionDescriptor? descriptor,
        ModuleCompilationOptions options,
        ICollection<GraphCompilationError> errors)
    {
        var unsupported = requested & ~options.SupportedActionCapabilities;
        if (descriptor is not null)
            unsupported |= requested & ~descriptor.Capabilities;
        if (unsupported == 0)
            return;

        errors.Add(Error(
            ModuleGraphErrorCodes.UnsupportedEffect,
            moduleId,
            target,
            unsupported.ToString(),
            $"Action hook '{target}' requests unsupported effects '{unsupported}'."));
    }

    private static void ValidateEventCapabilities(
        string moduleId,
        string target,
        EventInterceptionCapabilities requested,
        UntypedEventDescriptor? descriptor,
        ModuleCompilationOptions options,
        ICollection<GraphCompilationError> errors)
    {
        var unsupported = requested & ~options.SupportedEventCapabilities;
        if (descriptor is not null)
            unsupported |= requested & ~descriptor.Capabilities;
        if (unsupported == 0)
            return;

        errors.Add(Error(
            ModuleGraphErrorCodes.UnsupportedEffect,
            moduleId,
            target,
            unsupported.ToString(),
            $"Event hook '{target}' requests unsupported effects '{unsupported}'."));
    }

    private static ActionInterceptionCapabilities ResolveActionCapabilities(
        ActionInterceptionCapabilities? declared,
        ActionInterceptionCapabilities manifest,
        ModuleManifestHookRequest? request,
        bool requireManifest,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        if (request is null && requireManifest)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.MissingManifestRequest,
                moduleId,
                target,
                "manifest",
                $"Action hook '{target}' has no matching module.json request."));
        }

        if (declared.HasValue && request is not null && declared.Value != manifest)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.ManifestEffectMismatch,
                moduleId,
                target,
                manifest.ToString(),
                $"Action hook '{target}' effects do not equal its module.json request."));
        }

        return declared ?? manifest;
    }

    private static EventInterceptionCapabilities ResolveEventCapabilities(
        EventInterceptionCapabilities? declared,
        EventInterceptionCapabilities manifest,
        ModuleManifestEventRequest? request,
        bool requireManifest,
        string moduleId,
        string target,
        ModuleEventHookKind kind,
        ICollection<GraphCompilationError> errors)
    {
        if (request is null && requireManifest)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.MissingManifestRequest,
                moduleId,
                target,
                "manifest",
                $"Event hook '{target}' has no matching module.json request."));
        }

        var fallback = kind == ModuleEventHookKind.Listener
            ? EventInterceptionCapabilities.Observe
            : manifest;
        if (request is not null
            && kind == ModuleEventHookKind.Listener
            && manifest == 0)
        {
            manifest = EventInterceptionCapabilities.Observe;
        }

        if (declared.HasValue && request is not null && declared.Value != manifest)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.ManifestEffectMismatch,
                moduleId,
                target,
                manifest.ToString(),
                $"Event hook '{target}' effects do not equal its module.json request."));
        }

        return declared ?? fallback;
    }

    private static ActionInterceptionCapabilities ParseActionEffects(
        IReadOnlyList<string>? effects,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        var result = (ActionInterceptionCapabilities)0;
        foreach (var effect in effects ?? [])
        {
            result |= NormalizeEffect(effect) switch
            {
                "inspect" => ActionInterceptionCapabilities.Inspect,
                "replaceinput" => ActionInterceptionCapabilities.ReplaceInput,
                "cancel" => ActionInterceptionCapabilities.Cancel,
                "replaceresult" => ActionInterceptionCapabilities.ReplaceResult,
                "defer" => ActionInterceptionCapabilities.Defer,
                "repeat" => ActionInterceptionCapabilities.Repeat,
                "wrap" => ActionInterceptionCapabilities.Wrap,
                "observe" => ActionInterceptionCapabilities.Observe,
                "publishevents" => ActionInterceptionCapabilities.PublishEvents,
                _ => UnknownActionEffect(effect, moduleId, target, errors),
            };
        }

        return result;
    }

    private static EventInterceptionCapabilities ParseEventEffects(
        IReadOnlyList<string>? effects,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        var result = (EventInterceptionCapabilities)0;
        foreach (var effect in effects ?? [])
        {
            result |= NormalizeEffect(effect) switch
            {
                "inspect" => EventInterceptionCapabilities.Inspect,
                "replace" => EventInterceptionCapabilities.Replace,
                "cancel" => EventInterceptionCapabilities.Cancel,
                "stoppropagation" => EventInterceptionCapabilities.StopPropagation,
                "observe" => EventInterceptionCapabilities.Observe,
                _ => UnknownEventEffect(effect, moduleId, target, errors),
            };
        }

        return result;
    }

    private static ActionInterceptionCapabilities UnknownActionEffect(
        string effect,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        errors.Add(Error(
            ModuleGraphErrorCodes.ManifestEffectMismatch,
            moduleId,
            target,
            effect,
            $"Action hook '{target}' requests unknown effect '{effect}'."));
        return 0;
    }

    private static EventInterceptionCapabilities UnknownEventEffect(
        string effect,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        errors.Add(Error(
            ModuleGraphErrorCodes.ManifestEffectMismatch,
            moduleId,
            target,
            effect,
            $"Event hook '{target}' requests unknown effect '{effect}'."));
        return 0;
    }

    private static ModuleManifestHookRequest? FindHookRequest(ModuleManifest? manifest, string target) =>
        manifest?.RequestedHooks?.SingleOrDefault(request =>
            string.Equals(request.Target, target, StringComparison.Ordinal));

    private static ModuleManifestEventRequest? FindEventRequest(
        ModuleManifest? manifest,
        string target,
        EventDelivery delivery) =>
        manifest?.RequestedEvents?.SingleOrDefault(request =>
            string.Equals(request.Target, target, StringComparison.Ordinal)
            && string.Equals(request.Delivery, delivery.ToString(), StringComparison.OrdinalIgnoreCase));

    private static string Target(SidecarHookTargetKind kind, string? key, string? category) =>
        kind switch
        {
            SidecarHookTargetKind.Exact => key ?? string.Empty,
            SidecarHookTargetKind.Category => $"{category}.*",
            SidecarHookTargetKind.Wildcard => "*",
            _ => string.Empty,
        };

    private static string NormalizeEffect(string effect) =>
        new string(effect.Where(character => character is not '-' and not '_').ToArray()).ToLowerInvariant();

    private static IReadOnlyList<T> OrderHooks<T>(
        IReadOnlyList<T> hooks,
        Func<T, HookOrdering> ordering,
        string moduleId,
        string target,
        ICollection<GraphCompilationError> errors)
    {
        var byId = hooks.GroupBy(hook => ordering(hook).Id, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in byId.Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.DuplicateHook,
                moduleId,
                duplicate.Key ?? string.Empty,
                target,
                "Each hook ordering identifier must be nonempty and unique."));
        }

        if (byId.Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
            return hooks;

        var nodes = hooks.ToDictionary(hook => ordering(hook).Id, StringComparer.Ordinal);
        var edges = nodes.Keys.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var indegree = nodes.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var hook in hooks)
        {
            var data = ordering(hook);
            AddOrderingEdges(data.Id, data.Before, before: true);
            AddOrderingEdges(data.Id, data.After, before: false);
        }

        var ready = new SortedSet<string>(Comparer<string>.Create((left, right) =>
        {
            var priority = ordering(nodes[left]).Priority.CompareTo(ordering(nodes[right]).Priority);
            return priority != 0 ? priority : StringComparer.Ordinal.Compare(left, right);
        }));
        foreach (var pair in indegree.Where(pair => pair.Value == 0))
            ready.Add(pair.Key);

        var ordered = new List<T>(hooks.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(nodes[id]);
            foreach (var next in edges[id].Order(StringComparer.Ordinal))
            {
                indegree[next]--;
                if (indegree[next] == 0)
                    ready.Add(next);
            }
        }

        if (ordered.Count != hooks.Count)
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.InvalidOrdering,
                moduleId,
                target,
                "cycle",
                $"The {target} hook ordering contains a cycle."));
            return hooks;
        }

        return Array.AsReadOnly(ordered.ToArray());

        void AddOrderingEdges(string source, IReadOnlyList<string>? references, bool before)
        {
            foreach (var reference in references ?? [])
            {
                if (!nodes.ContainsKey(reference))
                {
                    errors.Add(Error(
                        ModuleGraphErrorCodes.InvalidOrdering,
                        moduleId,
                        source,
                        reference,
                        $"Hook '{source}' references unknown hook '{reference}'."));
                    continue;
                }

                var from = before ? source : reference;
                var to = before ? reference : source;
                if (edges[from].Add(to))
                    indegree[to]++;
            }
        }
    }

    private static void ValidateUniqueSidecarTargets(
        ModuleBuilderState state,
        IReadOnlyList<ModuleActionHook> actionHooks,
        IReadOnlyList<ModuleEventHook> eventHooks,
        string moduleId,
        ICollection<GraphCompilationError> errors)
    {
        foreach (var duplicate in actionHooks.GroupBy(ActionSubscriptionKey, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.UnsupportedTransport,
                moduleId,
                duplicate.Key,
                "sidecar",
                $"The published sidecar discovery contract permits one action subscription for target '{duplicate.Key}'."));
        }

        foreach (var duplicate in eventHooks.GroupBy(EventSubscriptionKey, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(Error(
                ModuleGraphErrorCodes.UnsupportedTransport,
                moduleId,
                duplicate.Key,
                "sidecar",
                $"The published sidecar discovery contract permits one event subscription for target '{duplicate.Key}'."));
        }
    }

    private static string ActionSubscriptionKey(ModuleActionHook hook) =>
        Target(hook.TargetKind, hook.ActionKey?.Value, hook.Category);

    private static string EventSubscriptionKey(ModuleEventHook hook) =>
        Target(hook.TargetKind, hook.EventKey?.Value, hook.Category);

    private static string ComputeHash(
        ModuleIdentity identity,
        IReadOnlyList<ModuleContractContribution> contracts,
        IReadOnlyList<ModuleStorageContractDescriptor> storage,
        IReadOnlyList<ModuleActionDefinition> actions,
        IReadOnlyList<ModuleEventDefinition> events,
        IReadOnlyList<ModuleActionHook> actionHooks,
        IReadOnlyList<ModuleEventHook> eventHooks,
        IReadOnlyList<ModuleToolRegistration> tools,
        ModuleChatContributions chat,
        ModuleApplicationContributions application,
        IReadOnlyList<ModuleActionEntryRegistration> actionEntries,
        IReadOnlyList<ModuleFeatureDescriptor> features)
    {
        var records = new List<string>
        {
            $"module|{identity.Id}|{identity.DisplayName}|{identity.ToolPrefix}",
        };
        records.AddRange(contracts.OrderBy(value => value.ContractName, StringComparer.Ordinal)
            .Select(value => $"contract|{value.ContractName}|{value.SchemaVersion}|{value.ServiceType.AssemblyQualifiedName}|{value.MaxBytes}|{value.IsExport}|{value.Optional}"));
        records.AddRange(storage.OrderBy(value => value.StorageName, StringComparer.Ordinal)
            .Select(value => $"storage|{value.ModuleId}|{value.StorageName}|{value.MaxDocumentBytes}|{value.MaxBatchSize}"));
        records.AddRange(actions.OrderBy(value => value.Descriptor.Key.Value, StringComparer.Ordinal)
            .Select(value => $"action|{value.Descriptor.Key.Value}|{value.Descriptor.Version}|{value.Descriptor.Category}|{(int)value.Descriptor.Capabilities}|{value.ActionType.AssemblyQualifiedName}|{value.ResultType.AssemblyQualifiedName}|{value.Descriptor.InputSchema.ContentHash}|{value.Descriptor.ResultSchema.ContentHash}"));
        records.AddRange(events.OrderBy(value => value.Descriptor.Key.Value, StringComparer.Ordinal)
            .Select(value => $"event|{value.Descriptor.Key.Value}|{value.Descriptor.Version}|{value.Descriptor.Category}|{(int)value.Descriptor.Capabilities}|{value.EventType.AssemblyQualifiedName}|{value.Descriptor.PayloadSchema.ContentHash}"));
        records.AddRange(actionHooks.Select(value => $"action-hook|{value.HookId}|{ActionSubscriptionKey(value)}|{value.HandlerType.AssemblyQualifiedName}|{(int)value.RequestedCapabilities}|{value.IsUntyped}"));
        records.AddRange(eventHooks.Select(value => $"event-hook|{value.HookId}|{EventSubscriptionKey(value)}|{value.HandlerType.AssemblyQualifiedName}|{(int)value.RequestedCapabilities}|{value.IsUntyped}"));
        records.AddRange(tools.OrderBy(value => value.Descriptor.Name, StringComparer.Ordinal)
            .Select(value => $"tool|{value.Descriptor.Name}|{value.Descriptor.Version}|{value.HandlerType.AssemblyQualifiedName}|{value.InputSchema.ContentHash}|{value.ResultSchema.ContentHash}"));
        records.Add($"chat|{chat.ConversationResolver?.AssemblyQualifiedName}|{chat.ProfileResolver?.AssemblyQualifiedName}|{string.Join(',', chat.ContextContributors.Select(type => type.AssemblyQualifiedName))}");
        records.Add($"application|{string.Join(',', application.EndpointTypes.Select(type => type.AssemblyQualifiedName))}|{string.Join(',', application.CliCommands.Select(value => value.Descriptor.Name))}|{string.Join(',', application.UiContributionTypes.Select(type => type.AssemblyQualifiedName))}");
        records.AddRange(actionEntries.OrderBy(value => value.Descriptor.Key.Value, StringComparer.Ordinal)
            .Select(value => $"action-entry|{value.OwnerModuleId}|{value.Descriptor.Key.Value}|{value.Descriptor.Version}|{value.Descriptor.DescriptorHash}|{value.TerminalId:D}|{value.TerminalType.AssemblyQualifiedName}"));
        records.AddRange(features.OrderBy(value => value.ContractName, StringComparer.Ordinal)
            .Select(value => $"feature|{value.ContractName}|{value.SchemaVersion}|{value.OwnerModuleId}|{value.MaxBytes}|{value.Required}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', records))));
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static GraphCompilationError Error(
        string code,
        string moduleId,
        string target,
        string effect,
        string message) =>
        new(code, moduleId, target, effect, message);
}
