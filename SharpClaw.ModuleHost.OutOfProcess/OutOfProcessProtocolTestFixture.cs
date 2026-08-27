#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

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

    internal static void RecordCallCreated(SidecarCapabilityCallIdentity call)
    {
        try
        {
            Volatile.Read(ref _callCreatedObserver)?.Invoke(call);
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
                + $"nonce={request.Call.ReplayNonce};module={request.Call.ModuleId};"
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
                + $"module={request.Call.ModuleId};graph={request.Call.GraphId};"
                + $"capability={request.Call.Capability}");
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
