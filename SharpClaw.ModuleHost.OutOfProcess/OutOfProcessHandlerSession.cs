using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal static class OutOfProcessHandlerSession
{
    public static async Task RunToolAsync(
        OutOfProcessModuleRuntime runtime,
        OutOfProcessProtocolSession protocol,
        SidecarToolHandlerInvokeStart start,
        CancellationToken ct)
    {
        if (!runtime.Graph.ToolDispatch.TryGet(start.ToolName, out var registration)
            || registration is null
            || !string.Equals(registration.HandlerId, start.HandlerId, StringComparison.Ordinal))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"Tool handler '{start.HandlerId}' is not registered for '{start.ToolName}'.");
        }
        if (!Equals(registration.InputSchema, start.InputSchema))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnsupportedSchema,
                $"Tool handler '{start.HandlerId}' does not accept the supplied input schema.");
        }
        if (start.HostActionContext is null
            || start.HostActionContext.Deadline != start.Header.Deadline
            || !OutOfProcessHostActionEntryContextRegistry.MatchesCaller(
                start.HostActionContext.Caller,
                start.Caller))
        {
            throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                "The tool caller does not match the issued host action context.");
        }

        ISidecarProtocolMessage terminal;
        try
        {
            var invocation = new ToolInvocation(
                start.InvocationId,
                null,
                start.InvocationId.ToString("D"),
                start.ToolName,
                start.Input,
                start.HostActionContext);
            var result = await runtime.Graph.ToolDispatch.InvokeAsync(
                start.ToolName,
                runtime.Services,
                invocation,
                ct)
                ?? throw new InvalidOperationException("The module tool returned no result.");
            terminal = protocol.Create(
                SidecarProtocolMessageKind.ToolHandlerResult,
                header => new SidecarToolHandlerResult(
                    header,
                    start.InvocationId,
                    start.HandlerId,
                    JsonSerializer.SerializeToElement(
                        result,
                        OutOfProcessProtocolCodec.CreatePayloadJsonOptions()),
                    registration.ResultSchema));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            terminal = protocol.Create(
                SidecarProtocolMessageKind.ToolHandlerCancelled,
                header => new SidecarToolHandlerCancelled(
                    header,
                    start.InvocationId,
                    start.HandlerId,
                    "tool_cancelled",
                    "The module tool cancelled its invocation."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            terminal = protocol.Create(
                SidecarProtocolMessageKind.ToolHandlerFailed,
                header => new SidecarToolHandlerFailed(
                    header,
                    start.InvocationId,
                    start.HandlerId,
                    new ExecutionError(
                        "module_tool_failed",
                        "The module tool handler failed.")));
        }
        await protocol.SendAsync(terminal, ct: ct);
    }

    public static async Task RunLifecycleAsync(
        OutOfProcessModuleRuntime runtime,
        OutOfProcessProtocolSession protocol,
        SidecarLifecycleHandlerInvokeStart start,
        CancellationToken ct)
    {
        var expectedHandlerId = start.Call switch
        {
            SidecarLifecycleCallKind.Start => $"{runtime.Graph.Identity.Id}:lifecycle:start",
            SidecarLifecycleCallKind.Stop => $"{runtime.Graph.Identity.Id}:lifecycle:stop",
            _ => null,
        };
        if (expectedHandlerId is null
            || !string.Equals(expectedHandlerId, start.HandlerId, StringComparison.Ordinal))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"Lifecycle handler '{start.HandlerId}' is not in the module discovery.");
        }

        ISidecarProtocolMessage terminal;
        try
        {
            switch (start.Call)
            {
                case SidecarLifecycleCallKind.Start:
                    var context = ReadStartContext(runtime, start.Input);
                    await runtime.StartAsync(context, ct);
                    break;
                case SidecarLifecycleCallKind.Stop:
                    if (start.Input is not null)
                    {
                        throw new OutOfProcessProtocolException(
                            SidecarProtocolErrors.MalformedMessage,
                            "The module stop call does not accept input.");
                    }
                    await runtime.StopAsync(ct);
                    break;
                default:
                    throw new InvalidOperationException("The lifecycle call is not supported.");
            }

            terminal = protocol.Create(
                SidecarProtocolMessageKind.LifecycleHandlerResult,
                header => new SidecarLifecycleHandlerResult(
                    header,
                    start.InvocationId,
                    start.Call,
                    start.HandlerId,
                    Result: null));
        }
        catch (OutOfProcessProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            terminal = protocol.Create(
                SidecarProtocolMessageKind.LifecycleHandlerCancelled,
                header => new SidecarLifecycleHandlerCancelled(
                    header,
                    start.InvocationId,
                    start.Call,
                    start.HandlerId,
                    "lifecycle_cancelled",
                    "The module lifecycle handler cancelled its invocation."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            terminal = protocol.Create(
                SidecarProtocolMessageKind.LifecycleHandlerFailed,
                header => new SidecarLifecycleHandlerFailed(
                    header,
                    start.InvocationId,
                    start.Call,
                    start.HandlerId,
                    new ExecutionError(
                        "module_lifecycle_failed",
                        "The module lifecycle handler failed.")));
        }
        await protocol.SendAsync(terminal, ct: ct);
    }

    private static ModuleStartContext ReadStartContext(
        OutOfProcessModuleRuntime runtime,
        JsonElement? input)
    {
        if (input is null)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The module start call requires input.");
        }

        var context = input.Value.Deserialize<ModuleStartContext>(
            OutOfProcessProtocolCodec.CreatePayloadJsonOptions())
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The module start context is invalid.");
        if (!Equals(context.Identity, runtime.Graph.Identity)
            || !string.Equals(
                context.ContractHash,
                runtime.Graph.ContractHash,
                StringComparison.Ordinal))
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.ExchangeIdentityMismatch,
                "The module start context does not match the compiled module graph.");
        }
        return context;
    }
}
