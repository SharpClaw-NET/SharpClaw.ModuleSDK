using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;
using SharpClaw.SidecarHost.InProcess;
using SharpClaw.SidecarHost.OutOfProcess.TestRegistration;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess.Tests;

[NonParallelizable]
public sealed class OutOfProcessEventProtocolTests
{
    private Uri _controlAddress = null!;
    private string _controlToken = null!;
    private OutOfProcessModuleServer _server = null!;
    private SidecarHostDescriptorCatalog _catalog = null!;
    private ServiceProvider _inProcessServices = null!;
    private ModuleContributionGraph _inProcessGraph = null!;
    private InProcessModuleInvoker _inProcessInvoker = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        var registrationDirectory = Path.Combine(root, "event-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registrationDirectory);
        var moduleAssemblyName = Path.GetFileName(typeof(EventSmokeModule).Assembly.Location);
        File.Copy(
            typeof(EventSmokeModule).Assembly.Location,
            Path.Combine(registrationDirectory, moduleAssemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(registrationDirectory, "package.json"),
            $$"""
            {
              "id": "{{EventSmokeModule.Id}}",
              "displayName": "Event Smoke",
              "version": "0.5.0-beta.2",
              "toolPrefix": "eventsmoke",
              "entryAssembly": "{{moduleAssemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "entryType": "{{typeof(EventSmokeModule).FullName}}",
              "requestedEvents": [
                {
                  "target": "host.smoke.event",
                  "delivery": "Inline",
                  "effects": ["inspect", "replace", "cancel", "stopPropagation"]
                },
                {
                  "target": "smoke.*",
                  "delivery": "Inline",
                  "effects": ["inspect", "replace"]
                },
                {
                  "target": "*",
                  "delivery": "Inline",
                  "effects": ["inspect"]
                },
                {
                  "target": "host.smoke.listener",
                  "delivery": "Queued",
                  "effects": ["observe"]
                },
                {
                  "target": "listen.*",
                  "delivery": "Queued",
                  "effects": ["observe"]
                }
              ]
            }
            """,
            Encoding.UTF8);
        _controlAddress = await FindFreeAddressAsync();
        _controlToken = "event-token-" + Guid.NewGuid().ToString("N");
        _server = await OutOfProcessModuleServer.CreateAsync(
            registrationDirectory,
            _controlAddress,
            _controlToken);
        await _server.StartAsync();
        _catalog = new SidecarHostDescriptorCatalog(
            [],
            [
                HostDescriptor(EventSmokeModule.HostEvent),
                HostDescriptor(EventSmokeModule.HostListenerEvent),
            ],
            OutOfProcessSidecarHostProtocol.Version,
            new SidecarPayloadLimits());

        var inProcessModule = new EventSmokeModule();
        _inProcessGraph = SharpClawModuleCompiler.Compile(
            inProcessModule,
            InProcessManifest(),
            new ModuleCompilationOptions
            {
                HostingMode = ModuleHostingMode.InProcess,
                HostEvents =
                [
                    HostDescriptor(EventSmokeModule.HostEvent),
                    HostDescriptor(EventSmokeModule.HostListenerEvent),
                ],
            });
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in _inProcessGraph.Services)
            services.Add(descriptor);
        services.AddSingleton(inProcessModule);
        services.AddSingleton(_inProcessGraph);
        _inProcessServices = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _inProcessInvoker = new InProcessModuleInvoker(_inProcessGraph, _inProcessServices);
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        OutOfProcessModuleServer? server = _server;
        _server = null!;
        if (server is not null)
            await server.DisposeAsync();
        if (_inProcessServices is not null)
            await _inProcessServices.DisposeAsync();
    }

