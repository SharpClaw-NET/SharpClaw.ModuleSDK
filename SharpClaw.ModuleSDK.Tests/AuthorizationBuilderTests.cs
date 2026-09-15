using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK.Tests;

public sealed class AuthorizationBuilderTests
{
    [Test]
    public async Task HostAuthorizationEntryPreservesDeclaredFailure()
    {
        var error = new ExecutionError(
            "policy_unavailable",
            "The authorization policy is unavailable.",
            IsRetryable: true,
            new Dictionary<string, string>
            {
                ["policy"] = "primary",
            });
        var authorization = new HostAuthorizationEntry(new FailedHostActionEntry(error));

        Func<Task> evaluate = async () =>
            await authorization.EvaluateAsync(
                CreateHostContext(),
                new AuthorizationRequest(
                    "resource.read",
                    new AuthorizationResource("resource", "sample")));

        var exception = await evaluate.Should().ThrowAsync<ActionFailedException>();
        exception.Which.Error.Should().BeSameAs(error);
    }

    [Test]
    public async Task HostAuthorizationEntryUsesSafeFailureWhenErrorIsMissing()
    {
        var authorization = new HostAuthorizationEntry(new FailedHostActionEntry(null));

        Func<Task> evaluate = async () =>
            await authorization.EvaluateAsync(
                CreateHostContext(),
                new AuthorizationRequest(
                    "resource.read",
                    new AuthorizationResource("resource", "sample")));

        var exception = await evaluate.Should().ThrowAsync<ActionFailedException>();
        exception.Which.Error.Should().Be(new ExecutionError(
            "authorization_failed",
            "Authorization failed without an error."));
    }

    private static HostActionEntryRequestContext CreateHostContext()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "authorization-test-capability",
            HostActionEntryIngress.Cli,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline);
    }

    private sealed class FailedHostActionEntry(ExecutionError? error) :
        IHostActionEntry,
        IModuleCrossSidecarActionEntry
    {
        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<TResult>>(new FailedActionOutcome<TResult>(error));

        public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailedActionOutcome<TResult>(ExecutionError? error) : IActionOutcome<TResult>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Failed;

        public TResult Result => default!;

        public ContinuationToken? Continuation => null;

        public ExecutionError? Error => error;

        public ActionUncertainty? Uncertainty => null;
    }
}
