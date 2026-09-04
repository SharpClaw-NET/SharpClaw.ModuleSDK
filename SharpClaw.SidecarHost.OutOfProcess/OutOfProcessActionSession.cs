using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess;

internal enum SidecarActionCompletionKind
{
    Continuation,
    ReplaceResult,
    Cancel,
    Fail,
}

internal sealed record SidecarActionCompletion(
    SidecarActionCompletionKind Kind,
    JsonElement? Result = null,
    string? Reason = null,
    ExecutionError? Error = null,
    ContinuationOutcome? HostOutcome = null);

internal interface ISidecarActionOutcomeCarrier
{
    SidecarActionCompletion Completion { get; }
}

internal sealed class SidecarActionOutcome<TResult>(
    SidecarActionCompletion completion,
    TResult? result,
    ActionOutcomeKind kind,
    ContinuationToken? continuation,
    ExecutionError? error,
    ActionUncertainty? uncertainty)
    : IActionOutcome<TResult>, ISidecarActionOutcomeCarrier
{
    public SidecarActionCompletion Completion { get; } = completion;

    public ActionOutcomeKind Kind { get; } = kind;

    public TResult? Result { get; } = result;

    public ContinuationToken? Continuation { get; } = continuation;

    public ExecutionError? Error { get; } = error;

    public ActionUncertainty? Uncertainty { get; } = uncertainty;
}

internal sealed class SidecarUntypedActionOutcome(
    SidecarActionCompletion completion,
    JsonElement? result,
    ActionOutcomeKind kind,
    ContinuationToken? continuation,
    ExecutionError? error,
    ActionUncertainty? uncertainty)
    : IUntypedActionOutcome, ISidecarActionOutcomeCarrier
{
    public SidecarActionCompletion Completion { get; } = completion;

    public ActionOutcomeKind Kind { get; } = kind;

    public JsonElement? Result { get; } = result;

    public ContinuationToken? Continuation { get; } = continuation;

    public ExecutionError? Error { get; } = error;

    public ActionUncertainty? Uncertainty { get; } = uncertainty;
}

internal sealed class SidecarActionControlSession(OutOfProcessProtocolSession protocol)
{
    private int _continuationUsed;

    public bool HasContinuation { get; private set; }

    public async ValueTask<ContinuationOutcome> ContinueAsync(
        SidecarContinuationCommand command,
        JsonElement? value,
        string? reason,
        string? code,
        string? message,
        ActionDeferRequest? defer,
        TimeSpan? backoff,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _continuationUsed, 1) != 0)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.ContinuationAlreadyUsed,
                "The module action control can use its continuation only once.");
        }

        HasContinuation = true;
        var request = protocol.Create(
            SidecarProtocolMessageKind.EffectRequest,
            header => new SidecarEffectRequest(
                header,
                protocol.State.ContinuationHandleId,
                command,
                value,
                reason,
                code,
                message,
                defer,
                backoff));
        await protocol.SendAsync(request, ct: ct);

        var acceptedFrame = await protocol.ReceiveAsync(ct);
        if (acceptedFrame.Message is SidecarProtocolError acceptedError)
            throw Error(acceptedError);
        if (acceptedFrame.Message is not ContinuationAccepted accepted
            || accepted.Command != command)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.ContinuationCommandMismatch,
                "The host did not accept the requested continuation command.");
        }

        var outcomeFrame = await protocol.ReceiveAsync(ct);
        if (outcomeFrame.Message is SidecarProtocolError outcomeError)
            throw Error(outcomeError);
        if (outcomeFrame.Message is not ContinuationOutcome outcome)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The host did not return a continuation outcome.");
        }

        return outcome;
    }

    private static OutOfProcessProtocolException Error(SidecarProtocolError error) =>
        new(error.Code, error.Message);
}

