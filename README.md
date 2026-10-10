# SpaceLens

A fast, local disk-space analyzer and uninstall assistant for Windows and macOS.
C# / .NET 10, with WinUI 3 (Windows App SDK) on Windows and Avalonia on macOS.
No Electron, no webview, no background service, no network access, no telemetry.

## Projects

| Project | Purpose |
|---|---|
| `SpaceLens.Core` | Platform-neutral engine: compact scan tree, parallel scanner, breakdowns, search, safety policy, snapshot format, treemap layout. |
| `SpaceLens.Windows` | Windows specifics: native enumerators (`NtQueryDirectoryFile`, `FindFirstFileEx`), drives, Recycle Bin, shell actions, installed-app sources (registry, MSI, MSIX). |
| `SpaceLens.Detectors` | `IStorageDetector` implementations: developer caches, games (Steam, Xbox, Epic, Battle.net, GOG), Windows-managed locations, and so on. |
| `SpaceLens.App` | WinUI 3 application (Windows). |
| `SpaceLens.Mac` | macOS specifics: `lstat`-based enumerator, volumes, Trash (`NSFileManager`), Finder actions, Full Disk Access check, applications and their Library leftovers, Mac storage detectors, property lists. |
| `SpaceLens.Avalonia` | Avalonia application (macOS). |
| `SpaceLens.Tests` | xUnit tests for the engine, safety policies, detectors, search and serialization. |
| `SpaceLens.Mac.Tests`, `SpaceLens.Avalonia.Tests` | Tests for the macOS layer, and headless UI tests that drive the Mac app against a fake home folder. |
| `SpaceLens.Benchmarks` | Scan harness and BenchmarkDotNet micro benchmarks. |

## Build, run, publish

Requires the .NET 10 SDK on Windows 10 19041 or later.

```powershell
dotnet build SpaceLens.slnx -c Release
dotnet test SpaceLens.Tests -c Release
dotnet run --project SpaceLens.App -c Release

# Self-contained, ReadyToRun build in .\publish\SpaceLens
dotnet publish SpaceLens.App -c Release -o publish\SpaceLens
```

`SpaceLens.exe <folder>` starts a scan of that folder right away. Settings can
add "Analyze with SpaceLens" to File Explorer's right-click menu for folders
and drives.

For files in OneDrive (or another provider using the Windows Cloud Files API),
the context menu offers "Free up space (online-only)": like the File Explorer
command of the same name, the files stay in the cloud and only the local copy
is removed by the provider. Nothing is deleted.

"Add to cleanup basket" in the context menu collects files and folders from any
page; the Cleanup basket page shows them together with their total and removes
them through the normal Recycle Bin confirmation.

Right-click a folder and choose "Rescan this folder" to update just that folder
in the results instead of rescanning the whole drive.

The map on the Overview can be coloured by location, by file type (the type
holding the most space in each folder) or by age (the newest change inside).

Export (next to Rescan) saves the folders or the large files as CSV, or a full
report as JSON. Sizes are in bytes and times are UTC (ISO 8601).

Keyboard: Ctrl+R / F5 rescan, Ctrl+F search, Ctrl+L scan a folder, Enter opens,
Delete shows the Recycle Bin confirmation (it never deletes without one), and
Shift+F10 or the context-menu key shows the actions for the selected item.

Search accepts plain text (`steam`), wildcards (`*.vmdk`), extensions (`.iso`),
folder names (`node_modules`, `.git`), size filters (`>5GB`) and modification
dates for files (`older:1y`, `newer:30d`, `older:2024-01-01`; units d, w, m, y).
`path:steamapps` (or `path:"Program Files"`) matches the full path, `-term`
excludes anything matching a term (`-node_modules`, `-type:video`), and `OR`
separates alternatives (`.iso >4GB OR .vhdx`). Searches can be saved from the
Saved searches menu on the results page.
Large Files can also be limited to files not modified in 6 months to 5 years.

## macOS

The Mac app runs on the same engine and has Overview (with the map), Folders,
Large Files, Search and Apps pages. It needs macOS 14 or later, on Apple
silicon or Intel. The Windows-only pages (Changes, Duplicates, Cleanup basket,
Settings and export) are not in it yet.