    [TestCase("continue", EventInterceptionKind.Continued)]
    [TestCase("replace", EventInterceptionKind.Replaced)]
    [TestCase("cancel", EventInterceptionKind.Cancelled)]
    [TestCase("stop", EventInterceptionKind.PropagationStopped)]
    [CancelAfter(15000)]
    public async Task TypedEventInterceptorReturnsEachAuthorizedOutcome(
        string mode,
        EventInterceptionKind expected)
    {
        await using var client = await CreateClientAsync();

        var result = await client.InterceptEventAsync(CreateStart(
            client,
            EventSmokeModule.ExactInterceptorId,
            mode));

        result.Kind.Should().Be(expected);
        if (expected == EventInterceptionKind.Replaced)
        {
            result.Payload!.Value.Deserialize<SmokeEvent>(OutOfProcessProtocolCodec.JsonOptions)!
                .Value.Should().Be("sidecar:value");
            result.Reason.Should().Be("smoke replacement");
        }
        if (expected == EventInterceptionKind.Cancelled)
            result.Error!.Code.Should().Be("smoke_cancelled");
    }

    [TestCase(EventSmokeModule.CategoryInterceptorId, "replace", EventInterceptionKind.Replaced)]
    [TestCase(EventSmokeModule.WildcardInterceptorId, "continue", EventInterceptionKind.Continued)]
    [CancelAfter(15000)]
    public async Task UntypedCategoryAndWildcardInterceptorsUseCompiledDispatch(
        string hookId,
        string mode,
        EventInterceptionKind expected)
    {
        await using var client = await CreateClientAsync();

        var result = await client.InterceptEventAsync(CreateStart(client, hookId, mode));

        result.Kind.Should().Be(expected);
    }

    [TestCase(EventSmokeModule.ExactListenerId, true)]
    [TestCase(EventSmokeModule.CategoryListenerId, false)]
    [CancelAfter(15000)]
    public async Task TypedAndUntypedListenersCompleteWithDeclaredAcknowledgement(
        string listenerId,
        bool requiresAcknowledgement)
    {
        await using var client = await CreateClientAsync();

        var result = await client.DeliverEventAsync(CreateDelivery(
            client,
            listenerId,
            requiresAcknowledgement));

        if (requiresAcknowledgement)
        {
            result.Should().NotBeNull();
            result!.Accepted.Should().BeTrue();
        }
        else
        {
            result.Should().BeNull();
        }
    }

