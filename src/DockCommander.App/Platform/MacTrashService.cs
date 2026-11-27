using System.Runtime.InteropServices;
using DockCommander.Core.Files;

namespace DockCommander.App.Platform;

// NSFileManager.trashItemAtURL:resultingItemURL:error: moves an item to the volume's Trash.
// A failure throws. It never deletes the item permanently, and it does not move the item itself into ~/.Trash.
public sealed class MacTrashService : ITrashService
{
    public bool IsSupported => OperatingSystem.IsMacOS() && Native.TryLoad();

    public Task RecycleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new NotSupportedException("The recycle bin is unavailable on this platform. Nothing was deleted.");
        var fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            LocalFileSystemProvider.ValidateMutationPath(fullPath);
            cancellationToken.ThrowIfCancellationRequested();
            Native.Trash(fullPath);
        }, CancellationToken.None);
    }

    internal static class Native
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";
        const int RtldNow = 2;
        static readonly object Gate = new();
        static bool _ready;

        public static bool TryLoad()
        {
            if (!OperatingSystem.IsMacOS()) return false;
            lock (Gate)
            {
                if (_ready) return true;
                try
                {
                    if (dlopen("/System/Library/Frameworks/Foundation.framework/Foundation", RtldNow) == IntPtr.Zero) return false;
                    _ready = objc_getClass("NSFileManager") != IntPtr.Zero && objc_getClass("NSURL") != IntPtr.Zero;
                    return _ready;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        public static string Trash(string fullPath)
        {
            if (!TryLoad())
                throw new NotSupportedException("The recycle bin is unavailable on this platform. Nothing was deleted.");
            var pool = Send(objc_getClass("NSAutoreleasePool"), Sel("new"));
            try
            {
                var text = Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), fullPath);
                if (text == IntPtr.Zero) throw new IOException($"Could not move item to Trash: {fullPath}");
                var url = Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), text);
                if (url == IntPtr.Zero) throw new IOException($"Could not move item to Trash: {fullPath}");
                var manager = Send(objc_getClass("NSFileManager"), Sel("defaultManager"));
                var resulting = IntPtr.Zero;
                var error = IntPtr.Zero;
                var ok = SendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, ref resulting, ref error);
                if (ok == 0)
                    throw new IOException("Could not move item to Trash: " + Describe(error));
                var location = PathOf(resulting);
                return string.IsNullOrWhiteSpace(location) ? fullPath : location;
            }
            finally
            {
                if (pool != IntPtr.Zero) Send(pool, Sel("drain"));
            }
        }

        static string Describe(IntPtr error)
        {
            if (error == IntPtr.Zero) return "Trash refused the item. Nothing was deleted.";
            var description = Send(error, Sel("localizedDescription"));
            var text = Utf8(description);
            return string.IsNullOrWhiteSpace(text) ? "Trash refused the item. Nothing was deleted." : text;
        }

        static string? PathOf(IntPtr url)
        {
            if (url == IntPtr.Zero) return null;
            return Utf8(Send(url, Sel("path")));
        }

        static string? Utf8(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return null;
            var bytes = Send(nsString, Sel("UTF8String"));
            return bytes == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(bytes);
        }

        static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")]
        static extern IntPtr dlopen(string path, int mode);

        [DllImport(ObjC)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, ref IntPtr resulting, ref IntPtr error);
    }
}
