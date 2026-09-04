using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Identifies the module host that will use a compiled contribution graph.</summary>
public enum ModuleHostingMode
{
    /// <summary>The module runs in the host process.</summary>
    InProcess,

    /// <summary>The module runs through the sidecar protocol.</summary>
    OutOfProcess,
}

/// <summary>Controls validation for one module graph compilation.</summary>
public sealed class ModuleCompilationOptions
{
    /// <summary>Gets the target host mode.</summary>
    public ModuleHostingMode HostingMode { get; init; } = ModuleHostingMode.InProcess;

    /// <summary>Gets the action effects that the host can apply.</summary>
    public ActionInterceptionCapabilities SupportedActionCapabilities { get; init; } =
        (ActionInterceptionCapabilities)511;

    /// <summary>Gets the event effects that the host can apply.</summary>
    public EventInterceptionCapabilities SupportedEventCapabilities { get; init; } =
        (EventInterceptionCapabilities)31;

    /// <summary>Gets the sidecar protocol versions that the SDK supports.</summary>
    public ContractVersionRange ProtocolVersionRange { get; init; } = ContractVersionRange.Exact(1);

    /// <summary>Gets the configured sidecar payload limits.</summary>
    public SidecarPayloadLimits PayloadLimits { get; init; } = new();

    /// <summary>Gets immutable host action descriptors used for exact validation.</summary>
    public IReadOnlyList<SidecarHostActionDescriptor> HostActions { get; init; } = [];

    /// <summary>Gets immutable host event descriptors used for exact validation.</summary>
    public IReadOnlyList<SidecarHostEventDescriptor> HostEvents { get; init; } = [];

    /// <summary>Gets whether each hook must have a matching manifest request.</summary>
    public bool RequireManifestRequests { get; init; } = true;
}

/// <summary>Reports all errors from one candidate module graph.</summary>
public sealed class ModuleGraphCompilationException : Exception
{
    /// <summary>Initializes the exception from compiled graph errors.</summary>
    public ModuleGraphCompilationException(IReadOnlyList<GraphCompilationError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets the complete error set.</summary>
    public IReadOnlyList<GraphCompilationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<GraphCompilationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0
            ? "The module graph is invalid."
            : string.Join(Environment.NewLine, errors.Select(error =>
                $"{error.Code}: {error.Message}"));
    }
}

internal static class ModuleGraphErrorCodes
{
    public const string InvalidIdentity = "invalid_module_identity";
    public const string ManifestMismatch = "manifest_identity_mismatch";
    public const string DuplicateAction = "duplicate_action";
    public const string DuplicateEvent = "duplicate_event";
    public const string DuplicateTool = "duplicate_tool";
    public const string DuplicateHook = "duplicate_hook";
    public const string InvalidDescriptor = "invalid_descriptor";
    public const string InvalidHandler = "invalid_handler";
    public const string InvalidTarget = "invalid_target";
    public const string MissingManifestRequest = "missing_manifest_request";
    public const string ManifestEffectMismatch = "manifest_effect_mismatch";
    public const string UnsupportedEffect = "unsupported_effect";
    public const string UnsupportedTransport = "unsupported_transport";
    public const string InvalidOrdering = "invalid_hook_ordering";
    public const string InvalidContract = "invalid_contract";
    public const string InvalidStorage = "invalid_storage";
    public const string InvalidApplication = "invalid_application_contribution";
}
