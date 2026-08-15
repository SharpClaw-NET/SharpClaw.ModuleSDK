using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class OutOfProcessHostActionEntry : IHostActionEntry
{
    private readonly OutOfProcessModuleCapabilityTransport _transport;

    public OutOfProcessHostActionEntry(
        OutOfProcessModuleCapabilityTransport transport) =>
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        if (!request.IsWellFormed(now))
        {
            throw new OutOfProcessCapabilityException(
                SidecarCapabilityErrors.MalformedMessage,
                "The host action entry request is invalid or expired.");
        }

        var context = request.Context;
        var lineageMatches = context?.Contribution is { } contribution
            && (context.Ingress == HostActionEntryIngress.Tool
                ? HostActionEntryAuthorityValidator.MatchesDescriptorLineage(
                    contribution.Lineage,
                    request.Descriptor)
                : HostActionEntryAuthorityValidator.MatchesLineage(
                    contribution.Lineage,
                    request.Descriptor,
                    request.Action));
        if (context is null
            || context.Ingress != HostActionEntryIngress.Tool
                && context.Ingress != HostActionEntryIngress.Cli
                && context.Ingress != HostActionEntryIngress.Endpoint
                && context.Ingress != HostActionEntryIngress.CrossModule
            || context.Contribution is null
            || !lineageMatches)
        {
            throw new OutOfProcessCapabilityException(
                SharpClaw.Contracts.Modules.SidecarCapabilityErrors.SpoofedIdentity,
                "The host action entry request context does not match the typed host authority.");
        }

        var call = _transport.CreateCall(
            SidecarCapabilityKind.Action,
            request.Deadline,
            cancellationToken);
        var identity = OutOfProcessActionDescriptorIdentity.Create(request.Descriptor);
        var actionPayload = OutOfProcessActionDispatcher.Payload(
            request.Action,
            identity.InputTypeIdentity,
            identity.InputSchemaVersion);
        var cancellation = new SidecarCancellationIdentity(
            call.CancellationId,
            SidecarCapabilitySessionValidator.ComputeBindingHash(_transport.Binding),
            request.Deadline);
        var sidecarRequest = SidecarActionCapabilityRequest.HostEntry(
            call,
            identity,
            actionPayload,
            cancellation,
            request.Deadline,
            context);
        var response = await _transport.InvokeActionAsync(
            sidecarRequest,
            terminal: null,
            cancellationToken);
        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
    }
}
