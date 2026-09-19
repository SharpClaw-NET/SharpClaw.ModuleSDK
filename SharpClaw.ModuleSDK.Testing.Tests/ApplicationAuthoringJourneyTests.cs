using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK.Testing.Tests;

public sealed class ApplicationAuthoringJourneyTests
{
    [Test]
    public async Task TestHostInvokesApplicationSurfacesWithScopedAuthority()
    {
        var capture = new ApplicationCapture();
        var hostActionEntry = new MarkerHostActionEntry();
        var caller = new RequestPrincipal(
            "application-author",
            "Application Author",
            new HashSet<string>(["developer"], StringComparer.Ordinal));
        var features = new ExtensionFeatureSet([]);
        await using var host = new SharpClawModuleTestBuilder()
            .AddRegistration(new ApplicationRegistration(), Manifest())
            .ConfigureServices(services => services.AddSingleton(capture))
            .UseExecutionContext(caller, features)
            .UseHostActionEntry(hostActionEntry)
            .Build();

        var conversationId = Guid.NewGuid();
        var tool = await host.InvokeToolAsync(
            "application_echo",
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            conversationId);
        var cli = await host.InvokeCliAsync("application", ["inspect"]);
        var http = await host.InvokeHttpAsync(
            "application.http",
            Encoding.UTF8.GetBytes("request"));
        var channel = new RecordingWebSocketChannel();
        await host.InvokeWebSocketAsync("application.websocket", channel);
        var firstScope = await host.InScopeAsync((services, _) =>
            ValueTask.FromResult(services.GetRequiredService<ApplicationScope>().Id));
        var secondScope = await host.InScopeAsync((services, _) =>
            ValueTask.FromResult(services.GetRequiredService<ApplicationScope>().Id));

        tool.Content.Should().Be("hello");
        cli.Succeeded.Should().BeTrue();
        http.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(http.Body).Should().Be("request");
        channel.Messages.Should().ContainSingle().Which.Payload.Should().Equal("socket"u8.ToArray());
        capture.Caller.Should().BeEquivalentTo(caller);
        capture.Features.Should().BeEquivalentTo(features);
        capture.ConversationId.Should().Be(conversationId);
        capture.HostEntries.Should().OnlyContain(entry => ReferenceEquals(entry, hostActionEntry));
        firstScope.Should().NotBe(secondScope);
        capture.CreatedScopes.Should().Be(capture.DisposedScopes);
    }

    [Test]
    public async Task PreCancellationRejectsApplicationWorkBeforeScopedConstruction()
    {
        var capture = new ApplicationCapture();
        await using var host = new SharpClawModuleTestBuilder()
            .AddRegistration(new ApplicationRegistration(), Manifest())
            .ConfigureServices(services => services.AddSingleton(capture))
            .Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var tool = async () => await host.InvokeToolAsync(
            "application_echo",
            JsonSerializer.SerializeToElement(new { text = "blocked" }),
            ct: cancellation.Token);
        var cli = async () => await host.InvokeCliAsync(
            "application",
            ct: cancellation.Token);
        var http = async () => await host.InvokeHttpAsync(
            "application.http",
            ct: cancellation.Token);
        var socket = async () => await host.InvokeWebSocketAsync(
            "application.websocket",
            new RecordingWebSocketChannel(),
            ct: cancellation.Token);

        await tool.Should().ThrowAsync<OperationCanceledException>();
        await cli.Should().ThrowAsync<OperationCanceledException>();
        await http.Should().ThrowAsync<OperationCanceledException>();
        await socket.Should().ThrowAsync<OperationCanceledException>();
        capture.CreatedScopes.Should().Be(0);
        capture.DisposedScopes.Should().Be(0);
    }

    private static PackageManifest Manifest() =>
        new(
            "application_registration",
            "Application Registration",
            "1.0.0",
            "application",
            "ApplicationRegistration.dll",
            "1.0.0",
            Runtime: PackageRuntimeInfo.DotNet,
            HostMode: PackageRuntimeInfo.HostModeInProcess);

