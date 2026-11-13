# Filesystem provider API (MVP)

The API is an in-process development contract, not a versioned binary plugin ABI.
Core has no dependency on Avalonia. Generic panes and operations never inspect a provider's
concrete type or a URL scheme.

## Lifecycle

1. The application composition root creates a provider (or calls
   `IFileSystemPlugin.CreateProvider()` for a future registered plugin).
2. One long-lived provider instance can be shared by both panes.
3. Each pane requests its provider's `GetLocationsAsync()` and `ListAsync()`.
   A location supplies its display name and an opaque root path.
4. Navigation and operations use that provider's path and filesystem methods.
5. The provider lives for the application lifetime. There is no dynamic discovery/unload
   or disposal contract in milestone 1. Plugins needing sessions will require an explicit
   lifecycle extension before implementation.

The built-in local provider follows exactly the same interface. Platform recycling is injected
through `ITrashService`; the UI only checks the provider's `SupportsRecycle` capability.

## Paths and listings

- Paths are provider-owned strings. UI/services do not call local `Path.Combine`.
- `NormalizePath` and `PathComparer` determine navigation identity.
- `CombinePath(directory, name)` validates a single child name and cannot escape its parent.
- `IsSameOrDescendant(path, ancestor)` prevents self-copy and recursive destinations.
- `ListAsync` returns a snapshot, with the synthetic `..` entry first when a parent exists,
  then directories and files, sorted case-insensitively by name within each group.
- Entries contain a stable full path, directory/link flags, optional size, timestamp, and extension.
- `GetEntryAsync` returns null only when the entry is absent; access failures must throw.
- Synchronous path methods should be cheap. Async filesystem methods must avoid blocking
  the calling UI thread. The local provider uses worker threads for synchronous OS operations.

## Operations

`FileOperationService` serializes batches with a semaphore. UI confirms deletion and transfer
intent before invoking it; conflict decisions are requested through an async callback.
A callback may show a modal dialog. No file is silently overwritten.

- `CopyFileAsync` / `MoveFileAsync` handle regular files with an explicit overwrite flag.
- `TransferFileAsync` optionally reports cumulative bytes for the current file through
  `IProgress<long>`. Its default implementation delegates to the original copy/move methods,
  so existing providers remain compatible. Callbacks may run on a worker thread.
  `TransferAsync` exposes this as `TransferProgress` (path, bytes, optional file size).
  The local provider copies in cancellable chunks into a staged file, then commits it.
  Same-drive Windows moves use the native move operation and report at file boundaries.
- Optional `batchProgress` on `TransferAsync` enables a cancellable recursive preflight
  listing, cached for traversal, and reports `BatchTransferProgress`. Processed byte/file
  totals include skipped subtrees; `SkippedFiles` identifies them separately. Unknown
  sizes give a null byte total. `IsCompleted` is emitted only after successful batch completion.
- Directory recursion and per-entry conflicts live in the operation service.
- Overwrite between two folders means merge. Nested conflicts are still resolved individually.
- Conflict rename changes only the incoming entry's name within its destination parent.
- `RenameAsync(path, newName)` renames an existing entry within its current parent,
  validates a single child name and never overwrites another entry.
- A move removes a source directory only after all its children were transferred.
  Skipped entries remain in place. `TryMoveDirectoryAsync` may instead rename the whole
  directory when the destination does not exist. The default returns false and the service
  copies. Links are not walked: a recursive transfer skips the link, and a directory rename
  moves it unchanged.
- `ValidateDeleteAsync` runs for every selected entry before the first removal.
  The default accepts the path. The local provider rejects permanent deletion, before
  anything is removed, when the tree contains a symbolic link, junction, or Windows
  read-only file.
- `DeleteAsync` takes an explicit `Recycle` or `Permanent` mode.
  Providers must never turn an unsupported recycle request into permanent deletion.
  A failure after removal has started names the paths already deleted.
- `RemoveEmptyDirectoryAsync` must fail if a directory is nonempty.
- All mutations must enforce their own path protections as well as service-level checks.
- Cancellation/errors stop the batch; completed changes are retained and panes refresh.
  Providers should check cancellation at safe boundaries. Immediate interruption of one OS
  call and transactional rollback are not promised.

Only transfers within the **same provider instance** are supported in this milestone.
Future transfers between providers will need streaming or another generic capability contract.
Do not add provider-specific branches to the service.

## Optional file access

External file drops require `SupportsLocalFileDrop` (false by default) and
`ResolveDroppedFileAsync(localPath)`, which maps an existing local item to an entry
owned by that provider. The local provider validates absolute paths and rejects roots
and linked components. UI uses this capability without checking provider types.
External drops only copy; the source application is never asked to remove its data.

Default interface implementations keep providers without these capabilities usable:

- `OpenReadAsync` returns a readable stream owned and disposed by the caller. The
  text viewer reads a bounded prefix. The default throws `NotSupportedException`.
- `GetLocalFilePathAsync` returns an existing local regular-file path for external
  applications, or null when unavailable. It does not imply download/upload support.
- `RenameAsync` defaults to `NotSupportedException`.

Implementations must honor cancellation and avoid blocking the UI thread. The UI
uses these contracts without inspecting provider types.

## Current limitations

Windows recycling is an App-layer adapter around Shell `IFileOperation` on an STA worker.
Linux recycling uses the App-layer `LinuxTrashService` and `/usr/bin/gio trash` with
literal arguments and exit-code checking. It never invokes a shell or permanent-delete fallback.
It checks the Shell's pre-delete flags and rejects permanent-delete fallback.
See [operation flags](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
and [transfer flags](https://learn.microsoft.com/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags).

The local provider rejects mutations involving reparse points/symbolic links and filesystem roots.
It is not a security boundary against a separate process racing filesystem changes.
Other-platform trash adapters, authenticated sessions,
cross-provider streams, provider identity in saved settings, and ABI versioning are deferred.