    [TestCaseSource(nameof(EventSemanticsCases))]
    [Category("ModuleHostEventConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsReturnTheSameEventSemantics(
        ModuleHostingMode hostingMode,
        string mode,
        EventConformanceResult expected)
    {
        var result = await InvokeInterceptionConformanceAsync(
            hostingMode,
            EventSmokeModule.ExactInterceptorId,
            mode,
            typed: true);

        result.Should().Be(expected);
    }

    [TestCaseSource(nameof(EventSelectorCases))]
    [Category("ModuleHostEventConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsUseTheSameEventSelectors(
        ModuleHostingMode hostingMode,
        string hookId,
        string mode,
        bool typed,
        EventConformanceResult expected)
    {
        var result = await InvokeInterceptionConformanceAsync(
            hostingMode,
            hookId,
            mode,
            typed);

        result.Should().Be(expected);
    }

    [TestCaseSource(nameof(EventListenerCases))]
    [Category("ModuleHostEventConformance")]
    [CancelAfter(15000)]
    public async Task ModuleHostsCompleteTypedAndUntypedObservation(
        ModuleHostingMode hostingMode,
        string listenerId,
        bool typed)
    {
        await InvokeListenerConformanceAsync(hostingMode, listenerId, typed);
    }

    private static IEnumerable<TestCaseData> EventSemanticsCases()
    {
        var scenarios = new (string Mode, EventConformanceResult Expected)[]
        {
            ("continue", new(EventInterceptionKind.Continued, null, null)),
            ("replace", new(EventInterceptionKind.Replaced, "sidecar:value", null)),
            ("cancel", new(EventInterceptionKind.Cancelled, null, "smoke_cancelled")),
            ("stop", new(EventInterceptionKind.PropagationStopped, null, null)),
        };
        foreach (var hostingMode in Enum.GetValues<ModuleHostingMode>())
        {
            foreach (var scenario in scenarios)
            {
                yield return new TestCaseData(hostingMode, scenario.Mode, scenario.Expected)
                    .SetName($"Event_{hostingMode}_{scenario.Mode}");
            }
        }
    }

    private static IEnumerable<TestCaseData> EventSelectorCases()
    {
        var selectors = new (string HookId, string Mode, bool Typed, EventConformanceResult Expected)[]
        {
            (
                EventSmokeModule.ExactInterceptorId,
                "replace",
                true,
                new(EventInterceptionKind.Replaced, "sidecar:value", null)),
            (
                EventSmokeModule.CategoryInterceptorId,
                "replace",
                false,
                new(EventInterceptionKind.Replaced, "sidecar:untyped", null)),
            (
                EventSmokeModule.WildcardInterceptorId,
                "continue",
                false,
                new(EventInterceptionKind.Continued, null, null)),
        };
        foreach (var hostingMode in Enum.GetValues<ModuleHostingMode>())
        {
            foreach (var selector in selectors)
            {
                yield return new TestCaseData(
                        hostingMode,
                        selector.HookId,
                        selector.Mode,
                        selector.Typed,
                        selector.Expected)
                    .SetName($"EventSelector_{hostingMode}_{selector.HookId}");
            }
        }
    }

    private static IEnumerable<TestCaseData> EventListenerCases()
    {
        foreach (var hostingMode in Enum.GetValues<ModuleHostingMode>())
        {
            yield return new TestCaseData(
                    hostingMode,
                    EventSmokeModule.ExactListenerId,
                    true)
                .SetName($"EventListener_{hostingMode}_typed");
            yield return new TestCaseData(
                    hostingMode,
                    EventSmokeModule.CategoryListenerId,
                    false)
                .SetName($"EventListener_{hostingMode}_untyped");
        }
    }

    private async ValueTask<EventConformanceResult> InvokeInterceptionConformanceAsync(
        ModuleHostingMode hostingMode,
        string hookId,
        string mode,
        bool typed)
    {
        if (hostingMode == ModuleHostingMode.OutOfProcess)
        {
            await using var client = await CreateClientAsync();
            return Normalize(await client.InterceptEventAsync(CreateStart(client, hookId, mode)));
        }

        var hook = _inProcessGraph.EventHooks.Single(item =>
            string.Equals(item.HookId, hookId, StringComparison.Ordinal));
        if (typed)
        {
            var outcome = await _inProcessInvoker.InvokeEventAsync(
                hook,
                TypedContext(mode),
                new ConformanceEventControl(),
                CancellationToken.None);
            return Normalize(outcome);
        }

        var untypedOutcome = await _inProcessInvoker.InvokeAnyEventAsync(
            hook,
            new UntypedEventContext(UntypedEnvelope(EventSmokeModule.HostEvent, mode, hookId)),
            new ConformanceUntypedEventControl(),
            CancellationToken.None);
        return Normalize(untypedOutcome);
    }

    private async ValueTask InvokeListenerConformanceAsync(
        ModuleHostingMode hostingMode,
        string listenerId,
        bool typed)
    {
        if (hostingMode == ModuleHostingMode.OutOfProcess)
        {
            await using var client = await CreateClientAsync();
            var result = await client.DeliverEventAsync(CreateDelivery(
                client,
                listenerId,
                requiresAcknowledgement: true));
            result.Should().NotBeNull();
            result!.Accepted.Should().BeTrue();
            return;
        }

        var hook = _inProcessGraph.EventHooks.Single(item =>
            string.Equals(item.HookId, listenerId, StringComparison.Ordinal));
        if (typed)
        {
            await _inProcessInvoker.InvokeEventListenerAsync(
                hook,
                TypedEnvelope("listen"),
                CancellationToken.None);
            return;
        }

        await _inProcessInvoker.InvokeAnyEventListenerAsync(
            hook,
            UntypedEnvelope(EventSmokeModule.HostListenerEvent, "listen", listenerId),
            CancellationToken.None);
    }

    private EventContext<SmokeEvent> TypedContext(string mode) =>
        new(
            EventSmokeModule.HostEvent,
            TypedEnvelope(mode),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            _inProcessGraph.ContractHash);

    private static EventEnvelope<SmokeEvent> TypedEnvelope(string mode) =>
        new(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "host",
            new SmokeEvent(mode, "value"));

    private static UntypedEventEnvelope UntypedEnvelope(
        EventDescriptor<SmokeEvent> typed,
        string mode,
        string hookId) =>
        new(
            UntypedDescriptor(
                typed,
                hookId != EventSmokeModule.ExactInterceptorId
                && hookId != EventSmokeModule.ExactListenerId),
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "host",
            JsonSerializer.SerializeToElement(
                new SmokeEvent(mode, "value"),
                OutOfProcessProtocolCodec.JsonOptions));

    private static EventConformanceResult Normalize(EventInterceptOutcome outcome) =>
        new(outcome.Kind, ReadPayload(outcome.Payload), outcome.Error?.Code);

    private static EventConformanceResult Normalize(IEventInterception<SmokeEvent> outcome) =>
        new(outcome.Kind, outcome.Payload?.Value, outcome.Error?.Code);

    private static EventConformanceResult Normalize(IUntypedEventInterception outcome) =>
        new(outcome.Kind, ReadPayload(outcome.Payload), outcome.Error?.Code);

    private static string? ReadPayload(JsonElement? payload) =>
        payload is { } value && value.TryGetProperty("value", out var property)
            ? property.GetString()
            : null;

    private Task<OutOfProcessRegistrationClient> CreateClientAsync() =>
        OutOfProcessRegistrationClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private static EventInterceptStart CreateStart(
        OutOfProcessRegistrationClient client,
        string hookId,
        string mode)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(10);
        var capabilities = hookId switch
        {
            EventSmokeModule.ExactInterceptorId =>
                EventInterceptionCapabilities.Inspect
                | EventInterceptionCapabilities.Replace
                | EventInterceptionCapabilities.Cancel
                | EventInterceptionCapabilities.StopPropagation,
            EventSmokeModule.CategoryInterceptorId =>
                EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Replace,
            _ => EventInterceptionCapabilities.Inspect,
        };
        var acceptsUnknown = hookId != EventSmokeModule.ExactInterceptorId;
        var grant = client.Authorization.EventGrants.Single(item =>
            item.EventKey == EventSmokeModule.HostEvent.Key
            && item.Capabilities == capabilities
            && item.AcceptUnknownSchemas == acceptsUnknown);
        var descriptor = UntypedDescriptor(acceptsUnknown);
        var eventId = Guid.NewGuid();
        var envelope = new UntypedEventEnvelope(
            descriptor,
            eventId,
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "host",
            JsonSerializer.SerializeToElement(
                new SmokeEvent(mode, "value"),
                OutOfProcessProtocolCodec.JsonOptions));
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessSidecarHostProtocol.Version,
            sequence: 1,
            expires,
            client.HostLimits.EventPayloadBytes,
            header => new EventInterceptStart(
                header,
                hookId,
                envelope,
                grant,
                new ContinuationHandle(
                    Guid.NewGuid(),
                    eventId,
                    hookId,
                    expires,
                    Sequence: 1)));
    }