    private sealed class ApplicationRegistration : ISharpClawModule
    {
        private static readonly JsonSchemaReference ArgumentsSchema =
            ModuleSchemaIdentity.UntypedAction("input", "application.cli");
        private static readonly JsonSchemaReference ResultSchema =
            ModuleSchemaIdentity.UntypedAction("result", "application.cli");

        public ModuleIdentity Identity { get; } =
            new("application_registration", "Application Registration", "application");

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IHostActionEntry>(new MarkerHostActionEntry());
            services.AddScoped<ApplicationScope>();
            services.AddTool<ApplicationTool>(new ToolDescriptor(
                "application_echo",
                "Returns the supplied text.",
                ToolSchemas.EmptyObject));
            services.AddCliCommand<ApplicationCli>(new CliCommandDescriptor(
                "application",
                ["app"],
                "Runs the application command.",
                ArgumentsSchema,
                ResultSchema));
            services.AddHttpEndpoint<ApplicationHttp>(new EndpointRouteDescriptor(
                "application.http",
                "/application",
                "POST",
                HostEndpointTransport.Http));
            services.AddWebSocketEndpoint<ApplicationWebSocket>(new EndpointRouteDescriptor(
                "application.websocket",
                "/application/ws",
                "GET",
                HostEndpointTransport.WebSocket));
        }
    }

    private sealed class ApplicationScope : IAsyncDisposable
    {
        private readonly ApplicationCapture _capture;
        private int _disposed;

        public ApplicationScope(ApplicationCapture capture)
        {
            _capture = capture;
            Id = Guid.NewGuid();
            _capture.CreatedScopes++;
        }

        public Guid Id { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _capture.DisposedScopes++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApplicationTool(ApplicationScope scope, ApplicationCapture capture)
        : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = scope.Id;
            capture.Caller = invocation.Caller;
            capture.Features = invocation.Features;
            capture.ConversationId = invocation.ConversationId;
            var text = invocation.Arguments.GetProperty("text").GetString()!;
            return ValueTask.FromResult(ToolResult.Text(text));
        }
    }

    private sealed class ApplicationCli(
        ApplicationScope scope,
        ApplicationCapture capture,
        IHostActionEntry hostActionEntry)
        : ICliHandler
    {
        public ValueTask<CliResult> ExecuteAsync(
            CliInvocation invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = scope.Id;
            capture.Caller = invocation.HostActionContext.Caller;
            capture.Features = invocation.HostActionContext.Features;
            capture.HostEntries.Add(hostActionEntry);
            return ValueTask.FromResult(new CliResult(true, []));
        }
    }

    private sealed class ApplicationHttp(ApplicationScope scope, ApplicationCapture capture)
        : IHttpEndpointHandler
    {
        public ValueTask<HttpEndpointResponse> InvokeAsync(
            HostEndpointRouteRequest request,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = scope.Id;
            capture.HostEntries.Add(hostActionEntry);
            return ValueTask.FromResult(new HttpEndpointResponse(200, EmptyMetadata, request.Body));
        }
    }

    private sealed class ApplicationWebSocket(ApplicationScope scope, ApplicationCapture capture)
        : IWebSocketEndpointHandler
    {
        public async ValueTask InvokeAsync(
            HostEndpointRouteRequest request,
            IWebSocketChannel channel,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = scope.Id;
            capture.HostEntries.Add(hostActionEntry);
            await channel.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, "socket"u8.ToArray()),
                cancellationToken);
        }
    }

    private sealed class ApplicationCapture
    {
        public int CreatedScopes { get; set; }
        public int DisposedScopes { get; set; }
        public RequestPrincipal? Caller { get; set; }
        public ExtensionFeatureSet? Features { get; set; }
        public Guid? ConversationId { get; set; }
        public List<IHostActionEntry> HostEntries { get; } = [];
    }

    private sealed class MarkerHostActionEntry : IHostActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWebSocketChannel : IWebSocketChannel
    {
        public List<WebSocketMessage> Messages { get; } = [];

        public ValueTask<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<WebSocketMessage?>(null);

        public ValueTask SendAsync(
            WebSocketMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(
            int closeStatus,
            string? description,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<string, string[]> EmptyMetadata { get; } =
        new Dictionary<string, string[]>();
}
