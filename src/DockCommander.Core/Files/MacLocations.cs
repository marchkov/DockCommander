using System.Runtime.InteropServices;

namespace DockCommander.Core.Files;

internal static class MacLocations
{
    public static IReadOnlyList<LocationEntry> List(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var volumes = new List<(string Path, bool SameAsRoot)>();
        long? rootDevice = TryDevice("/");
        if (!Directory.Exists("/Volumes")) return Build(home, volumes);
        string[] entries;
        try { entries = Directory.EnumerateDirectories("/Volumes").ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entries = []; }
        foreach (var path in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(path)) continue;
                var device = TryDevice(path);
                volumes.Add((path, rootDevice is not null && device == rootDevice));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Build(home, volumes);
    }

    internal static IReadOnlyList<LocationEntry> Build(string? home, IReadOnlyList<(string Path, bool SameAsRoot)> volumes)
    {
        var list = new List<LocationEntry>();
        if (!string.IsNullOrWhiteSpace(home)) list.Add(new(home, "Home", home, "Home"));
        list.Add(new("/", "Root", "/", "Root"));
        foreach (var (path, sameAsRoot) in volumes)
        {
            if (sameAsRoot || string.IsNullOrWhiteSpace(path)) continue;
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            if (trimmed is "" or "/" or "\\") continue;
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..") continue;
            if (list.Any(item => string.Equals(item.RootPath, trimmed, StringComparison.Ordinal))) continue;
            list.Add(new(trimmed, name, trimmed, "Volume"));
        }
        return list;
    }

    // The first field of Darwin's 64-bit stat is the device id. The buffer is larger than the
    // published struct so a longer system struct cannot write past it. A failed call just keeps the volume.
    static long? TryDevice(string path)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var buffer = new byte[512];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (Stat(path, handle.AddrOfPinnedObject()) != 0) return null;
                return BitConverter.ToInt32(buffer, 0);
            }
            finally { handle.Free(); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    static extern int Stat(string path, IntPtr buffer);
}