    private static SidecarEventListenerDelivery CreateDelivery(
        OutOfProcessRegistrationClient client,
        string listenerId,
        bool requiresAcknowledgement)
    {
        var descriptor = UntypedDescriptor(
            EventSmokeModule.HostListenerEvent,
            listenerId == EventSmokeModule.CategoryListenerId);
        var envelope = new UntypedEventEnvelope(
            descriptor,
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "host",
            JsonSerializer.SerializeToElement(
                new SmokeEvent("listen", "value"),
                OutOfProcessProtocolCodec.JsonOptions));
        return SidecarMessageHeaderFactory.CreateMeasured(
            OutOfProcessSidecarHostProtocol.Version,
            sequence: 1,
            DateTimeOffset.UtcNow.AddSeconds(10),
            client.HostLimits.EventPayloadBytes,
            header => new SidecarEventListenerDelivery(
                header,
                Guid.NewGuid(),
                listenerId,
                envelope,
                EventDelivery.Queued,
                requiresAcknowledgement));
    }

    private static PackageManifest InProcessManifest() =>
        new(
            EventSmokeModule.Id,
            "Event Smoke",
            "0.5.0-beta.2",
            "eventsmoke",
            "EventSmokeModule.dll",
            "0.5.0-beta.2",
            Runtime: PackageRuntimeInfo.DotNet,
            EntryType: typeof(EventSmokeModule).FullName,
            HostMode: PackageRuntimeInfo.HostModeInProcess,
            RequestedEvents:
            [
                new PackageEventRequest(
                    "host.smoke.event",
                    "Inline",
                    ["inspect", "replace", "cancel", "stopPropagation"]),
                new PackageEventRequest(
                    "smoke.*",
                    "Inline",
                    ["inspect", "replace"]),
                new PackageEventRequest("*", "Inline", ["inspect"]),
                new PackageEventRequest(
                    "host.smoke.listener",
                    "Queued",
                    ["observe"]),
                new PackageEventRequest(
                    "listen.*",
                    "Queued",
                    ["observe"]),
            ]);

