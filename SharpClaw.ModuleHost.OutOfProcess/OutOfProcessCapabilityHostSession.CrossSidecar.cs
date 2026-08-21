using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession
{
    private readonly ConcurrentDictionary<Guid, SidecarCrossSidecarActionEntryCarrier>
        _crossSidecarCarriers = new();
    private long _crossSidecarSequence;

    private async Task HandleCrossSidecarTerminalRequestAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken ct)
    {
        var crossRequest = request.CrossSidecarActionRequest
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The cross-sidecar terminal request has no neutral child request.");
        if (!_calls.TryGetValue(request.Call.CallId, out var active)
            || active.ActionRequest is not { } initiatingRequest
            || !_terminals.ContainsKey(request.Call.CallId)
            || request.Context is null)
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
            await SendCrossSidecarRelayResponseAsync(request, null, failure, ct);
            return;
        }

        var targetEntry = new SidecarModuleActionEntryDefinition(
            target.Entry.ModuleId,
            target.Entry.ContractHash,
            target.Entry.Descriptor,
            target.Entry.ModuleId,
            target.Entry.ContractHash);
        try
        {
            var relay = target.Client.CapabilitySession.IssueCrossSidecarCarrier(
                request.Call,
                BindingGeneration,
                request.Context,
                crossRequest,
                targetEntry,
                DateTimeOffset.UtcNow);
            await SendCrossSidecarRelayResponseAsync(request, relay, null, ct);
        }
        catch (OutOfProcessCapabilityException ex)
        {
            await SendCrossSidecarRelayResponseAsync(
                request,
                null,
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    ex.Code,
                    ex.Message,
                    Retryable: false),
                ct);
        }
    }

    private async Task SendCrossSidecarRelayResponseAsync(
        SidecarActionTerminalTransportRequest request,
        SidecarCrossSidecarActionEntryRelay? relay,
        SidecarSafeFailureIdentity? failure,
        CancellationToken ct)
    {
        var response = new SidecarActionTerminalTransportResponse(
            null,
            new SidecarTerminalExecutionResult(
                null,
                failure,
                Completed: true),
            request.Receipt,
            _session.Binding.SafeFailure)
        {
            TerminalId = request.TerminalId,
            CrossSidecarRelay = relay,
        };
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
    }

    private async Task HandleCrossSidecarActionRequestAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken channelCt)
    {
        ActiveCall? active = null;
        try
        {
            var validation = SidecarCapabilityTransportValidation.ValidateActionRequest(
                request,
                Session.Binding,
                DateTimeOffset.UtcNow);
            if (!validation.Accepted)
            {
                await SendActionFailureAsync(request, validation.Code, validation.Message, channelCt);
                return;
            }

            if (!IsHostActionAuthorized(request)
                || request.CrossSidecarCarrier is null
                || _options.CrossSidecarActionEntries is null)
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.Unauthorized,
                    "The cross-sidecar action request is not authorized by a target entry.",
                    channelCt);
                return;
            }

            active = RegisterCall(request.Call, channelCt, request);
            var begin = Session.BeginActionCall(
                request,
                request.Action.ByteLength,
                DateTimeOffset.UtcNow,
                out var hostContext);
            active.HostContext = hostContext;
            if (!begin.Accepted)
            {
                AbandonCall(request.Call.CallId, active);
                active = null;
                await SendActionFailureAsync(request, begin.Code, begin.Message, channelCt);
                return;
            }

            var target = _options.CrossSidecarActionEntries.TryResolve(
                request.CrossSidecarCarrier.Authority.TargetEntry.Descriptor.Key,
                request.CrossSidecarCarrier.Authority.TargetEntry.Descriptor.Version,
                out var resolvedTarget)
                ? resolvedTarget
                : throw new OutOfProcessCapabilityException(
                    SidecarCapabilityErrors.UnknownAction,
                    "The target action entry is no longer registered.");
            var targetResponse = await target.Client.CapabilitySession
                .ExecuteCrossSidecarCarrierAsync(
                    request.CrossSidecarCarrier,
                    active.Cancellation.Token);
            var response = CreateCrossSidecarActionResponse(request, targetResponse);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateActionResponse(
                request,
                response,
                Session.Binding,
                Session);
            if (!responseValidation.Accepted)
            {
                await SendActionFailureAsync(
                    request,
                    responseValidation.Code,
                    responseValidation.Message,
                    channelCt);
                return;
            }

            if (!CompleteCall(request.Call.CallId, response.Outcome.TerminalCallCount))
            {
                await SendActionFailureAsync(
                    request,
                    SidecarCapabilityErrors.HostFailure,
                    "The cross-sidecar action call could not be completed.",
                    channelCt);
                return;
            }

            await OutOfProcessCapabilityWire.SendAsync(
                _socket,
                OutOfProcessCapabilityFrameKind.ActionResponse,
                response,
                _limits.ProtocolMessageBytes,
                SendGate,
                channelCt);
        }
        catch (OperationCanceledException) when (
            active is not null
            && active.Cancellation.IsCancellationRequested
            && !channelCt.IsCancellationRequested)
        {
            if (active is not null)
                CompleteCall(request.Call.CallId, 1);
            await SendActionFailureAsync(
                request,
                SidecarCapabilityErrors.Cancelled,
                "The cross-sidecar action was cancelled.",
                channelCt);
        }
        catch (OperationCanceledException) when (channelCt.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (active is not null)
                CompleteCall(request.Call.CallId, 1);
            await SendActionFailureAsync(
                request,
                ex is OutOfProcessCapabilityException capability
                    ? capability.Code
                    : SidecarCapabilityErrors.HostFailure,
                ex is OutOfProcessCapabilityException capabilityFailure
                    ? capabilityFailure.Message
                    : "The target sidecar action entry failed.",
                channelCt);
        }
        finally
        {
            await FinishCallAsync(request.Call.CallId, active, channelCt);
        }
    }

    private SidecarActionCapabilityResponse CreateCrossSidecarActionResponse(
        SidecarActionCapabilityRequest request,
        SidecarActionTerminalTransportResponse targetResponse)
    {
        var execution = targetResponse.Execution;
        var result = execution.Result;
        var failure = execution.Failure;
        var receipt = new SidecarTerminalReceipt(
            Guid.NewGuid().ToString("N"),
            request.Descriptor.Key,
            request.Descriptor.Version,
            request.Call.CallId,
            1,
            $"{Session.Binding.GraphId}:{request.Call.CallId:N}",
            result?.ContentHash ?? request.Action.ContentHash);
        var record = Session.RecordTerminal(
            request.Call.CallId,
            Guid.NewGuid(),
            receipt);
        if (!record.Accepted && !Session.TryGetTerminalReceipt(request.Call.CallId, out _))
        {
            throw new OutOfProcessCapabilityException(
                record.Code ?? SidecarCapabilityErrors.HostFailure,
                record.Message ?? "The cross-sidecar terminal receipt was rejected.");
        }

        var succeeded = execution.Completed && failure is null && result is not null;
        var error = failure is null
            ? null
            : new ExecutionError(failure.Code, failure.Message);
        var outcome = new SidecarActionOutcomeEnvelope(
            succeeded ? ActionOutcomeKind.Completed : ActionOutcomeKind.Failed,
            result!,
            null!,
            error!,
            null!,
            receipt,
            _session.Binding.SafeFailure,
            1);
        return new SidecarActionCapabilityResponse(
            result is null
                ? null
                : new SidecarActionResultIdentity(
                    Guid.NewGuid(),
                    request.Call.CallId,
                    request.Descriptor.Key,
                    request.Descriptor.Version,
                    result.TypeIdentity,
                    result.ContentHash),
            outcome,
            null,
            _session.Binding.SafeFailure,
            Completed: true);
    }

    internal long BindingGeneration => Session.BindingGeneration;

    internal SidecarCrossSidecarActionEntryRelay IssueCrossSidecarCarrier(
        SidecarCapabilityCallIdentity sourceParentCall,
        long sourceBindingGeneration,
        SidecarActionTerminalExecutionContext parentContext,
        SidecarCrossSidecarActionEntryRequest request,
        SidecarModuleActionEntryDefinition targetEntry,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sourceParentCall);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetEntry);
        if (!request.IsWellFormed
            || !targetEntry.IsWellFormed
            || targetEntry.ModuleId != Session.Binding.ModuleId
            || targetEntry.GraphId != Session.Binding.GraphId
            || targetEntry.Descriptor.Key != request.ActionKey
            || targetEntry.Descriptor.Version != request.ActionVersion)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.SpoofedIdentity,
                "The target action entry does not match the neutral cross-sidecar request.");
        }

        var binding = Session.Binding;
        var deadline = request.Deadline;
        if (parentContext.Deadline < deadline)
            deadline = parentContext.Deadline;
        if (binding.ExpiresAt < deadline)
            deadline = binding.ExpiresAt;
        var expiresAt = request.ExpiresAt < deadline
            ? request.ExpiresAt
            : deadline;
        if (expiresAt <= now)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Expired,
                "The cross-sidecar action-entry request has expired.");
        }

        var childCall = CreateCrossSidecarCall(deadline);
        var childInvocationId = Guid.NewGuid();
        var capabilityId = Guid.NewGuid();
        var handle = Guid.NewGuid().ToString("N");
        var receipt = new SidecarTerminalReceipt(
            Guid.NewGuid().ToString("N"),
            targetEntry.Descriptor.Key,
            targetEntry.Descriptor.Version,
            childCall.CallId,
            1,
            $"{binding.GraphId}:{childCall.CallId:N}",
            request.Action.ContentHash);
        var cancellation = new SidecarCancellationIdentity(
            childCall.CancellationId,
            SidecarCapabilitySessionValidator.ComputeBindingHash(binding),
            deadline);
        var authority = new SidecarCrossSidecarActionEntryAuthority(
            sourceParentCall,
            childCall,
            parentContext.InvocationId,
            childInvocationId,
            capabilityId,
            handle,
            sourceBindingGeneration,
            BindingGeneration,
            targetEntry,
            SidecarActionPayloadLineage.From(request.Action),
            parentContext.Caller,
            parentContext.Features,
            parentContext.TraceId,
            parentContext.IdempotencyKey,
            cancellation,
            deadline,
            now,
            expiresAt,
            parentContext.Depth + 1,
            parentContext.Attempt,
            SidecarCapabilityTransportCodec.ComputeSha256(
                SidecarCapabilityTransportCodec.Serialize(_options.ActionSnapshot)),
            binding.ModuleId,
            binding.GraphId,
            receipt,
            "pending")
        {
            CanonicalBindingHash = SidecarCapabilitySessionValidator.ComputeBindingHash(binding),
            TerminalId = targetEntry.TerminalId,
        };
        authority = authority with
        {
            Proof = CreateCrossSidecarProof(authority, _controlToken),
        };
        var carrier = new SidecarCrossSidecarActionEntryCarrier(
            capabilityId,
            handle,
            authority,
            request.Action,
            BindingGeneration,
            expiresAt);
        RemoveExpiredCrossSidecarCarriers(now);
        if (!_crossSidecarCarriers.TryAdd(carrier.CarrierId, carrier))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The cross-sidecar carrier identifier was already issued.");
        }

        return new SidecarCrossSidecarActionEntryRelay(carrier, targetEntry);
    }

    internal async ValueTask<SidecarActionTerminalTransportResponse> ExecuteCrossSidecarCarrierAsync(
        SidecarCrossSidecarActionEntryCarrier carrier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        var now = DateTimeOffset.UtcNow;
        var validation = SidecarCrossSidecarActionEntryValidation.ValidateCarrier(
            carrier,
            Session.Binding,
            now,
            (authority, proof) => ValidateCrossSidecarProof(authority, proof));
        if (!validation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                validation.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                validation.Message ?? "The cross-sidecar carrier was rejected.");
        }

        if (!_crossSidecarCarriers.TryRemove(carrier.CarrierId, out var issued)
            || !issued.Equals(carrier))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.Replay,
                "The cross-sidecar carrier was already consumed or was not issued by this session.");
        }

        var authority = carrier.Authority;
        var target = authority.TargetEntry;
        var binding = Session.Binding;
        var action = carrier.Action;
        var receipt = authority.ResultReceipt;
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
            authority.Deadline,
            authority.IssuedAt,
            authority.ExpiresAt,
            "pending")
        {
            TerminalId = target.TerminalId,
            SnapshotContentHash = authority.SnapshotContentHash,
            Caller = authority.Caller,
            Features = authority.Features,
            TraceId = authority.TraceId,
            IdempotencyKey = authority.IdempotencyKey,
            InvocationId = authority.TargetChildInvocationId,
            ParentInvocationId = authority.SourceParentInvocationId,
            Depth = authority.Depth,
            Attempt = authority.Attempt,
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
            authority.Deadline)
        {
            Context = new SidecarActionTerminalExecutionContext(
                authority.TargetChildCall,
                SidecarActionInvocationKind.HostEntryCrossSidecar,
                target.Descriptor,
                action,
                _options.ActionSnapshot,
                authority.TargetChildInvocationId,
                authority.SourceParentInvocationId,
                authority.Depth,
                authority.Attempt,
                authority.Caller,
                authority.Features,
                authority.TraceId,
                authority.IdempotencyKey,
                authority.Cancellation,
                receipt,
                authority.Deadline),
            TerminalId = target.TerminalId,
        };
        var response = await SendTerminalAsync(request, cancellationToken);
        var responseValidation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
            request,
            response,
            binding,
            (terminal, proof) => ValidateTerminalAuthority(terminal, proof));
        if (!responseValidation.Accepted)
        {
            throw new OutOfProcessCapabilityException(
                responseValidation.Code ?? SidecarCapabilityErrors.HostFailure,
                responseValidation.Message ?? "The target terminal response was rejected.");
        }

        return response;
    }

    internal bool ValidateCrossSidecarCarrier(
        SidecarCrossSidecarActionEntryCarrier carrier,
        DateTimeOffset now)
    {
        var validation = SidecarCrossSidecarActionEntryValidation.ValidateCarrier(
            carrier,
            Session.Binding,
            now,
            (authority, proof) => ValidateCrossSidecarProof(authority, proof));
        return validation.Accepted
            && _crossSidecarCarriers.ContainsKey(carrier.CarrierId);
    }

    private SidecarCapabilityCallIdentity CreateCrossSidecarCall(DateTimeOffset deadline)
    {
        var binding = Session.Binding;
        var sequence = Interlocked.Increment(ref _crossSidecarSequence);
        return new SidecarCapabilityCallIdentity(
            binding.SessionId,
            binding.RequestId,
            binding.CancellationId,
            Guid.NewGuid(),
            $"cross:{binding.SessionId:N}:{sequence}:{Guid.NewGuid():N}",
            binding.ModuleId,
            binding.GraphId,
            SidecarCapabilityKind.Action,
            sequence,
            deadline);
    }

    private bool ValidateCrossSidecarProof(
        SidecarCrossSidecarActionEntryAuthority authority,
        string proof) =>
        string.Equals(
            authority.CanonicalBindingHash,
            SidecarCapabilitySessionValidator.ComputeBindingHash(Session.Binding),
            StringComparison.Ordinal)
        && string.Equals(
            CreateCrossSidecarProof(authority with { Proof = string.Empty }, _controlToken),
            proof,
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

    private void RemoveExpiredCrossSidecarCarriers(DateTimeOffset now)
    {
        foreach (var item in _crossSidecarCarriers)
        {
            if (item.Value.ExpiresAt <= now)
                _crossSidecarCarriers.TryRemove(item.Key, out _);
        }
    }

    private sealed record CrossSidecarRelayFailure(
        string Code,
        string Message);
}
