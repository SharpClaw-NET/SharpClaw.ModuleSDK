using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessActionDispatcher : IActionDispatcher
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;

    public OutOfProcessActionDispatcher(OutOfProcessModuleCapabilityTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(snapshot);
        var deadline = DateTimeOffset.UtcNow.Add(
            descriptor.DefaultTimeout > TimeSpan.Zero
                ? descriptor.DefaultTimeout
                : TimeSpan.FromMinutes(1));
        var call = _transport.CreateCall(SidecarCapabilityKind.Action, deadline, cancellationToken);
        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var request = new SidecarActionCapabilityRequest(
            call,
            SidecarActionInvocationKind.Run,
            identity,
            Payload(action, identity.InputTypeIdentity, identity.InputSchemaVersion),
            snapshot,
            new SidecarCancellationIdentity(
                call.CancellationId,
                SidecarCapabilitySessionValidator.ComputeBindingHash(_transport.Binding),
                deadline),
            new SidecarTerminalContinuationRequest(
                Guid.NewGuid(),
                true,
                null!,
                null!,
                deadline),
            deadline);
        var response = await _transport.InvokeActionAsync(
            request,
            (terminalRequest, ct) => ExecuteTerminalAsync(
                terminal,
                terminalRequest,
                identity,
                _transport.Binding.SafeFailure,
                ct),
            cancellationToken);
        return CreateOutcome<TResult>(response);
    }

    public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            descriptor,
            action,
            terminal,
            snapshot,
            cancellationToken);
        if (outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred)
            return outcome.Result!;
        if (outcome.Uncertainty is not null)
            throw new ActionOutcomeUncertainException(outcome.Uncertainty);
        throw new InvalidOperationException(
            outcome.Error?.Message ?? "The sidecar action did not complete.");
    }

    private static async ValueTask<SidecarActionTerminalTransportResponse> ExecuteTerminalAsync<TAction, TResult>(
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        SidecarActionTerminalTransportRequest request,
        SidecarActionDescriptorIdentity identity,
        SidecarSafeFailureIdentity safeFailure,
        CancellationToken ct)
    {
        var action = Deserialize<TAction>(request.EffectiveAction);
        try
        {
            var result = await terminal(action, ct);
            var payload = Payload(
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
                safeFailure);
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
                    EmptyPayload().ContentHash),
                new SidecarTerminalExecutionResult(
                    EmptyPayload(),
                    safeFailure,
                    Completed: false),
                request.Receipt,
                safeFailure);
        }
    }

    internal static IActionOutcome<TResult> CreateOutcome<TResult>(
        SidecarActionCapabilityResponse response)
    {
        var outcome = response.Outcome;
        return new OutOfProcessActionOutcome<TResult>(
            outcome.Kind,
            outcome.Result is null
                ? default!
                : Deserialize<TResult>(outcome.Result),
            outcome.Error,
            outcome.Uncertainty,
            outcome.Continuation);
    }

    internal static SidecarSerializedPayload Payload<T>(
        T value,
        string typeIdentity,
        int schemaVersion)
    {
        var bytes = SidecarCapabilityTransportCodec.Serialize(value);
        using var document = JsonDocument.Parse(bytes);
        var canonicalBytes = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        return new SidecarSerializedPayload(
            typeIdentity,
            schemaVersion,
            SidecarCapabilityTransportCodec.ComputeSha256(canonicalBytes),
            document.RootElement.Clone(),
            canonicalBytes.Length);
    }

    private static SidecarSerializedPayload EmptyPayload() =>
        new(
            "system.empty",
            1,
            SidecarCapabilityTransportCodec.ComputeSha256("null"u8),
            JsonDocument.Parse("null").RootElement.Clone(),
            4);

    private static T Deserialize<T>(SidecarSerializedPayload payload) =>
        JsonSerializer.Deserialize<T>(
            payload.Value.GetRawText(),
            SidecarCapabilityTransportCodec.CreateJsonOptions())
        ?? throw new OutOfProcessCapabilityException(
            SidecarCapabilityErrors.MalformedMessage,
            $"The sidecar returned no '{typeof(T).FullName}' value.");
}

internal sealed class OutOfProcessActionOutcome<TResult> : IActionOutcome<TResult>
{
    public OutOfProcessActionOutcome(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ActionUncertainty? uncertainty,
        ContinuationToken? continuation)
    {
        Kind = kind;
        Result = result;
        Error = error;
        Uncertainty = uncertainty;
        Continuation = continuation;
    }

    public ActionOutcomeKind Kind { get; }

    public TResult Result { get; }

    public ExecutionError? Error { get; }

    public ActionUncertainty? Uncertainty { get; }

    public ContinuationToken? Continuation { get; }
}
