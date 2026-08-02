namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Reports one rejected or disconnected sidecar exchange.</summary>
public sealed class OutOfProcessProtocolException : Exception
{
    /// <summary>Initializes one protocol exception.</summary>
    public OutOfProcessProtocolException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the stable protocol error code.</summary>
    public string Code { get; }
}