internal sealed class SidecarActionControl<TAction, TResult>(
    SidecarActionControlSession session,
    JsonSerializerOptions payloadJsonOptions)
    : IActionControl<TAction, TResult>
{
    public async ValueTask<IActionOutcome<TResult>> ProceedAsync(CancellationToken ct) =>
        FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.ContinueOriginal,
            null,
            null,
            null,
            null,
            null,
            null,
            ct));

    public async ValueTask<IActionOutcome<TResult>> ProceedWithInputAsync(
        ActionReplacement<TAction> replacement,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.ContinueReplacement,
            JsonSerializer.SerializeToElement(replacement.Value, payloadJsonOptions),
            replacement.Reason,
            null,
            null,
            null,
            null,
            ct));
    }

    public IActionOutcome<TResult> ReplaceResult(TResult result, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var value = JsonSerializer.SerializeToElement(result, payloadJsonOptions);
        return new SidecarActionOutcome<TResult>(
            new SidecarActionCompletion(SidecarActionCompletionKind.ReplaceResult, value, reason),
            result,
            ActionOutcomeKind.Completed,
            null,
            null,
            null);
    }

    public IActionOutcome<TResult> Cancel(string code, string message)
    {
        var error = RequireError(code, message);
        return new SidecarActionOutcome<TResult>(
            new SidecarActionCompletion(SidecarActionCompletionKind.Cancel, Error: error),
            default,
            ActionOutcomeKind.Cancelled,
            null,
            error,
            null);
    }

    public IActionOutcome<TResult> Fail(ExecutionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SidecarActionOutcome<TResult>(
            new SidecarActionCompletion(SidecarActionCompletionKind.Fail, Error: error),
            default,
            ActionOutcomeKind.Failed,
            null,
            error,
            null);
    }

    public async ValueTask<IActionOutcome<TResult>> DeferAsync(
        ActionDeferRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.Defer,
            null,
            null,
            null,
            null,
            request,
            null,
            ct));
    }

    public async ValueTask<IActionOutcome<TResult>> RepeatAsync(
        ActionRepeatRequest<TAction> request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.Repeat,
            JsonSerializer.SerializeToElement(request.Value, payloadJsonOptions),
            request.Reason,
            null,
            null,
            null,
            request.Backoff,
            ct));
    }

    private IActionOutcome<TResult> FromContinuation(ContinuationOutcome outcome)
    {
        var result = outcome.Result.HasValue
            ? outcome.Result.Value.Deserialize<TResult>(payloadJsonOptions)
            : default;
        return new SidecarActionOutcome<TResult>(
            new SidecarActionCompletion(
                SidecarActionCompletionKind.Continuation,
                outcome.Result,
                HostOutcome: outcome),
            result,
            outcome.Kind,
            outcome.Continuation,
            outcome.Error,
            outcome.Uncertainty);
    }

    private static ExecutionError RequireError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ExecutionError(code, message);
    }
}

internal sealed class SidecarUntypedActionControl(SidecarActionControlSession session)
    : IUntypedActionControl
{
    public async ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken ct) =>
        FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.ContinueOriginal,
            null,
            null,
            null,
            null,
            null,
            null,
            ct));

    public async ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
        JsonElement replacement,
        string reason,
        CancellationToken ct) =>
        FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.ContinueReplacement,
            replacement,
            reason,
            null,
            null,
            null,
            null,
            ct));

    public IUntypedActionOutcome ReplaceResult(JsonElement result, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SidecarUntypedActionOutcome(
            new SidecarActionCompletion(SidecarActionCompletionKind.ReplaceResult, result, reason),
            result,
            ActionOutcomeKind.Completed,
            null,
            null,
            null);
    }

    public IUntypedActionOutcome Cancel(string code, string message)
    {
        var error = RequireError(code, message);
        return new SidecarUntypedActionOutcome(
            new SidecarActionCompletion(SidecarActionCompletionKind.Cancel, Error: error),
            null,
            ActionOutcomeKind.Cancelled,
            null,
            error,
            null);
    }

    public IUntypedActionOutcome Fail(ExecutionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SidecarUntypedActionOutcome(
            new SidecarActionCompletion(SidecarActionCompletionKind.Fail, Error: error),
            null,
            ActionOutcomeKind.Failed,
            null,
            error,
            null);
    }

    public async ValueTask<IUntypedActionOutcome> DeferAsync(
        ActionDeferRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.Defer,
            null,
            null,
            null,
            null,
            request,
            null,
            ct));
    }

    public async ValueTask<IUntypedActionOutcome> RepeatAsync(
        JsonElement replacement,
        string reason,
        TimeSpan? backoff,
        CancellationToken ct) =>
        FromContinuation(await session.ContinueAsync(
            SidecarContinuationCommand.Repeat,
            replacement,
            reason,
            null,
            null,
            null,
            backoff,
            ct));

    private static IUntypedActionOutcome FromContinuation(ContinuationOutcome outcome) =>
        new SidecarUntypedActionOutcome(
            new SidecarActionCompletion(
                SidecarActionCompletionKind.Continuation,
                outcome.Result,
                HostOutcome: outcome),
            outcome.Result,
            outcome.Kind,
            outcome.Continuation,
            outcome.Error,
            outcome.Uncertainty);

    private static ExecutionError RequireError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ExecutionError(code, message);
    }
}

