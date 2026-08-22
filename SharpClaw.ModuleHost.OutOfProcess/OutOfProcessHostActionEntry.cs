using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessHostActionEntry : IHostActionEntry, IModuleCrossSidecarActionEntry
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;
    private readonly SidecarActionDescriptorIdentity? _parentDescriptor;
    private readonly SidecarActionTerminalTransportRequest? _parentTerminalRequest;
    private readonly HostActionEntryContribution? _parentContribution;

    public OutOfProcessHostActionEntry(
        OutOfProcessModuleCapabilityTransport transport,
        SidecarActionDescriptorIdentity? parentDescriptor = null,
        SidecarActionTerminalTransportRequest? parentTerminalRequest = null,
        HostActionEntryContribution? parentContribution = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _parentDescriptor = parentDescriptor;
        _parentTerminalRequest = parentTerminalRequest;
        _parentContribution = parentContribution;
    }

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
                _transport.ResolveActionEntryTerminalId(
                    identity,
                    terminal.TerminalId),
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.ResultTypeIdentity,
                identity.ResultSchemaVersion,
                identity.DescriptorHash));
        var response = await _transport.InvokeActionAsync(
            sidecarRequest,
            (terminalRequest, terminalCancellation) => ExecuteTerminalAsync(
                terminal,
                terminalRequest,
                _transport.Binding.SafeFailure,
                terminalCancellation,
                _transport,
                context.Contribution),
            cancellationToken);
        ThrowIfHostEntryFailed(response);
        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
    }

    public async ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
        HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terminal);
        var now = DateTimeOffset.UtcNow;
        if (!request.IsWellFormed(now)
            || _parentTerminalRequest is null
            || _parentContribution is null
            || !ReferenceEquals(request.ParentContext.HostActionEntry, this)
            || !MatchesParentContext(request.ParentContext, _parentTerminalRequest.Context))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The nested host action entry request does not match its terminal exchange.");
        }

        if (_parentDescriptor is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnknownAction,
                "The nested action has no parent descriptor authority.");
        }

        var metadata = _transport.ResolveNestedActionMetadata<TAction, TResult>(
            request.ActionKey,
            request.ActionVersion);
        var action = OutOfProcessActionDispatcher.Payload(
            request.Action,
            metadata.InputTypeIdentity,
            metadata.Hook.InputSchema.Version);
        var nestedDeadline = request.ParentContext.Deadline;
        if (_parentTerminalRequest.Deadline < nestedDeadline)
            nestedDeadline = _parentTerminalRequest.Deadline;
        if (_parentTerminalRequest.Call.Deadline < nestedDeadline)
            nestedDeadline = _parentTerminalRequest.Call.Deadline;
        var nestedRequest = new SidecarNestedHostActionEntryRequest(
            request.ActionKey,
            request.ActionVersion,
            action,
            nestedDeadline,
            nestedDeadline);
        var relayResponse = await _transport.InvokeActionTerminalAsync(
            _parentTerminalRequest with { NestedCarrierRequest = nestedRequest },
            cancellationToken);
        var relay = relayResponse.NestedCarrierRelay;
        if (relayResponse.NestedCarrierOutcome?.Kind
                != SidecarNestedHostActionEntryRelayOutcomeKind.Issued
            || relay is null)
        {
            throw new OutOfProcessCapabilityException(
                relayResponse.NestedCarrierOutcome?.Failure?.Code
                    ?? SidecarCapabilityErrors.HostFailure,
                relayResponse.NestedCarrierOutcome?.Failure?.Message
                    ?? "The host did not issue a nested host action carrier.");
        }

        var identity = relay.Carrier.Descriptor;
        if (identity.Key != request.ActionKey
            || identity.Version != request.ActionVersion
            || !OutOfProcessModuleCapabilityTransport.MatchesNestedActionMetadata(
                identity,
                metadata))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The nested relay descriptor does not match the exact typed module hook.");
        }

        var contribution = _parentContribution with
        {
            Lineage = new HostActionEntryLineage(
                identity.Key,
                identity.Version,
                identity.DescriptorHash,
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.InputSchemaHash,
                null,
                null),
        };

        var childRequest = SidecarActionCapabilityRequest.HostEntryNested(
            relay.Call,
            identity,
            action,
            new SidecarCancellationIdentity(
                relay.Call.CancellationId,
                SidecarCapabilitySessionValidator.ComputeBindingHash(_transport.Binding),
                relay.Call.Deadline),
            relay.Call.Deadline,
            relay.Carrier,
            new SidecarActionTerminalRegistration(
                _transport.ResolveActionEntryTerminalId(
                    identity,
                    terminal.TerminalId),
                identity.InputTypeIdentity,
                identity.InputSchemaVersion,
                identity.ResultTypeIdentity,
                identity.ResultSchemaVersion,
                identity.DescriptorHash));
        var response = await _transport.InvokeActionAsync(
            childRequest,
            (terminalRequest, terminalCancellation) => ExecuteTerminalAsync(
                terminal,
                terminalRequest,
                _transport.Binding.SafeFailure,
                terminalCancellation,
                _transport,
                contribution),
            cancellationToken);
        ThrowIfHostEntryFailed(response);
        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
    }

    public async ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
        ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_parentTerminalRequest is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.UnsupportedCapability,
                "Cross-sidecar action entry requires an authenticated parent terminal exchange.");
        }

        var identity = OutOfProcessActionDescriptorIdentity.Create(request.Descriptor);
        var deadline = _parentTerminalRequest.Deadline;
        if (_parentTerminalRequest.Call.Deadline < deadline)
            deadline = _parentTerminalRequest.Call.Deadline;
        var action = OutOfProcessActionDispatcher.Payload(
            request.Action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var neutralRequest = new SidecarCrossSidecarActionEntryRequest(
            identity.Key,
            identity.Version,
            action,
            deadline,
            deadline);
        var relayResponse = await _transport.InvokeActionTerminalAsync(
            _parentTerminalRequest with
            {
                CrossSidecarActionRequest = neutralRequest,
                NestedCarrierRequest = null,
            },
            cancellationToken);
        var relay = relayResponse.CrossSidecarRelay;
        if (relay is null)
        {
            throw new OutOfProcessCapabilityException(
                relayResponse.CrossSidecarOutcome?.Failure?.Code
                    ?? SidecarCapabilityErrors.UnknownAction,
                relayResponse.CrossSidecarOutcome?.Failure?.Message
                    ?? "The host did not issue a target action-entry carrier.");
        }

        if (!OutOfProcessActionDescriptorIdentity.Matches(
                identity,
                relay.TargetEntry.Descriptor))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The target relay descriptor does not match the requested typed action.");
        }

        if (relayResponse.CrossSidecarOutcome is not { } crossOutcome
            || !crossOutcome.IsWellFormed
            || crossOutcome.Outcome is not { } outcome)
        {
            throw new OutOfProcessCapabilityException(
                relayResponse.CrossSidecarRelay is null
                    ? relayResponse.Execution.Failure?.Code
                        ?? SidecarCapabilityErrors.UnknownAction
                    : SidecarCapabilityErrors.HostFailure,
                relayResponse.Execution.Failure?.Message
                    ?? "The target sidecar action did not return a terminal outcome.");
        }

        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(
            new SidecarActionCapabilityResponse(
                relayResponse.ResultIdentity,
                outcome,
                Continuation: null,
                SafeFailure: relayResponse.SafeFailure,
                Completed: true));
    }

    private static void ThrowIfHostEntryFailed(SidecarActionCapabilityResponse response)
    {
        var error = response.Outcome.Error;
        if (error is not null
            && string.Equals(
                error.Code,
                SidecarCapabilityErrors.HostFailure,
                StringComparison.Ordinal)
            && (response.Outcome.Kind is not (ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred)
                || response.Outcome.Result is null))
        {
            throw new OutOfProcessCapabilityException(
                error.Code,
                error.Message);
        }
    }

    private static async ValueTask<SidecarActionTerminalTransportResponse> ExecuteTerminalAsync<TAction, TResult>(
        IHostActionEntryTerminal<TAction, TResult> terminal,
        SidecarActionTerminalTransportRequest request,
        SidecarSafeFailureIdentity safeFailure,
        CancellationToken ct,
        OutOfProcessModuleCapabilityTransport transport,
        HostActionEntryContribution? parentContribution)
    {
        var action = JsonSerializer.Deserialize<TAction>(
                request.EffectiveAction.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host action terminal received no action value.");
        var identity = request.Descriptor;
        var context = OutOfProcessActionDispatcher.CreateActionContext(
            request,
            action,
            new OutOfProcessHostActionEntry(
                transport,
                identity,
                request,
                parentContribution));
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
                safeFailure)
            {
                TerminalId = request.TerminalId,
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            transport.RecordTerminalFailure(ex);
            return new SidecarActionTerminalTransportResponse(
                null,
                new SidecarTerminalExecutionResult(
                    null,
                    safeFailure,
                    Completed: true),
                request.Receipt,
                safeFailure)
            {
                TerminalId = request.TerminalId,
            };
        }
    }

    private static bool MatchesParentContext<TAction>(
        ActionContext<TAction> actual,
        SidecarActionTerminalExecutionContext? expected)
    {
        if (expected is null)
            return false;

        return actual.InvocationId == expected.InvocationId
            && actual.ParentInvocationId == expected.ParentInvocationId
            && actual.TraceId == expected.TraceId
            && actual.IdempotencyKey == expected.IdempotencyKey
            && actual.Depth == expected.Depth
            && actual.Attempt == expected.Attempt
            && actual.Deadline == expected.Deadline
            && actual.ActionKey == expected.Descriptor.Key
            && OutOfProcessHostActionEntryContextRegistry.MatchesCaller(
                actual.Caller,
                expected.Caller)
            && string.Equals(
                SidecarCapabilityTransportCodec.ComputeSha256(
                    SidecarCapabilityTransportCodec.Serialize(actual.Features)),
                SidecarCapabilityTransportCodec.ComputeSha256(
                    SidecarCapabilityTransportCodec.Serialize(expected.Features)),
                StringComparison.OrdinalIgnoreCase);
    }
}
