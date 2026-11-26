# Changelog

## Unreleased

- An open remote session is shared. Switching the other pane to Remote attaches that connection, so both panes can browse it. A remembered password or private key is encrypted for this user account. A failed connection asks for those sign-in details again. The server key or certificate is confirmed once per connection and asked again only if it changes.
- A connected pane keeps its session when switched to Local and back to Remote. A plus button beside the connection opens the saved connections without disconnecting.
- FTP and FTPS open in the pane that requested them. FTPS uses explicit TLS, or implicit TLS on port 990. Anonymous FTP does not ask for one. WebDAV is still not connected.
- SFTP opens in the pane that requested it. Copy and move use the existing queue.
- Copy and move can run between two different file-system providers through the same queue.
  The pane can replace its provider without a separate window.
- Each pane can switch between Local and Remote. Remote lists saved connections and does not open a network session yet.
- Drag local files and folders from DockCommander into Explorer and other file managers.
  A move completed by the other application refreshes the source pane.

## 0.2.1

- The Windows uninstaller removes only files recorded by the installer. A folder that merely contains `DC.exe` is left alone.
- Installer self-tests cover update, removal, a missing .NET 10 runtime, and a runtime of the wrong architecture.
- Linux installation adds an application-menu entry. The required framework is Microsoft.NETCore.App 10 x64. There is no Windows GUI installer.
- Match the Windows Shell popup menu to DockCommander's light/dark theme on supported
  Windows versions; preserve Windows high-contrast mode and native fallback.
- Windows: Shift + right-click opens the classic Shell menu for files, marked selections,
  or the folder background. Keep Linux behavior unchanged; block the Shell menu during queued transfers.
- Require Windows and Ubuntu tests before packaging or releasing the same commit, including tag and manual builds.
- Cancel transfers while waiting for conflict resolution, including the rename prompt.
- Synchronize queue clearing and disposal with new work; release cancellation resources.
- Cover job/refresh failures, cancellation, shutdown, conflict dialogs, and window close protection.
- Open saved folders without waiting for slow or disconnected drives. Discover locations
  once in the background for both panes and cancel discovery when the window closes.
- Isolate UI tests from host drives; cover delayed discovery and window shutdown.

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