    private static SidecarHostEventDescriptor HostDescriptor(
        EventDescriptor<SmokeEvent> descriptor)
    {
        return new SidecarHostEventDescriptor(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            ModuleSchemaIdentity.EventPayload(
                descriptor.Key,
                descriptor.Version,
                typeof(SmokeEvent)),
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.ProtocolVersionRange);
    }

    private static UntypedEventDescriptor UntypedDescriptor(bool acceptsUnknown)
        => UntypedDescriptor(EventSmokeModule.HostEvent, acceptsUnknown);

    private static UntypedEventDescriptor UntypedDescriptor(
        EventDescriptor<SmokeEvent> typed,
        bool acceptsUnknown)
    {
        var descriptor = HostDescriptor(typed);
        return new UntypedEventDescriptor(
            descriptor.EventKey,
            descriptor.Version,
            descriptor.Category,
            descriptor.Capabilities,
            descriptor.PayloadSchema,
            descriptor.ContainsSensitiveData)
        {
            ProtocolVersionRange = descriptor.ProtocolVersionRange,
            AcceptsUnknownNonSensitiveSchemas = acceptsUnknown,
        };
    }

    private static async Task<Uri> FindFreeAddressAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new Uri($"http://127.0.0.1:{port}/");
        }
        finally
        {
            listener.Stop();
            await Task.CompletedTask;
        }
    }

    public sealed record EventConformanceResult(
        EventInterceptionKind Kind,
        string? Value,
        string? ErrorCode);

    private sealed record ConformanceEventInterception(
        EventInterceptionKind Kind,
        SmokeEvent? Payload,
        ExecutionError? Error) : IEventInterception<SmokeEvent>;

    private sealed record ConformanceUntypedEventInterception(
        EventInterceptionKind Kind,
        JsonElement? Payload,
        ExecutionError? Error) : IUntypedEventInterception;

    private sealed class ConformanceEventControl : IEventControl<SmokeEvent>
    {
        public IEventInterception<SmokeEvent> Continue() =>
            new ConformanceEventInterception(
                EventInterceptionKind.Continued,
                Payload: null,
                Error: null);

        public IEventInterception<SmokeEvent> Replace(SmokeEvent payload, string reason) =>
            new ConformanceEventInterception(
                EventInterceptionKind.Replaced,
                payload,
                Error: null);

        public IEventInterception<SmokeEvent> Cancel(string code, string message) =>
            new ConformanceEventInterception(
                EventInterceptionKind.Cancelled,
                Payload: null,
                Error: new ExecutionError(code, message));

        public IEventInterception<SmokeEvent> StopPropagation() =>
            new ConformanceEventInterception(
                EventInterceptionKind.PropagationStopped,
                Payload: null,
                Error: null);
    }

    private sealed class ConformanceUntypedEventControl : IUntypedEventControl
    {
        public IUntypedEventInterception Continue() =>
            new ConformanceUntypedEventInterception(
                EventInterceptionKind.Continued,
                Payload: null,
                Error: null);

        public IUntypedEventInterception Replace(JsonElement payload, string reason) =>
            new ConformanceUntypedEventInterception(
                EventInterceptionKind.Replaced,
                payload,
                Error: null);

        public IUntypedEventInterception Cancel(string code, string message) =>
            new ConformanceUntypedEventInterception(
                EventInterceptionKind.Cancelled,
                Payload: null,
                Error: new ExecutionError(code, message));

        public IUntypedEventInterception StopPropagation() =>
            new ConformanceUntypedEventInterception(
                EventInterceptionKind.PropagationStopped,
                Payload: null,
                Error: null);
    }
}