Build it on a Mac (or any OS with the .NET 10 SDK):

```bash
dotnet run --project SpaceLens.Avalonia -c Release        # run from source
scripts/publish-macos.sh arm64                             # or x64 for Intel
# -> publish/macos-arm64/SpaceLens.app (self-contained, about 115 MB)
```

On a Mac the script signs the bundle ad hoc. A bundle built on another OS must
be signed on a Mac before it runs (`codesign --force --deep --sign - SpaceLens.app`).
The app is not notarized, so open it the first time with right-click > Open.

How it differs from Windows:

- **Full Disk Access.** macOS hides Mail, Messages, Safari and other app data
  until you allow it in System Settings > Privacy & Security > Full Disk
  Access. Without it those folders are left out of the totals, and the app
  shows a banner with a button that opens that settings page.
- **Sizes in decimal units**, like Finder (1 GB = 1,000,000,000 bytes), so the
  numbers match what Finder and the Storage settings show.
- **Scanning "/"** skips `/System/Volumes` (the firmlinked Data volume,
  which would otherwise be counted twice) and other volumes under `/Volumes`.
- **Removal** always goes to the Trash, the same way Finder does it, so items
  can be put back. macOS itself, top-level folders like `/Library` and
  `/Applications`, the home folder's own folders, `~/Library` and Apple's apps
  are protected. Device backups, Photos libraries and Time Machine data point
  to the app that manages them instead of being removable. Items in iCloud
  Drive and shared `/Library` data come with a warning first.
- **Apps** lists the applications in `/Applications` and `~/Applications`
  with their size and what they keep in `~/Library`. Removing an app moves its
  bundle to the Trash. Its Library data is listed afterwards and removed only
  if you confirm separately.
- Files of 1 MB or more are counted by the space they use on disk (so sparse
  files are not overcounted). Smaller files are counted by
  their length.

The Mac code has been tested on Linux, where its native calls are checked at
runtime and its UI runs headless. It has not yet been run on a real Mac.

## Architecture

- The scanner sits behind `IDiskScanner`. `ParallelDirectoryScanner` runs a
  work-stealing pool of directory workers. Each worker uses an enumerator
  engine (native `NtQueryDirectoryFile` by default, or `FindFirstFileEx`, or
  managed `FileSystemEnumerator`).
- The scan result is an index-based `ScanTree` of `DirNode`/`FileRecord`
  structs stored in chunked arrays. Directory sizes roll up incrementally.
  Only files of 1 MB or more get their own record; smaller files are counted
  in their folder's totals.
- The UI never waits on the scanner. A 200 ms timer reads progress counters
  and the visible pages refresh about once per second during a scan.
  Lists are virtualized, and row view models are created only for rows on
  screen.
- After each scan the tree is saved as a Brotli-compressed snapshot in
  `%LOCALAPPDATA%\SpaceLens\Snapshots`, so the next start shows
  "Last scanned … [Rescan]" without rescanning. The snapshot also records the
  volume's USN journal position, ready for a future incremental scan.
- The snapshot a new scan replaces is kept as the previous scan. The Changes
  page compares the two (`ScanComparer`): folders are matched by name level by
  level, and the difference is explained by a short list of the folders that
  grew, shrank, appeared or disappeared, drilling down to the folder that
  actually changed. The previous scan is only loaded while comparing.
- Duplicates (`DuplicateFinder`) runs only when asked, because it reads file
  contents. Indexed files are grouped by size, hard links are collapsed (same
  volume and file ID), then the first and last 64 KB are hashed, and only
  files that still match are hashed completely (SHA-256). Online-only cloud
  files are never read, since that would download them, and C:\Windows is
  skipped. Copies are removed through the normal Recycle Bin confirmation.

## Safety rules

- C:\Windows, Program Files, Program Files (x86), ProgramData, system32,
  WinSxS, boot files, and EFI/recovery partitions are protected and can
  never be removed from SpaceLens. Other Windows-managed locations show
  "Managed by Windows" and point to the Windows tool that manages them.
