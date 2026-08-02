namespace SharpClaw.ModuleSDK;

/// <summary>Reports one local or remote sidecar protocol failure.</summary>
public class SidecarProtocolException : Exception
{
    /// <summary>Initializes a sidecar protocol failure with its stable code.</summary>
    public SidecarProtocolException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the stable protocol error code.</summary>
    public string Code { get; }
}
