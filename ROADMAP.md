# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P0 — Correctness and safety

- [ ] P0 — Route BrowserExtensionScanner.RemoveExtension through SafeDeleteDirectory/SafeDeleteFile
  Why: Last destructive path using raw `Directory.Delete(true)` and `File.Delete` — bypasses child-reparse safety and locked-file recovery.
  Evidence: `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs:235`; CVE-2026-50656 (Defender junction redirect).
  Touches: `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs`
  Acceptance: `RemoveExtension` uses `SafetyGuard.SafeDeleteDirectory` and `SafetyGuard.SafeDeleteFile` instead of raw calls.
  Complexity: S

- [ ] P0 — Fix hardcoded v0.8.1 version strings in MainWindow.xaml
  Why: Users see `v0.8.1` in the sidebar header and About panel while the actual version is 0.9.0+ — visible misinformation.
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml:278,1594`; VM already has `AppVersionDisplay`.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml`
  Acceptance: Both version TextBlocks bind to `{Binding AppVersionDisplay}` or equivalent assembly-version source. No hardcoded version strings remain in XAML.
  Complexity: S

- [ ] P0 — Add thread-safe synchronization to AppSettings.Save
  Why: Concurrent saves from different UI actions (expert toggle + exclusion edit) can corrupt `settings.json`.
  Evidence: `src/DeepPurge.Core/App/AppSettings.cs:14`; Lazy<T> ensures thread-safe load but Save has no lock.
  Touches: `src/DeepPurge.Core/App/AppSettings.cs`
  Acceptance: `Save()` uses a `lock` or `SemaphoreSlim` to prevent concurrent writes. File write is atomic (write-to-temp, rename).
  Complexity: S

### P1 — Documentation and discoverability

- [ ] P1 — Add missing CLI commands to PrintHelp
  Why: 4 of 19 CLI commands are undiscoverable — `register-shell`, `unregister-shell`, `cleaners`, `orphans --remnants`.
  Evidence: `src/DeepPurge.Cli/Program.cs:675` help text vs command switch at `:54-76`.
  Touches: `src/DeepPurge.Cli/Program.cs`
  Acceptance: All 19 commands appear in `--help` output with usage examples.
  Complexity: S

- [ ] P1 — Update README with shipped features not yet documented
  Why: 8+ shipped features have no README documentation: portable app detection, game platforms, health dashboard, system slimming, bundleware detection, expert mode, custom JSON cleaners, BAM remnants, shell integration, ARM64.
  Evidence: `README.md` feature list vs actual CLI commands and Core modules.
  Touches: `README.md`
  Acceptance: Each shipped feature has a README entry with a one-line description matching the existing style.
  Complexity: S

### P2 — Testing and hardening

- [ ] P2 — Add tests for SafeDeleteFile/SafeDeleteDirectory/SafeEnumerateFiles
  Why: Safety-critical deletion primitives have zero unit tests — regressions could silently bypass junction/symlink protection.
  Evidence: `src/DeepPurge.Core/Safety/SafetyGuard.cs:328-428`; CVE-2025-55247 (.NET junction EoP); Defender CVE-2026-50656.
  Touches: `tests/DeepPurge.Tests/SafetyGuardTests.cs`
  Acceptance: Tests cover: normal file delete, directory delete, child junction skip, sharing-violation fallback to delete-on-reboot, ".." path rejection. Use disposable temp directories.
  Complexity: M

- [ ] P2 — Add tests for new untested modules (CleanerDefinition, GamePlatformScanner, HealthScorer, AppSettings)
  Why: 11 of 57 Core modules have test files (19%). New features shipped without any test coverage.
  Evidence: `tests/DeepPurge.Tests/` (11 test files); new Core modules with zero tests.
  Touches: `tests/DeepPurge.Tests/`
  Acceptance: At least one test file per module covering: CleanerDefinition detect/preview/execute/dry-run, GamePlatformScanner VDF parsing, HealthScorer score boundaries, AppSettings save/load roundtrip.
  Complexity: M

- [ ] P2 — Verify .NET 10 runtime is patched for CVE-2025-55247
  Why: CVE-2025-55247 (CVSS 7.3) enables privilege escalation via junction/symlink on elevated .NET processes. DeepPurge runs elevated.
  Evidence: SentinelOne CVE-2025-55247; `Directory.Build.props` has `TargetLatestRuntimePatch=true`.
  Touches: `Directory.Build.props`, potentially runtime version pins
  Acceptance: Build output or `doctor` command confirms runtime version includes the CVE-2025-55247 fix.
  Complexity: S

- [ ] P2 — Add `--registry-only` flag to CLI `list` command
  Why: Package enrichment (winget/scoop/portable/game scan) adds latency unsuitable for IT scripting. Registry-only was the previous default.
  Evidence: `src/DeepPurge.Cli/Program.cs:119-131`; IT admin persona needs fast inventory.
  Touches: `src/DeepPurge.Cli/Program.cs`
  Acceptance: `deeppurgecli list --registry-only` skips `PackageManagerScanner.EnrichAsync` and returns in <1s. Default behavior unchanged.
  Complexity: S

