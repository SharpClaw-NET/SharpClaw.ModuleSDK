internal static class OutOfProcessPathGuard
{
    public static string EnsureContainedIn(string combined, string parentDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(combined);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDir);

        if (combined.Contains('\0') || parentDir.Contains('\0'))
            throw new InvalidOperationException("Path contains null bytes.");

        var canonical = Path.GetFullPath(combined);
        var canonicalParent = Path.GetFullPath(parentDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalParentWithSep = canonicalParent + Path.DirectorySeparatorChar;

        if (!canonical.Equals(canonicalParent, StringComparison.OrdinalIgnoreCase)
            && !canonical.StartsWith(canonicalParentWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{combined}' escapes the allowed directory '{parentDir}'.");
        }

        return canonical;
    }
}
