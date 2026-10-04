# SpaceLens

A fast, local, native Windows disk-space analyzer and uninstall assistant.
C# / .NET 10 / WinUI 3 (Windows App SDK). No Electron, no webview, no background
service, no network access, no telemetry.

## Projects

| Project | Purpose |
|---|---|
| `SpaceLens.Core` | Platform-neutral engine: compact scan tree, parallel scanner, breakdowns, search, safety policy, snapshot format, treemap layout. |
| `SpaceLens.Windows` | Windows specifics: native enumerators (`NtQueryDirectoryFile`, `FindFirstFileEx`), drives, Recycle Bin, shell actions, installed-app sources (registry, MSI, MSIX). |
| `SpaceLens.Detectors` | `IStorageDetector` implementations: developer caches, games (Steam, Xbox, Epic, Battle.net, GOG), Windows-managed locations, and so on. |
| `SpaceLens.App` | WinUI 3 application. |
| `SpaceLens.Tests` | xUnit tests for the engine, safety policy, detectors, search and serialization. |
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

`SpaceLens.exe <folder>` starts a scan of that folder right away.

Keyboard: Ctrl+R / F5 rescan, Ctrl+F search, Ctrl+L scan a folder, Enter opens,
Delete shows the Recycle Bin confirmation (it never deletes without one), and
Shift+F10 or the context-menu key shows the actions for the selected item.

Search accepts plain text (`steam`), wildcards (`*.vmdk`), extensions (`.iso`),
folder names (`node_modules`, `.git`) and size filters (`>5GB`).

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
- SpaceLens never changes the registry and never "cleans" Windows.
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
