# DockCommander on macOS

This is an experimental Apple Silicon build of DockCommander 0.3.0-alpha.2. It is not a stable
release. Intel Macs are not supported. Windows and Linux remain the platforms exercised by the
release gate.

## Install

The disk image is `DockCommander-macos-arm64.dmg`. It contains `DockCommander.app` and a shortcut
to Applications. Drag the app to Applications, or run it from the image.

The app is self-contained. A separate .NET install is not required. The runtime bundled in the app
is .NET 10 for `osx-arm64`. The build is not trimmed and is not Native AOT.

The bundle identifier is `cz.marchkov.dockcommander`. The app is ad-hoc signed. It is not signed
with a Developer ID and it is not notarized.

Gatekeeper will warn on first launch. Do not turn Gatekeeper off, and do not remove the quarantine
flag for the whole disk. Use the exception macOS offers for this copy:

- On current macOS: System Settings → Privacy & Security → Open Anyway.
- Or right-click `DockCommander.app`, choose Open, then choose Open again.

## Files

Create, rename, copy, move and delete work on local disks. `/tmp`, `/var` and `/etc` are accepted
only when those symbolic links point at `/private/tmp`, `/private/var` and `/private/etc`. The check
reads the link target. It does not trust the prefix, and it does not follow any other symbolic link.
A link or junction anywhere else in the path is still refused. The check and the later filesystem
call are separate, so this does not close a race where a link changes in between.

File names follow the volume. `pathconf` (`_PC_CASE_SENSITIVE`) is read for the mount that contains
the path, so an external disk can differ from the boot volume. The bytes of a name are not rewritten.
A rename that changes only letter case is kept. If the volume cannot be identified, DockCommander
treats a name collision as case-insensitive and still asks the filesystem whether the destination
exists before replacing it.

F8 moves local files and directories to the Trash with `NSFileManager.trashItemAtURL`. It is not a
rename into `~/.Trash`, and a Trash failure is not turned into a permanent delete. Shift+F8 deletes
permanently after the usual confirmation. A remote connection has no Trash and still asks before a
permanent delete.

The location buttons are Home, the filesystem root, and volumes mounted under `/Volumes`. A volume
that is unavailable is skipped. The boot volume is not listed twice when its device id matches `/`.

## Connections

Saved passwords and keys are encrypted with a local key file, not the macOS Keychain. The key file
is mode `0600` and its directory is mode `0700`. If those permissions cannot be set, the password is
not saved. Windows builds still use DPAPI. A secret saved on one operating system does not open on
another.

## Keyboard

Commander shortcuts stay on Ctrl. Command is not used as a replacement. Ctrl+F and Alt+F7 open Find
Files. Space calculates the current folder size; Ctrl+Space marks a file. F3 views, F4 edits, and
Shift+F4 creates a file. On a laptop keyboard the function keys may need Fn or the “Use F1, F2, etc.
as standard function keys” setting. There is no Finder context menu.

Dragging between the two panes is the supported drag-and-drop path. Dragging between Finder and
DockCommander has not been signed off by an interactive test.

## Report a problem

Use non-critical files for the first copy, move, Trash and delete tests. Include the macOS version,
whether the disk is the internal volume or an external one, and what you expected. Issues belong on
the [GitHub repository](https://github.com/marchkov/DockCommander/issues).

## Tester checklist

- Install from the DMG and launch it with Open Anyway or right-click Open. Do not disable Gatekeeper.
- Browse Home and a mounted volume.
- Use both panes.
- Create, rename and delete disposable files.
- Copy and move files between the panes.
- Press F8 to Trash a disposable file, and Shift+F8 to delete one permanently.
- Open a text file with F3 and edit one with F4.
- Find a file by name.
- Select a folder and press Space to calculate its size.
- Connect to a remote server.
- Save the password, quit, reopen, and connect again without typing it.
- Try dragging a disposable file between Finder and DockCommander.
- Switch the light and dark themes.
- Note any freeze, crash, or result you did not expect.
