namespace DockCommander.Core.Files;

// macOS ships /tmp, /var and /etc as symbolic links to /private/tmp, /private/var and /private/etc.
// Those three aliases are real system paths. Every other symbolic link stays rejected.
internal static class MacSystemPaths
{
    static readonly (string Alias, string Canonical)[] Aliases =
    [
        ("/tmp", "/private/tmp"),
        ("/var", "/private/var"),
        ("/etc", "/private/etc")
    ];

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Verified = new(StringComparer.Ordinal);

    // fullPath must already be absolute and have ".." removed. A custom reader is for tests.
    public static string Canonicalize(string fullPath, Func<string, string?>? linkTarget = null)
    {
        if (linkTarget is null && !OperatingSystem.IsMacOS()) return fullPath;
        var path = Path.TrimEndingDirectorySeparator(fullPath.Replace('\\', '/'));
        if (path.Length == 0) return fullPath;
        foreach (var (alias, canonical) in Aliases)
        {
            if (!HasAliasPrefix(path, alias)) continue;
            if (!PointsAt(alias, canonical, linkTarget)) return fullPath;
            var suffix = path.Length == alias.Length ? "" : path[alias.Length..];
            return canonical + suffix;
        }
        return fullPath;
    }

    internal static bool HasAliasPrefix(string path, string alias) =>
        path.Equals(alias, StringComparison.Ordinal) || path.StartsWith(alias + "/", StringComparison.Ordinal);

    static bool PointsAt(string alias, string canonical, Func<string, string?>? linkTarget)
    {
        if (linkTarget is not null)
            return string.Equals(NormalizeTarget(linkTarget(alias)), canonical, StringComparison.Ordinal);
        return Verified.GetOrAdd(alias, static key =>
            string.Equals(NormalizeTarget(NativeLinkTarget(key)), Expected(key), StringComparison.Ordinal));
    }

    static string Expected(string alias) => alias switch
    {
        "/tmp" => "/private/tmp",
        "/var" => "/private/var",
        "/etc" => "/private/etc",
        _ => ""
    };

    static string? NormalizeTarget(string? target) =>
        string.IsNullOrWhiteSpace(target) ? null : Path.TrimEndingDirectorySeparator(target.Replace('\\', '/'));

    // The immediate link text only. A second symlink is not followed.
    static string? NativeLinkTarget(string alias)
    {
        try
        {
            var link = File.ResolveLinkTarget(alias, returnFinalTarget: false);
            if (link is null) return null;
            var stored = link.LinkTarget;
            if (string.IsNullOrWhiteSpace(stored)) stored = link.FullName;
            if (string.IsNullOrWhiteSpace(stored)) return null;
            if (!Path.IsPathRooted(stored))
            {
                var parent = Path.GetDirectoryName(alias);
                if (string.IsNullOrEmpty(parent)) parent = "/";
                stored = Path.GetFullPath(Path.Combine(parent, stored));
            }
            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
