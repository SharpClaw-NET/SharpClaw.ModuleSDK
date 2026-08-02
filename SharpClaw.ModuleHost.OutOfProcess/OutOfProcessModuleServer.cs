using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Runs one .NET module through the authenticated sidecar protocol.</summary>
public sealed class OutOfProcessModuleServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly OutOfProcessModuleRuntime _runtime;
    private readonly BoundedExecutionQueue _actionQueue;
    private readonly BoundedExecutionQueue _eventQueue;
    private readonly BoundedExecutionQueue _toolQueue;
    private readonly BoundedExecutionQueue _lifecycleQueue;
    private readonly string _controlToken;
    private SidecarHostAuthorization? _authorization;
    private int _disposed;

    private OutOfProcessModuleServer(
        WebApplication app,
        OutOfProcessModuleRuntime runtime,
        BoundedExecutionQueue actionQueue,
        BoundedExecutionQueue eventQueue,
        BoundedExecutionQueue toolQueue,
        BoundedExecutionQueue lifecycleQueue,
        string controlToken)
    {
        _app = app;
        _runtime = runtime;
        _actionQueue = actionQueue;
        _eventQueue = eventQueue;
        _toolQueue = toolQueue;
        _lifecycleQueue = lifecycleQueue;
        _controlToken = controlToken;
        MapEndpoints();
    }

    /// <summary>Creates a server from the standard module-host environment.</summary>
    public static Task<OutOfProcessModuleServer> CreateAsync(
        string[] args,
        CancellationToken ct = default) =>
        CreateAsync(
            ReadRequiredEnvironment(
                OutOfProcessModuleHostProtocol.ModuleDirectoryEnvironmentVariable),
            new Uri(ReadRequiredEnvironment(
                OutOfProcessModuleHostProtocol.ControlAddressEnvironmentVariable)),
            ReadRequiredEnvironment(
                OutOfProcessModuleHostProtocol.ControlTokenEnvironmentVariable),
            args,
            ct);

    /// <summary>Creates a server for one module directory and control address.</summary>
    public static async Task<OutOfProcessModuleServer> CreateAsync(
        string moduleDirectory,
        Uri controlAddress,
        string controlToken,
        string[]? args = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(controlAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlToken);
        var runtime = await OutOfProcessModuleRuntime.LoadAsync(moduleDirectory, ct);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args ?? [],
                ContentRootPath = runtime.ModuleDirectory,
            });
            builder.WebHost.UseUrls(controlAddress.ToString());
            builder.Services.Configure<JsonOptions>(options =>
            {
                options.SerializerOptions.MaxDepth = 32;
                options.SerializerOptions.PropertyNameCaseInsensitive = false;
            });
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20),
            });
            var actionQueue = new BoundedExecutionQueue(capacity: 32, concurrency: 1);
            var eventQueue = new BoundedExecutionQueue(capacity: 32, concurrency: 1);
            var toolQueue = new BoundedExecutionQueue(capacity: 32, concurrency: 1);
            var lifecycleQueue = new BoundedExecutionQueue(capacity: 8, concurrency: 1);
            return new OutOfProcessModuleServer(
                app,
                runtime,
                actionQueue,
                eventQueue,
                toolQueue,
                lifecycleQueue,
                controlToken);
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs the server until shutdown.</summary>
    public Task RunAsync(CancellationToken ct = default) => _app.RunAsync(ct);

    /// <summary>Starts the server without blocking the caller.</summary>
    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);

    /// <summary>Stops the server.</summary>
    public Task StopAsync(CancellationToken ct = default) => _app.StopAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _app.StopAsync(CancellationToken.None);
        await _actionQueue.DisposeAsync();
        await _eventQueue.DisposeAsync();
        await _toolQueue.DisposeAsync();
        await _lifecycleQueue.DisposeAsync();
        await _app.DisposeAsync();
        await _runtime.DisposeAsync();
    }

    private void MapEndpoints()
    {
        _app.Use(async (context, next) =>
        {
            if (!HasExpectedToken(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        _app.MapGet(OutOfProcessModuleHostProtocol.ReadinessPath, () =>
            Results.Json(new
            {
                moduleId = _runtime.Graph.Identity.Id,
                authorized = Volatile.Read(ref _authorization) is not null,
            }, OutOfProcessProtocolCodec.JsonOptions));

        _app.MapGet(OutOfProcessModuleHostProtocol.DiscoveryPath, () =>
        {
            var discovery = _runtime.Graph.CreateSidecarDiscovery(
                OutOfProcessModuleHostProtocol.Version,
                sequence: 1,
                DateTimeOffset.UtcNow.AddMinutes(1));
            return Results.Json(discovery, OutOfProcessProtocolCodec.JsonOptions);
        });

        _app.MapPost(OutOfProcessModuleHostProtocol.AuthorizationPath, AuthorizeAsync);
        _app.MapGet(OutOfProcessModuleHostProtocol.ExchangePath, HandleExchangeAsync);
    }

    private async Task<IResult> AuthorizeAsync(HttpContext context, CancellationToken ct)
    {
        var decision = await context.Request.ReadFromJsonAsync<SidecarDiscoveryDecision>(
            OutOfProcessProtocolCodec.JsonOptions,
            ct);
        if (decision is null)
            return Results.BadRequest();
        if (!decision.Accepted || decision.Authorization is null)
        {
            return Results.BadRequest(new
            {
                error = decision.ErrorCode ?? SidecarProtocolErrors.UnsupportedCapability,
            });
        }

        var initial = new SidecarProtocolState(
            SidecarExchangeKind.LifecycleHandler,
            Guid.Empty,
            Guid.Empty,
            SidecarProtocolPhase.Discovered,
            LastSequence: 1,
            decision.Header.Deadline,
            decision.Header.ProtocolVersion,
            _runtime.Graph.PayloadLimits);
        var validation = SidecarProtocolStateMachine.Validate(
            initial,
            decision,
            DateTimeOffset.UtcNow);
        if (!validation.Accepted)
        {
            return Results.BadRequest(new
            {
                error = validation.ErrorCode,
                message = validation.ErrorMessage,
            });
        }

        if (!string.Equals(decision.ModuleId, _runtime.Graph.Identity.Id, StringComparison.Ordinal)
            || !string.Equals(
                decision.Authorization.ModuleId,
                _runtime.Graph.Identity.Id,
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = SidecarProtocolErrors.ExchangeIdentityMismatch,
            });
        }

        Volatile.Write(ref _authorization, decision.Authorization);
        return Results.NoContent();
    }

    private async Task HandleExchangeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var authorization = Volatile.Read(ref _authorization);
        if (authorization is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        OutOfProcessProtocolSession? protocol = null;
        try
        {
            var firstFrame = await OutOfProcessProtocolCodec.ReceiveAsync(
                socket,
                _runtime.Graph.PayloadLimits.ProtocolMessageBytes,
                context.RequestAborted);
            var exchangeKind = firstFrame.Message switch
            {
                HookInvokeStart => SidecarExchangeKind.ActionHook,
                EventInterceptStart => SidecarExchangeKind.EventIntercept,
                SidecarEventListenerDelivery => SidecarExchangeKind.EventListener,
                SidecarToolHandlerInvokeStart => SidecarExchangeKind.ToolHandler,
                SidecarLifecycleHandlerInvokeStart => SidecarExchangeKind.LifecycleHandler,
                SidecarStreamChunk => SidecarExchangeKind.Stream,
                _ => throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.InvalidLifecyclePhase,
                    "The first exchange message does not start a supported exchange."),
            };
            var listenerDelivery = firstFrame.Message as SidecarEventListenerDelivery;
            var state = new SidecarProtocolState(
                exchangeKind,
                Guid.Empty,
                Guid.Empty,
                SidecarProtocolPhase.Negotiated,
                LastSequence: 0,
                firstFrame.Message.Header.Deadline,
                firstFrame.Message.Header.ProtocolVersion,
                _runtime.Graph.PayloadLimits,
                EventKey: listenerDelivery?.Envelope.Descriptor.Key,
                EventVersion: listenerDelivery?.Envelope.Descriptor.Version,
                EventDescriptor: listenerDelivery?.Envelope.Descriptor,
                HostAuthorization: authorization);
            protocol = new OutOfProcessProtocolSession(socket, state);
            protocol.Accept(firstFrame.Message);

            BoundedExecutionQueue queue;
            Func<CancellationToken, Task> operation;
            string exchangeClass;
            switch (firstFrame.Message)
            {
                case HookInvokeStart actionStart:
                    queue = _actionQueue;
                    exchangeClass = "action";
                    operation = ct => OutOfProcessActionSession.RunAsync(
                        _runtime,
                        protocol,
                        actionStart,
                        authorization,
                        ct);
                    break;
                case EventInterceptStart eventStart:
                    queue = _eventQueue;
                    exchangeClass = "event";
                    operation = ct => OutOfProcessEventSession.RunInterceptorAsync(
                        _runtime,
                        protocol,
                        eventStart,
                        authorization,
                        ct);
                    break;
                case SidecarEventListenerDelivery delivery:
                    queue = _eventQueue;
                    exchangeClass = "event";
                    operation = ct => OutOfProcessEventSession.RunListenerAsync(
                        _runtime,
                        protocol,
                        delivery,
                        ct);
                    break;
                case SidecarToolHandlerInvokeStart toolStart:
                    queue = _toolQueue;
                    exchangeClass = "tool";
                    operation = ct => OutOfProcessHandlerSession.RunToolAsync(
                        _runtime,
                        protocol,
                        toolStart,
                        ct);
                    break;
                case SidecarLifecycleHandlerInvokeStart lifecycleStart:
                    queue = _lifecycleQueue;
                    exchangeClass = "lifecycle";
                    operation = ct => OutOfProcessHandlerSession.RunLifecycleAsync(
                        _runtime,
                        protocol,
                        lifecycleStart,
                        ct);
                    break;
                default:
                    await protocol.SendErrorAsync(
                        SidecarProtocolErrors.UnsupportedCapability,
                        "This host build does not implement the requested exchange class.",
                        context.RequestAborted);
                    await protocol.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        SidecarProtocolErrors.UnsupportedCapability,
                        context.RequestAborted);
                    return;
            }

            if (!queue.TrySchedule(
                operation,
                context.RequestAborted,
                out var completion))
            {
                await protocol.SendErrorAsync(
                    SidecarProtocolErrors.ModuleBusy,
                    $"The module {exchangeClass} queue is full.",
                    context.RequestAborted);
                await protocol.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    SidecarProtocolErrors.ModuleBusy,
                    context.RequestAborted);
                return;
            }

            await completion;
            await protocol.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "completed",
                context.RequestAborted);
        }
        catch (SidecarProtocolException ex)
        {
            await TrySendProtocolErrorAsync(protocol, ex, context.RequestAborted);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    ex.Code,
                    context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _app.Logger.LogError(ex, "The module exchange failed.");
            var protocolError = new OutOfProcessProtocolException(
                "module_exchange_failed",
                "The module exchange failed.");
            await TrySendProtocolErrorAsync(protocol, protocolError, context.RequestAborted);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InternalServerError,
                    protocolError.Code,
                    context.RequestAborted);
            }
        }
    }

    private static async Task TrySendProtocolErrorAsync(
        OutOfProcessProtocolSession? protocol,
        SidecarProtocolException error,
        CancellationToken ct)
    {
        if (protocol is null)
            return;
        try
        {
            await protocol.SendErrorAsync(error.Code, error.Message, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
        }
        catch (SidecarProtocolException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private bool HasExpectedToken(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(
                OutOfProcessModuleHostProtocol.TokenHeaderName,
                out var supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(_controlToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static string ReadRequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required environment variable '{name}' is not set.");
}
