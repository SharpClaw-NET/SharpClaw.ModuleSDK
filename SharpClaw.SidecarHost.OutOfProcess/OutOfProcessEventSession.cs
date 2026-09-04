using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed record SidecarEventCompletion(
    EventInterceptionKind Kind,
    JsonElement? Payload = null,
    ExecutionError? Error = null,
    string? Reason = null);

internal interface ISidecarEventOutcomeCarrier
{
    SidecarEventCompletion Completion { get; }
}

internal sealed class SidecarEventInterception<TEvent>(
    SidecarEventCompletion completion,
    TEvent? payload)
    : IEventInterception<TEvent>, ISidecarEventOutcomeCarrier
{
    public SidecarEventCompletion Completion { get; } = completion;

    public EventInterceptionKind Kind => Completion.Kind;

    public TEvent? Payload { get; } = payload;

    public ExecutionError? Error => Completion.Error;
}

internal sealed class SidecarUntypedEventInterception(SidecarEventCompletion completion)
    : IUntypedEventInterception, ISidecarEventOutcomeCarrier
{
    public SidecarEventCompletion Completion { get; } = completion;

    public EventInterceptionKind Kind => Completion.Kind;

    public JsonElement? Payload => Completion.Payload;

    public ExecutionError? Error => Completion.Error;
}

internal sealed class SidecarEventControl<TEvent>(JsonSerializerOptions payloadJsonOptions)
    : IEventControl<TEvent>
{
    public IEventInterception<TEvent> Continue() =>
        Create(new SidecarEventCompletion(EventInterceptionKind.Continued), default);

    public IEventInterception<TEvent> Replace(TEvent payload, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Create(
            new SidecarEventCompletion(
                EventInterceptionKind.Replaced,
                JsonSerializer.SerializeToElement(payload, payloadJsonOptions),
                Reason: reason),
            payload);
    }

    public IEventInterception<TEvent> Cancel(string code, string message) =>
        Create(
            new SidecarEventCompletion(
                EventInterceptionKind.Cancelled,
                Error: RequireError(code, message)),
            default);

    public IEventInterception<TEvent> StopPropagation() =>
        Create(new SidecarEventCompletion(EventInterceptionKind.PropagationStopped), default);

    private static SidecarEventInterception<TEvent> Create(
        SidecarEventCompletion completion,
        TEvent? payload) => new(completion, payload);

    private static ExecutionError RequireError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ExecutionError(code, message);
    }
}

internal sealed class SidecarUntypedEventControl : IUntypedEventControl
{
    public IUntypedEventInterception Continue() =>
        new SidecarUntypedEventInterception(
            new SidecarEventCompletion(EventInterceptionKind.Continued));

    public IUntypedEventInterception Replace(JsonElement payload, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SidecarUntypedEventInterception(
            new SidecarEventCompletion(EventInterceptionKind.Replaced, payload, Reason: reason));
    }

    public IUntypedEventInterception Cancel(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new SidecarUntypedEventInterception(
            new SidecarEventCompletion(
                EventInterceptionKind.Cancelled,
                Error: new ExecutionError(code, message)));
    }

    public IUntypedEventInterception StopPropagation() =>
        new SidecarUntypedEventInterception(
            new SidecarEventCompletion(EventInterceptionKind.PropagationStopped));
}

internal static class OutOfProcessEventSession
{
    public static async Task RunInterceptorAsync(
        OutOfProcessModuleRuntime runtime,
        OutOfProcessProtocolSession protocol,
        EventInterceptStart start,
        SidecarHostAuthorization authorization,
        CancellationToken ct)
    {
        var descriptor = start.Envelope.Descriptor;
        var hook = runtime.Graph.EventDispatch.SelectInterceptors(descriptor)
            .SingleOrDefault(candidate => string.Equals(
                candidate.HookId,
                start.HookId,
                StringComparison.Ordinal))
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"Event hook '{start.HookId}' is not selected for '{descriptor.Key.Value}'.");
        RequireGrant(hook, descriptor, start.Grant, authorization);

        SidecarEventCompletion completion;
        try
        {
            completion = hook.IsUntyped
                ? await InvokeUntypedInterceptorAsync(runtime, hook, start, ct)
                : await InvokeTypedInterceptorAsync(runtime, hook, start, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutOfProcessProtocolException)
        {
            throw;
        }
        catch when (hook.RequestedCapabilities.HasFlag(EventInterceptionCapabilities.Inspect))
        {
            completion = new SidecarEventCompletion(
                EventInterceptionKind.Failed,
                Error: new ExecutionError(
                    "module_handler_failed",
                    "The module event interceptor failed."));
        }

        var outcome = protocol.Create(
            SidecarProtocolMessageKind.EventInterceptOutcome,
            header => new EventInterceptOutcome(
                header,
                protocol.State.ContinuationHandleId,
                descriptor.Key,
                descriptor.Version,
                descriptor.PayloadSchema,
                completion.Kind,
                completion.Payload,
                completion.Error,
                completion.Reason));
        await protocol.SendAsync(outcome, ct: ct);
    }

