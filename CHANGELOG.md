# Changelog

## 0.2.0

Copy and move enter a queue. One transfer runs at a time, and the panes stay usable for the next copy or move.
Delete, rename, and new folder wait until that queue is empty.
Windows and Debian/Ubuntu installers check for the .NET 10 x64 runtime and stop when it is missing. The runtime is still not bundled.

## 0.1.0

First public release for Windows x64 and Debian/Ubuntu x64.
The archives do not include .NET. Install the .NET 10 x64 runtime on the target computer.

### Included

- Two equal panes, a keyboard-first command set, and light and dark themes.
- Copy, move, rename, new folder, recycle, and permanent delete.
- Conflict prompts, progress, and cancellation.
- Read-only text view and an external editor.
- Windows Recycle Bin and Linux trash through GIO.

### Limits

- One file operation runs at a time.
- There is no installer and no automatic .NET setup.
- Permanent delete refuses the whole selection when the tree contains a symbolic link,
  a junction, or a Windows read-only file.
- Copy and a file-by-file move skip links and do not follow them.
  Moving a whole folder onto a new path on the same volume renames the folder.
- Plugins, archives, and network locations are not included.
