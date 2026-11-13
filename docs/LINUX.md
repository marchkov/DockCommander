# DockCommander on Debian / Ubuntu (x64)

The Linux executable is `DC` (Windows uses `DC.exe`). Keep all published files together.
This is a framework-dependent folder distribution: .NET is not bundled.

## Requirements

- A graphical Linux desktop (X11 or an XWayland-capable session).
- .NET 10 runtime, x64. Follow the instructions for your distribution:
  [Ubuntu](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-install) /
  [Debian](https://learn.microsoft.com/dotnet/core/install/linux-debian).
- Avalonia desktop libraries, GIO for trash, and xdg-utils for opening files:

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libglib2.0-bin xdg-utils
```

See [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/deployment/linux).

## Run

```sh
tar -xzf DockCommander-linux-x64.tar.gz
cd linux-x64
./DC
```

If copying the folder from Windows instead of extracting the archive, first run
`chmod +x DC`. A locally installed .NET runtime may require `DOTNET_ROOT` to point
to its installation directory. `dotnet DC.dll` also works.

F4 needs an editor configured with Shift+F4: enter an installed GUI editor executable,
without arguments or surrounding quotes. Enter uses the desktop file association.
Alt+F1/F2 can be intercepted by the desktop environment; drive buttons remain available.

Each pane offers compact location shortcuts: «Домой», «Корень», and user-facing mounted
volumes. WSL `/mnt/c`, `/mnt/d`, etc. appear as `C:`, `D:`, etc. Other volumes use their
label or mount directory name. Hover to see the full path. System mounts are omitted.
Buttons that do not fit move into «Ещё ▾» and return when the window grows; no horizontal
scrollbar is needed. Alt+F1/F2 shows the same filtered locations with full paths.

## Trash

F8 invokes `/usr/bin/gio trash -- <absolute-path>` without a shell. GIO manages
freedesktop trash metadata and mounted-volume rules, so supported desktop file managers
can restore the entries. No permanent-delete fallback is used. Unsupported mounts or
missing GIO produce an error. Shift+F8 remains a separate confirmed permanent deletion.
Cancellation waits for the current GIO request to finish before stopping the batch.

The same restrictions on symbolic links and filesystem roots apply as on Windows.
See [GIO trash behavior](https://docs.gtk.org/gio/method.File.trash.html).

## Build and test

With the .NET 10 SDK:

```sh
dotnet publish src/DockCommander.App -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
dotnet test DockCommander.slnx
```

The optional real-trash test uses a separate data directory and leaves its disposable
entries there for inspection; it never empties your desktop trash:

```sh
DC_TEST_TRASH=1 XDG_DATA_HOME="$(mktemp -d)" dotnet test DockCommander.slnx
```

## Validation

Verified on Ubuntu 24.04 x64 under WSL: automated filesystem and headless UI tests,
including GIO trash and recovery metadata.
The graphical application also passed a five-second startup smoke check under WSLg.
Debian and a native desktop session still
need manual validation of window decoration, hotkey conflicts, and native drag/drop.
No `.deb` installer or automatic .NET installation is included yet.