### P3 — Polish and competitive parity

- [ ] P3 — Add "last used" date display for installed programs
  Why: BCU's most-requested feature (#941, Jun 2026). Shows when a program was last launched, helping identify bloat.
  Evidence: BCU issue #941; Windows Prefetch files (`%SystemRoot%\Prefetch\*.pf`) contain last-execution timestamps.
  Touches: `src/DeepPurge.Core/Registry/InstalledProgramScanner.cs`, `src/DeepPurge.Core/Models/InstalledProgram.cs`
  Acceptance: Programs list shows a "Last Used" column sourced from Prefetch timestamps. Empty for programs without Prefetch data.
  Complexity: M

- [ ] P3 — Add Restart Manager retry loop per Microsoft guidance
  Why: Between RM sizing call and data call, the process list can change. Microsoft recommends 3-attempt retry.
  Evidence: `src/DeepPurge.Core/FileSystem/LockedFileResolver.cs`; Microsoft RM docs; CrowdStrike RM abuse analysis.
  Touches: `src/DeepPurge.Core/FileSystem/LockedFileResolver.cs`
  Acceptance: `GetLockingProcesses` retries RmGetList up to 3 times on ERROR_MORE_DATA to handle the sizing race.
  Complexity: S

### P1 — Safety and dependency alignment (research round 2)

- [ ] P1 — Route remaining raw File.Delete/Directory.Delete calls through SafetyGuard in GUI destructive paths
  Why: Two GUI delete paths bypass SafeDeleteFile, losing locked-file recovery (RM query + delete-on-reboot). One path skips SafetyGuard validation entirely.
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs:561` (`DeleteLargeFiles_Click` uses raw `File.Delete`); `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs:83` (`DeleteEmptyFolders` calls `Directory.Delete` without `SafetyGuard.IsPathSafeToDelete`).
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs`
  Acceptance: `DeleteLargeFiles_Click` uses `SafetyGuard.SafeDeleteFile`. `DeleteEmptyFolders` checks `SafetyGuard.IsPathSafeToDelete` before deleting. No raw `File.Delete` or `Directory.Delete` calls remain in user-facing destructive GUI paths.
  Complexity: S

- [ ] P1 — Align System.* NuGet packages with .NET 10 TFM
  Why: Three first-party packages are at 8.0.x while the project targets `net10.0-windows10.0.17763.0`. Version mismatch risks subtle runtime behavior differences and misses perf improvements (e.g. System.IO.Hashing XXH3 hardware-accelerated paths).
  Evidence: `src/DeepPurge.Core/DeepPurge.Core.csproj` — `System.Management` 8.0.0, `System.ServiceProcess.ServiceController` 8.0.1, `System.IO.Hashing` 8.0.0; latest: 10.0.9 for all three.
  Touches: `src/DeepPurge.Core/DeepPurge.Core.csproj`
  Acceptance: All three packages updated to 10.0.x. `dotnet build` and `dotnet test` pass. No runtime regressions.
  Complexity: S

### P2 — Reliability and code quality (research round 2)

- [ ] P2 — Add failure logging to SecureDelete.Wipe
  Why: The catch block at line 68 catches all exceptions and returns `false` without calling `Log.Warn`. A file partially overwritten (step 2 complete) but not deleted (step 3/4 failed) is left in a corrupted state with no diagnostic trail.
  Evidence: `src/DeepPurge.Core/Safety/SecureDelete.cs:68-71`; contrast with `SafeDeleteFile` which logs all failure paths.
  Touches: `src/DeepPurge.Core/Safety/SecureDelete.cs`
  Acceptance: `Wipe()` catch block calls `Log.Warn` with the file path and exception message before returning false.
  Complexity: S

- [ ] P2 — Extract shared FormatSize utility to eliminate 6 duplicate implementations
  Why: Byte-to-human-readable size formatting is independently implemented in 6 files with slightly different thresholds and formatting. Maintenance burden and inconsistent display.
  Evidence: `InstalledProgram.FormatBytes`, `EvidenceRemover.TraceCategory.FormatSize`, `MainViewModel.FormatSize`, `MainViewModel.Extensions.FormatSize`, `Program.cs FormatSize`, `ToastNotifier FormatSize`.
  Touches: New `Core/Diagnostics/SizeFormatter.cs` or similar, plus 6 existing files to remove duplicates.
  Acceptance: One canonical implementation; all 6 callers delegate to it. Display output unchanged.
  Complexity: S

### P3 — Dependency maintenance (research round 2)

- [ ] P3 — Update Microsoft.NET.Test.Sdk from 17.11.1 to 18.x
  Why: Major version available (18.7.0). Project stays on 17.x while targeting .NET 10. Picks up test host improvements and .NET 10 TFM support.
  Evidence: `tests/DeepPurge.Tests/DeepPurge.Tests.csproj` — `Microsoft.NET.Test.Sdk` 17.11.1; latest: 18.7.0.
  Touches: `tests/DeepPurge.Tests/DeepPurge.Tests.csproj`
  Acceptance: Package updated. `dotnet test` passes. Stryker.NET (`dotnet stryker`) still runs without regressions.
  Complexity: S

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

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
