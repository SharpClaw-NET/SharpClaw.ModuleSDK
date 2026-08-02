using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.OutOfProcess.TestModule;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.Tests;

[NonParallelizable]
public sealed class OutOfProcessEventProtocolTests
{
    private Uri _controlAddress = null!;
    private string _controlToken = null!;
    private OutOfProcessModuleServer _server = null!;
    private SidecarHostDescriptorCatalog _catalog = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var root = Environment.GetEnvironmentVariable("SHARPCLAW_MODULESDK_TEST_ROOT")
            ?? throw new InvalidOperationException(
                "SHARPCLAW_MODULESDK_TEST_ROOT must identify the D: test root.");
        var moduleDirectory = Path.Combine(root, "event-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDirectory);
        var moduleAssemblyName = Path.GetFileName(typeof(EventSmokeModule).Assembly.Location);
        File.Copy(
            typeof(EventSmokeModule).Assembly.Location,
            Path.Combine(moduleDirectory, moduleAssemblyName),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(moduleDirectory, "module.json"),
            $$"""
            {
              "id": "{{EventSmokeModule.Id}}",
              "displayName": "Event Smoke",
              "version": "0.5.0-beta.2",
              "toolPrefix": "eventsmoke",
              "entryAssembly": "{{moduleAssemblyName}}",
              "runtime": "dotnet",
              "hostMode": "sidecar",
              "moduleType": "{{typeof(EventSmokeModule).FullName}}",
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
                  "target": "host.smoke.event",
                  "delivery": "Queued",
                  "effects": ["observe"]
                },
                {
                  "target": "*",
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
            moduleDirectory,
            _controlAddress,
            _controlToken);
        await _server.StartAsync();
        _catalog = new SidecarHostDescriptorCatalog(
            [],
            [HostDescriptor()],
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        OutOfProcessModuleServer? server = _server;
        _server = null!;
        if (server is not null)
            await server.DisposeAsync();
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
    [TestCase(EventSmokeModule.WildcardListenerId, false)]
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

    private Task<OutOfProcessModuleClient> CreateClientAsync() =>
        OutOfProcessModuleClient.CreateAuthorizedAsync(
            _controlAddress,
            _controlToken,
            _catalog);

    private static EventInterceptStart CreateStart(
        OutOfProcessModuleClient client,
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
            OutOfProcessModuleHostProtocol.Version,
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
        OutOfProcessModuleClient client,
        string listenerId,
        bool requiresAcknowledgement)
    {
        var descriptor = UntypedDescriptor(
            listenerId == EventSmokeModule.WildcardListenerId);
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
            OutOfProcessModuleHostProtocol.Version,
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

    private static SidecarHostEventDescriptor HostDescriptor()
    {
        var descriptor = EventSmokeModule.HostEvent;
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
    {
        var descriptor = HostDescriptor();
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
}
