using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.ModuleSDK;

/// <summary>Identifies one neutral chat contribution exported by a sidecar.</summary>
public enum SidecarChatContributionKind
{
    /// <summary>Resolves the canonical conversation for one chat turn.</summary>
    ConversationResolver,

    /// <summary>Resolves the provider and model profile for one chat turn.</summary>
    ProfileResolver,

    /// <summary>Reads committed conversation history.</summary>
    HistoryLoad,

    /// <summary>Commits one completed conversation exchange.</summary>
    ExchangeCommit,

    /// <summary>Contributes bounded context to one chat turn.</summary>
    ContextContributor,
}

/// <summary>Describes one sidecar chat contribution and its action entry.</summary>
public sealed record SidecarChatContributionDefinition(
    SidecarChatContributionKind Kind,
    string RegistrationId,
    SidecarActionDescriptorIdentity Descriptor,
    Guid TerminalId);

/// <summary>Input for the neutral conversation resolver action entry.</summary>
public sealed record SidecarConversationResolveAction(ChatTurnInput Input);

/// <summary>Input for the neutral chat profile resolver action entry.</summary>
public sealed record SidecarProfileResolveAction(ChatTurnContext Turn);

/// <summary>Input for the neutral conversation history read action entry.</summary>
public sealed record SidecarHistoryLoadAction(Guid ConversationId);

/// <summary>Input for the neutral chat context contributor action entry.</summary>
public sealed record SidecarContextContributeAction(ChatContextRequest Request);

/// <summary>Input for the neutral conversation commit action entry.</summary>
public sealed record SidecarExchangeCommitAction(ChatExchange Exchange);

/// <summary>Defines the exact neutral action entries used for sidecar chat.</summary>
public static class SidecarChatActionDescriptors
{
    /// <summary>Gets the stable conversation resolver terminal identity.</summary>
    public static readonly Guid ConversationResolverTerminalId =
        Guid.Parse("305f55f7-1088-44b2-a0df-380e9477df6e");

    /// <summary>Gets the stable profile resolver terminal identity.</summary>
    public static readonly Guid ProfileResolverTerminalId =
        Guid.Parse("6add8b5b-66ab-419b-90e5-786bb253fc28");

    /// <summary>Gets the stable history read terminal identity.</summary>
    public static readonly Guid HistoryLoadTerminalId =
        Guid.Parse("07a2840c-2285-456c-960b-00cd3f91f1af");

    /// <summary>Gets the stable context contributor terminal identity.</summary>
    public static readonly Guid ContextContributorTerminalId =
        Guid.Parse("7eff72c5-b042-49f1-96a4-d4b83d548111");

    /// <summary>Gets the stable exchange commit terminal identity.</summary>
    public static readonly Guid ExchangeCommitTerminalId =
        Guid.Parse("9bedbc9a-b22f-49d2-99b1-e1763874490f");

    /// <summary>Gets the neutral conversation resolver descriptor.</summary>
    public static ActionDescriptor<SidecarConversationResolveAction, ConversationSelection>
        ConversationResolver { get; } = Create<SidecarConversationResolveAction, ConversationSelection>(
            "sidecar.chat.conversation.resolve",
            "sidecar.chat.conversation",
            hasIrreversibleEffects: true);

    /// <summary>Gets the neutral profile resolver descriptor.</summary>
    public static ActionDescriptor<SidecarProfileResolveAction, ChatProfile>
        ProfileResolver { get; } = Create<SidecarProfileResolveAction, ChatProfile>(
            "sidecar.chat.profile.resolve",
            "sidecar.chat.profile",
            hasIrreversibleEffects: false);

    /// <summary>Gets the neutral history read descriptor.</summary>
    public static ActionDescriptor<SidecarHistoryLoadAction, IReadOnlyList<ChatCompletionMessage>>
        HistoryLoad { get; } = Create<SidecarHistoryLoadAction, IReadOnlyList<ChatCompletionMessage>>(
            "sidecar.chat.history.load",
            "sidecar.chat.history",
            hasIrreversibleEffects: false);

