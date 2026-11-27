# Changelog

## 0.3.0-alpha.2

### What's new

DockCommander now includes experimental native macOS support for Apple Silicon.

- Native macOS Trash integration.
- Safe handling of standard macOS system path aliases.
- Volume-aware filesystem case sensitivity.
- Improved credential-storage security on macOS and Linux.
- macOS Home, Root and mounted volume locations.
- Self-contained macOS ARM64 DMG package.
- Automated regression testing on Windows, Linux and macOS.

### Supported platforms

- Windows x64
- Linux x64
- macOS ARM64 (Apple Silicon, experimental)

### Known limitations

- This is an Alpha release.
- Windows direct-download binaries are not digitally signed.
- The macOS application is ad-hoc signed but not notarized.
- macOS may require explicit approval through Privacy & Security.
- Finder drag-and-drop and some macOS-specific interactions still require real-world testing.
- Intel Mac is not supported by the current distribution.

For macOS installation and testing instructions, refer to [docs/MACOS.md](docs/MACOS.md).

### Changes

- Experimental Apple Silicon support. The macOS build is a self-contained `osx-arm64` app, ad-hoc signed and not notarized. It is not a stable release and does not support Intel Macs. See [docs/MACOS.md](docs/MACOS.md).
- `/tmp`, `/var` and `/etc` are accepted when those macOS aliases really point at `/private/tmp`, `/private/var` and `/private/etc`. Other symbolic links and junctions are still rejected. Windows and Linux path checks are unchanged.
- Saved connection secrets on macOS and Linux use an owner-only key file (`0600`) and directory (`0700`). The files are created with those permissions, existing files are tightened, and a save fails instead of leaving them readable by other accounts. Windows still uses DPAPI. There is no macOS Keychain integration.
- Local file names on macOS are compared with `pathconf` for the volume that contains the path. A case-sensitive volume and a case-insensitive volume are not forced into one rule. If the volume cannot be identified, collisions are treated as case-insensitive and the filesystem is still asked whether the destination exists.
- F8 on a local macOS pane uses `NSFileManager` Trash. It does not fall back to permanent deletion. Shift+F8 still deletes permanently after confirmation. Remote panes still delete permanently.
- The macOS location row lists Home, the filesystem root, and mounted volumes under `/Volumes`, without a second entry for the boot volume when its device id is available.

## 0.3.0-alpha.1

- Find files (Ctrl+F or Alt+F7) searches file and folder names from the current directory, including a remote connection. Space calculates the current folder size. These are the last file-manager features before the Alpha freeze.
- Reload in the editor rereads the file through its provider. A failed reload, or a reload while the text is unsaved, leaves the open text in place.
- Copy and move walk directories with an explicit stack, including very deep trees.
- A deletion that stops partway reports the removed names through a structured error instead of matching message text.
- WebDAV accepts percent-encoded names, including names that contain spaces, on Linux and Windows.
- WebDAV replaces an existing file with one MOVE. SFTP and FTP still delete the destination and then rename the temporary file, so a failed rename can remove the original. A failed upload does not.
- Double-click a saved connection to connect. The Edit and Connect buttons keep their own actions.
- A remote session can be disconnected without deleting its profile. The name stays visible as Disconnected and reconnects with the saved sign-in. Forget sign-in removes only the saved password or key. A connection the server closes becomes Disconnected.
- Shift+F4 creates a file in the current folder and opens it in the editor. The same command is on the right-click menu. F4 opens a file that is not text, or is too large to edit, with the system application when that file has a local path.
- F4 opens the built-in text editor. F3 still only views a file. Ctrl+Shift+F4 chooses the external editor, and the file menu opens the current file in it.
  The built-in editor saves through the file provider, including a remote connection, and keeps unsaved text when a save fails.
- WebDAV is one provider. A connection type of Nextcloud, ownCloud, Synology, QNAP, or Generic only fills in the server address. Nextcloud and ownCloud add the DAV path for the user, including when the server is installed in a subdirectory. ownCloud falls back to the classic `/remote.php/webdav/` path when the modern one is missing. Saved profiles without a type keep their host and initial path.
- F8 on a remote connection asks to delete permanently, because that connection has no recycle bin.
- Delete, rename, and new folder can run while a copy or move is still transferring.
- Drag between panes works from Local to Remote, Remote to Local, and between two remote connections. Files dropped from Explorer onto a remote pane are copied there.
- An open remote session stays available to both panes. Another connection opens beside it, newest on the left, and each pane can show a different one. Right-click a connection name to reconnect, edit, or close it. A remembered password or private key is encrypted for this user account. A failed connection asks for those sign-in details again. The server key or certificate is confirmed once per connection and asked again only if it changes.
- A connected pane keeps its session when switched to Local and back to Remote. A plus button beside the connection opens the saved connections without disconnecting.
- FTP and FTPS open in the pane that requested them. FTPS uses explicit TLS, or implicit TLS on port 990. Anonymous FTP does not ask for one.
- WebDAV opens in the pane that requested it. HTTPS is the default, and port 80 stays plain HTTP. Password authentication uses Basic, then Digest when the server asks for it. The certificate follows the same remembered trust as FTPS. A typed connection uses the server URL; a saved profile without a type still uses its host and initial path.
- SFTP opens in the pane that requested it. Copy and move use the existing queue.
- Copy and move can run between two different file-system providers through the same queue.
  The pane can replace its provider without a separate window.
- Each pane can switch between Local and Remote. Remote lists saved connections and opens supported protocols in that pane.
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
