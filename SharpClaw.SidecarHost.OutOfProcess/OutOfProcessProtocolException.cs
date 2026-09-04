using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess;

/// <summary>Reports one rejected or disconnected sidecar exchange.</summary>
public sealed class OutOfProcessProtocolException : SidecarProtocolException
{
    /// <summary>Initializes one protocol exception.</summary>
    public OutOfProcessProtocolException(string code, string message)
        : base(code, message)
    {
    }
}