    /// <summary>Gets the neutral context contributor descriptor.</summary>
    public static ActionDescriptor<SidecarContextContributeAction, ChatContextContribution>
        ContextContributor { get; } = Create<SidecarContextContributeAction, ChatContextContribution>(
            "sidecar.chat.context.contribute",
            "sidecar.chat.context",
            hasIrreversibleEffects: false);

    /// <summary>Gets the neutral exchange commit descriptor.</summary>
    public static ActionDescriptor<SidecarExchangeCommitAction, bool>
        ExchangeCommit { get; } = Create<SidecarExchangeCommitAction, bool>(
            "sidecar.chat.exchange.commit",
            "sidecar.chat.history",
            hasIrreversibleEffects: true);

    private static ActionDescriptor<TAction, TResult> Create<TAction, TResult>(
        string key,
        string category,
        bool hasIrreversibleEffects)
    {
        var actionKey = new SharpClawActionKey(key);
        return new ActionDescriptor<TAction, TResult>(
            actionKey,
            1,
            category,
            ActionInterceptionCapabilities.Inspect |
            ActionInterceptionCapabilities.Cancel |
            ActionInterceptionCapabilities.Observe,
            ContainsSensitiveData: true,
            hasIrreversibleEffects,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, key),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(30))
        {
            SafePoints = [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(actionKey, 1, typeof(TAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(actionKey, 1, typeof(TResult)),
        };
    }
}

internal static class SidecarChatActionEntryFactory
{
    public static void Add(SharpClawModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var state = builder.State;
        if (state.ConversationResolvers.Count == 1)
        {
            Add(
                state,
                SidecarChatActionDescriptors.ConversationResolver,
                SidecarChatActionDescriptors.ConversationResolverTerminalId,
                typeof(IConversationResolver),
                async (services, action, context, ct) =>
                {
                    var resolver = (IConversationResolver)services.GetRequiredService(
                        state.ConversationResolvers[0]);
                    var effective = action.Input with
                    {
                        Caller = context.Caller,
                        Features = context.Features,
                    };
                    return await resolver.ResolveAsync(effective, context, ct);
                });
        }

        if (state.ProfileResolvers.Count == 1)
        {
            Add(
                state,
                SidecarChatActionDescriptors.ProfileResolver,
                SidecarChatActionDescriptors.ProfileResolverTerminalId,
                typeof(IChatProfileResolver),
                async (services, action, context, ct) =>
                {
                    var resolver = (IChatProfileResolver)services.GetRequiredService(
                        state.ProfileResolvers[0]);
                    return await resolver.ResolveAsync(
                        Normalize(action.Turn, context),
                        context,
                        ct);
                });
        }

        if (state.Services.Any(item => item.ServiceType == typeof(IConversationStore)))
        {
            Add(
                state,
                SidecarChatActionDescriptors.HistoryLoad,
                SidecarChatActionDescriptors.HistoryLoadTerminalId,
                typeof(IConversationStore),
                async (services, action, context, ct) =>
                {
                    var store = services.GetRequiredService<IConversationStore>();
                    return await store.LoadHistoryAsync(action.ConversationId, context, ct);
                });
            Add(
                state,
                SidecarChatActionDescriptors.ExchangeCommit,
                SidecarChatActionDescriptors.ExchangeCommitTerminalId,
                typeof(IConversationStore),
                async (services, action, context, ct) =>
                {
                    var store = services.GetRequiredService<IConversationStore>();
                    await store.CommitExchangeAsync(
                        action.Exchange with { Turn = Normalize(action.Exchange.Turn, context) },
                        context,
                        ct);
                    return true;
                });
        }

        if (state.ContextContributors.Count > 0)
        {
            Add(
                state,
                SidecarChatActionDescriptors.ContextContributor,
                SidecarChatActionDescriptors.ContextContributorTerminalId,
                typeof(IChatContextContributor),
                async (services, action, context, ct) =>
                {
                    var request = action.Request with
                    {
                        Turn = action.Request.Turn is null
                            ? null
                            : Normalize(action.Request.Turn, context),
                    };
                    var segments = new List<SystemPromptSegment>();
                    var messages = new List<ChatCompletionMessage>();
                    var features = new List<ExtensionFeature>();
                    foreach (var contributorType in state.ContextContributors)
                    {
                        var contributor = (IChatContextContributor)services.GetRequiredService(
                            contributorType);
                        var contribution = await contributor.ContributeAsync(request, context, ct);
                        segments.AddRange(contribution.SystemPromptSegments);
                        messages.AddRange(contribution.Messages);
                        features.AddRange(contribution.Features);
                    }
                    return new ChatContextContribution(segments, messages, features);
                });
        }
    }

    private static ChatTurnContext Normalize(
        ChatTurnContext turn,
        ChatOperationContext context) =>
        turn with
        {
            Input = turn.Input with
            {
                Caller = context.Caller,
                Features = context.Features,
            },
        };

    private static void Add<TAction, TResult>(
        ModuleBuilderState state,
        ActionDescriptor<TAction, TResult> descriptor,
        Guid terminalId,
        Type terminalType,
        Func<IServiceProvider, TAction, ChatOperationContext, CancellationToken, ValueTask<TResult>> terminal)
    {
        var actionBuilder = new ModuleActionDefinitionBuilder(state);
        actionBuilder.Add(descriptor);
        var identity = Identity(descriptor);
        state.ActionEntries.Add(new ModuleActionEntryRegistration(
            state.Identity.Id,
            identity,
            typeof(TAction),
            typeof(TResult),
            terminalType,
            terminalId,
            new SidecarChatActionEntryInvoker<TAction, TResult>(
                identity,
                terminalId,
                terminal)));
    }

    private static SidecarActionDescriptorIdentity Identity<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor)
    {
        var input = descriptor.InputSchema
            ?? throw new InvalidOperationException("A sidecar chat action requires an input schema.");
        var result = descriptor.ResultSchema
            ?? throw new InvalidOperationException("A sidecar chat action requires a result schema.");
        return new SidecarActionDescriptorIdentity(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            TypeIdentity(typeof(TAction)),
            input.ContentHash
                ?? throw new InvalidOperationException("A sidecar chat input schema requires a hash."),
            input.Version,
            TypeIdentity(typeof(TResult)),
            result.ContentHash
                ?? throw new InvalidOperationException("A sidecar chat result schema requires a hash."),
            result.Version,
            HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor));
    }

