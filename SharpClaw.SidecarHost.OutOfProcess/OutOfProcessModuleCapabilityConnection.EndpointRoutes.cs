using System.Collections.Concurrent;
using System.Net.WebSockets;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed partial class OutOfProcessModuleCapabilityConnection
{
    private readonly ConcurrentDictionary<Guid, SidecarHostEndpointRouteReservation>
        _endpointRouteReservations = new();
    private readonly ConcurrentDictionary<Guid, ImportedEndpointRouteState>
        _importedEndpointRoutes = new();
    private readonly ConcurrentDictionary<Guid, ImportedEndpointRouteLease>
        _activeEndpointRouteStates = new();
    private readonly ConcurrentDictionary<Guid, PendingEndpointTypedActionChild>
        _endpointTypedActionChildren = new();
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<OutOfProcessEndpointTypedActionChildReservationResponse>>
        _endpointTypedActionChildReservationResponses = new();
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<OutOfProcessEndpointTypedActionChildRelayResponse>>
        _endpointTypedActionChildRelayResponses = new();
    private readonly object _endpointRouteSync = new();

    private sealed record ImportedEndpointRouteState(
        SidecarHostEndpointRouteRelay Relay,
        HostActionEntryRequestContext Context,
        SidecarCapabilityCallIdentity Call);

    private sealed class PendingEndpointTypedActionChild(
        SidecarEndpointTypedActionChildRelay relay,
        SidecarActionCapabilityRequest request,
        SidecarActionTerminalRegistration terminal,
        Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? terminalCallback)
    {
        internal SidecarEndpointTypedActionChildRelay Relay { get; } = relay;

        internal SidecarActionCapabilityRequest Request { get; } = request;

        internal SidecarActionTerminalRegistration Terminal { get; } = terminal;

        internal Func<
            SidecarActionTerminalTransportRequest,
            CancellationToken,
            ValueTask<SidecarActionTerminalTransportResponse>>? TerminalCallback { get; } = terminalCallback;
    }

    internal sealed class ImportedEndpointRouteLease(
        SidecarHostEndpointRouteRelay relay,
        HostActionEntryRequestContext context,
        SidecarCapabilityCallIdentity call,
        HostActionEntryCarrierAuthority carrier) : IDisposable
    {
        private int _disposed;

        internal SidecarHostEndpointRouteRelay Relay { get; } = relay;

        internal HostActionEntryRequestContext Context { get; } = context;

        internal SidecarCapabilityCallIdentity Call { get; } = call;

        internal HostActionEntryCarrierAuthority Carrier { get; } = carrier;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private bool HasPendingEndpointRouteWork() =>
        !_endpointRouteReservations.IsEmpty
        || !_importedEndpointRoutes.IsEmpty
        || !_activeEndpointRouteStates.IsEmpty
        || !_endpointTypedActionChildren.IsEmpty
        || !_endpointTypedActionChildReservationResponses.IsEmpty
        || !_endpointTypedActionChildRelayResponses.IsEmpty;

    private async Task HandleEndpointRouteReservationRequestAsync(
        HostEndpointRouteRequest? request,
        CancellationToken channelCt)
    {
        var invocationId = request?.Invocation?.InvocationId ?? Guid.Empty;
        SidecarCapabilityValidationResult validation;
        SidecarHostEndpointRouteReservation? reservation = null;
        SidecarCapabilityCallIdentity? call = null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (request is null || !request.IsWellFormed(now))
            {
                validation = SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The endpoint route reservation request is incomplete.");
            }
            else
            {
                call = CreateCall(
                    SidecarCapabilityKind.Action,
                    request.Invocation.HostActionContext.Deadline,
                    channelCt);
                validation = _session.IssueHostEndpointRouteReservation(
                    request,
                    call,
                    now,
                    candidate => OutOfProcessCapabilitySecurity
                        .CreateEndpointRouteReservationProof(candidate, _controlToken),
                    out reservation);
                if (validation.Accepted && reservation is null)
                {
                    validation = SidecarCapabilityValidationResult.Reject(
                        SidecarCapabilityErrors.Unauthenticated,
                        "The endpoint route reservation was not issued.");
                }
                else if (validation.Accepted && reservation is not null
                    && !_endpointRouteReservations.TryAdd(
                        reservation.ReservationId,
                        reservation))
                {
                    _session.ReleaseHostEndpointRouteReservation(
                        reservation,
                        DateTimeOffset.UtcNow);
                    reservation = null;
                    validation = SidecarCapabilityValidationResult.Reject(
                        SidecarCapabilityErrors.Duplicate,
                        "The endpoint route reservation identifier was reused.");
                }
            }
        }
        catch (OperationCanceledException) when (channelCt.IsCancellationRequested)
        {
            return;
        }
        catch (OutOfProcessCapabilityException exception)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                exception.Code,
                exception.Message);
            reservation = null;
        }
        catch (Exception)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                SidecarCapabilityErrors.HostFailure,
                "The endpoint route reservation failed.");
            reservation = null;
        }
        finally
        {
            if (call is not null)
                CompleteOutgoingCallSequence(call);
        }

        if (!channelCt.IsCancellationRequested)
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointRouteReservationResponse,
                new OutOfProcessEndpointRouteReservationResponse(
                    invocationId,
                    validation,
                    reservation),
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
    }

    private async Task HandleEndpointRouteReservationReleaseAsync(
        SidecarHostEndpointRouteReservation? reservation,
        CancellationToken channelCt)
    {
        if (reservation is null)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.InvalidBinding,
                "The endpoint route reservation release is empty.");

        SidecarHostEndpointRouteReservation? stored = null;
        if (_endpointRouteReservations.TryRemove(reservation.ReservationId, out stored)
            && SidecarCapabilityTransportCodec.Serialize(stored).SequenceEqual(
                SidecarCapabilityTransportCodec.Serialize(reservation)))
        {
            var result = _session.ReleaseHostEndpointRouteReservation(
                stored,
                DateTimeOffset.UtcNow);
            if (!result.Accepted && result.Code != SidecarCapabilityErrors.Replay)
            {
                throw new OutOfProcessCapabilityException(
                    result.Code ?? SidecarCapabilityErrors.Unauthorized,
                    result.Message ?? "The endpoint route reservation release was rejected.");
            }

            return;
        }

        throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.Replay,
            "The endpoint route reservation was already released or does not match.");
    }

    private async Task HandleEndpointRouteRelayAsync(
        SidecarHostEndpointRouteRelay? relay,
        CancellationToken channelCt)
    {
        var invocationId = relay?.Request?.Invocation?.InvocationId ?? Guid.Empty;
        SidecarCapabilityValidationResult validation;
        HostActionEntryRequestContext? context = null;
        try
        {
            validation = relay is null
                ? SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The endpoint route relay is empty.")
                : _session.ImportHostEndpointRouteRelay(
                    relay,
                    DateTimeOffset.UtcNow,
                    out context);
            if (validation.Accepted && context is null)
            {
                validation = SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint route relay returned no receiving context.");
            }
            else if (validation.Accepted && relay is not null && context is not null)
            {
                var state = new ImportedEndpointRouteState(
                    relay,
                    context,
                    relay.ReceivingParentCall);
                if (!_importedEndpointRoutes.TryAdd(invocationId, state))
                {
                    if (_session.TryGetActiveHostActionEntryCarrier(
                            context.CapabilityId,
                            out var carrier)
                        && carrier is not null)
                    {
                        _session.CompleteHostActionEntryCarrier(
                            carrier,
                            HostActionEntryCarrierCompletionKind.Failed,
                            DateTimeOffset.UtcNow);
                    }

                    context = null;
                    validation = SidecarCapabilityValidationResult.Reject(
                        SidecarCapabilityErrors.Duplicate,
                        "The endpoint route relay identifier was reused.");
                }
                else
                {
                    if (relay.ReceivingReservation is not null)
                    {
                        _endpointRouteReservations.TryRemove(
                            relay.ReceivingReservation.ReservationId,
                            out _);
                    }

                }
            }
        }
        catch (OutOfProcessCapabilityException exception)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                exception.Code,
                exception.Message);
            context = null;
        }
        catch (Exception)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                SidecarCapabilityErrors.HostFailure,
                "The endpoint route relay import failed.");
            context = null;
        }

        if (!channelCt.IsCancellationRequested)
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointRouteRelayResponse,
                new OutOfProcessEndpointRouteRelayResponse(
                    invocationId,
                    validation),
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
    }

    internal SidecarCapabilityValidationResult BeginImportedEndpointRoute(
        HostEndpointRouteRequest request,
        DateTimeOffset now,
        out ImportedEndpointRouteLease? lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        lease = null;
        if (!request.IsWellFormed(now))
            return SidecarCapabilityValidationResult.Reject(
                SidecarCapabilityErrors.InvalidBinding,
                "The endpoint route request is incomplete.");

        var invocationId = request.Invocation.InvocationId;
        lock (_endpointRouteSync)
        {
            if (!_importedEndpointRoutes.TryGetValue(invocationId, out var state))
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route relay is not active.");

            var authorityResult = HostEndpointRouteAuthorityValidator.Validate(
                request,
                state.Relay.Authority,
                now,
                (authority, bindingHash) =>
                    string.Equals(
                        authority.CanonicalBindingHash,
                        bindingHash,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        OutOfProcessCapabilitySecurity.CreateEndpointRouteAuthorityProof(
                            authority,
                            _controlToken),
                        authority.Proof,
                        StringComparison.Ordinal));
            if (!authorityResult.Accepted)
                return authorityResult;

            var payload = OutOfProcessActionDispatcher.Payload(
                request.Invocation,
                typeof(HostEndpointInvocation).AssemblyQualifiedName!,
                schemaVersion: 1);
            var beginResult = _session.BeginCall(
                state.Call,
                SidecarCapabilityKind.Action,
                payload,
                payload.ByteLength,
                now,
                state.Context);
            if (!beginResult.Accepted)
                return beginResult;

            if (!_importedEndpointRoutes.TryRemove(invocationId, out _))
            {
                CompleteCall(state.Call.CallId, 0);
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route relay was already consumed.");
            }

            if (!_session.TryGetActiveHostActionEntryCarrier(
                    state.Context.CapabilityId,
                    out var carrier)
                || carrier is null)
            {
                CompleteCall(state.Call.CallId, 0);
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint route carrier is not active.");
            }

            var candidateLease = new ImportedEndpointRouteLease(
                state.Relay,
                state.Context,
                state.Call,
                carrier);
            if (!_activeEndpointRouteStates.TryAdd(
                    state.Context.CapabilityId,
                    candidateLease))
            {
                CompleteCall(state.Call.CallId, 0);
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route carrier was already consumed.");
            }

            lease = candidateLease;
            return beginResult;
        }
    }

    internal void RejectImportedEndpointRoute(
        Guid invocationId,
        DateTimeOffset now)
    {
        lock (_endpointRouteSync)
        {
            if (!_importedEndpointRoutes.TryRemove(invocationId, out var state))
                return;

            if (_session.TryGetActiveHostActionEntryCarrier(
                    state.Context.CapabilityId,
                    out var carrier)
                && carrier is not null)
            {
                _session.CompleteHostActionEntryCarrier(
                    carrier,
                    HostActionEntryCarrierCompletionKind.Failed,
                    now);
            }
        }

    }

    internal void CompleteImportedEndpointRoute(
        ImportedEndpointRouteLease lease,
        HostActionEntryCarrierCompletionKind completion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        try
        {
            var callResult = CompleteCallResult(lease.Call.CallId, 0);
            if (!callResult.Accepted)
                throw new OutOfProcessCapabilityException(
                    callResult.Code ?? SidecarCapabilityErrors.Unauthorized,
                    callResult.Message ?? "The endpoint route call could not be completed.");

            var carrierResult = _session.CompleteHostActionEntryCarrier(
                lease.Carrier,
                completion,
                now);
            if (!carrierResult.Accepted)
                throw new OutOfProcessCapabilityException(
                    carrierResult.Code ?? SidecarCapabilityErrors.Unauthorized,
                    carrierResult.Message ?? "The endpoint route carrier could not be completed.");
        }
        finally
        {
            _activeEndpointRouteStates.TryRemove(
                lease.Context.CapabilityId,
                out _);
        }
    }

    internal bool TryGetActiveEndpointRoute(
        HostActionEntryRequestContext sourceContext,
        out ImportedEndpointRouteLease? lease)
    {
        ArgumentNullException.ThrowIfNull(sourceContext);

        foreach (var candidate in _activeEndpointRouteStates.Values)
        {
            if (!SidecarCapabilityTransportCodec.Serialize(candidate.Context).SequenceEqual(
                    SidecarCapabilityTransportCodec.Serialize(sourceContext)))
                continue;

            lease = candidate;
            return true;
        }

        lease = null;
        return false;
    }

    internal async ValueTask<SidecarActionCapabilityResponse>
        InvokeEndpointTypedActionChildAsync(
            ImportedEndpointRouteLease routeLease,
            SidecarActionDescriptorIdentity descriptor,
            SidecarSerializedPayload action,
            SidecarActionTerminalRegistration terminal,
            Func<
                SidecarActionTerminalTransportRequest,
                CancellationToken,
                ValueTask<SidecarActionTerminalTransportResponse>>? terminalCallback,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(routeLease);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(terminal);

        if (!descriptor.IsWellFormed
            || !terminal.IsWellFormed
            || !string.Equals(
                action.TypeIdentity,
                descriptor.InputTypeIdentity,
                StringComparison.Ordinal)
            || action.SchemaVersion != descriptor.InputSchemaVersion
            || !SidecarCapabilityTransportValidation.ValidateSerializedPayload(
                    action,
                    required: true,
                    Binding.PayloadLimits.ActionInputBytes)
                .Accepted)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The endpoint child action is not bound to its descriptor.");
        }

        if (!_activeEndpointRouteStates.TryGetValue(
                routeLease.Context.CapabilityId,
                out var activeRoute)
            || !ReferenceEquals(activeRoute, routeLease))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The endpoint route carrier is not active.");
        }

        var deadline = routeLease.Context.Deadline;
        if (routeLease.Call.Deadline < deadline)
            deadline = routeLease.Call.Deadline;
        using var deadlineCts = CreateCallCancellation(deadline, ct);
        var callCancellation = deadlineCts.Token;
        var reservationInvocationId = Guid.NewGuid();
        var reservationCompletion = NewCompletion<
            OutOfProcessEndpointTypedActionChildReservationResponse>();
        if (!_endpointTypedActionChildReservationResponses.TryAdd(
                reservationInvocationId,
                reservationCompletion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The endpoint child reservation request identifier was reused.");
        }

        SidecarEndpointTypedActionChildReservation? reservation = null;
        SidecarEndpointTypedActionChildRelay? relay = null;
        SidecarActionCapabilityRequest? childRequest = null;
        TaskCompletionSource<OutOfProcessEndpointTypedActionChildRelayResponse>? relayCompletion = null;
        var relaySent = false;
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointTypedActionChildReservationRequest,
                new OutOfProcessEndpointTypedActionChildReservationRequest(
                    reservationInvocationId,
                    routeLease.Relay.Authority,
                    descriptor,
                    action,
                    terminal),
                _limits.ProtocolMessageBytes,
                SendGate,
                callCancellation);
            var reservationResponse = await reservationCompletion.Task.WaitAsync(
                callCancellation);
            ThrowIfRejected(reservationResponse.Validation);
            reservation = reservationResponse.Reservation
                ?? throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthenticated,
                    "The endpoint child reservation response has no reservation.");
            if (reservation.ParentRouteAuthority is null
                || !SidecarCapabilityTransportCodec.Serialize(
                        reservation.ParentRouteAuthority)
                    .SequenceEqual(
                        SidecarCapabilityTransportCodec.Serialize(
                            routeLease.Relay.Authority)))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint child reservation is bound to another route.");
            }

            if (reservation.Child is null
                || !SidecarCapabilityTransportCodec.Serialize(
                        reservation.Child.Descriptor)
                    .SequenceEqual(
                        SidecarCapabilityTransportCodec.Serialize(descriptor))
                || !SidecarCapabilityTransportCodec.Serialize(
                        reservation.Action)
                    .SequenceEqual(
                        SidecarCapabilityTransportCodec.Serialize(action)))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint child reservation does not match the requested action.");
            }

            var relayResult = _session.IssueHostEndpointTypedActionChildRelay(
                routeLease.Relay.Authority,
                routeLease.Call,
                routeLease.Context,
                reservation,
                DateTimeOffset.UtcNow,
                (candidate, canonicalBindingHash) =>
                    string.Equals(
                        candidate.CanonicalBindingHash,
                        canonicalBindingHash,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        OutOfProcessCapabilitySecurity
                            .CreateEndpointTypedActionChildReservationProof(
                                candidate,
                                _controlToken),
                        candidate.Proof,
                        StringComparison.Ordinal),
                candidate => OutOfProcessCapabilitySecurity
                    .CreateEndpointTypedActionChildRelayProof(
                        candidate,
                        _controlToken),
                out relay);
            ThrowIfRejected(relayResult);
            if (relay is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthenticated,
                    "The endpoint child relay was not issued.");
            }

            var childCancellation = new SidecarCancellationIdentity(
                relay.Child.Call.CancellationId,
                SidecarCapabilitySessionValidator.ComputeBindingHash(Binding),
                relay.Child.Call.Deadline);

            childRequest = SidecarActionCapabilityRequest.HostEntryNested(
                relay.Child.Call,
                descriptor,
                reservation.Action,
                childCancellation,
                relay.Child.Call.Deadline,
                relay.Child.Carrier,
                terminal);
            var pending = new PendingEndpointTypedActionChild(
                relay,
                childRequest,
                terminal,
                terminalCallback);
            if (!_endpointTypedActionChildren.TryAdd(
                    relay.Child.Call.CallId,
                    pending))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint child call identifier was reused.");
            }

            relayCompletion = NewCompletion<
                OutOfProcessEndpointTypedActionChildRelayResponse>();
            if (!_endpointTypedActionChildRelayResponses.TryAdd(
                    relay.ReceivingReservation.ReservationId,
                    relayCompletion))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint child relay identifier was reused.");
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointTypedActionChildRelay,
                relay,
                _limits.ProtocolMessageBytes,
                SendGate,
                callCancellation);
            relaySent = true;

            var relayResponse = await relayCompletion.Task.WaitAsync(
                callCancellation);
            if (relayResponse.Acknowledgment is not null
                && relayResponse.Abort is not null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint child relay response has conflicting import outcomes.");
            }

            if (relayResponse.Acknowledgment is not null)
            {
                ThrowIfRejected(_session.CompleteHostEndpointTypedActionChildRelay(
                    relay,
                    relayResponse.Acknowledgment,
                    DateTimeOffset.UtcNow));
                ObserveSequence(relay.Child.Call.Sequence);
            }
            else if (relayResponse.Abort is not null)
            {
                if (relayResponse.Validation.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The accepted endpoint child relay response cannot abort its import.");
                }

                var abortValidation = _session
                    .ConsumeHostEndpointTypedActionChildImportAbort(
                        relayResponse.Abort,
                        DateTimeOffset.UtcNow);
                await OutOfProcessCapabilityWire.SendAsync(
                    _socket,
                    OutOfProcessCapabilityFrameKind.EndpointTypedActionChildImportAbortResponse,
                    new OutOfProcessEndpointTypedActionChildImportAbortResponse(
                        relayResponse.Abort,
                        abortValidation),
                    _limits.ProtocolMessageBytes,
                    SendGate,
                    callCancellation);
                ThrowIfRejected(abortValidation);
            }

            ThrowIfRejected(relayResponse.Validation);
            if (relayResponse.Acknowledgment is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Unauthenticated,
                    "The endpoint child relay response has no authenticated import acknowledgment.");
            }

            var response = relayResponse.Response
                ?? throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The endpoint child relay response has no action response.");
            return response;
        }
        catch (OperationCanceledException) when (
            callCancellation.IsCancellationRequested)
        {
            if (relay is not null && relaySent)
            {
                var childCancellation = new SidecarCancellationIdentity(
                    relay.Child.Call.CancellationId,
                    SidecarCapabilitySessionValidator.ComputeBindingHash(Binding),
                    relay.Child.Call.Deadline);
                await SendCancellationAsync(
                    relay.Child.Call,
                    childCancellation,
                    relay.Child.Call.Deadline,
                    callCancellation,
                    ct);
            }
            throw;
        }
        finally
        {
            _endpointTypedActionChildReservationResponses.TryRemove(
                reservationInvocationId,
                out _);
            if (relay is not null)
            {
                _endpointTypedActionChildRelayResponses.TryRemove(
                    relay.ReceivingReservation.ReservationId,
                    out _);
                _endpointTypedActionChildren.TryRemove(
                    relay.Child.Call.CallId,
                    out _);
            }

            if (reservation is not null && !relaySent)
                await TryReleaseEndpointTypedActionChildReservationAsync(
                    reservation);
        }
    }

    internal void CompleteEndpointTypedActionChildReservationResponse(
        OutOfProcessEndpointTypedActionChildReservationResponse response)
    {
        if (!_endpointTypedActionChildReservationResponses.TryGetValue(
                response.InvocationId,
                out var completion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The endpoint child reservation response does not match an active request.");
        }

        completion.TrySetResult(response);
    }

    internal void CompleteEndpointTypedActionChildRelayResponse(
        OutOfProcessEndpointTypedActionChildRelayResponse response)
    {
        if (!_endpointTypedActionChildRelayResponses.TryGetValue(
                response.ReservationId,
                out var completion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The endpoint child relay response does not match an active request.");
        }

        completion.TrySetResult(response);
    }

    private async ValueTask TryReleaseEndpointTypedActionChildReservationAsync(
        SidecarEndpointTypedActionChildReservation reservation)
    {
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointTypedActionChildReservationRelease,
                reservation,
                _limits.ProtocolMessageBytes,
                SendGate,
                CancellationToken.None);
        }
        catch (Exception) when (
            _disconnect.IsCancellationRequested
            || _socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
        }
    }

    private async Task HandleEndpointTypedActionChildTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        PendingEndpointTypedActionChild pending,
        CancellationToken ct)
    {
        var validation = SidecarCapabilityTransportValidation
            .ValidateActionTerminalRequest(
                pending.Request,
                request,
                Binding,
                DateTimeOffset.UtcNow,
                (authority, proof) => ValidateTerminalAuthority(authority, proof));
        ThrowIfRejected(validation);
        ObserveSequence(request.Call.Sequence);
        var terminalCallback = pending.TerminalCallback
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The endpoint child has no terminal callback.");
        using var carrierScope = _transport.PushActiveCarrier(
            pending.Relay.Child.Carrier.CarrierId,
            pending.Relay.Child.Call);
        var response = await terminalCallback(request, ct);
        ThrowIfRejected(
            SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                request,
                response,
                Binding,
                (authority, proof) => ValidateTerminalAuthority(authority, proof)));
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private async Task ObserveEndpointRouteCompletionAsync(Task completion)
    {
        try
        {
            await completion;
        }
        catch (OperationCanceledException) when (_disconnect.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _runFailure, exception);
            _disconnect.Cancel();
        }
    }
}
