using System.Collections.Concurrent;
using System.Net.WebSockets;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession
{
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<OutOfProcessEndpointRouteReservationResponse>>
        _endpointRouteReservationResponses = new();
    private readonly ConcurrentDictionary<
        Guid,
        TaskCompletionSource<OutOfProcessEndpointRouteRelayResponse>>
        _endpointRouteRelayResponses = new();
    private readonly ConcurrentDictionary<Guid, PendingEndpointTypedActionChild>
        _pendingEndpointTypedActionChildren = new();
    private readonly ConcurrentDictionary<Guid, EndpointRouteLease>
        _activeEndpointRoutes = new();

    private sealed class PendingEndpointTypedActionChild(
        SidecarEndpointTypedActionChildReservation reservation,
        OutOfProcessActionDescriptorCatalog.Registration registration,
        SidecarActionTerminalRegistration terminal)
    {
        internal SidecarEndpointTypedActionChildReservation Reservation { get; } = reservation;

        internal OutOfProcessActionDescriptorCatalog.Registration Registration { get; } = registration;

        internal SidecarActionTerminalRegistration Terminal { get; } = terminal;

        internal SidecarNestedHostActionEntryRelay? ChildRelay { get; set; }

        internal HostActionEntryRequestContext? HostContext { get; set; }

        internal SidecarActionCapabilityRequest? Request { get; set; }

        internal ActiveCall? ActiveCall { get; set; }
    }

    internal bool HasPendingEndpointTypedActionChildWork =>
        !_pendingEndpointTypedActionChildren.IsEmpty;

    internal sealed class EndpointRouteLease(
        HostEndpointRouteRequest request,
        HostActionEntryRequestContext context,
        SidecarCapabilityCallIdentity sourceCall,
        SidecarHostEndpointRouteRelay relay) : IDisposable
    {
        private int _disposed;

        internal HostEndpointRouteRequest Request { get; } = request;

        internal HostActionEntryRequestContext Context { get; } = context;

        internal SidecarCapabilityCallIdentity SourceCall { get; } = sourceCall;

        internal SidecarHostEndpointRouteRelay Relay { get; } = relay;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    internal async ValueTask<EndpointRouteLease> BeginEndpointRouteAsync(
        HostEndpointRouteRequest request,
        HostActionEntryRequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var now = DateTimeOffset.UtcNow;
        if (!request.IsWellFormed(now))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.InvalidBinding,
                "The endpoint route request is incomplete.");
        }

        if (!HostActionEntryAuthorityValidator.SameContextIgnoringPayload(
                context,
                request.Invocation.HostActionContext))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The endpoint route context does not match the issued host context.");
        }

        await WaitForRotationAsync(
            ct,
            context.CapabilityId,
            allowPendingCarrier: true);
        await _rotationGate.WaitAsync(ct);

        SidecarHostEndpointRouteReservation? reservation = null;
        SidecarHostEndpointRouteRelay? relay = null;
        SidecarCapabilityCallIdentity? sourceCall = null;
        var carrierStarted = false;
        try
        {
            if (!_options.HostActionEntryContexts.TryBeginCarrier(context))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint host action context is not pending for a carrier.");
            }

            var carrier = new HostActionEntryCarrierIdentity(
                context.Ingress,
                context.InvocationId,
                context.Contribution!.IngressBinding);

            var reservationCompletion = new TaskCompletionSource<
                OutOfProcessEndpointRouteReservationResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_endpointRouteReservationResponses.TryAdd(
                    request.Invocation.InvocationId,
                    reservationCompletion))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route reservation identifier was reused.");
            }

            try
            {
                await OutOfProcessCapabilityWire.SendAsync(
                    _socket,
                    OutOfProcessCapabilityFrameKind.EndpointRouteReservationRequest,
                    request,
                    _limits.ProtocolMessageBytes,
                    SendGate,
                    ct);
                var reservationResponse = await reservationCompletion.Task.WaitAsync(ct);
                if (!reservationResponse.Validation.Accepted ||
                    reservationResponse.Reservation is null)
                {
                    throw new OutOfProcessCapabilityException(
                        reservationResponse.Validation.Code
                            ?? SidecarCapabilityErrors.Unauthorized,
                        reservationResponse.Validation.Message
                            ?? "The module rejected the endpoint route reservation.");
                }

                reservation = reservationResponse.Reservation;
            }
            finally
            {
                _endpointRouteReservationResponses.TryRemove(
                    request.Invocation.InvocationId,
                    out _);
            }

            sourceCall = CreateOutgoingCall(context.Deadline);
            if (!_outgoingCapabilityCalls.TryAdd(sourceCall.CallId, 0))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route call identifier was reused.");
            }

            var relayResult = Session.IssueHostEndpointRouteRelay(
                request,
                sourceCall,
                reservation,
                DateTimeOffset.UtcNow,
                authority => OutOfProcessCapabilitySecurity
                    .CreateEndpointRouteAuthorityProof(authority, _controlToken),
                (candidate, bindingHash) =>
                    string.Equals(
                        candidate.CanonicalBindingHash,
                        bindingHash,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        OutOfProcessCapabilitySecurity.CreateEndpointRouteReservationProof(
                            candidate,
                            _controlToken),
                        candidate.Proof,
                        StringComparison.Ordinal),
                (candidate, bindingHash) =>
                    OutOfProcessCapabilitySecurity.CreateEndpointRouteRelayProof(
                        candidate,
                        _controlToken),
                out relay);
            if (!relayResult.Accepted || relay is null)
            {
                throw new OutOfProcessCapabilityException(
                    relayResult.Code ?? SidecarCapabilityErrors.Unauthorized,
                    relayResult.Message
                        ?? "The host could not issue the endpoint route relay.");
            }

            var carrierResult = Session.BeginHostEndpointRouteCarrier(
                request,
                relay.Authority,
                carrier,
                DateTimeOffset.UtcNow,
                out var carrierAuthority);
            if (!carrierResult.Accepted || carrierAuthority is null)
            {
                throw new OutOfProcessCapabilityException(
                    carrierResult.Code ?? SidecarCapabilityErrors.Unauthorized,
                    carrierResult.Message
                        ?? "The endpoint route carrier was rejected.");
            }

            carrierStarted = true;
            var relayCompletion = new TaskCompletionSource<
                OutOfProcessEndpointRouteRelayResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_endpointRouteRelayResponses.TryAdd(
                    request.Invocation.InvocationId,
                    relayCompletion))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route relay identifier was reused.");
            }

            try
            {
                await OutOfProcessCapabilityWire.SendAsync(
                    _socket,
                    OutOfProcessCapabilityFrameKind.EndpointRouteRelay,
                    relay,
                    _limits.ProtocolMessageBytes,
                    SendGate,
                    ct);
                var relayResponse = await relayCompletion.Task.WaitAsync(ct);
                if (!relayResponse.Validation.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        relayResponse.Validation.Code
                            ?? SidecarCapabilityErrors.Unauthorized,
                        relayResponse.Validation.Message
                            ?? "The module rejected the endpoint route relay.");
                }
            }
            finally
            {
                _endpointRouteRelayResponses.TryRemove(
                    request.Invocation.InvocationId,
                    out _);
            }

            var lease = new EndpointRouteLease(request, context, sourceCall, relay);
            if (!_activeEndpointRoutes.TryAdd(context.CapabilityId, lease))
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint route carrier was already admitted.");

            return lease;
        }
        catch
        {
            if (relay is not null && carrierStarted)
            {
                Session.CompleteHostEndpointRouteRelay(relay, DateTimeOffset.UtcNow);
            }

            if (reservation is not null)
            {
                await TryReleaseEndpointRouteReservationAsync(reservation);
            }

            if (sourceCall is not null)
                ReleaseOutgoingCapabilityCall(sourceCall.CallId);

            if (!carrierStarted)
                _options.HostActionEntryContexts.RestorePendingCarrier(context);
            else
                _options.HostActionEntryContexts.CompleteCarrier(context.CapabilityId);

            RequestRotationRetry();
            throw;
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    internal async ValueTask CompleteEndpointRouteAsync(
        EndpointRouteLease lease,
        HostActionEntryCarrierCompletionKind completion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var completed = false;
        try
        {
            WaitForNestedHostActionCallsToFinish(
                lease.SourceCall.CallId,
                lease.Context,
                ct);
            await _rotationGate.WaitAsync(ct);
            try
            {
                var result = Session.CompleteHostEndpointRouteRelay(
                    lease.Relay,
                    DateTimeOffset.UtcNow);
                if (!result.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        result.Code ?? SidecarCapabilityErrors.Unauthorized,
                        result.Message
                            ?? "The endpoint route relay could not be completed.");
                }

                completed = true;
            }
            finally
            {
                _rotationGate.Release();
            }
        }
        finally
        {
            _activeEndpointRoutes.TryRemove(
                lease.Context.CapabilityId,
                out _);
            if (completed)
                _options.HostActionEntryContexts.CompleteCarrier(
                    lease.Context.CapabilityId);
            else
                _options.HostActionEntryContexts.CompleteCarrier(
                    lease.Context.CapabilityId);

            ReleaseOutgoingCapabilityCall(lease.SourceCall.CallId);
            ArmRotationAfterCarrier();
            RequestRotationRetry();
        }

        if (completed && !_disconnect.IsCancellationRequested)
            await StartRotationIfReadyAsync(_disconnect.Token);
    }

    internal bool TryGetActiveEndpointRoute(
        Guid capabilityId,
        out EndpointRouteLease? lease) =>
        _activeEndpointRoutes.TryGetValue(capabilityId, out lease);

    internal bool TryGetActiveEndpointRoute(
        HostEndpointRouteAuthority authority,
        out EndpointRouteLease? lease)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var authorityBytes = SidecarCapabilityTransportCodec.Serialize(authority);
        foreach (var candidate in _activeEndpointRoutes.Values)
        {
            if (authorityBytes.SequenceEqual(
                    SidecarCapabilityTransportCodec.Serialize(candidate.Relay.Authority)))
            {
                lease = candidate;
                return true;
            }
        }

        lease = null;
        return false;
    }

    internal void CompleteEndpointRouteReservationResponse(
        OutOfProcessEndpointRouteReservationResponse response)
    {
        if (!_endpointRouteReservationResponses.TryGetValue(
                response.InvocationId,
                out var completion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The endpoint route reservation response does not match an active request.");
        }

        completion.TrySetResult(response);
    }

    internal void CompleteEndpointRouteRelayResponse(
        OutOfProcessEndpointRouteRelayResponse response)
    {
        if (!_endpointRouteRelayResponses.TryGetValue(
                response.InvocationId,
                out var completion))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The endpoint route relay response does not match an active request.");
        }

        completion.TrySetResult(response);
    }

    private async ValueTask TryReleaseEndpointRouteReservationAsync(
        SidecarHostEndpointRouteReservation reservation)
    {
        try
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointRouteReservationRelease,
                reservation,
                _limits.ProtocolMessageBytes,
                SendGate,
                CancellationToken.None);
        }
        catch (Exception) when (
            _disconnect.IsCancellationRequested ||
            _socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
        }
    }

    private async Task HandleEndpointTypedActionChildReservationRequestAsync(
        OutOfProcessEndpointTypedActionChildReservationRequest? request,
        CancellationToken channelCt)
    {
        var invocationId = request?.InvocationId ?? Guid.Empty;
        var validation = SidecarCapabilityValidationResult.Reject(
            SidecarCapabilityErrors.InvalidBinding,
            "The endpoint typed action child reservation request is incomplete.");
        SidecarEndpointTypedActionChildReservation? reservation = null;
        PendingEndpointTypedActionChild? pending = null;

        try
        {
            if (request is null
                || request.ParentRouteAuthority is null
                || request.Descriptor is null
                || request.Action is null
                || request.Terminal is null
                || !request.Terminal.IsWellFormed)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The endpoint typed action child reservation request is incomplete.");
            }

            if (!TryGetActiveEndpointRoute(
                    request.ParentRouteAuthority,
                    out var route)
                || route is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint route parent is not active.");
            }

            if (!_options.ActionDescriptors.TryGet(
                    request.Descriptor,
                    out var registration))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnknownAction,
                    "The endpoint child descriptor is not registered by the host.");
            }

            if (!TerminalMatchesDescriptor(request.Terminal, request.Descriptor))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The endpoint child terminal does not match its descriptor.");
            }

            await _rotationGate.WaitAsync(channelCt);
            try
            {
                if (!TryGetActiveEndpointRoute(
                        request.ParentRouteAuthority,
                        out route)
                    || route is null)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.SpoofedIdentity,
                        "The endpoint route parent is not active.");
                }

                var childCall = CreateOutgoingCall(route.Context.Deadline);
                var issue = Session.IssueHostEndpointTypedActionChildReservation(
                    route.SourceCall,
                    route.Request.Invocation.HostActionContext,
                    childCall,
                    request.Descriptor,
                    request.Action,
                    DateTimeOffset.UtcNow,
                    candidate => OutOfProcessCapabilitySecurity
                        .CreateEndpointTypedActionChildReservationProof(
                            candidate,
                            _controlToken),
                    out reservation);
                if (!issue.Accepted || reservation is null)
                {
                    throw new OutOfProcessCapabilityException(
                        issue.Code ?? SidecarCapabilityErrors.Unauthorized,
                        issue.Message
                            ?? "The endpoint child reservation was rejected.");
                }

                pending = new PendingEndpointTypedActionChild(
                    reservation,
                    registration,
                    request.Terminal);
                if (!_pendingEndpointTypedActionChildren.TryAdd(
                        reservation.ReservationId,
                        pending))
                {
                    Session.ReleaseHostEndpointTypedActionChildReservation(
                        reservation,
                        DateTimeOffset.UtcNow);
                    reservation = null;
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Duplicate,
                        "The endpoint child reservation identifier was reused.");
                }

                validation = SidecarCapabilityValidationResult.Accept();
            }
            finally
            {
                _rotationGate.Release();
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
                "The endpoint child reservation failed.");
            reservation = null;
        }

        if (!channelCt.IsCancellationRequested)
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointTypedActionChildReservationResponse,
                new OutOfProcessEndpointTypedActionChildReservationResponse(
                    invocationId,
                    validation,
                    reservation),
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
    }

    private async Task HandleEndpointTypedActionChildReservationReleaseAsync(
        SidecarEndpointTypedActionChildReservation? reservation,
        CancellationToken channelCt)
    {
        if (reservation is null)
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.InvalidBinding,
                "The endpoint child reservation release is empty.");

        await _rotationGate.WaitAsync(channelCt);
        try
        {
            if (!_pendingEndpointTypedActionChildren.TryGetValue(
                    reservation.ReservationId,
                    out var pending)
                || !SidecarCapabilityTransportCodec.Serialize(
                        pending.Reservation)
                    .SequenceEqual(
                        SidecarCapabilityTransportCodec.Serialize(reservation)))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint child reservation was already released or does not match.");
            }

            if (!_pendingEndpointTypedActionChildren.TryRemove(
                    reservation.ReservationId,
                    out pending))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.Replay,
                    "The endpoint child reservation was already released.");
            }

            var result = Session.ReleaseHostEndpointTypedActionChildReservation(
                pending.Reservation,
                DateTimeOffset.UtcNow);
            if (!result.Accepted && result.Code != SidecarCapabilityErrors.Replay)
            {
                throw new OutOfProcessCapabilityException(
                    result.Code ?? SidecarCapabilityErrors.Unauthorized,
                    result.Message
                        ?? "The endpoint child reservation release was rejected.");
            }
        }
        finally
        {
            _rotationGate.Release();
            RequestRotationRetry();
        }
    }

    private async Task HandleEndpointTypedActionChildRelayAsync(
        SidecarEndpointTypedActionChildRelay? relay,
        CancellationToken channelCt)
    {
        var reservationId = relay?.ReceivingReservation?.ReservationId ?? Guid.Empty;
        var validation = SidecarCapabilityValidationResult.Reject(
            SidecarCapabilityErrors.InvalidBinding,
            "The endpoint typed action child relay is incomplete.");
        SidecarActionCapabilityResponse? response = null;
        SidecarEndpointTypedActionChildImportAcknowledgment? acknowledgment = null;
        SidecarEndpointTypedActionChildImportAbort? abort = null;
        PendingEndpointTypedActionChild? pending = null;
        ActiveCall? active = null;
        var carrierCompleted = false;

        try
        {
            await _rotationGate.WaitAsync(channelCt);
            try
            {
                if (relay is null
                    || relay.ReceivingReservation is null
                    || !_pendingEndpointTypedActionChildren.TryGetValue(
                        relay.ReceivingReservation.ReservationId,
                        out pending)
                    || !SidecarCapabilityTransportCodec.Serialize(
                            pending.Reservation)
                        .SequenceEqual(
                            SidecarCapabilityTransportCodec.Serialize(
                                relay.ReceivingReservation)))
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.Replay,
                        "The endpoint child relay does not match an active reservation.");
                }

                var import = Session.ImportHostEndpointTypedActionChildRelay(
                    relay,
                    DateTimeOffset.UtcNow,
                    candidate => OutOfProcessCapabilitySecurity
                        .CreateEndpointTypedActionChildImportAcknowledgmentProof(
                            candidate,
                            _controlToken),
                    out var childRelay,
                    out var hostContext,
                    out acknowledgment);
                if (!import.Accepted
                    || childRelay is null
                    || hostContext is null
                    || acknowledgment is null)
                {
                    throw new OutOfProcessCapabilityException(
                        import.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                        import.Message
                            ?? "The endpoint child relay import was rejected.");
                }

                pending.ChildRelay = childRelay;
                pending.HostContext = hostContext;
                var childRequest = SidecarActionCapabilityRequest.HostEntryNested(
                    childRelay.Call,
                    pending.Registration.Identity,
                    pending.Reservation.Action,
                    new SidecarCancellationIdentity(
                        childRelay.Call.CancellationId,
                        SidecarCapabilitySessionValidator.ComputeBindingHash(
                            Session.Binding),
                        childRelay.Call.Deadline),
                    childRelay.Call.Deadline,
                    childRelay.Carrier,
                    pending.Terminal);
                active = RegisterCall(
                    childRelay.Call,
                    channelCt,
                    childRequest);
                var begin = Session.BeginNestedHostActionEntryCall(
                    childRelay.Carrier,
                    childRelay.Call,
                    pending.Reservation.Action,
                    pending.Reservation.Action.ByteLength,
                    DateTimeOffset.UtcNow,
                    out var begunContext);
                if (!begin.Accepted || begunContext is null)
                {
                    AbandonCall(childRelay.Call.CallId, active);
                    active = null;
                    throw new OutOfProcessCapabilityException(
                        begin.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                        begin.Message
                            ?? "The endpoint child call was rejected.");
                }

                active.HostContext = begunContext;
                pending.Request = childRequest;
                pending.ActiveCall = active;
            }
            finally
            {
                _rotationGate.Release();
            }

            OutOfProcessActionDispatchResult outcome;
            try
            {
                outcome = await pending!.Registration.Dispatch(
                    this,
                    pending.Request!,
                    active!.Cancellation.Token);
            }
            catch (OperationCanceledException) when (
                active!.Cancellation.IsCancellationRequested)
            {
                outcome = new OutOfProcessActionDispatchResult(
                    ActionOutcomeKind.Cancelled,
                    null,
                    null,
                    null,
                    null,
                    0);
            }
            catch (OutOfProcessCapabilityException exception)
            {
                outcome = new OutOfProcessActionDispatchResult(
                    ActionOutcomeKind.Failed,
                    null,
                    new ExecutionError(exception.Code, exception.Message),
                    null,
                    null,
                    0);
            }
            catch (Exception)
            {
                outcome = new OutOfProcessActionDispatchResult(
                    ActionOutcomeKind.Failed,
                    null,
                    new ExecutionError(
                        SidecarCapabilityErrors.HostFailure,
                        "The endpoint child dispatcher failed."),
                    null,
                    null,
                    0);
            }

            response = CreateActionResponse(
                pending!.Request!,
                pending.Registration,
                outcome);
            validation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                pending.Request!,
                response,
                Session.Binding,
                Session);
            if (!validation.Accepted)
                throw new OutOfProcessCapabilityException(
                    validation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                    validation.Message
                        ?? "The endpoint child response was rejected.");

            WaitForNestedHostActionCallsToFinish(
                pending.Request!.Call.CallId,
                active!.HostContext!,
                channelCt);
            if (!CompleteCall(
                    pending.Request.Call.CallId,
                    response.Outcome.TerminalCallCount))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The endpoint child call could not be completed.");
            }
            // Contracts consumes the nested carrier as part of child-call completion.
            carrierCompleted = true;
            await FinishCallAsync(
                pending.Request.Call.CallId,
                active,
                channelCt);
            active = null;
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
            response = null;
        }
        catch (Exception)
        {
            validation = SidecarCapabilityValidationResult.Reject(
                SidecarCapabilityErrors.HostFailure,
                "The endpoint child relay failed.");
            response = null;
        }
        finally
        {
            if (active is not null)
            {
                try
                {
                    CompleteCall(active.ActionRequest?.Call.CallId ?? Guid.Empty, 0);
                    await FinishCallAsync(
                        active.ActionRequest?.Call.CallId ?? Guid.Empty,
                        active,
                        CancellationToken.None);
                }
                catch
                {
                }
            }

            if (!carrierCompleted && pending?.ChildRelay is { } childRelay)
            {
                try
                {
                    Session.RevokeNestedHostActionEntryRelay(
                        childRelay.Carrier.ParentCallId,
                        DateTimeOffset.UtcNow);
                }
                catch
                {
                }
            }

            if (pending is not null)
                _pendingEndpointTypedActionChildren.TryRemove(
                    pending.Reservation.ReservationId,
                    out _);
            RequestRotationRetry();
        }

        if (acknowledgment is null && relay is not null)
        {
            var abortResult = Session.IssueHostEndpointTypedActionChildImportAbort(
                relay,
                DateTimeOffset.UtcNow,
                candidate => OutOfProcessCapabilitySecurity
                    .CreateEndpointTypedActionChildImportAbortProof(
                        candidate,
                        _controlToken),
                out abort);
            if (!abortResult.Accepted)
                abort = null;
        }

        if (!channelCt.IsCancellationRequested)
        {
            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.EndpointTypedActionChildRelayResponse,
                new OutOfProcessEndpointTypedActionChildRelayResponse(
                    reservationId,
                    validation,
                    response,
                    acknowledgment,
                    abort),
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
    }

    internal void CompleteEndpointTypedActionChildImportAbortResponse(
        OutOfProcessEndpointTypedActionChildImportAbortResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.Abort);
        if (!response.Validation.Accepted)
        {
            RequestRotationRetry();
            return;
        }

        var completion = Session.CompleteHostEndpointTypedActionChildImportAbort(
            response.Abort,
            DateTimeOffset.UtcNow);
        if (!completion.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                completion.Code ?? SidecarCapabilityErrors.Unauthorized,
                completion.Message
                    ?? "The endpoint child import abort completion was rejected.");
        }

        RequestRotationRetry();
    }

    private static bool TerminalMatchesDescriptor(
        SidecarActionTerminalRegistration terminal,
        SidecarActionDescriptorIdentity descriptor) =>
        terminal.IsWellFormed
        && terminal.ActionTypeIdentity == descriptor.InputTypeIdentity
        && terminal.ActionSchemaVersion == descriptor.InputSchemaVersion
        && terminal.ResultTypeIdentity == descriptor.ResultTypeIdentity
        && terminal.ResultSchemaVersion == descriptor.ResultSchemaVersion
        && terminal.DescriptorHash == descriptor.DescriptorHash;

}
