using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess.TestRegistration;

public sealed record SmokeEvent(string Mode, string Value);

public sealed class EventSmokeModule : ISharpClawModule
{
    public const string Id = "event_smoke_module";
    public const string ExactInterceptorId = "smoke.event.exact";
    public const string CategoryInterceptorId = "smoke.event.category";
    public const string WildcardInterceptorId = "smoke.event.wildcard";
    public const string ExactListenerId = "smoke.event.listener.exact";
    public const string CategoryListenerId = "smoke.event.listener.category";

    public static EventDescriptor<SmokeEvent> HostEvent { get; } = new(
        new SharpClawEventKey("host.smoke.event"),
        1,
        "smoke",
        EventInterceptionCapabilities.Inspect
        | EventInterceptionCapabilities.Replace
        | EventInterceptionCapabilities.Cancel
        | EventInterceptionCapabilities.StopPropagation
        | EventInterceptionCapabilities.Observe,
        DurableByDefault: false,
        ContainsSensitiveData: false)
    {
        ProtocolVersionRange = ContractVersionRange.Exact(1),
        DeliveryClasses = [EventDelivery.Inline, EventDelivery.Queued],
    };

    public static EventDescriptor<SmokeEvent> HostListenerEvent { get; } = new(
        new SharpClawEventKey("host.smoke.listener"),
        1,
        "listen",
        EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
        DurableByDefault: false,
        ContainsSensitiveData: false)
    {
        ProtocolVersionRange = ContractVersionRange.Exact(1),
        DeliveryClasses = [EventDelivery.Queued],
    };

    public ModuleIdentity Identity { get; } = new(Id, "Event Smoke", "eventsmoke");

    public void ConfigureServices(IServiceCollection services)
    {
        services.OnEvent(HostEvent).Intercept<SmokeTypedInterceptor>(
            EventInterceptionCapabilities.Inspect
            | EventInterceptionCapabilities.Replace
            | EventInterceptionCapabilities.Cancel
            | EventInterceptionCapabilities.StopPropagation,
            new HookOrdering(ExactInterceptorId, Before: [CategoryInterceptorId]));
        services.OnEventCategory(
                "smoke",
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedEvent("smoke.*"),
                acceptUnknownNonSensitiveSchemas: true)
            .InterceptAny<SmokeUntypedInterceptor>(
                EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
                new HookOrdering(CategoryInterceptorId, Before: [WildcardInterceptorId]));
        services.OnAnyEvent(
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedEvent("*"),
                acceptUnknownNonSensitiveSchemas: true)
            .InterceptAny<SmokeUntypedInterceptor>(
                EventInterceptionCapabilities.Inspect,
                new HookOrdering(WildcardInterceptorId));
        services.OnEvent(HostListenerEvent).Listen<SmokeTypedListener>(
            EventDelivery.Queued,
            new HookOrdering(ExactListenerId));
        services.OnEventCategory(
                "listen",
                ContractVersionRange.Exact(1),
                ModuleSchemaIdentity.UntypedEvent("listen.*"),
                acceptUnknownNonSensitiveSchemas: true)
            .ListenAny<SmokeUntypedListener>(
                EventDelivery.Queued,
                new HookOrdering(CategoryListenerId));
    }

    public sealed class SmokeTypedInterceptor : IEventInterceptor<SmokeEvent>
    {
        public ValueTask<IEventInterception<SmokeEvent>> InterceptAsync(
            EventContext<SmokeEvent> context,
            IEventControl<SmokeEvent> control,
            CancellationToken ct) => ValueTask.FromResult(context.Envelope.Payload.Mode switch
            {
                "replace" => control.Replace(
                    context.Envelope.Payload with { Value = "sidecar:" + context.Envelope.Payload.Value },
                    "smoke replacement"),
                "cancel" => control.Cancel("smoke_cancelled", "The smoke event was cancelled."),
                "stop" => control.StopPropagation(),
                _ => control.Continue(),
            });
    }

    public sealed class SmokeUntypedInterceptor : IAnyEventInterceptor
    {
        public ValueTask<IUntypedEventInterception> InterceptAsync(
            UntypedEventContext context,
            IUntypedEventControl control,
            CancellationToken ct)
        {
            var mode = context.Envelope.Payload.GetProperty("mode").GetString();
            if (string.Equals(mode, "replace", StringComparison.Ordinal))
            {
                var replacement = JsonSerializer.SerializeToElement(
                    new
                    {
                        mode = "replaced",
                        value = "sidecar:untyped",
                    });
                return ValueTask.FromResult(control.Replace(replacement, "untyped replacement"));
            }

            return ValueTask.FromResult(control.Continue());
        }
    }

    public sealed class SmokeTypedListener : IEventListener<SmokeEvent>
    {
        public ValueTask OnEventAsync(EventEnvelope<SmokeEvent> evt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(evt.Payload.Value))
                throw new InvalidOperationException("The typed event payload is empty.");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class SmokeUntypedListener : IAnyEventListener
    {
        public ValueTask OnEventAsync(UntypedEventEnvelope evt, CancellationToken ct)
        {
            if (!evt.Payload.TryGetProperty("value", out _))
                throw new InvalidOperationException("The untyped event payload is empty.");
            return ValueTask.CompletedTask;
        }
    }
}
