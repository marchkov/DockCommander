using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DockCommander.App.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsMenuTheme
{
    // Windows has no public API for classic popup-menu dark mode. These optional
    // exports are also used by Microsoft's PowerToys (ZoomIt/Utility.cpp).
    // Ordinal 135 had a different signature before Windows 10 1903.
    public static void Apply(nint owner, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(0x42, contrast.Size, ref contrast, 0)) return;
        var highContrast = (contrast.Flags & 1) != 0;
        try
        {
            SetPreferredAppMode(highContrast ? 0 : dark ? 2 : 3); // Default / ForceDark / ForceLight, process-local.
            AllowDarkModeForWindow(owner, dark && !highContrast);
            FlushMenuThemes(); // Rebuild cached menus when the app theme changes.
        }
        catch (EntryPointNotFoundException) { /* Unsupported Windows: retain the native menu appearance. */ }
        catch (DllNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("uxtheme.dll", EntryPoint = "#135", ExactSpelling = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("uxtheme.dll", EntryPoint = "#133", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AllowDarkModeForWindow(nint window, [MarshalAs(UnmanagedType.U1)] bool allow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("uxtheme.dll", EntryPoint = "#136", ExactSpelling = true)]
    private static extern void FlushMenuThemes();

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrast value, uint flags);
}