- Other users' profiles, the Public folder and the Default profile are
  protected like your own profile; their contents can be reviewed individually.
- Every removal asks for confirmation, and Cancel is the default button.
  Removal goes to the Recycle Bin. Permanent deletion is a separate action,
  for files only, and requires ticking an acknowledgement box. On drives
  without a Recycle Bin (removable and network drives) the confirmation says
  that the removal is permanent and requires the same acknowledgement.
- Installed apps are removed only through their official uninstaller, which is
  interactive by default. A quiet uninstall is offered only when the app
  provides one, and it is never the default. Install folders are never
  deleted in place of uninstalling. Leftover folders are reported, never
  removed automatically.
- Game folders managed by a launcher, and other storage owned by a tool,
  point to that tool instead of offering direct deletion.
- No label says "safe to delete". Developer storage is explained before any
  removal.
- SpaceLens never "cleans" Windows and never changes the registry, with one
  opt-in exception: the "Analyze with SpaceLens" entry in File Explorer's
  right-click menu (Settings > File Explorer) adds keys under
  `HKEY_CURRENT_USER\Software\Classes`, for the current user only. Turning
  the setting off deletes them again.
- Administrator rights are never required.

## Privacy

Everything stays on this PC. There is no account, no cloud backend, no
analytics, and no network access. Filenames, paths, installed programs and
filesystem information never leave the machine. Unhandled errors are logged
only to `%LOCALAPPDATA%\SpaceLens\errors.log`. Saved scans can be deleted
from Settings, or turned off.

## Measured performance

All numbers below were measured on the development machine (AMD Ryzen 5 2600,
12 logical processors; C: on a SATA SSD; warm OS cache). Results on other hardware will differ.

**Synthetic tree of 1,000,000 files** (`SpaceLens.Benchmarks synthetic`, median of 3):

| Engine | Time | Files/s | Allocated | Peak working set |
|---|---|---|---|---|
| Native (`NtQueryDirectoryFile`) | 1.241 s | 805,640 | 23.2 MB | 69.3 MB |
| Managed | 1.307 s | | | |
| `FindFirstFileEx` | 1.480 s | | | |

**C:\Windows**, 219,738 files and 170,925 folders
(`SpaceLens.Benchmarks scan C:\Windows`, median of 3; full table in
`bench-windows.txt`):

| Engine | 1 worker | 4 workers | 12 workers | Allocated (12 workers) |
|---|---|---|---|---|
| Native | 24.0 s | 9.44 s | 5.44 s | 101 MB |
| `FindFirstFileEx` | 25.6 s | 9.88 s | 6.02 s | 142 MB |
| Managed | 24.3 s | 9.43 s | 5.82 s | 113 MB |

**Whole C: drive through the app** (Release build, 12 workers): 1,264,892 files
and 293k folders (381 GB) in 34.8 s. Peak working set was 342 MB, and the UI
refreshed 4.8 times per second throughout.

**Startup** (Release publish, 3 runs): window ready in 744–794 ms. Saved C:
results appear in about 1.5 s; reading the 25–28 MB snapshot accounts for
about 0.63 s of that.

**Memory**: idle without results, 148 MB working set. With C: results on the
Overview, 244 MB. Opening Installed Apps loads the Store (MSIX) package API,
which brings it to about 334 MB.

## Known limitations


- On macOS, APFS clones (copies that share their data until changed) are
  counted in full each time, so folder totals can be higher than the space
  actually used. Finder counts them the same way.
- Hard-linked files, which are common in WinSxS, are counted once per link,
  so totals for C:\Windows are higher than the space they actually use.
- Files under 1 MB are counted in folder totals but are not listed or
  searchable on their own.
- The Recycle Bin shell operation refuses paths of 260 characters or more.
- Loading Store (MSIX) apps adds about 80 MB, so it happens only when the
  Installed Apps page is opened, and it can be turned off in Settings.
- An NTFS MFT/USN "Turbo" scan is not implemented. It needs administrator
  rights, and the normal scan has not been shown to be too slow to justify
  it.
