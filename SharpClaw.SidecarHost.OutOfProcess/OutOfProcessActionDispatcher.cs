using System.Text.Json;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal sealed class OutOfProcessActionDispatcher : IActionDispatcher
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;

    public OutOfProcessActionDispatcher(OutOfProcessModuleCapabilityTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
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
        try
        {
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
        catch
        {
            _transport.ReleaseCallReservation(call);
            throw;
        }
    }

    public ValueTask<IActionOutcome<TResult>> RunExternalAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<IActionOutcome<TResult>>(
            new NotSupportedException(
                "External action dispatch is host-owned and cannot start from a module sidecar."));

    public ValueTask<IActionOutcome<JsonElement>> RunExternalSerializedAsync(
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptor,
        JsonElement action,
        Func<ActionContext<JsonElement>, CancellationToken, ValueTask<JsonElement>> terminal,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<IActionOutcome<JsonElement>>(
            new NotSupportedException(
                "External action dispatch is host-owned and cannot start from a module sidecar."));

    public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
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
        throw new ActionFailedException(
            outcome.Error
                ?? new ExecutionError(
                    SidecarCapabilityErrors.HostFailure,
                    "The sidecar action did not complete."));
    }

    public ValueTask<TResult> RunExternalRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<TResult>(
            new NotSupportedException(
                "External action dispatch is host-owned and cannot start from a module sidecar."));

    private static async ValueTask<SidecarActionTerminalTransportResponse> ExecuteTerminalAsync<TAction, TResult>(
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        SidecarActionTerminalTransportRequest request,
        SidecarActionDescriptorIdentity identity,
        SidecarSafeFailureIdentity safeFailure,
        CancellationToken ct)
    {
        var action = Deserialize<TAction>(request.EffectiveAction);
        var context = CreateActionContext<TAction>(request, action);
        try
        {
            var result = await terminal(context, ct);
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
                safeFailure)
            {
                TerminalId = request.TerminalId,
            };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
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

    internal static ActionContext<TAction> CreateActionContext<TAction>(
        SidecarActionTerminalTransportRequest request,
        TAction action,
        IHostActionEntry? hostActionEntry = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.Context
            ?? throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal request has no dispatcher action context.");
        if (!context.IsWellFormed)
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The terminal request dispatcher action context is invalid.");
        }

        return new ActionContext<TAction>(
            context.InvocationId,
            context.ParentInvocationId,
            context.TraceId,
            context.IdempotencyKey,
            context.Depth,
            context.Attempt,
            context.Deadline,
            request.Descriptor.Key,
            request.Authority.SourceId,
            context.Caller,
            action,
            context.Features,
            context.Snapshot)
        {
            HostActionEntry = hostActionEntry,
        };
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

    internal static SidecarSerializedPayload Payload<T>(T value) =>
        Payload(
            value,
            typeof(T).AssemblyQualifiedName
                ?? typeof(T).FullName
                ?? typeof(T).Name,
            schemaVersion: 1);

    internal static SidecarSerializedPayload EmptyPayloadForFailure() =>
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
