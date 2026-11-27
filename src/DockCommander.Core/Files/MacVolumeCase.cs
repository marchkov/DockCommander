using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DockCommander.Core.Files;

// APFS can be case-sensitive or case-insensitive, and an external volume can differ from the boot volume.
// pathconf(_PC_CASE_SENSITIVE) is 11 on Darwin and is asked per mount. A failed query is not treated as
// proof: name collisions then use case-insensitive comparison, and copy, move and rename still ask the
// filesystem whether the destination exists. This is not a claim about races between the check and the call.
internal static class MacVolumeCase
{
    const int CaseSensitiveName = 11;
    static readonly ConcurrentDictionary<string, int> Cache = new(StringComparer.Ordinal);
    static readonly object MountGate = new();
    static string[] MountCache = [];

    public static StringComparer ComparerFor(string path) => FromResult(Sensitivity(path));

    internal static StringComparer FromResult(int? value) =>
        value == 1 ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // A case-sensitive volume on either side keeps names distinct. Unknown results do not.
    internal static StringComparison Comparison(int? left, int? right) =>
        left == 1 || right == 1 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    internal static bool IsReliable(int? value) => value is 0 or 1;

    public static int? Sensitivity(string path)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var full = path;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        var mount = SelectMount(full, Mounts(full));
        var code = Cache.GetOrAdd(mount, static root => Query(root));
        return code < 0 ? null : code;
    }

    internal static string SelectMount(string fullPath, IReadOnlyList<string> mounts)
    {
        var path = fullPath.Replace('\\', '/');
        var best = "/";
        var bestLength = 1;
        foreach (var mount in mounts)
        {
            var root = mount.Replace('\\', '/').TrimEnd('/');
            if (root.Length == 0) root = "/";
            if (root == "/") continue;
            var matches = path.Equals(root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);
            if (matches && root.Length > bestLength)
            {
                best = root;
                bestLength = root.Length;
            }
        }
        return best;
    }

    static IReadOnlyList<string> Mounts(string fullPath)
    {
        lock (MountGate)
        {
            if (MountCache.Length == 0 || NeedsMountRefresh(MountCache, fullPath))
                MountCache = LoadMounts();
            return MountCache;
        }
    }

    static bool NeedsMountRefresh(IReadOnlyList<string> mounts, string fullPath)
    {
        if (!fullPath.StartsWith("/Volumes/", StringComparison.Ordinal)) return false;
        return SelectMount(fullPath, mounts) == "/";
    }

    static string[] LoadMounts()
    {
        try
        {
            return DriveInfo.GetDrives().Where(drive =>
            {
                try { return drive.IsReady; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }).Select(drive => drive.RootDirectory.FullName).ToArray();
        }
        catch (IOException) { return ["/"]; }
        catch (UnauthorizedAccessException) { return ["/"]; }
    }

    static int Query(string mount)
    {
        try
        {
            var value = pathconf(mount, CaseSensitiveName);
            if (value < 0) return -1;
            return value == 0 ? 0 : 1;
        }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }

    [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
    static extern long pathconf(string path, int name);
}

internal sealed class MacPathComparer : StringComparer
{
    public static readonly MacPathComparer Instance = new();

    public override int Compare(string? x, string? y)
    {
        var left = Identity(x);
        var right = Identity(y);
        return string.Compare(left, right, Rule(x, y));
    }

    public override bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return string.Equals(Identity(x), Identity(y), Rule(x, y));
    }

    public override int GetHashCode(string obj) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Identity(obj));

    static string Identity(string? path) =>
        path is null ? "" : MacSystemPaths.Canonicalize(path);

    static StringComparison Rule(string? x, string? y) =>
        MacVolumeCase.Comparison(
            x is null ? null : MacVolumeCase.Sensitivity(x),
            y is null ? null : MacVolumeCase.Sensitivity(y));
}