    private static string TypeIdentity(Type type) =>
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
}

internal sealed class SidecarChatActionEntryInvoker<TAction, TResult>(
    SidecarActionDescriptorIdentity descriptor,
    Guid terminalId,
    Func<IServiceProvider, TAction, ChatOperationContext, CancellationToken, ValueTask<TResult>> terminal) :
    IModuleActionEntryInvoker
{
    public SidecarActionDescriptorIdentity Descriptor { get; } = descriptor;

    public Guid TerminalId { get; } = terminalId;

    public async ValueTask<SidecarTerminalExecutionResult> InvokeAsync(
        IServiceProvider services,
        SidecarActionTerminalExecutionContext context,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        if (context.Descriptor != Descriptor || !context.IsWellFormed)
            throw new InvalidOperationException("The sidecar chat terminal context is invalid.");

        var action = JsonSerializer.Deserialize<TAction>(
                context.EffectiveAction.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new InvalidOperationException("The sidecar chat action has no payload.");
        var operationContext = new ChatOperationContext(
            context.InvocationId,
            context.ParentInvocationId,
            context.TraceId,
            context.IdempotencyKey,
            context.Depth,
            context.Attempt,
            context.Deadline,
            context.Caller,
            context.Features,
            hostActionEntry);
        var result = await terminal(services, action, operationContext, cancellationToken);
        var bytes = SidecarCapabilityTransportCodec.Serialize(result);
        using var document = JsonDocument.Parse(bytes);
        var canonical = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        return new SidecarTerminalExecutionResult(
            new SidecarSerializedPayload(
                Descriptor.ResultTypeIdentity,
                Descriptor.ResultSchemaVersion,
                SidecarCapabilityTransportCodec.ComputeSha256(canonical),
                document.RootElement.Clone(),
                canonical.Length),
            null!,
            Completed: true);
    }
}
