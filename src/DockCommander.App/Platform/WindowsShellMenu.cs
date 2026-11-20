using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DockCommander.App.Platform;

// Hosts the classic Explorer menu, including IContextMenu2/3 extension submenus.
// https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-icontextmenu
[SupportedOSPlatform("windows")]
internal static class WindowsShellMenu
{
    public static void Show(nint owner, string directory, IReadOnlyList<string> paths, int x, int y, bool dark)
    {
        if (owner == 0) throw new InvalidOperationException("A native window is required.");
        WindowsMenuTheme.Apply(owner, dark);
        WithMenu(directory, paths, (context, menu) =>
        {
            SubclassProc callback = (hwnd, message, wParam, lParam, _, _) =>
            {
                try
                {
                    if (message is 0x117 or 0x2b or 0x2c or 0x120)
                    {
                        if (context is IContextMenu3 third && third.HandleMenuMsg2(message, wParam, lParam, out var result) == 0)
                            return result;
                        if (message != 0x120 && context is IContextMenu2 second && second.HandleMenuMsg(message, wParam, lParam) == 0)
                            return 0;
                    }
                }
                catch { /* Never unwind a managed exception through the native window procedure. */ }
                return DefSubclassProc(hwnd, message, wParam, lParam);
            };
            if (!SetWindowSubclass(owner, callback, 0x44434d, 0)) throw new Win32Exception();
            try
            {
                var command = TrackPopupMenuEx(menu, 0x100 | 0x2, x, y, owner, 0); // Return command, right button.
                if (command == 0) return;
                var info = new InvokeInfo
                {
                    Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000 | 0x20000000 | 0x10000000,
                    Owner = owner, Verb = (nint)(command - 1), VerbW = (nint)(command - 1),
                    Directory = directory, DirectoryW = directory, Show = 1, Point = new() { X = x, Y = y }
                }; // Unicode, Shift, invocation point. Commands run synchronously unless the extension decides otherwise.
                Marshal.ThrowExceptionForHR(context.InvokeCommand(ref info));
            }
            finally
            {
                RemoveWindowSubclass(owner, callback, 0x44434d);
                GC.KeepAlive(callback);
            }
        });
    }

    internal static int CountItems(string directory, IReadOnlyList<string> paths)
    {
        var count = 0;
        WithMenu(directory, paths, (_, menu) => count = GetMenuItemCount(menu));
        return count;
    }

    private static void WithMenu(string directory, IReadOnlyList<string> paths, Action<IContextMenu, nint> use)
    {
        object? source = null;
        IContextMenu? context = null;
        var pidls = new List<nint>();
        nint menu = 0;
        try
        {
            var contextId = typeof(IContextMenu).GUID;
            if (paths.Count == 0)
            {
                var itemId = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(directory, 0, ref itemId, out var item));
                source = item;
                var handler = new Guid("3981E226-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFViewObject: folder background.
                Marshal.ThrowExceptionForHR(item.BindToHandler(0, ref handler, ref contextId, out context));
            }
            else
            {
                foreach (var path in paths)
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))))
                        throw new IOException("System menu entries must belong to the current folder.");
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out var pidl, 0, out _));
                    pidls.Add(pidl);
                }
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out var array));
                source = array;
                var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject.
                Marshal.ThrowExceptionForHR(array.BindToHandler(0, ref handler, ref contextId, out context));
            }
            menu = CreatePopupMenu();
            if (menu == 0) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7fff, 0x100)); // Extended verbs (Shift).
            use(context, menu);
        }
        finally
        {
            if (menu != 0) DestroyMenu(menu);
            if (context is not null) Marshal.ReleaseComObject(context);
            if (source is not null) Marshal.ReleaseComObject(source);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bind, ref Guid handler, ref Guid iid, out IContextMenu result);
    }
    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bind, ref Guid handler, ref Guid iid, out IContextMenu result);
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
    }
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, nuint wParam, nint lParam);
    }
    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, nuint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nuint wParam, nint lParam, out nint result);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public int Size;
        public uint Mask;
        public nint Owner, Verb;
        [MarshalAs(UnmanagedType.LPStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
        [MarshalAs(UnmanagedType.LPStr)] public string? Title;
        public nint VerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TitleW;
        public NativePoint Point;
    }
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint bind, ref Guid iid, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, nint bind, out nint pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint count, [In] nint[] pidls, out IShellItemArray array);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
