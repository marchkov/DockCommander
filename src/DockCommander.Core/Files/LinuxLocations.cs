namespace DockCommander.Core.Files;

internal static class LinuxLocations
{
    internal static bool IsUserMount(string path, string format)
    {
        path = path.TrimEnd('/');
        if (path.Length == 0) return false;
        if (format is "proc" or "sysfs" or "devtmpfs" or "devpts" or "tmpfs" or "cgroup" or "cgroup2" or
            "squashfs" or "overlay" or "debugfs" or "tracefs" or "securityfs" or "pstore" or "fusectl" or "mqueue") return false;
        foreach (var system in new[] { "/proc", "/sys", "/dev", "/boot", "/etc", "/usr", "/var", "/tmp", "/snap", "/init", "/mnt/wsl", "/mnt/wslg" })
            if (path == system || path.StartsWith(system + "/", StringComparison.Ordinal)) return false;
        if ((path == "/run" || path.StartsWith("/run/", StringComparison.Ordinal)) &&
            !path.StartsWith("/run/media/", StringComparison.Ordinal)) return false;
        return true;
    }

    internal static string ShortName(string path, string? volumeLabel)
    {
        path = path.TrimEnd('/');
        if (path.Length == 6 && path.StartsWith("/mnt/", StringComparison.Ordinal) && char.IsAsciiLetter(path[5]))
            return char.ToUpperInvariant(path[5]) + ":";
        if (!string.IsNullOrWhiteSpace(volumeLabel) && !volumeLabel.Contains('/')) return volumeLabel;
        return path[(path.LastIndexOf('/') + 1)..];
    }
}
