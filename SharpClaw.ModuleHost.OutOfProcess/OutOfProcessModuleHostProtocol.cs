namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Defines the authenticated control endpoint for one .NET module sidecar.</summary>
public static class OutOfProcessModuleHostProtocol
{
    /// <summary>Gets the supported protocol version.</summary>
    public const int Version = 1;

    /// <summary>Gets the module directory environment-variable name.</summary>
    public const string ModuleDirectoryEnvironmentVariable = "SHARPCLAW_MODULE_DIRECTORY";

    /// <summary>Gets the control address environment-variable name.</summary>
    public const string ControlAddressEnvironmentVariable = "SHARPCLAW_MODULE_CONTROL_ADDRESS";

    /// <summary>Gets the control token environment-variable name.</summary>
    public const string ControlTokenEnvironmentVariable = "SHARPCLAW_MODULE_CONTROL_TOKEN";

    /// <summary>Gets the HTTP header that carries the control token.</summary>
    public const string TokenHeaderName = "X-SharpClaw-Module-Token";

    /// <summary>Gets the sidecar discovery route.</summary>
    public const string DiscoveryPath = "/.sharpclaw/module/v1/discovery";

    /// <summary>Gets the host authorization route.</summary>
    public const string AuthorizationPath = "/.sharpclaw/module/v1/authorization";

    /// <summary>Gets the duplex exchange route.</summary>
    public const string ExchangePath = "/.sharpclaw/module/v1/exchange";

    /// <summary>Gets the authenticated host-capability route.</summary>
    public const string CapabilityPath = "/.sharpclaw/module/v1/capabilities";

    /// <summary>Gets the bounded readiness route.</summary>
    public const string ReadinessPath = "/.sharpclaw/module/v1/readiness";

    /// <summary>Gets the application contribution discovery route.</summary>
    public const string ApplicationPath = "/.sharpclaw/module/v1/application";

    /// <summary>Gets the module CLI invocation route.</summary>
    public const string ApplicationCliPath = "/.sharpclaw/module/v1/application/cli";

    /// <summary>Gets the module endpoint invocation route.</summary>
    public const string ApplicationEndpointPath = "/.sharpclaw/module/v1/application/endpoint";
}
