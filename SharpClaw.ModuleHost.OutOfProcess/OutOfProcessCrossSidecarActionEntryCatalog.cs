using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess;

/// <summary>Routes neutral cross-sidecar action requests to owned target entries.</summary>
public sealed class OutOfProcessCrossSidecarActionEntryCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal);

    /// <summary>Adds every action entry advertised by one authorized target module.</summary>
    public void Add(OutOfProcessModuleClient target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Application.ActionEntries.Count == 0)
        {
            throw new ArgumentException(
                "The target module does not advertise an action entry.",
                nameof(target));
        }

        lock (_sync)
        {
            foreach (var entry in target.Application.ActionEntries)
            {
                if (!string.Equals(entry.ModuleId, target.Discovery.ModuleId, StringComparison.Ordinal)
                    || !string.Equals(entry.ContractHash, target.Discovery.ContractHash, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The target action entry metadata does not match its discovery identity.",
                        nameof(target));
                }

                var key = Key(entry.Descriptor.Key, entry.Descriptor.Version);
                if (_targets.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"The cross-sidecar action entry '{key}' is already owned by another target.");
                }

                _targets.Add(key, new Target(target, entry));
            }
        }
    }

    internal bool TryResolve(
        SharpClawActionKey actionKey,
        int actionVersion,
        out Target target)
    {
        lock (_sync)
            return _targets.TryGetValue(Key(actionKey, actionVersion), out target!);
    }

    internal static string Key(SharpClawActionKey actionKey, int actionVersion) =>
        $"{actionKey.Value}|{actionVersion}";

    internal sealed record Target(
        OutOfProcessModuleClient Client,
        SidecarApplicationActionEntry Entry);
}
