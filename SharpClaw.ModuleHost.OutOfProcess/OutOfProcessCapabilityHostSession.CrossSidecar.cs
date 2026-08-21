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
            await SendCrossSidecarRelayResponseAsync(request, null, null, failure, ct);
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
        try
        {
            var targetResponse = await target.Client.CapabilitySession
                .ExecuteCrossSidecarCarrierAsync(
                    relay.Carrier,
                    targetTerminal,
                    ct);
            await SendCrossSidecarRelayResponseAsync(
                request,
                relay,
                targetResponse,
                null,
                ct);
        }
        catch (OutOfProcessCapabilityException ex)
        {
            await SendCrossSidecarRelayResponseAsync(
                request,
                relay,
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
        SidecarActionTerminalTransportResponse? targetResponse,
        SidecarSafeFailureIdentity? failure,
        CancellationToken ct)
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
        await OutOfProcessCapabilityWire.SendAsync(
            _socket,
            OutOfProcessCapabilityFrameKind.ActionTerminalResponse,
            response,
            _limits.ProtocolMessageBytes,
            SendGate,
            ct);
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
            var diagnosticBinding = Session.Binding;
            var diagnosticAuthority = carrier.Authority;
            var diagnosticTargetCall = diagnosticAuthority.TargetChildCall;
            var diagnosticRequest = SidecarActionCapabilityRequest.HostEntryCrossSidecar(
                diagnosticTargetCall,
                diagnosticAuthority.TargetEntry.Descriptor,
                carrier.Action,
                new SidecarCancellationIdentity(
                    diagnosticBinding.CancellationId,
                    diagnosticAuthority.CanonicalBindingHash,
                    carrier.ExpiresAt),
                diagnosticAuthority.Deadline,
                carrier,
                terminal);
            var diagnosticRequestValidation = SidecarCapabilityTransportValidation
                .ValidateActionRequest(diagnosticRequest, diagnosticBinding, now);
            var diagnosticBindingValidation = SidecarCapabilitySessionValidator.Validate(
                diagnosticBinding,
                authority => OutOfProcessCapabilityWire.Authenticate(authority, _controlToken),
                _ => true,
                now,
                RegisterAuthenticationNonce: false);
            var carrierValidation = SidecarCrossSidecarActionEntryValidation.ValidateCarrier(
                carrier,
                diagnosticBinding,
                now,
                (candidate, proof) => ValidateCrossSidecarProof(candidate, proof));
            throw new OutOfProcessCapabilityException(
                begin.Code ?? SidecarCapabilityErrors.SpoofedIdentity,
                $"{begin.Message ?? "The cross-sidecar carrier was rejected."}; "
                + $"carrierValidation={carrierValidation.Code}:{carrierValidation.Message}; "
                + $"requestValidation={diagnosticRequestValidation.Code}:{diagnosticRequestValidation.Message}; "
                + $"bindingValidation={diagnosticBindingValidation.Code}:{diagnosticBindingValidation.Message}; "
                + $"beginCode={begin.Code}; "
                + $"carrierWellFormed={carrier.IsWellFormed}; authorityValid={diagnosticAuthority.IsValid}; "
                + $"targetSession={diagnosticTargetCall.SessionId == diagnosticBinding.SessionId}; "
                + $"targetRequest={diagnosticTargetCall.RequestId == diagnosticBinding.RequestId}; "
                + $"targetCancellation={diagnosticTargetCall.CancellationId == diagnosticBinding.CancellationId}; "
                + $"targetModule={string.Equals(diagnosticTargetCall.ModuleId, diagnosticBinding.ModuleId, StringComparison.Ordinal)}; "
                + $"targetGraph={string.Equals(diagnosticTargetCall.GraphId, diagnosticBinding.GraphId, StringComparison.Ordinal)}; "
                + $"targetDeadline={diagnosticTargetCall.Deadline == diagnosticAuthority.Deadline}; "
                + $"sourceDeadline={diagnosticAuthority.SourceParentCall.Deadline == diagnosticAuthority.Deadline}; "
                + $"targetDeadlineValid={diagnosticAuthority.Deadline > now}; "
                + $"targetGenerationPositive={diagnosticAuthority.TargetBindingGeneration > 0}; "
                + $"targetGeneration={diagnosticAuthority.TargetBindingGeneration == Session.BindingGeneration}; "
                + $"carrierGeneration={carrier.BindingGeneration}; "
                + $"sessionGeneration={Session.BindingGeneration}; "
                + $"sourceParentValid={diagnosticAuthority.SourceParentCall.IsValid}; "
                + $"targetChildValid={diagnosticTargetCall.IsValid}; "
                + $"targetEntryModule={string.Equals(diagnosticAuthority.TargetEntry.ModuleId, diagnosticBinding.ModuleId, StringComparison.Ordinal)}; "
                + $"targetEntryGraph={string.Equals(diagnosticAuthority.TargetEntry.GraphId, diagnosticBinding.GraphId, StringComparison.Ordinal)}; "
                + $"expiresNotAfter={diagnosticAuthority.ExpiresAt <= diagnosticBinding.ExpiresAt}; "
                + $"payloadValidation={SidecarCapabilityTransportValidation.ValidateSerializedPayload(carrier.Action, true, diagnosticBinding.PayloadLimits.ActionInputBytes).Code}; "
                + $"expiry={diagnosticAuthority.ExpiresAt > now && diagnosticAuthority.Deadline > now}; "
                + $"expiryWithinBinding={diagnosticAuthority.ExpiresAt <= diagnosticBinding.ExpiresAt}; "
                + $"actionGrant={diagnosticBinding.Grant.Allows(SidecarCapabilityKind.Action)}; "
                + $"owner={string.Equals(diagnosticAuthority.TargetEntry.ModuleId, diagnosticBinding.ModuleId, StringComparison.Ordinal) && string.Equals(diagnosticAuthority.TargetEntry.GraphId, diagnosticBinding.GraphId, StringComparison.Ordinal)}; "
                + $"canonical={string.Equals(diagnosticAuthority.CanonicalBindingHash, SidecarCrossSidecarActionEntryValidation.ComputeAuthorityHash(diagnosticAuthority), StringComparison.OrdinalIgnoreCase)}; "
                + $"proof={ValidateCrossSidecarProof(diagnosticAuthority, diagnosticAuthority.Proof)}");
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
                    completion.Code ?? SidecarCapabilityErrors.HostFailure,
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