internal static class OutOfProcessActionSession
{
    public static async Task RunAsync(
        OutOfProcessModuleRuntime runtime,
        OutOfProcessProtocolSession protocol,
        HookInvokeStart start,
        SidecarHostAuthorization authorization,
        CancellationToken ct)
    {
        var descriptor = start.UntypedDescriptor
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                "The action invocation has no immutable descriptor.");
        var hook = runtime.Graph.ActionDispatch.Select(descriptor)
            .SingleOrDefault(candidate => string.Equals(
                candidate.HookId,
                start.HookId,
                StringComparison.Ordinal))
            ?? throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnknownHostDescriptor,
                $"Action hook '{start.HookId}' is not selected for '{start.ActionKey.Value}'.");
        var expectedMode = hook.IsUntyped ? SidecarPayloadMode.Untyped : SidecarPayloadMode.Typed;
        if (start.PayloadMode != expectedMode)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.UnsupportedSchema,
                $"Action hook '{hook.HookId}' requires payload mode '{expectedMode}'.");
        }

        var controlSession = new SidecarActionControlSession(protocol);
        await using var scope = runtime.Services.CreateAsyncScope();
        SidecarActionCompletion completion;
        try
        {
            completion = hook.IsUntyped
                ? await InvokeUntypedAsync(
                    runtime,
                    scope.ServiceProvider,
                    hook,
                    start,
                    authorization,
                    controlSession,
                    ct)
                : await InvokeTypedAsync(
                    runtime,
                    scope.ServiceProvider,
                    hook,
                    start,
                    authorization,
                    controlSession,
                    ct);
        }
        catch (OutOfProcessProtocolException ex) when (controlSession.HasContinuation)
        {
            completion = new SidecarActionCompletion(
                SidecarActionCompletionKind.Fail,
                Error: new ExecutionError(ex.Code, ex.Message));
        }
        catch (OutOfProcessProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            completion = new SidecarActionCompletion(
                SidecarActionCompletionKind.Fail,
                Error: new ExecutionError(
                    "module_handler_failed",
                    "The module action handler failed."));
        }

        if (!controlSession.HasContinuation
            && completion.Kind == SidecarActionCompletionKind.Cancel)
        {
            completion = await ContinueCancellationAsync(
                controlSession,
                completion.Error!,
                ct);
        }

        await SendCompletionAsync(protocol, completion, controlSession.HasContinuation, ct);
        var completedFrame = await protocol.ReceiveAsync(ct);
        if (completedFrame.Message is SidecarProtocolError error)
            throw new OutOfProcessProtocolException(error.Code, error.Message);
        if (completedFrame.Message is not HookCompleted)
        {
            throw new OutOfProcessProtocolException(
                SidecarProtocolErrors.MalformedMessage,
                "The host did not close the action hook exchange.");
        }
    }

    private static async ValueTask<SidecarActionCompletion> InvokeUntypedAsync(
        OutOfProcessModuleRuntime runtime,
        IServiceProvider services,
        ModuleActionHook hook,
        HookInvokeStart start,
        SidecarHostAuthorization authorization,
        SidecarActionControlSession controlSession,
        CancellationToken ct)
    {
        var handler = ActivatorUtilities.GetServiceOrCreateInstance(
            services,
            hook.HandlerType) as IAnyActionInterceptor
            ?? throw new InvalidOperationException(
                $"Handler '{hook.HandlerType.FullName}' is not an untyped action interceptor.");
        var context = new UntypedActionContext(
            start.InvocationId,
            start.ParentInvocationId,
            start.TraceId,
            start.InvocationId,
            0,
            1,
            start.Header.Deadline,
            start.ActionKey.Value,
            start.Caller,
            start.Features,
            runtime.Graph.ContractHash,
            start.UntypedDescriptor!,
            start.Input);
        var outcome = await handler.InvokeAsync(
            context,
            new SidecarUntypedActionControl(controlSession),
            ct);
        return GetCompletion(outcome);
    }

    private static async ValueTask<SidecarActionCompletion> InvokeTypedAsync(
        OutOfProcessModuleRuntime runtime,
        IServiceProvider services,
        ModuleActionHook hook,
        HookInvokeStart start,
        SidecarHostAuthorization authorization,
        SidecarActionControlSession controlSession,
        CancellationToken ct)
    {
        var interceptor = hook.HandlerType.GetInterfaces().Single(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IActionInterceptor<,>));
        var arguments = interceptor.GetGenericArguments();
        var adapterType = typeof(TypedActionHookAdapter<,>).MakeGenericType(arguments);
        var adapter = (ITypedActionHookAdapter)(Activator.CreateInstance(adapterType, nonPublic: true)
            ?? throw new InvalidOperationException("The typed action adapter could not be created."));
        return await adapter.InvokeAsync(
            runtime,
            services,
            hook,
            start,
            authorization,
            controlSession,
            ct);
    }

    private static SidecarActionCompletion GetCompletion(object outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome is ISidecarActionOutcomeCarrier carrier)
            return carrier.Completion;
        throw new InvalidOperationException(
            "The action handler returned an outcome that did not come from its action control.");
    }

    private static async ValueTask<SidecarActionCompletion> ContinueCancellationAsync(
        SidecarActionControlSession session,
        ExecutionError error,
        CancellationToken ct)
    {
        var outcome = await session.ContinueAsync(
            SidecarContinuationCommand.Cancel,
            null,
            null,
            error.Code,
            error.Message,
            null,
            null,
            ct);
        return new SidecarActionCompletion(
            SidecarActionCompletionKind.Continuation,
            outcome.Result,
            HostOutcome: outcome);
    }

    private static async Task SendCompletionAsync(
        OutOfProcessProtocolSession protocol,
        SidecarActionCompletion completion,
        bool hasContinuation,
        CancellationToken ct)
    {
        if (!hasContinuation && completion.Kind == SidecarActionCompletionKind.ReplaceResult)
        {
            await protocol.SendAsync(CreateReplacement(protocol, completion), ct: ct);
            return;
        }

        var hookKind = completion.Kind switch
        {
            SidecarActionCompletionKind.Fail => SidecarHookOutcomeKind.Failed,
            SidecarActionCompletionKind.Cancel => SidecarHookOutcomeKind.Cancelled,
            _ => SidecarHookOutcomeKind.Completed,
        };
        var hasReplacement = completion.Kind == SidecarActionCompletionKind.ReplaceResult;
        var hookOutcome = protocol.Create(
            SidecarProtocolMessageKind.HookOutcome,
            header => new HookOutcome(
                header,
                protocol.State.ContinuationHandleId,
                hookKind,
                completion.Error));
        await protocol.SendAsync(hookOutcome, hasReplacement, ct);
        if (hasReplacement)
            await protocol.SendAsync(CreateReplacement(protocol, completion), ct: ct);
    }

    private static SidecarResultReplacement CreateReplacement(
        OutOfProcessProtocolSession protocol,
        SidecarActionCompletion completion) =>
        protocol.Create(
            SidecarProtocolMessageKind.ResultReplacement,
            header => new SidecarResultReplacement(
                header,
                protocol.State.ContinuationHandleId,
                completion.Result!.Value,
                completion.Reason!));

    private interface ITypedActionHookAdapter
    {
        ValueTask<SidecarActionCompletion> InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            IServiceProvider services,
            ModuleActionHook hook,
            HookInvokeStart start,
            SidecarHostAuthorization authorization,
            SidecarActionControlSession controlSession,
            CancellationToken ct);
    }

    private sealed class TypedActionHookAdapter<TAction, TResult> : ITypedActionHookAdapter
    {
        public async ValueTask<SidecarActionCompletion> InvokeAsync(
            OutOfProcessModuleRuntime runtime,
            IServiceProvider services,
            ModuleActionHook hook,
            HookInvokeStart start,
            SidecarHostAuthorization authorization,
            SidecarActionControlSession controlSession,
            CancellationToken ct)
        {
            var handler = ActivatorUtilities.GetServiceOrCreateInstance(
                services,
                hook.HandlerType) as IActionInterceptor<TAction, TResult>
                ?? throw new InvalidOperationException(
                    $"Handler '{hook.HandlerType.FullName}' has an invalid typed action contract.");
            var payloadJsonOptions = OutOfProcessProtocolCodec.CreatePayloadJsonOptions();
            var action = start.Input.Deserialize<TAction>(payloadJsonOptions)
                ?? throw new OutOfProcessProtocolException(
                    SidecarProtocolErrors.UnsupportedSchema,
                    "The typed action input could not be deserialized.");
            var snapshot = new ActionPipelineSnapshot(
                runtime.Graph.ContractHash,
                authorization.ActionGrants,
                authorization.EventGrants);
            var context = new ActionContext<TAction>(
                start.InvocationId,
                start.ParentInvocationId,
                start.TraceId,
                start.InvocationId,
                0,
                1,
                start.Header.Deadline,
                start.ActionKey,
                start.ActionKey.Value,
                start.Caller,
                action,
                start.Features,
                snapshot);
            var outcome = await handler.InvokeAsync(
                context,
                new SidecarActionControl<TAction, TResult>(
                    controlSession,
                    payloadJsonOptions),
                ct);
            return GetCompletion(outcome);
        }
    }
}
