# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Tray icon** — background scheduled cleaning with tray notifications
- **Registry ETW tracing** — add `Microsoft.Diagnostics.Tracing.TraceEvent` for
  real-time registry change capture alongside USN journal filesystem tracking
- **MSI/MSP Installer cache orphan cleanup** — scan `%WINDIR%\Installer` for
  orphaned MSI/MSP patch files not referenced by any installed product. Can
  recover multi-GB. See InstallerClean (github.com/no-faff/InstallerClean) for
  the approach: query the Windows Installer database for active products, flag
  everything else as reclaimable.

## Research-Driven Additions

### P0 — Safety

- [ ] P0 — Validate CLI `--export` paths against traversal
  Why: `File.WriteAllText(exportPath, ...)` and `GridExporter.Export*()` accept raw user paths without validation — `--export ..\..\windows\system32\evil.txt` writes outside intended directory
  Evidence: `src/DeepPurge.Cli/Program.cs` lines 250, 278, 298, 337, 489
  Touches: `Program.cs` — add `Path.GetFullPath()` normalization + reject paths containing `..` or rooted outside current directory / user profile
  Acceptance: `deeppurgecli drivers --export ..\..\test.csv` returns exit code 2 with error message; unit test confirms
  Complexity: S

### P1 — Reliability

- [ ] P1 — Lock `ActivityLog.LoadRecent()` reads
  Why: `File.ReadAllLines()` without `_lock` can race with concurrent `Record()` or `Prune()` causing partial reads or IOException
  Evidence: `src/DeepPurge.Core/Diagnostics/ActivityLog.cs:40` — `Record()` and `Prune()` hold `_lock` but `LoadRecent()` does not
  Touches: `ActivityLog.cs` — wrap `ReadAllLines` in `lock (_lock)` block
  Acceptance: Concurrent Record + LoadRecent calls in a test never throw or return truncated data
  Complexity: S

- [ ] P1 — Add timeout to initial scan
  Why: `RunInitialScanAsync()` has no timeout; if any scanner hangs (WMI timeout, disk I/O stall), the window stays in loading overlay indefinitely with no recovery
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs:53`
  Touches: `MainWindow.xaml.cs` — wrap in `Task.WhenAny(scan, Task.Delay(timeout))` with fallback to show partial results + error toast
  Acceptance: Initial scan completes or times out within 60s; UI always becomes interactive
  Complexity: S

- [ ] P1 — Route EmptyFolderScanner through SafeDeleteDirectory
  Why: `Directory.Delete(folder.Path, recursive: false)` bypasses reparse-point guards and locked-file recovery in SafetyGuard
  Evidence: `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs:85` — `IsPathSafeToDelete` is checked but deletion uses raw API
  Touches: `EmptyFolderScanner.cs` — replace `Directory.Delete` with `SafetyGuard.SafeDeleteDirectory`
  Acceptance: Deleting a junction-pointed empty folder does not follow the junction; build passes
  Complexity: S

- [ ] P1 — Fix HealthScorer system-drive fallback
  Why: Falls back to `@"C:\"` when `Path.GetPathRoot()` returns null — incorrect on systems where Windows is installed on D:\ or another drive
  Evidence: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs:169`
  Touches: `HealthScorer.cs` — replace `@"C:\"` with `Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\"`
  Acceptance: On a system with Windows on any drive letter, HealthScorer reports correct free space
  Complexity: S

### P2 — Quality and trust

- [ ] P2 — Dynamic user-agent version from assembly
  Why: Hardcoded `"DeepPurge/0.9"` user-agent string will become stale on version bumps
  Evidence: `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs:40`
  Touches: `MainViewModel.Extensions.cs` — replace with `$"DeepPurge/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(2)}"`
  Acceptance: After version bump, UpdateChecker and winapp2 download requests send correct version in User-Agent header
  Complexity: S

- [ ] P2 — Settings export/import CLI commands
  Why: IT technicians deploying DeepPurge across machines need to replicate configuration (expert mode, excluded paths, age thresholds) without manual setup
  Evidence: FluentCleaner v26.06.02 settings export/import; InstallerClean CLI packaging pattern
  Touches: `Program.cs` (add `settings export <path>` and `settings import <path>` commands), `AppSettings.cs` (add `ExportTo`/`ImportFrom` methods)
  Acceptance: `deeppurgecli settings export config.json` produces valid JSON; `settings import config.json` on another machine applies all settings
  Complexity: S

- [ ] P2 — Unit tests for SafetyGuard deletion primitives
  Why: `SafeDeleteFile`, `SafeDeleteDirectory`, `SafeEnumerateFiles` are the foundation of every destructive operation but have no dedicated tests — only indirect coverage through higher-level tests
  Evidence: `src/DeepPurge.Core/Safety/SafetyGuard.cs` — SafeDeleteFile (line 406+), SafeDeleteDirectory (line 376+), SafeEnumerateFiles (line 331+)
  Touches: New `tests/DeepPurge.Tests/SafetyGuardDeletionTests.cs` — test reparse-point skipping, locked-file fallback, protected-path rejection, recursive enumeration safety
  Acceptance: ≥12 tests covering happy path + reparse point + protected path + nonexistent path scenarios; all pass
  Complexity: M

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
