using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessHostActionEntry : IHostActionEntry
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;

    public OutOfProcessHostActionEntry(
        OutOfProcessModuleCapabilityTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terminal);
        var now = DateTimeOffset.UtcNow;
        if (!request.IsWellFormed(now))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host action entry request is invalid or expired.");
        }

        var context = request.Context;
        var lineageMatches = context?.Contribution is { } contribution
            && HostActionEntryAuthorityValidator.MatchesDescriptorLineage(
                contribution.Lineage,
                request.Descriptor);
        if (context is null
            || context.Ingress != HostActionEntryIngress.Tool
                && context.Ingress != HostActionEntryIngress.Cli
                && context.Ingress != HostActionEntryIngress.Endpoint
                && context.Ingress != HostActionEntryIngress.CrossModule
            || context.Contribution is null
            || !lineageMatches)
        {
            throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                "The host action entry request context does not match the typed host authority.");
        }

        var call = _transport.CreateCall(
            SidecarCapabilityKind.Action,
            request.Deadline,
            cancellationToken);
        var identity = OutOfProcessActionDescriptorIdentity.Create(request.Descriptor);
        var actionPayload = OutOfProcessActionDispatcher.Payload(
            request.Action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var cancellation = new SidecarCancellationIdentity(
            call.CancellationId,
            SidecarCapabilitySessionValidator.ComputeBindingHash(_transport.Binding),
            request.Deadline);
        var sidecarRequest = SidecarActionCapabilityRequest.HostEntry(
            call,
            identity,
            actionPayload,
            cancellation,
            request.Deadline,
            context,
            new SidecarActionTerminalRegistration(
                terminal.TerminalId,
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.ResultTypeIdentity,
                identity.ResultSchemaVersion,
                identity.DescriptorHash));
        var response = await _transport.InvokeActionAsync(
            sidecarRequest,
            (terminalRequest, terminalCancellation) => ExecuteTerminalAsync(
                request.Descriptor,
                terminal,
                terminalRequest,
                terminalCancellation),
            cancellationToken);
        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
    }

    public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
        HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terminal);
        throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.UnsupportedCapability,
            "Nested host action entry is not available on this terminal exchange.");
    }

    private static async ValueTask<SidecarActionTerminalTransportResponse> ExecuteTerminalAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var action = JsonSerializer.Deserialize<TAction>(
                request.EffectiveAction.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host action terminal received no action value.");
        var context = OutOfProcessActionDispatcher.CreateActionContext(
            request,
            action);
        try
        {
            var result = await terminal.InvokeAsync(context, ct);
            var payload = OutOfProcessActionDispatcher.Payload(
                result,
                identity.ResultTypeIdentity,
                identity.ResultSchemaVersion);
            return new SidecarActionTerminalTransportResponse(
                new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    identity.Key,
                    identity.Version,
                    identity.ResultTypeIdentity,
                    payload.ContentHash),
                new SidecarTerminalExecutionResult(payload, null!, Completed: true),
                request.Receipt,
                null!)
            {
                TerminalId = request.TerminalId,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new SidecarActionTerminalTransportResponse(
                new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    identity.Key,
                    identity.Version,
                    identity.ResultTypeIdentity,
                    OutOfProcessActionDispatcher.EmptyPayloadForFailure().ContentHash),
                new SidecarTerminalExecutionResult(
                    OutOfProcessActionDispatcher.EmptyPayloadForFailure(),
                new SidecarSafeFailureIdentity(
                        Guid.NewGuid(),
                        SidecarCapabilityErrors.HostFailure,
                        ex.Message,
                        Retryable: false),
                    Completed: false),
                request.Receipt,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    SidecarCapabilityErrors.HostFailure,
                    ex.Message,
                    Retryable: false))
            {
                TerminalId = request.TerminalId,
            };
        }
    }
}
