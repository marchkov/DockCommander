# DockCommander

Minimal, keyboard-first, dual-pane file manager inspired by Total Commander's workflow.
Windows is the first supported platform. C# / .NET 10 and Avalonia 12.1.2 keep the UI and
filesystem contracts portable. The Windows executable is **DC.exe**.

Theme previews with sample files: [Light](docs/images/light.png) · [Dark](docs/images/dark.png).

## Build and run

Install the .NET 10 SDK for development:

```powershell
dotnet restore DockCommander.slnx
dotnet build DockCommander.slnx
dotnet run --project src/DockCommander.App
dotnet test DockCommander.slnx
```

GitHub Actions runs that test suite on Windows and Ubuntu 24.04, and publishes the
framework-dependent `win-x64` and `linux-x64` archives as workflow artifacts.

For sandboxed builds that cannot write Avalonia's build telemetry log, set
`$env:AVALONIA_TELEMETRY_OPTOUT = '1'` in that shell before building.
See [Avalonia's build telemetry documentation](https://github.com/AvaloniaUI/Avalonia/wiki/Telemetry).

Create a Windows x64 release without bundling .NET:

```powershell
dotnet publish src/DockCommander.App -c Release -r win-x64 --self-contained false -o artifacts/win-x64
.\artifacts\win-x64\DC.exe
```

Keep the published files together. This is a folder distribution, not a standalone EXE.
The target computer needs the **.NET 10 runtime (x64)**. The installer, runtime detection,
and installation prompt are deferred to the packaging milestone. No runtime is bundled.

## Implemented

- Two equal panes with independent drive buttons and editable paths.
- Compact location buttons with full-path tooltips and an adaptive «Ещё ▾» overflow menu.
  Linux includes home/root shortcuts and hides system mounts; WSL drives use C:/D: labels.
- Light and dark themes with a compact toolbar switch; the choice is saved on close.
- Integrated title bar with custom minimize, maximize/restore and close buttons.
  Drag its empty area to move the window; double-click to maximize/restore; drag edges to resize.
- Vector folder/file/action icons, coordinated panel colors, and distinct active/inactive selection.
- Compact Name / Type / Size / Modified columns; directories before files.
- Restrained active-pane border, local timestamps, preserved cursor and marks on refresh.
- Local recursive copy and move through an operation service, outside the UI thread.
- Copy/move progress below the panes shows both the whole batch and the current file.
  A cancellable scan counts files and bytes before any changes. Overall progress includes
  skipped files as processed work and displays their count separately; it is not a count
  of bytes physically copied. Unknown sizes use file-count progress instead.
  Totals reflect the initial scan; files modified externally during transfer can differ.
  Cancel or Escape requests cancellation; completed files remain in place.
- Confirmation before copy, move, and deletion.
- Operation dialogs use custom frameless headers and follow the current theme.
  The confirmation button starts focused: Enter accepts, Escape or the close button cancels.
  Conflict dialogs default to Overwrite/Merge, or Skip when replacement is unavailable.
- Conflict choices: overwrite, skip, rename incoming entry, cancel.
  Existing folders can be merged; conflicting files inside are asked about separately.
  A file cannot overwrite a directory or vice versa.
- F7 directory-name prompt.
- F8 uses the Windows recycle bin or Linux GIO trash; Shift+F8 explicitly deletes permanently.
- Errors stay in the status bar; hover over the status text to read a long message.
- Paths, normal window size, maximized state, and active pane are saved on close.

## Keyboard and selection

| Key | Action |
| --- | --- |
| Tab / Shift+Tab | Switch pane |
| Arrows / Home / End / Page Up / Page Down | Move cursor |
| Enter / double-click | Open file with its system application, or enter directory |
| F3 | Read-only text viewer |
| F4 | Open current file in external editor |
| Shift+F4 | Choose editor executable; empty value restores platform default |
| Ctrl+L | Edit the active path; Enter opens it |
| Ctrl+Shift+T | Toggle light/dark theme |
| Ctrl+R | Refresh active pane and drive buttons |
| Insert | Toggle current entry's mark and advance |
| Num + / Num - | Mark / unmark files by mask |
| Num * | Invert file marks |
| Alt+F1 / Alt+F2 | Choose a location for the left / right pane |
| Ctrl+Space / Ctrl+click | Toggle a mark |
| Shift+Up / Shift+Down / Shift+click | Mark a range |
| F5 | Copy to the opposite pane |
| F6 | Move to the opposite pane |
| Shift+F6 | Rename current file or directory |
| F7 | Create directory |
| F8 | Move selected entries to the recycle bin, after confirmation |
| Shift+F8 | Delete permanently, after separate confirmation |
| Esc | Cancel a dialog or request cancellation of the running operation |

A dot marks selected entries independently of the cursor. Copy, move and delete use all marked entries;
if none are marked, they use the current row. The parent row is never operated on.
Marks survive pane switching and refresh when the entries still exist.
Open, view, edit and rename use the current row regardless of marks.
Numpad selection affects files only and preserves folder marks and the cursor.
Masks are case-insensitive: `*` matches any text, `?` one character, and `;` separates
patterns (for example `*.txt;*.md`). Both `*` and `*.*` include extensionless files.
These shortcuts do not intercept typing in the path field. In the location chooser,
use arrows and Enter; Escape keeps the current paths and active pane.

F3 previews up to 256 KiB of UTF-8 text, or UTF-16/UTF-32 text with a BOM.
Binary files and unsupported encodings report an error. The viewer supports copying,
line wrapping, both themes, and Escape to close; it never writes the file.
F4 defaults to Notepad on Windows. Linux requires an editor
configured with Shift+F4. Specify an executable path or command without arguments or
surrounding quotes. Use Ctrl+R after saving changes in the external editor.

## Mouse

- Click selects the current row; double-click opens a file or directory.
- Ctrl+click toggles a mark; Shift+click marks a range.
- Right-click opens the action menu for the clicked row. Right-clicking a marked row
  keeps the group; an unmarked row clears the old marks. Empty space keeps the current
  marks and cursor, and offers only folder creation and refresh.
- Drag a row to the other pane to copy; hold Shift to move. Dragging a marked row
  transfers the marked group; an unmarked row transfers only itself.
- Drop on a folder row (including `..`) to target that folder, or on other space in
  the file list to target the pane's current directory. The target is highlighted.
- Dropping still asks for confirmation and uses normal conflicts, progress and cancellation.
  Dragging within one pane and dragging out to other applications are not supported yet.
- Drop local files/folders from Explorer or a Linux file manager into a pane or folder
  to copy them. External drops always copy (even with Shift); originals are retained.
  Confirmation, conflicts, progress and cancellation are the same as F5. Duplicate
  paths and explicitly selected children of a selected folder are processed only once.
  Virtual/cloud-only items without a local path are not accepted.

## Settings

Settings use `Environment.SpecialFolder.ApplicationData/DockCommander/settings.json`.
On Windows this is normally `%APPDATA%\DockCommander\settings.json`.
Invalid settings fall back to defaults; unavailable saved paths fall back to the home directory.
The optional `Theme` setting is `Light` or `Dark`; existing settings without it use the light theme.
`EditorExecutable` stores the optional external editor, saved on close.
Dialogs follow the main window's theme. Theme colors and styles live in
`src/DockCommander.App/Styles.axaml`.

## Operation limits

- Recycling is supported on Windows and on Linux with `/usr/bin/gio` installed.
  Other platforms report recycling as unavailable; there is no permanent-delete fallback.
- Windows also refuses a recycle request when the Shell would delete permanently
  (for example, on an unsupported volume). Shift+F8 is always a separate explicit action.
- Copying/moving into the source itself is rejected.
- Symbolic links and junctions can be listed and navigated. Copy and a file-by-file move
  skip the link and continue with the rest of the tree; the link target is not followed.
  Moving a whole folder onto a path that does not exist yet renames the folder when the
  volume allows it, so links inside move with the folder. Permanent deletion still refuses
  the selection when a link or a Windows read-only file is present. A failure after removal
  has started names the entries already deleted.
- A failed or cancelled batch keeps already completed changes; there is no rollback.
  Skipped move entries remain at the source. Cancellation is checked between entries and
  during each buffered file copy. A native same-volume move of a file or of a whole folder
  cannot be interrupted mid-call. A folder rename is used only when the destination path
  does not exist yet; merges and cross-volume moves still walk file by file.
  Cancellation removes the unfinished staged copy and preserves the previous destination.
  Cross-drive moves commit the copy before deleting the source; a failed source deletion may leave both copies.
- Copies are staged beside the destination before replacing an existing file.
  A process crash may leave a clearly named `.dc-*.tmp` file.
- Only one operation runs at a time. Cross-provider transfers and a transfer queue are deferred.
- External opening and editing require a provider to expose an existing local file.

## Tests

The suite covers filesystem operations, conflicts, cancellation, deletion routing, selection,
navigation, keyboard dialogs, focus restoration, and settings persistence.
Tests use isolated temporary directories.

The optional Windows integration test creates one disposable file and sends it to the real
Windows recycle bin. It leaves that test file in the bin and does not empty it:

```powershell
$env:DC_TEST_RECYCLE = '1'
dotnet test DockCommander.slnx --filter Category=WindowsIntegration
Remove-Item Env:DC_TEST_RECYCLE
```

Run this test in a normal Windows session; a restricted sandbox may make recycling unavailable.

## Architecture and next milestone

- `DockCommander.Core`: provider/plugin contracts, local filesystem provider, operation service;
  no Avalonia dependency.
- `DockCommander.App`: panes, dialogs, settings, composition, Windows recycle-bin adapter.
- [Provider lifecycle and guarantees](docs/PLUGIN_API.md).

Supported systems are Windows and Debian/Ubuntu. Next: a transfer queue, then an installer
and .NET 10 runtime check for those two systems. Dynamic plugin loading and remote or archive
providers come later.

The active solution is `DockCommander.slnx` in the repository root.

Linux x64 builds and GIO trash support are available. See [Debian/Ubuntu setup](docs/LINUX.md).

## License

MIT. See [LICENSE](LICENSE).
