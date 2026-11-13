using System.Diagnostics;
using DockCommander.Core.Files;

namespace DockCommander.App.Platform;

// Delegate freedesktop trash layout, mount handling and metadata to the desktop's GIO implementation.
public sealed class LinuxTrashService : ITrashService
{
    public bool IsSupported => OperatingSystem.IsLinux() && File.Exists("/usr/bin/gio");

    public async Task RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new NotSupportedException("Linux trash requires /usr/bin/gio (libglib2.0-bin). Nothing was deleted.");
        var fullPath = Path.GetFullPath(path);
        await Task.Run(() => LocalFileSystemProvider.ValidateMutationPath(fullPath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(fullPath) };
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        // Finish this single trash request before observing cancellation again: killing it could
        // interrupt trash metadata creation. The service checks cancellation before the next item.
        await process.WaitForExitAsync();
        var message = await error;
        await output;
        if (process.ExitCode != 0)
            throw new IOException($"Could not move item to trash: {message.Trim()}");
    }

    internal static ProcessStartInfo CreateStartInfo(string fullPath)
    {
        var info = new ProcessStartInfo("/usr/bin/gio")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add("trash");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(fullPath);
        return info;
    }
}
