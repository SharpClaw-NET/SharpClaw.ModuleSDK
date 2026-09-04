using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Invokes typed actions through the host-owned action entry.</summary>
public sealed class HostActionInvoker(IHostActionEntry host)
{
    public async ValueTask<TResult> InvokeAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        HostActionEntryRequestContext context,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terminal);

        var outcome = await host.InvokeAsync(
            new HostActionEntryRequest<TAction, TResult>(descriptor, action, context),
            terminal,
            cancellationToken);
        return RequireResult(descriptor.Key, outcome, cancellationToken);
    }

    public async ValueTask<TResult> InvokeNestedAsync<TParentAction, TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        ActionContext<TParentAction> parentContext,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(terminal);

        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException("The parent action has no host action entry.");
        var outcome = await hostEntry.InvokeNestedAsync(
            new HostActionEntryNestedRequest<TParentAction, TAction, TResult>(
                descriptor.Key,
                descriptor.Version,
                action,
                parentContext),
            terminal,
            cancellationToken);
        return RequireResult(descriptor.Key, outcome, cancellationToken);
    }

    private static TResult RequireResult<TResult>(
        SharpClawActionKey key,
        IActionOutcome<TResult> outcome,
        CancellationToken cancellationToken) =>
        outcome.Kind switch
        {
            ActionOutcomeKind.Completed => outcome.Result
                ?? throw new InvalidOperationException($"Action '{key.Value}' returned no result."),
            ActionOutcomeKind.Cancelled => throw new OperationCanceledException(
                $"Action '{key.Value}' was cancelled.",
                cancellationToken),
            ActionOutcomeKind.Deferred => throw new InvalidOperationException(
                $"Action '{key.Value}' was deferred."),
            ActionOutcomeKind.Failed => throw new InvalidOperationException(
                outcome.Error is null
                    ? $"Action '{key.Value}' failed without an error."
                    : $"Action '{key.Value}' failed: {outcome.Error.Code}: {outcome.Error.Message}"),
            ActionOutcomeKind.Uncertain => throw new InvalidOperationException(
                outcome.Uncertainty is null
                    ? $"Action '{key.Value}' has uncertain execution."
                    : $"Action '{key.Value}' has uncertain execution: {outcome.Uncertainty.Code}: {outcome.Uncertainty.Message}"),
            _ => throw new InvalidOperationException($"Action '{key.Value}' returned an unknown outcome."),
        };
}
