using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Binds one authorized sidecar to the neutral module graph.</summary>
public sealed class OutOfProcessModuleProxy :
    ISharpClawModule,
    IAuthorizedExternalModule,
    IAsyncDisposable
{
    private const string HostModuleId = "sharpclaw.runtime.host";
    private readonly OutOfProcessModuleClient _client;
    private readonly SidecarHostAuthorization _authorization;
    private int _started;

    public OutOfProcessModuleProxy(
        ModuleIdentity identity,
        OutOfProcessModuleClient client)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (!string.Equals(identity.Id, client.Discovery.ModuleId, StringComparison.Ordinal))
            throw new ArgumentException("The module identity does not match sidecar discovery.", nameof(identity));
        if (string.Equals(identity.Id, HostModuleId, StringComparison.Ordinal))
            throw new ArgumentException("The module identity conflicts with the host identity.", nameof(identity));
        _authorization = CreateAuthorization(client);
    }

    public ModuleIdentity Identity { get; }

    public SidecarHostAuthorization Authorization => _authorization;

    public SidecarDiscoveryEnvelope Discovery => _client.Discovery;

    public OutOfProcessModuleClient Client => _client;

    private static SidecarHostAuthorization CreateAuthorization(OutOfProcessModuleClient client)
    {
        var grants = client.Authorization.ActionGrants.ToList();
        foreach (var entry in client.Application.ActionEntries)
        {
            var definitions = client.Discovery.ActionDefinitions.Where(item =>
                item.ActionKey == entry.Descriptor.Key
                && item.Version == entry.Descriptor.Version).ToArray();
            if (definitions.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Application action '{entry.Descriptor.Key.Value}' has no unique discovered definition.");
            }

            var definition = definitions[0];
            var grant = new ActionCapabilityGrant(
                definition.ActionKey,
                definition.Version,
                definition.Capabilities,
                SensitiveApproved: definition.ContainsSensitiveData,
                AcceptUnknownSchemas: false);
            var existing = grants.Where(item =>
                item.ActionKey == grant.ActionKey
                && item.ActionVersion == grant.ActionVersion).ToArray();
            if (existing.Any(item => item != grant))
            {
                throw new InvalidOperationException(
                    $"Application action '{entry.Descriptor.Key.Value}' conflicts with sidecar authorization.");
            }
            if (existing.Length == 0)
                grants.Add(grant);
        }

        return client.Authorization with
        {
            ActionGrants = Array.AsReadOnly(grants
                .OrderBy(item => item.ActionKey.Value, StringComparer.Ordinal)
                .ThenBy(item => item.ActionVersion)
                .ToArray()),
        };
    }

    public void Configure(ISharpClawModuleBuilder module)
    {
        ArgumentNullException.ThrowIfNull(module);
        foreach (var storage in _client.StorageContracts)
            module.Storage.Add(storage);

        if (module is not IBoundModuleContributionBuilder bound)
            return;

        foreach (var subscription in _client.Discovery.Actions.Where(item =>
                     item.PayloadMode == SidecarPayloadMode.Untyped))
        {
            bound.AddActionHook(
                subscription,
                new ActionInterceptor(_client, subscription),
                subscription.Ordering.Id);
        }

        foreach (var subscription in _client.Discovery.Events.Where(item =>
                     item.PayloadMode == SidecarPayloadMode.Untyped))
        {
            if (subscription.Kind == SidecarEventSubscriptionKind.Interceptor)
            {
                bound.AddEventInterceptor(
                    subscription,
                    new EventInterceptor(_client, subscription),
                    subscription.Ordering.Id);
            }
            else
            {
                bound.AddEventListener(
                    subscription,
                    new EventListener(_client, subscription),
                    subscription.Ordering.Id);
            }
        }

        foreach (var definition in _client.Discovery.ToolHandlers)
        {
            bound.AddTool(
                new ToolDescriptor(
                    definition.ToolName,
                    definition.Description,
                    definition.ParametersSchema.Clone(),
                    definition.Version,
                    definition.ContainsSensitiveData),
                new ToolHandler(_client, definition),
                definition.HandlerId);
        }

        AddChatContributions(bound);
    }

    public async ValueTask StartAsync(ModuleStartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Equals(context.Identity, Identity))
            throw new InvalidOperationException("The module start context does not match sidecar discovery.");
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ContractHash);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        try
        {
            var sidecarContext = context with
            {
                ContractHash = _client.Discovery.ContractHash,
            };
            await InvokeLifecycleAsync(
                SidecarLifecycleCallKind.Start,
                JsonSerializer.SerializeToElement(sidecarContext, OutOfProcessProtocolCodec.JsonOptions),
                ct);
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;
        await InvokeLifecycleAsync(SidecarLifecycleCallKind.Stop, null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            await _client.DisposeAsync();
        }
    }

    private void AddChatContributions(IBoundModuleContributionBuilder bound)
    {
        var chat = _client.Application.Chat;
        foreach (var contribution in chat)
        {
            var handlerId = $"{Identity.Id}:chat:{contribution.Kind}:{contribution.RegistrationId}";
            switch (contribution.Kind)
            {
                case SidecarChatContributionKind.ConversationResolver:
                    bound.UseConversationResolver(
                        new ConversationResolver(_client),
                        new ExclusiveRegistration(contribution.RegistrationId),
                        handlerId);
                    break;
                case SidecarChatContributionKind.ProfileResolver:
                    bound.UseChatProfileResolver(
                        new ProfileResolver(_client),
                        new ExclusiveRegistration(contribution.RegistrationId),
                        handlerId);
                    break;
                case SidecarChatContributionKind.HistoryLoad:
                    bound.UseConversationStore(
                        new ConversationStore(_client),
                        handlerId);
                    break;
                case SidecarChatContributionKind.ExchangeCommit:
                    break;
                case SidecarChatContributionKind.ContextContributor:
                    bound.AddContextContributor(
                        new ContextContributor(_client),
                        handlerId);
                    break;
                default:
                    throw new InvalidOperationException("The sidecar chat contribution is not supported.");
            }
        }
    }

    private async ValueTask InvokeLifecycleAsync(
        SidecarLifecycleCallKind call,
        JsonElement? input,
        CancellationToken ct)
    {
        var definition = _client.Discovery.LifecycleHandlers.Single(item => item.Call == call);
        var start = SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessModuleHostProtocol.Version,
            sequence: 1,
            DateTimeOffset.UtcNow.Add(definition.Deadline),
            _client.HostLimits.ActionInputBytes,
            header => new SidecarLifecycleHandlerInvokeStart(
                header,
                Guid.NewGuid(),
                call,
                definition.HandlerId,
                input));
        await _client.InvokeLifecycleAsync(start, ct);
    }

    private sealed class ActionInterceptor(
        OutOfProcessModuleClient client,
        SidecarActionSubscription subscription) : IAnyActionInterceptor
    {
        public async ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(control);
            var grant = RequireActionGrant(client.Authorization, context.Descriptor);
            var deadline = Earlier(context.Deadline, DateTimeOffset.UtcNow.AddMinutes(1));
            var handle = new ContinuationHandle(
                Guid.NewGuid(),
                context.InvocationId,
                subscription.Ordering.Id,
                deadline,
                1);
            var start = SidecarMessageHeaderFactory.CreateMeasured(
                OutOfProcessModuleHostProtocol.Version,
                sequence: 1,
                deadline,
                client.HostLimits.ActionInputBytes,
                header => new HookInvokeStart(
                    header,
                    context.InvocationId,
                    context.ParentInvocationId,
                    context.TraceId,
                    subscription.Ordering.Id,
                    context.Descriptor.Key,
                    context.Descriptor.Version,
                    SidecarPayloadMode.Untyped,
                    context.Input.Clone(),
                    context.Descriptor,
                    grant,
                    context.Caller,
                    context.Features,
                    handle));

            IUntypedActionOutcome? continued = null;
            var result = await client.InvokeActionAsync(
                start,
                async (request, token) =>
                {
                    continued = await ContinueAsync(request, control, token);
                    return CreateContinuation(request, continued, client.HostLimits);
                },
                ct);
            if (continued is not null)
                return continued;

            var completion = result.Completion;
            return completion.Kind switch
            {
                ActionOutcomeKind.Completed when completion.Result is JsonElement replacement =>
                    control.ReplaceResult(replacement.Clone(), "The sidecar replaced the action result."),
                ActionOutcomeKind.Cancelled => control.Cancel(
                    completion.Error?.Code ?? SidecarCapabilityErrors.Cancelled,
                    completion.Error?.Message ?? "The sidecar cancelled the action."),
                ActionOutcomeKind.Failed => control.Fail(
                    completion.Error ?? new ExecutionError(
                        SidecarCapabilityErrors.HostFailure,
                        "The sidecar action hook failed.")),
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar action hook completed without a valid host outcome."),
            };
        }

        private static async ValueTask<IUntypedActionOutcome> ContinueAsync(
            SidecarEffectRequest request,
            IUntypedActionControl control,
            CancellationToken ct) => request.Command switch
            {
                SidecarContinuationCommand.ContinueOriginal => await control.ProceedAsync(ct),
                SidecarContinuationCommand.ContinueReplacement when request.Value is JsonElement replacement =>
                    await control.ProceedWithInputAsync(
                        replacement.Clone(),
                        request.Reason ?? "The sidecar replaced the action input.",
                        ct),
                SidecarContinuationCommand.Cancel => control.Cancel(
                    request.Code ?? SidecarCapabilityErrors.Cancelled,
                    request.Message ?? "The sidecar cancelled the action."),
                SidecarContinuationCommand.Defer when request.Defer is not null =>
                    await control.DeferAsync(request.Defer, ct),
                SidecarContinuationCommand.Repeat when request.Value is JsonElement repeat =>
                    await control.RepeatAsync(
                        repeat.Clone(),
                        request.Reason ?? "The sidecar requested an action repeat.",
                        request.Backoff,
                        ct),
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar requested an invalid action continuation."),
            };

        private static (ContinuationAccepted Accepted, ContinuationOutcome Outcome) CreateContinuation(
            SidecarEffectRequest request,
            IUntypedActionOutcome outcome,
            SidecarPayloadLimits limits)
        {
            var accepted = SidecarMessageHeaderFactory.CreateMeasured(
                request.Header.ProtocolVersion,
                request.Header.Sequence + 1,
                request.Header.Deadline,
                limits.ProtocolMessageBytes,
                header => new ContinuationAccepted(
                    header,
                    request.ContinuationHandleId,
                    request.Command,
                    ActionSafePoint.BeforeContinuation,
                    ContinuationState.Claimed));
            var completed = SidecarMessageHeaderFactory.CreateMeasured(
                request.Header.ProtocolVersion,
                request.Header.Sequence + 2,
                request.Header.Deadline,
                limits.ActionResultBytes,
                header => new ContinuationOutcome(
                    header,
                    request.ContinuationHandleId,
                    outcome.Kind,
                    outcome.Uncertainty is null
                        ? ActionOutcomeCertainty.Certain
                        : ActionOutcomeCertainty.Uncertain,
                    ActionSafePoint.BeforeTerminal,
                    outcome.Result?.Clone(),
                    outcome.Error,
                    outcome.Uncertainty,
                    outcome.Continuation));
            return (accepted, completed);
        }
    }

    private sealed class EventInterceptor(
        OutOfProcessModuleClient client,
        SidecarEventSubscription subscription) : IAnyEventInterceptor
    {
        public async ValueTask<IUntypedEventInterception> InterceptAsync(
            UntypedEventContext context,
            IUntypedEventControl control,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(control);
            var grant = RequireEventGrant(client.Authorization, context.Descriptor);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
            var start = SidecarMessageHeaderFactory.CreateMeasured(
                OutOfProcessModuleHostProtocol.Version,
                sequence: 1,
                deadline,
                client.HostLimits.EventPayloadBytes,
                header => new EventInterceptStart(
                    header,
                    subscription.Ordering.Id,
                    context.Envelope,
                    grant,
                    new ContinuationHandle(
                        Guid.NewGuid(),
                        context.Envelope.EventId,
                        subscription.Ordering.Id,
                        deadline,
                        1)));
            var outcome = await client.InterceptEventAsync(start, ct);
            return outcome.Kind switch
            {
                EventInterceptionKind.Continued => control.Continue(),
                EventInterceptionKind.Replaced when outcome.Payload is JsonElement replacement =>
                    control.Replace(
                        replacement.Clone(),
                        outcome.Reason ?? "The sidecar replaced the event payload."),
                EventInterceptionKind.Cancelled => control.Cancel(
                    outcome.Error?.Code ?? SidecarCapabilityErrors.Cancelled,
                    outcome.Error?.Message ?? "The sidecar cancelled the event."),
                EventInterceptionKind.PropagationStopped => control.StopPropagation(),
                EventInterceptionKind.Failed => throw new OutOfProcessProtocolException(
                    outcome.Error?.Code ?? SidecarCapabilityErrors.HostFailure,
                    outcome.Error?.Message ?? "The sidecar event interceptor failed."),
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar event interceptor returned an invalid outcome."),
            };
        }
    }

    private sealed class EventListener(
        OutOfProcessModuleClient client,
        SidecarEventSubscription subscription) : IAnyEventListener
    {
        public async ValueTask OnEventAsync(UntypedEventEnvelope evt, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(evt);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
            var delivery = SidecarMessageHeaderFactory.CreateMeasured(
                OutOfProcessModuleHostProtocol.Version,
                sequence: 1,
                deadline,
                client.HostLimits.EventPayloadBytes,
                header => new SidecarEventListenerDelivery(
                    header,
                    Guid.NewGuid(),
                    subscription.Ordering.Id,
                    evt,
                    subscription.Delivery,
                    RequiresAcknowledgement: true));
            var acknowledgement = await client.DeliverEventAsync(delivery, ct);
            if (acknowledgement is null || !acknowledgement.Accepted)
            {
                throw new OutOfProcessProtocolException(
                    acknowledgement?.Error?.Code ?? SidecarCapabilityErrors.HostFailure,
                    acknowledgement?.Error?.Message ?? "The sidecar did not acknowledge the event.");
            }
        }
    }

    private sealed class ToolHandler(
        OutOfProcessModuleClient client,
        SidecarToolHandlerDefinition definition) : IToolHandler
    {
        public async ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ct.ThrowIfCancellationRequested();
            if (!invocation.IsWellFormed(DateTimeOffset.UtcNow)
                || !string.Equals(invocation.ToolName, definition.ToolName, StringComparison.Ordinal))
            {
                throw new OutOfProcessProtocolException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The Tool invocation does not match the sidecar handler.");
            }

            var carrier = client.IssueToolCarrier(invocation.HostActionContext);
            var start = SidecarMessageHeaderFactory.CreateMeasured(
                OutOfProcessModuleHostProtocol.Version,
                sequence: 1,
                carrier.Deadline,
                client.HostLimits.ActionInputBytes,
                header => new SidecarToolHandlerInvokeStart(
                    header,
                    invocation.InvocationId,
                    invocation.ToolName,
                    definition.HandlerId,
                    invocation.Arguments.Clone(),
                    definition.InputSchema,
                    invocation.Caller,
                    carrier,
                    invocation.ConversationId));
            var result = await client.InvokeToolAsync(start, ct);
            return JsonSerializer.Deserialize<ToolResult>(
                    result.Result.GetRawText(),
                    OutOfProcessProtocolCodec.JsonOptions)
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.MalformedMessage,
                    "The sidecar returned no Tool result.");
        }
    }

    private sealed class ConversationResolver(OutOfProcessModuleClient client) : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken ct) => RunChatAsync(
                client,
                SidecarChatActionDescriptors.ConversationResolver,
                new SidecarConversationResolveAction(input),
                context,
                ct);
    }

    private sealed class ProfileResolver(OutOfProcessModuleClient client) : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            ChatOperationContext context,
            CancellationToken ct) => RunChatAsync(
                client,
                SidecarChatActionDescriptors.ProfileResolver,
                new SidecarProfileResolveAction(turn),
                context,
                ct);
    }

    private sealed class ConversationStore(OutOfProcessModuleClient client) : IConversationStore
    {
        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            ChatOperationContext context,
            CancellationToken ct) => RunChatAsync(
                client,
                SidecarChatActionDescriptors.HistoryLoad,
                new SidecarHistoryLoadAction(conversationId),
                context,
                ct);

        public async ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            ChatOperationContext context,
            CancellationToken ct)
        {
            var committed = await RunChatAsync(
                client,
                SidecarChatActionDescriptors.ExchangeCommit,
                new SidecarExchangeCommitAction(exchange),
                context,
                ct);
            if (!committed)
                throw new OutOfProcessProtocolException(
                    SidecarCapabilityErrors.HostFailure,
                    "The sidecar did not commit the chat exchange.");
        }
    }

    private sealed class ContextContributor(OutOfProcessModuleClient client) : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken ct) => RunChatAsync(
                client,
                SidecarChatActionDescriptors.ContextContributor,
                new SidecarContextContributeAction(request),
                context,
                ct);
    }

    private static async ValueTask<TResult> RunChatAsync<TAction, TResult>(
        OutOfProcessModuleClient client,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        ChatOperationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var invocationId = Guid.NewGuid();
        var authority = client.IssueHostActionContext(
            HostActionEntryIngress.CrossModule,
            HostModuleId,
            client.Discovery.ModuleId,
            descriptor,
            action,
            context.Caller,
            context.Features,
            context.TraceId,
            context.IdempotencyKey,
            context.Deadline,
            invocationId,
            context.InvocationId,
            context.Depth + 1,
            context.Attempt);
        var outcome = await client.InvokeModuleActionEntryAsync(
            descriptor,
            action,
            authority,
            ct);
        if (outcome.Kind == ActionOutcomeKind.Completed && outcome.Result is not null)
            return outcome.Result;
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
            throw new OperationCanceledException(
                outcome.Error?.Message ?? "The sidecar cancelled the chat operation.");
        throw new OutOfProcessProtocolException(
            outcome.Error?.Code ?? SidecarCapabilityErrors.HostFailure,
            outcome.Error?.Message ?? "The sidecar chat operation failed.");
    }

    private static ActionCapabilityGrant RequireActionGrant(
        SidecarHostAuthorization authorization,
        UntypedActionDescriptor descriptor) => authorization.ActionGrants.Single(grant =>
            grant.ActionKey == descriptor.Key
            && grant.ActionVersion == descriptor.Version);

    private static EventCapabilityGrant RequireEventGrant(
        SidecarHostAuthorization authorization,
        UntypedEventDescriptor descriptor) => authorization.EventGrants.Single(grant =>
            grant.EventKey == descriptor.Key
            && grant.EventVersion == descriptor.Version);

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