    public static async Task RunListenerAsync(
        OutOfProcessModuleRuntime runtime,
        OutOfProcessProtocolSession protocol,
        SidecarEventListenerDelivery delivery,
        CancellationToken ct)
    {
        var descriptor = delivery.Envelope.Descriptor;
        var hook = runtime.Graph.EventDispatch.SelectListeners(descriptor, delivery.Delivery)
            .SingleOrDefault(candidate => string.Equals(
                candidate.HookId,
                delivery.ListenerId,
                StringComparison.Ordinal))
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"Event listener '{delivery.ListenerId}' is not selected for '{descriptor.Key.Value}'.");
        RequireDescriptor(hook, descriptor);

        ExecutionError? error = null;
        try
        {
            if (hook.IsUntyped)
                await InvokeUntypedListenerAsync(runtime, hook, delivery.Envelope, ct);
            else
                await InvokeTypedListenerAsync(runtime, hook, delivery.Envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            error = new ExecutionError(
                "module_listener_failed",
                "The module event listener failed.");
        }

        if (!delivery.RequiresAcknowledgement)
        {
            if (error is not null)
                throw new OutOfProcessProtocolException(error.Code, error.Message);
            return;
        }

        var acknowledgement = protocol.Create(
            SidecarProtocolMessageKind.EventListenerAcknowledgement,
            header => new SidecarEventListenerAcknowledgement(
                header,
                delivery.DeliveryId,
                delivery.ListenerId,
                descriptor,
                delivery.Delivery,
                Accepted: error is null,
                error));
        await protocol.SendAsync(acknowledgement, ct: ct);
    }

    private static async ValueTask<SidecarEventCompletion> InvokeUntypedInterceptorAsync(
        OutOfProcessModuleRuntime runtime,
        ModuleEventHook hook,
        EventInterceptStart start,
        CancellationToken ct)
    {
        var handler = ActivatorUtilities.GetServiceOrCreateInstance(
            runtime.Services,
            hook.HandlerType) as IAnyEventInterceptor
            ?? throw new InvalidOperationException(
                $"Handler '{hook.HandlerType.FullName}' is not an untyped event interceptor.");
        var outcome = await handler.InterceptAsync(
            new UntypedEventContext(start.Envelope),
            new SidecarUntypedEventControl(),
            ct);
        return GetCompletion(outcome);
    }

