using System.Diagnostics;

namespace DockCommander.App.Platform;

internal interface IExternalFileLauncher
{
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
    Task EditAsync(string path, string? editorExecutable, CancellationToken cancellationToken = default);
}

internal sealed class ExternalFileLauncher : IExternalFileLauncher
{
    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        void Open()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(CreateOpenInfo(path));
        }
        if (!OperatingSystem.IsWindows()) return Task.Run(Open, cancellationToken);
        // Shell associations can invoke COM handlers requiring an STA thread.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Open(); completion.SetResult(); }
            catch (OperationCanceledException) { completion.SetCanceled(cancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "DockCommander open file" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public Task EditAsync(string path, string? editorExecutable, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(CreateEditInfo(path, editorExecutable));
    }, cancellationToken);

    internal static ProcessStartInfo CreateOpenInfo(string path)
    {
        var full = RequireFile(path);
        return new(full) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full)! };
    }

    internal static ProcessStartInfo CreateEditInfo(string path, string? editorExecutable)
    {
        var full = RequireFile(path);
        var editor = editorExecutable?.Trim();
        var macDefault = false;
        if (string.IsNullOrEmpty(editor))
        {
            if (OperatingSystem.IsWindows()) editor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            else if (OperatingSystem.IsMacOS()) { editor = "/usr/bin/open"; macDefault = true; }
            else throw new NotSupportedException("Choose an external editor with Shift+F4 first.");
        }
        // No shell command or concatenated arguments: a file path is exactly one argument.
        var start = new ProcessStartInfo(editor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(full)! };
        if (macDefault) start.ArgumentList.Add("-e");
        start.ArgumentList.Add(full);
        return start;
    }

    private static string RequireFile(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A full local file path is required.");
        var full = Path.GetFullPath(path);
        if (File.GetAttributes(full).HasFlag(FileAttributes.Directory)) throw new IOException("Select a regular file.");
        return full;
    }
}
