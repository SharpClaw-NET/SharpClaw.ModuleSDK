using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleSDK;

/// <summary>Invokes one module-owned action entry without runtime reflection.</summary>
public interface IModuleActionEntryInvoker
{
    /// <summary>Gets the exact action descriptor owned by the module.</summary>
    SidecarActionDescriptorIdentity Descriptor { get; }

    /// <summary>Gets the terminal registration identity.</summary>
    Guid TerminalId { get; }

    /// <summary>Invokes the registered terminal with host-authenticated context.</summary>
    ValueTask<SidecarTerminalExecutionResult> InvokeAsync(
        IServiceProvider services,
        SidecarActionTerminalExecutionContext context,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken);
}

/// <summary>Describes one neutral request for an action owned by another sidecar.</summary>
public sealed record ModuleCrossSidecarActionEntryRequest<TAction, TResult>(
    string TargetModuleId,
    ActionDescriptor<TAction, TResult> Descriptor,
    TAction Action);

/// <summary>Provides cross-sidecar action entry transport for an out-of-process host.</summary>
public interface IModuleCrossSidecarActionEntry
{
    /// <summary>Invokes one target-sidecar-owned action entry.</summary>
    ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
        ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
        CancellationToken cancellationToken);
}

/// <summary>Provides the cross-sidecar action-entry extension on the Contracts host entry.</summary>
public static class ModuleCrossSidecarActionEntryExtensions
{
    /// <summary>Invokes one target module terminal through the existing action exchange.</summary>
    public static ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
        this IHostActionEntry hostActionEntry,
        ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Descriptor);
        if (hostActionEntry is not IModuleCrossSidecarActionEntry crossSidecar)
        {
            throw new InvalidOperationException(
                "The configured host action entry does not support cross-sidecar transport.");
        }

        return crossSidecar.InvokeCrossSidecarAsync(
            request,
            cancellationToken);
    }
}

/// <summary>Describes one action terminal owned by a module.</summary>
public sealed class ModuleActionEntryRegistration
{
    internal ModuleActionEntryRegistration(
        string ownerModuleId,
        SidecarActionDescriptorIdentity descriptor,
        Type actionType,
        Type resultType,
        Type terminalType,
        Guid terminalId,
        IModuleActionEntryInvoker invoker)
    {
        OwnerModuleId = ownerModuleId;
        Descriptor = descriptor;
        ActionType = actionType;
        ResultType = resultType;
        TerminalType = terminalType;
        TerminalId = terminalId;
        Invoker = invoker;
    }

    /// <summary>Gets the owning module identifier.</summary>
    public string OwnerModuleId { get; }

    /// <summary>Gets the exact action descriptor identity.</summary>
    public SidecarActionDescriptorIdentity Descriptor { get; }

    /// <summary>Gets the action payload type.</summary>
    public Type ActionType { get; }

    /// <summary>Gets the result payload type.</summary>
    public Type ResultType { get; }

    /// <summary>Gets the terminal implementation type.</summary>
    public Type TerminalType { get; }

    /// <summary>Gets the stable terminal identifier.</summary>
    public Guid TerminalId { get; }

    /// <summary>Gets the typed terminal invoker.</summary>
    public IModuleActionEntryInvoker Invoker { get; }
}

internal sealed class ModuleActionEntryInvoker<TAction, TResult, TTerminal> : IModuleActionEntryInvoker
    where TTerminal : class, IHostActionEntryTerminal<TAction, TResult>
{
    private readonly SidecarActionDescriptorIdentity _descriptor;

    public ModuleActionEntryInvoker(
        SidecarActionDescriptorIdentity descriptor,
        Guid terminalId)
    {
        _descriptor = descriptor;
        TerminalId = terminalId;
    }

    public SidecarActionDescriptorIdentity Descriptor => _descriptor;

    public Guid TerminalId { get; }

    public async ValueTask<SidecarTerminalExecutionResult> InvokeAsync(
        IServiceProvider services,
        SidecarActionTerminalExecutionContext context,
        IHostActionEntry hostActionEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        if (!OutOfProcessDescriptorMatches(context.Descriptor, _descriptor)
            || !context.IsWellFormed)
        {
            throw new InvalidOperationException(
                "The host terminal context does not match the module action entry.");
        }

        var action = JsonSerializer.Deserialize<TAction>(
                context.EffectiveAction.Value.GetRawText(),
                SidecarCapabilityTransportCodec.CreateJsonOptions())
            ?? throw new InvalidOperationException(
                "The host terminal context contains no action payload.");
        var actionContext = new ActionContext<TAction>(
            context.InvocationId,
            context.ParentInvocationId,
            context.TraceId,
            context.IdempotencyKey,
            context.Depth,
            context.Attempt,
            context.Deadline,
            _descriptor.Key,
            context.Call.ModuleId,
            context.Caller,
            action,
            context.Features,
            context.Snapshot)
        {
            HostActionEntry = hostActionEntry,
        };
        var terminal = services.GetRequiredService<TTerminal>();
        var result = await terminal.InvokeAsync(actionContext, cancellationToken);
        var payload = CreatePayload(
            result,
            _descriptor.ResultTypeIdentity,
            _descriptor.ResultSchemaVersion);
        return new SidecarTerminalExecutionResult(
            payload,
            null!,
            Completed: true);
    }

    private static bool OutOfProcessDescriptorMatches(
        SidecarActionDescriptorIdentity actual,
        SidecarActionDescriptorIdentity expected) =>
        actual.Key == expected.Key
        && actual.Version == expected.Version
        && string.Equals(actual.Category, expected.Category, StringComparison.Ordinal)
        && string.Equals(actual.InputTypeIdentity, expected.InputTypeIdentity, StringComparison.Ordinal)
        && actual.InputSchemaVersion == expected.InputSchemaVersion
        && string.Equals(actual.InputSchemaHash, expected.InputSchemaHash, StringComparison.Ordinal)
        && string.Equals(actual.ResultTypeIdentity, expected.ResultTypeIdentity, StringComparison.Ordinal)
        && actual.ResultSchemaVersion == expected.ResultSchemaVersion
        && string.Equals(actual.ResultSchemaHash, expected.ResultSchemaHash, StringComparison.Ordinal)
        && string.Equals(actual.DescriptorHash, expected.DescriptorHash, StringComparison.Ordinal);

    private static SidecarSerializedPayload CreatePayload<T>(
        T value,
        string typeIdentity,
        int schemaVersion)
    {
        var bytes = SidecarCapabilityTransportCodec.Serialize(value);
        using var document = JsonDocument.Parse(bytes);
        var canonical = SidecarCapabilityTransportCodec.Serialize(document.RootElement);
        return new SidecarSerializedPayload(
            typeIdentity,
            schemaVersion,
            SidecarCapabilityTransportCodec.ComputeSha256(canonical),
            document.RootElement.Clone(),
            canonical.Length);
    }
}
