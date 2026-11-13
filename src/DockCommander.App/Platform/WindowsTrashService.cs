using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DockCommander.Core.Files;

namespace DockCommander.App.Platform;

// Windows implementation is selected only at the composition root.
// Reference: https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
[SupportedOSPlatform("windows")]
public sealed class WindowsTrashService : ITrashService
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return Task.FromException(new PlatformNotSupportedException("The Windows recycle bin is unavailable."));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Recycle(path, cancellationToken);
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(cancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "DockCommander recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Recycle(string path, CancellationToken cancellationToken)
    {
        LocalFileSystemProvider.ValidateMutationPath(path);
        var classId = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        var interfaceId = typeof(IFileOperation).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out var operation));
        IntPtr item = IntPtr.Zero;
        try
        {
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref itemId, out item));
            // Silent, no confirmation/error UI, recycle, early failure, record undo.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x4 | 0x10 | 0x400 | 0x80000 | 0x100000 | 0x20000000));
            var sink = new RecycleProgressSink(cancellationToken);
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
            var result = operation.PerformOperations();
            var abortedResult = operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            if (sink.RefusedPermanentDelete)
                throw new IOException("Windows cannot recycle this item. Nothing was permanently deleted.");
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(abortedResult);
            if (sink.Error < 0) Marshal.ThrowExceptionForHR(sink.Error);
            if (aborted || !sink.Completed) throw new IOException("Windows did not complete the recycle operation.");
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            Marshal.FinalReleaseComObject(operation);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IFileOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(uint window);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IntPtr item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleProgressSink(CancellationToken cancellationToken) : IFileOperationProgressSink
    {
        public bool RefusedPermanentDelete { get; private set; }
        public bool Completed { get; private set; }
        public int Error { get; private set; }
        public int PreDeleteItem(uint flags, IntPtr item)
        {
            // Refuse a Shell fallback to permanent deletion (TSF_DELETE_RECYCLE_IF_POSSIBLE).
            if ((flags & 0x80) == 0) { RefusedPermanentDelete = true; return unchecked((int)0x80004005); }
            return cancellationToken.IsCancellationRequested ? unchecked((int)0x800704C7) : 0;
        }
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newItem)
        {
            if (result < 0) Error = result;
            else Completed = true;
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) { if (result < 0) Error = result; return 0; }
        public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr newItem) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr newItem) => 0;
        public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
        public int PostNewItem(uint flags, IntPtr destination, string name, string template, uint attributes, int result, IntPtr newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
