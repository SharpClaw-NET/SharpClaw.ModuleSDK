#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal static class OutOfProcessProtocolTestFixture
{
    private static Func<int, int>? _responseTerminalCallCountTransform;
    private static Func<CancellationToken, Task>? _beforeActionResponseAsync;
    private static Func<SidecarCapabilityCallIdentity, CancellationToken, Task>?
        _beforeActionResponseForCallAsync;
    private static Func<CancellationToken, Task>? _beforeStorageResponseAsync;
    private static Action<string>? _rebindStateObserver;
    private static Action<string>? _failureObserver;
    private static string? _lastActionResponseCallId;
    private static Action<SidecarCapabilityCallIdentity>? _callCreatedObserver;
    private static Func<SidecarActionCapabilityRequest, CancellationToken, Task>?
        _beforeOutgoingCallRegistrationAsync;
    private static Func<SidecarActionTerminalTransportRequest, CancellationToken, Task>?
        _beforeIncomingTerminalReleaseAsync;

    internal static void ConfigureResponseTerminalCallCountTransform(
        Func<int, int>? transform) =>
        Interlocked.Exchange(ref _responseTerminalCallCountTransform, transform);

    internal static int TransformResponseTerminalCallCount(int value) =>
        Volatile.Read(ref _responseTerminalCallCountTransform)?.Invoke(value) ?? value;

    internal static void ConfigureBeforeActionResponseAsync(
        Func<CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeActionResponseAsync, callback);

    internal static Task BeforeActionResponseAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeActionResponseAsync)?.Invoke(cancellationToken)
        ?? Task.CompletedTask;

    internal static void ConfigureBeforeActionResponseForCallAsync(
        Func<SidecarCapabilityCallIdentity, CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeActionResponseForCallAsync, callback);

    internal static Task BeforeActionResponseAsync(
        SidecarCapabilityCallIdentity call,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeActionResponseForCallAsync)?.Invoke(call, cancellationToken)
        ?? BeforeActionResponseAsync(cancellationToken);

    internal static void ConfigureBeforeStorageResponseAsync(
        Func<CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeStorageResponseAsync, callback);

    internal static Task BeforeStorageResponseAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeStorageResponseAsync)?.Invoke(cancellationToken)
        ?? Task.CompletedTask;

    internal static void ConfigureRebindStateObserver(Action<string>? observer) =>
        Interlocked.Exchange(ref _rebindStateObserver, observer);

    internal static void ConfigureFailureObserver(Action<string>? observer) =>
        Interlocked.Exchange(ref _failureObserver, observer);

    internal static void ConfigureCallCreatedObserver(
        Action<SidecarCapabilityCallIdentity>? observer) =>
        Interlocked.Exchange(ref _callCreatedObserver, observer);

    internal static void ConfigureBeforeOutgoingCallRegistrationAsync(
        Func<SidecarActionCapabilityRequest, CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeOutgoingCallRegistrationAsync, callback);

    internal static Task BeforeOutgoingCallRegistrationAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeOutgoingCallRegistrationAsync)?.Invoke(
            request,
            cancellationToken)
        ?? Task.CompletedTask;

    internal static void ConfigureBeforeIncomingTerminalReleaseAsync(
        Func<SidecarActionTerminalTransportRequest, CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeIncomingTerminalReleaseAsync, callback);

    internal static Task BeforeIncomingTerminalReleaseAsync(
        SidecarActionTerminalTransportRequest request,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeIncomingTerminalReleaseAsync)?.Invoke(
            request,
            cancellationToken)
        ?? Task.CompletedTask;

    internal static void RecordCallCreated(SidecarCapabilityCallIdentity call)
    {
        try
        {
            Volatile.Read(ref _callCreatedObserver)?.Invoke(call);
            Emit(
                "call-created|"
                + $"call={call.CallId:N};sequence={call.Sequence};"
                + $"capability={call.Capability};module={call.SourceId};graph={call.GraphId}");
        }
        catch
        {
        }
    }

    internal static void RecordActionResponseFrame(Guid callId)
    {
        Interlocked.Exchange(ref _lastActionResponseCallId, callId.ToString("N"));
        RecordRebindState(
            "action-response-frame-received",
            callId.ToString("N"));
    }

    internal static void RecordRebindState(string phase, string state)
    {
        try
        {
            var observedState = state;
            var lastActionResponseCallId = Volatile.Read(ref _lastActionResponseCallId);
            if (phase.StartsWith("rebind-", StringComparison.Ordinal)
                && lastActionResponseCallId is not null)
            {
                observedState += $";lastActionResponse={lastActionResponseCallId}";
            }

            Emit($"{phase}|{observedState}", notifyRebindObserver: true);
        }
        catch
        {
        }
    }

    internal static void RecordActionFailure(
        SidecarCapabilityCallIdentity call,
        Guid terminalId,
        Exception exception) =>
        RecordFailure("action-failure", call, terminalId, exception);

    internal static void RecordTerminalFailure(
        SidecarCapabilityCallIdentity call,
        Guid terminalId,
        Exception exception) =>
        RecordFailure("terminal-failure", call, terminalId, exception);

    internal static void RecordStorageStage(
        SidecarStorageCapabilityRequest request,
        string phase,
        bool accepted,
        string? code = null,
        string? message = null,
        Exception? exception = null)
    {
        try
        {
            var resolvedCode = code
                ?? (exception as OutOfProcessCapabilityException)?.Code
                ?? (exception is null ? null : "exception");
            var exceptionType = exception?.GetType().FullName;
            var exceptionMessage = exception?.Message;
            Emit(
                "storage-stage|"
                + $"phase={Sanitize(phase)};result={(accepted ? "accepted" : "rejected")};"
                + $"session={request.Call.SessionId};request={request.Call.RequestId};"
                + $"cancellation={request.Call.CancellationId};call={request.Call.CallId:N};"
                + $"nonce={request.Call.ReplayNonce};module={request.Call.SourceId};"
                + $"graph={request.Call.GraphId};capability={request.Call.Capability};"
                + $"sequence={request.Call.Sequence};deadline={request.Call.Deadline:o};"
                + $"code={Sanitize(resolvedCode)};message={Sanitize(message)};"
                + $"exception={Sanitize(exceptionType)};exceptionMessage={Sanitize(exceptionMessage)}");
        }
        catch
        {
        }
    }

    internal static void RecordStorageContinuationBoundary(
        SidecarStorageCapabilityRequest request,
        SidecarCapabilityCallIdentity? parentCall,
        Guid? activeCarrierId,
        bool activeContextFound,
        long receivingLastSequence,
        DateTimeOffset? targetContextDeadline)
    {
        try
        {
            var parentCallId = parentCall?.CallId.ToString("N") ?? "none";
            var parentSequence = parentCall?.Sequence.ToString() ?? "none";
            var carrierId = activeCarrierId?.ToString() ?? "none";
            var deadline = targetContextDeadline?.ToString("O") ?? "none";
            Emit(
                "storage-continuation-predicate|"
                + $"session={request.Call.SessionId};request={request.Call.RequestId};"
                + $"cancellation={request.Call.CancellationId};call={request.Call.CallId:N};"
                + $"parentCall={parentCallId};parentSequence={parentSequence};"
                + $"activeCarrier={carrierId};activeContextFound={activeContextFound};"
                + $"continuationAuthorityNull={request.HostEntryContinuationAuthority is null};"
                + $"sequence={request.Call.Sequence};receivingLastSequence={receivingLastSequence};"
                + $"requestDeadline={request.Deadline:O};targetContextDeadline={deadline};"
                + $"module={request.Call.SourceId};graph={request.Call.GraphId};"
                + $"capability={request.Call.Capability}");
        }
        catch
        {
        }
    }

    internal static void RecordRootRelayImport(
        SidecarHostActionEntryRootRelay relay,
        SidecarCapabilitySession session,
        SidecarCapabilityValidationResult result,
        DateTimeOffset now)
    {
        try
        {
            var peerCall = relay.PeerCall;
            var binding = session.Binding;
            var context = relay.Context;
            var authority = relay.Authority;
            var authorityHash = SidecarCapabilityTransportValidation
                .ComputeTerminalAuthorityBindingHash(authority);
            var authorityDomain = authority.CancellationState
                == SidecarHostTerminalCancellationState.None
                && authority.CancellationAt == DateTimeOffset.MinValue;
            var authorityHashMatch = string.Equals(
                authority.CanonicalBindingHash,
                authorityHash,
                StringComparison.OrdinalIgnoreCase);
            Emit(
                "root-relay-import|"
                + $"result={(result.Accepted ? "accepted" : "rejected")};"
                + $"code={Sanitize(result.Code)};message={Sanitize(result.Message)};"
                + $"call={peerCall.CallId:N};sequence={peerCall.Sequence};"
                + $"sessionMatch={peerCall.SessionId == binding.SessionId};"
                + $"requestMatch={peerCall.RequestId == binding.RequestId};"
                + $"cancellationMatch={peerCall.CancellationId == binding.CancellationId};"
                + $"moduleMatch={string.Equals(peerCall.SourceId, binding.SourceId, StringComparison.Ordinal)};"
                + $"graphMatch={string.Equals(peerCall.GraphId, binding.GraphId, StringComparison.Ordinal)};"
                + $"peerGeneration={relay.PeerBindingGeneration};"
                + $"bindingGeneration={session.BindingGeneration};"
                + $"lastSequence={session.LastSequence};"
                + $"sequenceIsNext={peerCall.Sequence == session.LastSequence + 1};"
                + $"deadline={peerCall.Deadline:O};now={now:O};"
                + $"bindingExpires={binding.ExpiresAt:O};"
                + $"contextDeadline={context.Deadline:O};"
                + $"deadlineValid={peerCall.Deadline > now};"
                + $"deadlineWithinBinding={peerCall.Deadline <= binding.ExpiresAt};"
                + $"deadlineWithinContext={peerCall.Deadline <= context.Deadline};"
                + $"relayWellFormed={relay.IsWellFormed};"
                + $"rootBudgetPresent={relay.RootBudgetId != Guid.Empty};"
                + $"authorityDomain={authorityDomain};"
                + $"authorityHashMatch={authorityHashMatch};"
                + $"activeCarriers={session.ActiveHostActionEntryCarrierCount};"
                + $"issuedContexts={session.IssuedHostActionEntryContextCount};"
                + $"completedTombstones={session.CompletedHostActionEntryTombstoneCount}");
        }
        catch
        {
        }
    }

    private static void RecordFailure(
        string phase,
        SidecarCapabilityCallIdentity call,
        Guid terminalId,
        Exception exception)
    {
        try
        {
            var code = exception is OutOfProcessCapabilityException capability
                ? capability.Code
                : "exception";
            var type = exception.GetType().FullName ?? exception.GetType().Name;
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 160
                || message.Contains('{')
                || message.Contains('}')
                || message.Contains('[')
                || message.Contains(']'))
            {
                message = "redacted";
            }
            Emit(
                $"{phase}|call={call.CallId:N};terminal={terminalId:N};"
                + $"type={type};code={code};message={message}",
                notifyFailureObserver: true);
        }
        catch
        {
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "none";

        var sanitized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace(';', ',')
            .Replace('|', ',');
        if (sanitized.Length > 160
            || sanitized.Contains('{')
            || sanitized.Contains('}')
            || sanitized.Contains('[')
            || sanitized.Contains(']'))
        {
            return "redacted";
        }

        return sanitized;
    }

    private static void Emit(
        string record,
        bool notifyRebindObserver = false,
        bool notifyFailureObserver = false)
    {
        try
        {
            if (notifyRebindObserver)
                Volatile.Read(ref _rebindStateObserver)?.Invoke(record);
            if (notifyFailureObserver)
                Volatile.Read(ref _failureObserver)?.Invoke(record);
            var path = Environment.GetEnvironmentVariable(
                "SHARPCLAW_MODULESDK_PROTOCOL_EVIDENCE_PATH");
            if (string.IsNullOrWhiteSpace(path))
                return;

            lock (typeof(OutOfProcessProtocolTestFixture))
            {
                File.AppendAllText(
                    path,
                    record + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
#endif