    private static async ValueTask<SidecarEventCompletion> InvokeTypedInterceptorAsync(
        OutOfProcessModuleRuntime runtime,
        ModuleEventHook hook,
        EventInterceptStart start,
        CancellationToken ct)
    {
        var contract = hook.HandlerType.GetInterfaces().Single(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IEventInterceptor<>));
        var adapterType = typeof(TypedEventInterceptorAdapter<>).MakeGenericType(
            contract.GetGenericArguments());
        var adapter = (ITypedEventInterceptorAdapter)(Activator.CreateInstance(
            adapterType,
            nonPublic: true)
            ?? throw new InvalidOperationException(
                "The typed event interceptor adapter could not be created."));
        return await adapter.InvokeAsync(runtime, hook, start, ct);
    }

    private static ValueTask InvokeUntypedListenerAsync(
        OutOfProcessModuleRuntime runtime,
        ModuleEventHook hook,
        UntypedEventEnvelope envelope,
        CancellationToken ct)
    {
        var handler = ActivatorUtilities.GetServiceOrCreateInstance(
            runtime.Services,
            hook.HandlerType) as IAnyEventListener
            ?? throw new InvalidOperationException(
                $"Handler '{hook.HandlerType.FullName}' is not an untyped event listener.");
        return handler.OnEventAsync(envelope, ct);
    }

    private static async ValueTask InvokeTypedListenerAsync(
        OutOfProcessModuleRuntime runtime,
        ModuleEventHook hook,
        UntypedEventEnvelope envelope,
        CancellationToken ct)
    {
        var contract = hook.HandlerType.GetInterfaces().Single(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IEventListener<>));
        var adapterType = typeof(TypedEventListenerAdapter<>).MakeGenericType(
            contract.GetGenericArguments());
        var adapter = (ITypedEventListenerAdapter)(Activator.CreateInstance(
            adapterType,
            nonPublic: true)
            ?? throw new InvalidOperationException(
                "The typed event listener adapter could not be created."));
        await adapter.InvokeAsync(runtime, hook, envelope, ct);
    }

    private static SidecarEventCompletion GetCompletion(object outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome is ISidecarEventOutcomeCarrier carrier)
            return carrier.Completion;
        throw new InvalidOperationException(
            "The event handler returned an outcome that did not come from its event control.");
    }

    private static void RequireGrant(
        ModuleEventHook hook,
        UntypedEventDescriptor descriptor,
        EventCapabilityGrant grant,
        SidecarHostAuthorization authorization)
    {
        RequireDescriptor(hook, descriptor);
        if (grant.EventKey != descriptor.Key
            || grant.EventVersion != descriptor.Version
            || grant.Capabilities != hook.RequestedCapabilities
            || grant.SensitiveApproved != descriptor.ContainsSensitiveData
            || grant.AcceptUnknownSchemas != descriptor.AcceptsUnknownNonSensitiveSchemas
            || !authorization.EventGrants.Contains(grant))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.ForgedApproval,
                $"Event hook '{hook.HookId}' does not have the supplied host grant.");
        }
    }

    private static void RequireDescriptor(
        ModuleEventHook hook,
        UntypedEventDescriptor descriptor)
    {
        var acceptsUnknown = hook.AcceptUnknownNonSensitiveSchemas
            && descriptor.AcceptsUnknownNonSensitiveSchemas
            && !descriptor.ContainsSensitiveData;
        if (!hook.VersionRange.Contains(descriptor.Version)
            || !Equals(hook.PayloadSchema, descriptor.PayloadSchema) && !acceptsUnknown)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnsupportedSchema,
                $"Event hook '{hook.HookId}' does not accept the supplied event schema.");
        }
    }

    private interface ITypedEventInterceptorAdapter
    {
        ValueTask<SidecarEventCompletion> InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            ModuleEventHook hook,
            EventInterceptStart start,
            CancellationToken ct);
    }

    private sealed class TypedEventInterceptorAdapter<TEvent> : ITypedEventInterceptorAdapter
    {
        public async ValueTask<SidecarEventCompletion> InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            ModuleEventHook hook,
            EventInterceptStart start,
            CancellationToken ct)
        {
            var handler = ActivatorUtilities.GetServiceOrCreateInstance(
                runtime.Services,
                hook.HandlerType) as IEventInterceptor<TEvent>
                ?? throw new InvalidOperationException(
                    $"Handler '{hook.HandlerType.FullName}' has an invalid typed event contract.");
            var payloadJsonOptions = OutOfProcessProtocolCodec.CreatePayloadJsonOptions();
            var payload = start.Envelope.Payload.Deserialize<TEvent>(payloadJsonOptions)
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.UnsupportedSchema,
                    "The typed event payload could not be deserialized.");
            var untyped = start.Envelope.Descriptor;
            var descriptor = new EventDescriptor<TEvent>(
                untyped.Key,
                untyped.Version,
                untyped.Category,
                untyped.Capabilities,
                DurableByDefault: false,
                ContainsSensitiveData: untyped.ContainsSensitiveData)
            {
                ProtocolVersionRange = untyped.ProtocolVersionRange,
                DeliveryClasses = [EventDelivery.Inline],
            };
            var envelope = new EventEnvelope<TEvent>(
                start.Envelope.EventId,
                start.Envelope.ActionInvocationId,
                start.Envelope.TraceId,
                start.Envelope.Timestamp,
                start.Envelope.OwnerId,
                payload);
            var context = new EventContext<TEvent>(
                descriptor,
                envelope,
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                runtime.Graph.ContractHash);
            var outcome = await handler.InterceptAsync(
                context,
                new SidecarEventControl<TEvent>(payloadJsonOptions),
                ct);
            return GetCompletion(outcome);
        }
    }

    private interface ITypedEventListenerAdapter
    {
        ValueTask InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            ModuleEventHook hook,
            UntypedEventEnvelope envelope,
            CancellationToken ct);
    }

    private sealed class TypedEventListenerAdapter<TEvent> : ITypedEventListenerAdapter
    {
        public async ValueTask InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            ModuleEventHook hook,
            UntypedEventEnvelope envelope,
            CancellationToken ct)
        {
            var handler = ActivatorUtilities.GetServiceOrCreateInstance(
                runtime.Services,
                hook.HandlerType) as IEventListener<TEvent>
                ?? throw new InvalidOperationException(
                    $"Handler '{hook.HandlerType.FullName}' has an invalid typed event listener contract.");
            var payload = envelope.Payload.Deserialize<TEvent>(
                OutOfProcessProtocolCodec.CreatePayloadJsonOptions())
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.UnsupportedSchema,
                    "The typed event payload could not be deserialized.");
            await handler.OnEventAsync(
                new EventEnvelope<TEvent>(
                    envelope.EventId,
                    envelope.ActionInvocationId,
                    envelope.TraceId,
                    envelope.Timestamp,
                    envelope.OwnerId,
                    payload),
                ct);
        }
    }
}
