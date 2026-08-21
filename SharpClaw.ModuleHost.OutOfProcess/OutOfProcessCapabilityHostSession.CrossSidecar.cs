using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed partial class OutOfProcessCapabilityHostSession
{
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
                new SidecarSafeFailureIdentity(
                    Guid.NewGuid(),
                    issuance.Code ?? SidecarCapabilityErrors.Unauthorized,
                    issuance.Message ?? "The target action entry authority was rejected.",
                    Retryable: false),
                ct);
            return;
        }

        await SendCrossSidecarRelayResponseAsync(request, relay, null, ct);
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
            var targetTerminal = new SidecarActionTerminalRegistration(
                resolvedTarget.Entry.TerminalId,
                resolvedTarget.Entry.Descriptor.InputTypeIdentity,
                resolvedTarget.Entry.Descriptor.InputSchemaVersion,
                resolvedTarget.Entry.Descriptor.ResultTypeIdentity,
                resolvedTarget.Entry.Descriptor.ResultSchemaVersion,
                resolvedTarget.Entry.Descriptor.DescriptorHash);
            var targetResponse = await target.Client.CapabilitySession
                .ExecuteCrossSidecarCarrierAsync(
                    request.CrossSidecarCarrier,
                    targetTerminal,
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
            Volatile.Write(ref _lastHandledFailure, ex);
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

    internal async ValueTask<SidecarActionTerminalTransportResponse> ExecuteCrossSidecarCarrierAsync(
        SidecarCrossSidecarActionEntryCarrier carrier,
        SidecarActionTerminalRegistration terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(terminal);
        var now = DateTimeOffset.UtcNow;
        var begin = Session.BeginCrossSidecarActionEntryCall(
            carrier,
            terminal,
            carrier.Action.ByteLength,
            now,
            out var hostContext,
            (authority, proof) => ValidateCrossSidecarProof(authority, proof));
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
        };
        try
        {
            var response = await SendTerminalAsync(request, cancellationToken);
            var responseValidation = SidecarCapabilityTransportValidation.ValidateActionTerminalResponse(
                request,
                response,
                binding,
                (targetTerminalAuthority, proof) => ValidateTerminalAuthority(targetTerminalAuthority, proof));
            if (!responseValidation.Accepted)
            {
                throw new OutOfProcessCapabilityException(
                    responseValidation.Code ?? SidecarCapabilityErrors.HostFailure,
                    responseValidation.Message ?? "The target terminal response was rejected.");
            }

            var kind = response.Execution.Result is not null
                ? ActionOutcomeKind.Completed
                : response.Execution.Failure?.Code == SidecarCapabilityErrors.Cancelled
                    ? ActionOutcomeKind.Cancelled
                    : ActionOutcomeKind.Failed;
            var outcome = new SidecarActionOutcomeEnvelope(
                kind,
                response.Execution.Result,
                null,
                kind == ActionOutcomeKind.Failed && response.Execution.Failure is not null
                    ? new ExecutionError(
                        response.Execution.Failure.Code,
                        response.Execution.Failure.Message)
                    : null,
                null,
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
                    completion.Code ?? SidecarCapabilityErrors.InvalidResponse,
                    completion.Message ?? "The cross-sidecar result authority was rejected.");
            }

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
            (authority, proof) => ValidateCrossSidecarProof(authority, proof));
        return validation.Accepted;
    }

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
