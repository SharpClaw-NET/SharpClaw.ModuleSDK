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
                SidecarCapabilityErrors.InvalidPayload,
                "The host action entry request is invalid or expired.");
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
            request.Deadline);
        var response = await _transport.InvokeActionAsync(
            sidecarRequest,
            terminal: null,
            cancellationToken);
        return OutOfProcessActionDispatcher.CreateOutcome<TResult>(response);
    }
}
