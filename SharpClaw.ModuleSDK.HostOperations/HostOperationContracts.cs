using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleSDK.HostOperations;

/// <summary>Requests the current neutral host module roster.</summary>
public sealed record HostModuleListAction;

/// <summary>Describes one module in the active host graph.</summary>
public sealed record HostModuleSummary(
    RegistrationStateResponse State,
    IReadOnlyList<string> ExportedContractNames)
{
    /// <summary>Gets whether the summary contains canonical module metadata.</summary>
    public bool IsWellFormed =>
        State is not null &&
        HostOperationContractValidation.IsCanonicalIdentifier(State.SourceId) &&
        HostOperationContractValidation.IsCanonicalIdentifier(State.ToolPrefix) &&
        ExportedContractNames is not null &&
        ExportedContractNames.All(HostOperationContractValidation.IsCanonicalIdentifier) &&
        ExportedContractNames.Distinct(StringComparer.Ordinal).Count() == ExportedContractNames.Count;
}

/// <summary>Contains the current external module root and module roster.</summary>
public sealed record HostModuleListResult(
    string ExternalModulesDirectory,
    IReadOnlyList<HostModuleSummary> Modules);

/// <summary>Identifies one host module lifecycle operation.</summary>
public enum HostModuleLifecycleOperation
{
    /// <summary>Loads one module into the active host graph.</summary>
    Load,

    /// <summary>Unloads one module from the active host graph.</summary>
    Unload,

    /// <summary>Reloads one module in the active host graph.</summary>
    Reload,
}

/// <summary>Requests one host module lifecycle transition.</summary>
public sealed record HostModuleLifecycleAction(
    HostModuleLifecycleOperation Operation,
    string SourceId)
{
    /// <summary>Gets whether the request has one canonical module identity.</summary>
    public bool IsWellFormed =>
        Enum.IsDefined(Operation) &&
        HostOperationContractValidation.IsCanonicalIdentifier(SourceId);
}

/// <summary>Contains the completed host module lifecycle transition.</summary>
public sealed record HostModuleLifecycleResult(
    HostModuleLifecycleOperation Operation,
    string SourceId,
    RegistrationStateResponse? Module)
{
    /// <summary>Gets whether the result matches one completed transition.</summary>
    public bool IsWellFormed =>
        Enum.IsDefined(Operation) &&
        HostOperationContractValidation.IsCanonicalIdentifier(SourceId) &&
        (Operation == HostModuleLifecycleOperation.Unload
            ? Module is null
            : Module is not null &&
              string.Equals(Module.SourceId, SourceId, StringComparison.Ordinal));
}

/// <summary>Requests one Tool invocation through the host Tool pipeline.</summary>
public sealed record HostToolInvokeAction(
    Guid InvocationId,
    Guid? ConversationId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments)
{
    /// <summary>Gets whether the request has canonical Tool target data.</summary>
    public bool IsWellFormed =>
        InvocationId != Guid.Empty &&
        ConversationId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(ToolCallId) &&
        ToolCallId == ToolCallId.Trim() &&
        ToolCallId.Length <= 256 &&
        HostOperationContractValidation.IsCanonicalIdentifier(ToolName) &&
        Arguments.ValueKind == JsonValueKind.Object;
}

/// <summary>Defines stable action identities for neutral host operations.</summary>
public static class HostOperationActionDescriptors
{
    private const ActionInterceptionCapabilities SafeCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.Observe;

    private static readonly ActionRepeatPolicy ReadRepeatPolicy =
        new(ActionRepeatKind.Idempotent, 1, TimeSpan.Zero, "host.module.list");

    private static readonly ActionRepeatPolicy MutationRepeatPolicy =
        new(ActionRepeatKind.None, 1, TimeSpan.Zero, "host.operation");

    /// <summary>Gets the stable module-list terminal identity.</summary>
    public static Guid ModuleListTerminalId { get; } =
        new("3e24fab8-cbb9-4f5c-9a7f-a9cae69f6a01");

    /// <summary>Gets the stable module-lifecycle terminal identity.</summary>
    public static Guid ModuleLifecycleTerminalId { get; } =
        new("3e24fab8-cbb9-4f5c-9a7f-a9cae69f6a02");

    /// <summary>Gets the stable host Tool terminal identity.</summary>
    public static Guid ToolInvokeTerminalId { get; } =
        new("3e24fab8-cbb9-4f5c-9a7f-a9cae69f6a03");

    /// <summary>Gets the neutral module-list descriptor.</summary>
    public static ActionDescriptor<HostModuleListAction, HostModuleListResult> ModuleList { get; } =
        Create<HostModuleListAction, HostModuleListResult>(
            "host.module.list",
            "host.module",
            containsSensitiveData: true,
            irreversible: false,
            ReadRepeatPolicy);

    /// <summary>Gets the neutral module-lifecycle descriptor.</summary>
    public static ActionDescriptor<HostModuleLifecycleAction, HostModuleLifecycleResult>
        ModuleLifecycle { get; } =
        Create<HostModuleLifecycleAction, HostModuleLifecycleResult>(
            "host.module.lifecycle",
            "host.module",
            containsSensitiveData: true,
            irreversible: true,
            MutationRepeatPolicy);

    /// <summary>Gets the neutral host Tool-invocation descriptor.</summary>
    public static ActionDescriptor<HostToolInvokeAction, ToolInvocationOutcome> ToolInvoke { get; } =
        Create<HostToolInvokeAction, ToolInvocationOutcome>(
            "host.tool.invoke",
            "host.tool",
            containsSensitiveData: true,
            irreversible: true,
            MutationRepeatPolicy);

    private static ActionDescriptor<TAction, TResult> Create<TAction, TResult>(
        string keyValue,
        string category,
        bool containsSensitiveData,
        bool irreversible,
        ActionRepeatPolicy repeatPolicy)
    {
        var key = new SharpClawActionKey(keyValue);
        return new ActionDescriptor<TAction, TResult>(
            key,
            1,
            category,
            SafeCapabilities,
            containsSensitiveData,
            irreversible,
            repeatPolicy,
            ContinuationPolicy: null,
            TimeSpan.FromMinutes(3))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(key, 1, typeof(TAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(key, 1, typeof(TResult)),
        };
    }
}

internal static class HostOperationContractValidation
{
    public static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() &&
        value.Length <= 128 &&
        value.All(character =>
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character is '_' or '-' or '.');
}
