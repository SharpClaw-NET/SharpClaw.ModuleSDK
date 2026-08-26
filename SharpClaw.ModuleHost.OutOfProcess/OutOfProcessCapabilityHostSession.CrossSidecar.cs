using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession
{
    private SidecarCrossSidecarActionEntryOutcome? _lastCrossSidecarOutcome;

    internal Func<
        SidecarActionTerminalTransportResponse,
        SidecarActionTerminalTransportResponse>? TestCrossSidecarResponseMutator { get; set; }

    private async Task HandleCrossSidecarTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var crossRequest = request.CrossSidecarActionRequest
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The cross-sidecar terminal request has no neutral child request.");
        SidecarActionCapabilityRequest? initiatingRequest = null;
        if (_calls.TryGetValue(request.Call.CallId, out var active)
            && active.ActionRequest is { } incomingRequest
            && _terminals.ContainsKey(request.Call.CallId))
        {
            initiatingRequest = incomingRequest;
        }
        else if (_outgoingCapabilityCalls.ContainsKey(request.Call.CallId)
            && _outgoingActions.TryGetValue(request.Call.CallId, out var pending)
            && pending.Request is { } outgoingRequest)
        {
            initiatingRequest = outgoingRequest;
        }

        if (initiatingRequest is null || request.Context is null)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Unauthorized,
                "The cross-sidecar terminal request has no active parent terminal exchange.");
        }

        var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalRequest(
            initiatingRequest,
            request,
            Session.Binding,
            DateTimeOffset.UtcNow,
            ValidateTerminalAuthority);
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                validation.Message ?? "The cross-sidecar terminal request was rejected.");
        }

        var crossValidation = SidecarCrossSidecarActionEntryValidation.ValidateRequest(
            crossRequest,
            request.Call,
            Session.Binding,
            DateTimeOffset.UtcNow);
        if (!crossValidation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                crossValidation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                crossValidation.Message ?? "The neutral cross-sidecar request was rejected.");
        }

        var terminalRecord = Session.RecordTerminal(
            request.Call.CallId,
            request.Authority.AuthorityId,
            request.Receipt);
        if (!terminalRecord.Accepted && !Session.TryGetTerminalReceipt(request.Call.CallId, out _))
        {
            throw new OutOfProcessCapabilityException(
                terminalRecord.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                terminalRecord.Message ?? "The parent terminal authority was rejected.");
        }

        var catalog = _options.CrossSidecarActionEntries;
        if (catalog is null
            || !catalog.TryResolve(crossRequest.ActionKey, crossRequest.ActionVersion, out var target))
        {
            var failure = new SidecarSafeFailureIdentity(
                Guid.NewGuid(),
                SidecarCapabilityErrors.UnknownAction,
                "The requested target action entry is not registered.",
                Retryable: false);
            await SendCrossSidecarRelayResponseAsync(request, null, null, failure, ct);
            return;
        }

        var targetEntry = new SidecarModuleActionEntryDefinition(
            target.Entry.ModuleId,
            target.Entry.ContractHash,
            target.Entry.Descriptor,
            target.Entry.ModuleId,
            target.Entry.ContractHash);
        if (!target.Client.CapabilitySession._options.ActionDescriptors.TryGet(
                target.Entry.Descriptor,
                out var targetRegistration))
        {
            await SendCrossSidecarRelayResponseAsync(
                request,
                null,
                null,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    SidecarCapabilityErrors.UnknownAction,
                    "The target host has no exact dispatcher descriptor for the target action entry.",
                    Retryable: false),
                ct);
            return;
        }
        var issuance = Session.IssueCrossSidecarActionEntryRelay(
            request.Call,
            crossRequest,
            target.Client.CapabilitySession.Session,
            targetEntry,
            target.Client.CapabilitySession._options.ActionSnapshot,
            DateTimeOffset.UtcNow,
            target.Client.CapabilitySession.IssueCrossSidecarProof,
            out var relay);
        if (!issuance.Accepted || relay is null)
        {
            await SendCrossSidecarRelayResponseAsync(
                request,
                null,
                null,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    issuance.Code ?? SidecarCapabilityErrors.Unauthorized,
                    issuance.Message ?? "The target action entry authority was rejected.",
                    Retryable: false),
                ct);
            return;
        }

        var targetTerminal = new SidecarActionTerminalRegistration(
            target.Entry.TerminalId,
            target.Entry.Descriptor.InputTypeIdentity,
            target.Entry.Descriptor.InputSchemaVersion,
            target.Entry.Descriptor.ResultTypeIdentity,
            target.Entry.Descriptor.ResultSchemaVersion,
            target.Entry.Descriptor.DescriptorHash);
        var relayRevoked = false;
        try
        {
            var targetResponse = await target.Client.CapabilitySession
                .ExecuteCrossSidecarCarrierAsync(
                    relay.Carrier,
                    targetTerminal,
                    targetRegistration,
                    ct);
            var revocation = Session.RevokeCrossSidecarActionEntry(
                relay.Carrier.CarrierId,
                DateTimeOffset.UtcNow);
            if (!revocation.Accepted
                && !string.Equals(
                    revocation.Code,
                    SidecarCapabilityErrors.Duplicate,
                    StringComparison.Ordinal))
            {
                throw new OutOfProcessCapabilityException(
                    revocation.Code ?? SidecarCapabilityErrors.HostFailure,
                    revocation.Message ?? "The cross-sidecar relay cleanup was rejected.");
            }

            relayRevoked = true;
            await SendCrossSidecarRelayResponseAsync(
                request,
                relay,
                targetResponse,
                null,
                ct,
                target.Client.CapabilitySession);
        }
        finally
        {
            if (!relayRevoked)
            {
                _ = Session.RevokeCrossSidecarActionEntry(
                    relay.Carrier.CarrierId,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    private async Task SendCrossSidecarRelayResponseAsync(
        SidecarActionTerminalTransportRequest request,
        SidecarCrossSidecarActionEntryRelay? relay,
        SidecarActionTerminalTransportResponse? targetResponse,
        SidecarSafeFailureIdentity? failure,
        CancellationToken ct,
        OutOfProcessCapabilityHostSession? targetSession = null)
    {
        var execution = targetResponse?.Execution
            ?? new SidecarTerminalExecutionResult(
                null,
                failure,
                Completed: true);
        var response = new SidecarActionTerminalTransportResponse(
            targetResponse?.ResultIdentity,
            execution,
            request.Receipt,
            targetResponse?.SafeFailure ?? _session.Binding.SafeFailure)
        {
            TerminalId = request.TerminalId,
            CrossSidecarRelay = relay,
            CrossSidecarOutcome = targetResponse?.CrossSidecarOutcome,
        };

        if (relay is not null && response.CrossSidecarOutcome is { } targetOutcome)
        {
            if (targetSession is null)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The cross-sidecar target session is missing for outcome validation.");
            }

            var validation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                request,
                response,
                Session.Binding,
                targetSession.Session.Binding,
                DateTimeOffset.UtcNow,
                targetSession.ValidateCrossSidecarOutcomeProof);
            if (!validation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    validation.Code ?? SidecarCapabilityErrors.HostFailure,
                    validation.Message ?? "The target cross-sidecar outcome was rejected.");
            }

            response = response with
            {
                CrossSidecarOutcome = targetOutcome with
                {
                    Authority = targetOutcome.Authority with
                    {
                        Proof = IssueCrossSidecarProof(
                            targetOutcome.Authority,
                            targetOutcome.Authority.CanonicalBindingHash),
                    },
                },
            };
        }

        response = TestCrossSidecarResponseMutator?.Invoke(response) ?? response;
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    internal long BindingGeneration => Session.BindingGeneration;

    internal async ValueTask<OutOfProcessCrossSidecarDispatchResult> DispatchCrossSidecarAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        SidecarActionDescriptorIdentity identity,
        SidecarCrossSidecarActionEntryCarrier carrier,
        SidecarActionTerminalTransportRequest request,
        SidecarActionTerminalRegistration terminal,
        HostActionEntryRequestContext hostContext,
        CancellationToken cancellationToken)
    {
        var action = Deserialize<TAction>(carrier.Action);
        if (!terminal.IsWellFormed || terminal.TerminalId != request.TerminalId)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The target terminal registration does not match the cross-sidecar request.");
        }
        SidecarActionTerminalTransportResponse? terminalResponse = null;
        try
        {
            var externalAuthority = CreateExternalActionDispatchAuthority(
                identity,
                request.Call,
                action,
                request.EffectiveAction,
                terminal,
                hostContext,
                request.Cancellation,
                request.Invocation);
            var outcome = await _options.ActionDispatcher.RunExternalAsync(
                descriptor,
                action,
                async (dispatcherContext, terminalCancellation) =>
                {
                    var dispatcherPayload = OutOfProcessActionDispatcher.Payload(
                        dispatcherContext.Action,
                        identity.InputTypeIdentity,
                        identity.InputSchemaVersion);
                    if (!PayloadsMatch(dispatcherPayload, request.EffectiveAction))
                    {
                        throw new OutOfProcessCapabilityException(
                            SidecarCapabilityErrors.SpoofedIdentity,
                            "The target dispatcher changed the authenticated cross-sidecar action payload.");
                    }

                    var terminalRequest = CreateCrossSidecarTerminalRequest(
                        request,
                        hostContext);
                    terminalResponse = await SendTerminalAsync(
                        terminalRequest,
                        terminalCancellation);
                    if (terminalResponse.Execution.Result is null)
                    {
                        throw new OutOfProcessCapabilityException(
                            terminalResponse.Execution.Failure?.Code
                                ?? SidecarCapabilityErrors.HostFailure,
                            terminalResponse.Execution.Failure?.Message
                                ?? "The target module terminal did not return a result.");
                    }

                    return Deserialize<TResult>(terminalResponse.Execution.Result);
                },
                _options.ActionSnapshot,
                externalAuthority,
                cancellationToken);

            return new OutOfProcessCrossSidecarDispatchResult(
                outcome.Kind,
                outcome.Result is null
                    ? null
                    : OutOfProcessActionDispatcher.Payload(
                        outcome.Result,
                        identity.ResultTypeIdentity,
                        identity.ResultSchemaVersion),
                outcome.Kind == ActionOutcomeKind.Cancelled
                    ? null
                    : outcome.Error,
                outcome.Uncertainty,
                outcome.Continuation,
                terminalResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new OutOfProcessCrossSidecarDispatchResult(
                ActionOutcomeKind.Cancelled,
                null,
                null,
                null,
                null,
                terminalResponse);
        }
        catch (OutOfProcessCapabilityException ex)
        {
            return new OutOfProcessCrossSidecarDispatchResult(
                ex.Code == SidecarCapabilityErrors.Cancelled
                    ? ActionOutcomeKind.Cancelled
                    : ActionOutcomeKind.Failed,
                null,
                ex.Code == SidecarCapabilityErrors.Cancelled
                    ? null
                    : new ExecutionError(ex.Code, ex.Message),
                null,
                null,
                terminalResponse);
        }
        catch (Exception ex)
        {
            return new OutOfProcessCrossSidecarDispatchResult(
                ActionOutcomeKind.Failed,
                null,
                new ExecutionError(
                    SidecarCapabilityErrors.HostFailure,
                    ex.Message),
                null,
                null,
                terminalResponse);
        }
    }

    private SidecarActionTerminalTransportRequest CreateCrossSidecarTerminalRequest(
        SidecarActionTerminalTransportRequest request,
        HostActionEntryRequestContext hostContext) =>
        request with
        {
            Context = new SidecarActionTerminalExecutionContext(
                request.Call,
                request.Invocation,
                request.Descriptor,
                request.EffectiveAction,
                _options.ActionSnapshot,
                hostContext.InvocationId,
                hostContext.ParentInvocationId,
                hostContext.Depth,
                hostContext.Attempt,
                hostContext.Caller,
                hostContext.Features,
                hostContext.TraceId,
                hostContext.IdempotencyKey,
                request.Cancellation,
                request.Receipt,
                hostContext.Deadline),
        };

    private static bool PayloadsMatch(
        SidecarSerializedPayload actual,
        SidecarSerializedPayload expected) =>
        string.Equals(actual.TypeIdentity, expected.TypeIdentity, StringComparison.Ordinal)
        && actual.SchemaVersion == expected.SchemaVersion
        && string.Equals(actual.ContentHash, expected.ContentHash, StringComparison.Ordinal)
        && actual.ByteLength == expected.ByteLength
        && string.Equals(
            actual.Value.GetRawText(),
            expected.Value.GetRawText(),
            StringComparison.Ordinal);

    private static SidecarActionTerminalTransportResponse CreateCrossSidecarDispatchResponse(
        SidecarActionTerminalTransportRequest request,
        OutOfProcessCrossSidecarDispatchResult dispatch,
        SidecarSafeFailureIdentity safeFailure)
    {
        var result = dispatch.Result;
        var resultIdentity = result is null
            ? null
            : new SidecarActionResultIdentity(
                Guid.NewGuid(),
                request.Call.CallId,
                request.Descriptor.Key,
                request.Descriptor.Version,
                result.TypeIdentity,
                result.ContentHash);
        var execution = new SidecarTerminalExecutionResult(
            result,
            result is null ? safeFailure : null!,
            Completed: true);
        return new SidecarActionTerminalTransportResponse(
            resultIdentity,
            execution,
            request.Receipt,
            safeFailure)
        {
            TerminalId = request.TerminalId,
        };
    }

    internal async ValueTask<SidecarActionTerminalTransportResponse> ExecuteCrossSidecarCarrierAsync(
        SidecarCrossSidecarActionEntryCarrier carrier,
        SidecarActionTerminalRegistration terminal,
        OutOfProcessActionDescriptorCatalog.Registration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(registration);
        var now = DateTimeOffset.UtcNow;
        var begin = Session.BeginCrossSidecarActionEntryCall(
            carrier,
            terminal,
            carrier.Action.ByteLength,
            now,
            out var hostContext,
            (authority, _) => ValidateCrossSidecarProof(authority, authority.Proof));
        if (!begin.Accepted || hostContext is null)
        {
            throw new OutOfProcessCapabilityException(
                begin.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                begin.Message ?? "The cross-sidecar carrier was rejected.");
        }

        var authority = carrier.Authority;
        var target = authority.TargetEntry;
        var binding = Session.Binding;
        var action = carrier.Action;
        var peerRelay = new SidecarCrossSidecarActionEntryRelay(carrier, target)
        {
            PeerCall = authority.PeerCall,
            PeerBindingGeneration = carrier.Authority.PeerBindingGeneration,
        };
        var crossSidecarActionRequest = new SidecarCrossSidecarActionEntryRequest(
            target.Descriptor.Key,
            target.Descriptor.Version,
            action,
            authority.Deadline,
            authority.ExpiresAt);
        var receipt = new SidecarTerminalReceipt(
            Guid.NewGuid().ToString("N"),
            target.Descriptor.Key,
            target.Descriptor.Version,
            authority.TargetChildCall.CallId,
            hostContext.Attempt,
            $"{binding.GraphId}:{authority.TargetChildCall.CallId:N}",
            action.ContentHash);
        var terminalAuthority = new SidecarHostTerminalAuthority(
            Guid.NewGuid(),
            binding.SessionId,
            binding.RequestId,
            binding.CancellationId,
            authority.TargetChildCall.CallId,
            binding.ModuleId,
            binding.GraphId,
            SidecarActionInvocationKind.HostEntryCrossSidecar,
            target.Descriptor.Key,
            target.Descriptor.Version,
            target.Descriptor.DescriptorHash,
            action.TypeIdentity,
            action.SchemaVersion,
            action.ContentHash,
            action.ByteLength,
            receipt.ReceiptId,
            receipt.ActionKey,
            receipt.ActionVersion,
            receipt.CallId,
            receipt.Attempt,
            receipt.IdempotencyScope,
            receipt.ContentHash,
            hostContext.Deadline,
            authority.IssuedAt,
            authority.ExpiresAt,
            "pending")
        {
            TerminalId = terminal.TerminalId,
            SnapshotContentHash = authority.SnapshotContentHash,
            Caller = hostContext.Caller,
            Features = hostContext.Features,
            TraceId = hostContext.TraceId,
            IdempotencyKey = hostContext.IdempotencyKey,
            InvocationId = hostContext.InvocationId,
            ParentInvocationId = hostContext.ParentInvocationId,
            Depth = hostContext.Depth,
            Attempt = hostContext.Attempt,
            ReceivingRootBudgetId = hostContext.CapabilityId,
            ReceivingPeerBindingGeneration = Session.BindingGeneration,
            RootPeerCall = peerRelay.PeerCall,
            CrossSidecarPeerRelayBindingHash = SidecarCapabilityTransportValidation
                .ComputeCrossSidecarPeerRelayBindingHash(peerRelay),
        };
        var targetAuthority = terminalAuthority with
        {
            CanonicalBindingHash = SidecarCapabilityTransportValidation
                .ComputeTerminalAuthorityBindingHash(terminalAuthority),
        };
        targetAuthority = targetAuthority with
        {
            Proof = OutOfProcessCapabilitySecurity.CreateTerminalProof(
                targetAuthority,
                _controlToken),
        };
        var request = new SidecarActionTerminalTransportRequest(
            authority.TargetChildCall,
            SidecarActionInvocationKind.HostEntryCrossSidecar,
            target.Descriptor,
            action,
            targetAuthority,
            receipt,
            authority.Cancellation,
            hostContext.Deadline)
        {
            Context = new SidecarActionTerminalExecutionContext(
                authority.TargetChildCall,
                SidecarActionInvocationKind.HostEntryCrossSidecar,
                target.Descriptor,
                action,
                _options.ActionSnapshot,
                hostContext.InvocationId,
                hostContext.ParentInvocationId,
                hostContext.Depth,
                hostContext.Attempt,
                hostContext.Caller,
                hostContext.Features,
                hostContext.TraceId,
                hostContext.IdempotencyKey,
                authority.Cancellation,
                receipt,
                hostContext.Deadline),
            TerminalId = terminal.TerminalId,
            CrossSidecarActionRequest = crossSidecarActionRequest,
            CrossSidecarPeerRelay = peerRelay,
        };
        try
        {
            var dispatch = await registration.DispatchCrossSidecar(
                this,
                carrier,
                request,
                terminal,
                hostContext,
                cancellationToken);
            var receivedTerminalResponse = dispatch.TerminalResponse is not null;
            var response = dispatch.TerminalResponse
                ?? CreateCrossSidecarDispatchResponse(
                    request,
                    dispatch,
                    binding.SafeFailure);
            if (receivedTerminalResponse)
            {
                if (response.CrossSidecarRelay is not null
                    || response.CrossSidecarOutcome is not null)
                {
                    throw new OutOfProcessCapabilityException(
                        SidecarCapabilityErrors.InvalidResponse,
                        "The target terminal response contains an unexpected cross-sidecar envelope.");
                }

                var targetValidationRequest = request with
                {
                    CrossSidecarActionRequest = null,
                    CrossSidecarPeerRelay = null,
                };
                var targetValidationResponse = response with
                {
                    CrossSidecarRelay = null,
                    CrossSidecarOutcome = null,
                };
                var responseValidation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                    targetValidationRequest,
                    targetValidationResponse,
                    binding,
                    ValidateTerminalAuthority);
                if (!responseValidation.Accepted)
                {
                    throw new OutOfProcessCapabilityException(
                        responseValidation.Code ?? SidecarCapabilityErrors.HostFailure,
                        responseValidation.Message ?? "The target terminal response was rejected.");
                }
            }
            else if (!response.Execution.Completed
                || response.Execution.Result is not null
                || response.Execution.Failure != binding.SafeFailure
                || response.SafeFailure != binding.SafeFailure
                || response.Receipt != request.Receipt
                || response.TerminalId != request.TerminalId)
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.SpoofedIdentity,
                    "The synthetic target terminal response is not bound to the target safe failure.");
            }

            var terminalRecord = Session.RecordTerminal(
                authority.TargetChildCall.CallId,
                targetAuthority.AuthorityId,
                response.Receipt);
            if (!terminalRecord.Accepted && !Session.TryGetTerminalReceipt(
                    authority.TargetChildCall.CallId,
                    out _))
            {
                throw new OutOfProcessCapabilityException(
                    terminalRecord.Code ?? SidecarCapabilityErrors.HostFailure,
                    terminalRecord.Message ?? "The target terminal receipt was rejected.");
            }
            if (!Session.TryGetTerminalReceipt(authority.TargetChildCall.CallId, out _))
            {
                throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.HostFailure,
                    "The target terminal receipt was not recorded.");
            }

            var kind = dispatch.Kind switch
            {
                ActionOutcomeKind.Completed => ActionOutcomeKind.Completed,
                ActionOutcomeKind.Cancelled => ActionOutcomeKind.Cancelled,
                _ => ActionOutcomeKind.Failed,
            };
            var outcome = new SidecarActionOutcomeEnvelope(
                kind,
                response.Execution.Result,
                dispatch.Continuation,
                dispatch.Error,
                dispatch.Uncertainty,
                response.Receipt,
                binding.SafeFailure,
                1);
            var completion = Session.CompleteCrossSidecarActionEntry(
                carrier,
                outcome,
                response.Receipt,
                response.Execution,
                response.ResultIdentity,
                binding.SafeFailure,
                DateTimeOffset.UtcNow,
                IssueCrossSidecarProof,
                out var completed);
            if (!completion.Accepted || completed is null)
            {
                throw new OutOfProcessCapabilityException(
                    completion.Code ?? SidecarCapabilityErrors.HostFailure,
                    completion.Message ?? "The cross-sidecar result authority was rejected.");
            }

            var outcomeValidation = ValidateCrossSidecarOutcome(
                completed,
                DateTimeOffset.UtcNow);
            if (!outcomeValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    outcomeValidation.Code ?? SidecarCapabilityErrors.HostFailure,
                    outcomeValidation.Message ?? "The signed cross-sidecar outcome was rejected.");
            }

            Volatile.Write(ref _lastCrossSidecarOutcome, completed);
            return response with { CrossSidecarOutcome = completed };
        }
        catch
        {
            Session.CompleteCrossSidecarActionEntry(carrier, DateTimeOffset.UtcNow);
            throw;
        }
    }

    internal bool ValidateCrossSidecarCarrier(
        SidecarCrossSidecarActionEntryCarrier carrier,
        DateTimeOffset now)
    {
        var validation = SidecarCrossSidecarActionEntryValidation.ValidateCarrier(
            carrier,
            Session.Binding,
            now,
            (authority, _) => ValidateCrossSidecarProof(authority, authority.Proof));
        return validation.Accepted;
    }

    internal SidecarCrossSidecarActionEntryOutcome? LastCrossSidecarOutcome =>
        Volatile.Read(ref _lastCrossSidecarOutcome);

    internal SidecarCapabilityValidationResult ValidateCrossSidecarOutcome(
        SidecarCrossSidecarActionEntryOutcome outcome,
        DateTimeOffset now) =>
        SidecarCrossSidecarActionEntryValidation.ValidateOutcome(
            outcome,
            Session.Binding,
            now,
            (authority, canonicalHash) =>
                ValidateCrossSidecarOutcomeProof(authority, canonicalHash));

    internal string IssueCrossSidecarProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string canonicalHash) =>
        CreateCrossSidecarProof(
            authority with
            {
                CanonicalBindingHash = canonicalHash,
                Proof = string.Empty,
            },
            _controlToken);

    private bool ValidateCrossSidecarProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string proof) =>
        string.Equals(
            authority.CanonicalBindingHash,
            SidecarCrossSidecarActionEntryValidation.ComputeAuthorityHash(authority),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            CreateCrossSidecarProof(authority with { Proof = string.Empty }, _controlToken),
            proof,
            StringComparison.Ordinal);

    private bool ValidateCrossSidecarOutcomeProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string canonicalHash) =>
        string.Equals(
            authority.CanonicalBindingHash,
            canonicalHash,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            CreateCrossSidecarProof(authority with { Proof = string.Empty }, _controlToken),
            authority.Proof,
            StringComparison.Ordinal);

    private static string CreateCrossSidecarProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string controlToken)
    {
        var value = "cross-sidecar|"
            + SidecarCrossSidecarActionEntryValidation.ComputeAuthorityHash(authority);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(controlToken),
            Encoding.UTF8.GetBytes(value)));
    }
}
