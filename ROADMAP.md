# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P1 — High value, competitive differentiation

- [ ] P1 — **Orphaned artifact scanner (services, tasks, firewall rules, PATH)**
  Why: Uninstalled programs leave orphaned services, scheduled tasks, firewall rules, and PATH entries. No OSS tool covers all four systematically.
  Evidence: Community reports of orphaned scheduled tasks ("task image is corrupt"); BCU #890 (shell extension detection gap); forum complaints about orphaned update-checker services.
  Touches: `Core/Services/ServiceScanner.cs`, `Core/Tasks/ScheduledTaskScanner.cs`, new `Core/Firewall/FirewallRuleScanner.cs`, new `Core/Shell/PathCleaner.cs`
  Acceptance: Scan detects orphaned services (exe missing), orphaned tasks (action exe missing), firewall rules referencing deleted programs, and PATH entries pointing to non-existent directories. Results shown in a unified "Orphans" panel.
  Complexity: L

- [ ] P1 — **Upgrade to .NET 9 + CommunityToolkit.Mvvm 8.4.2**
  Why: .NET 9 brings SearchValues SIMD acceleration for path scanning, FrozenDictionary for lookup tables, native WPF Fluent theme. Toolkit 8.4.2 adds partial properties — eliminates magic field pattern.
  Evidence: .NET 9 release notes; CommunityToolkit 8.4 announcement; System.IO.Hashing 9.0.13 SIMD optimizations.
  Touches: All 4 `.csproj` files (TFM `net9.0-windows10.0.17763.0`), `CommunityToolkit.Mvvm` → 8.4.2, `System.IO.Hashing` → 9.0.13, ViewModels (partial property migration)
  Acceptance: Build succeeds on net9.0, all 111+ tests pass, ViewModels use `[ObservableProperty] public partial` syntax.
  Complexity: M

- [ ] P1 — **CsWin32 type-safe PInvoke**
  Why: Hand-rolled PInvoke in FastDiskAnalyzer (MFT structs), UsnJournalReader, SecureDelete, ShortcutRepairScanner, UninstallEngine risks struct alignment bugs. CsWin32 generates correct marshaling from official SDK metadata.
  Evidence: Microsoft.Windows.CsWin32 0.3.296; CsWin32 GitHub; hand-rolled `USN_RECORD_V2` with `Pack=1` in FastDiskAnalyzer.
  Touches: `Core/FileSystem/FastDiskAnalyzer.cs`, `Core/InstallMonitor/UsnJournalReader.cs`, `Core/Safety/SecureDelete.cs`, `Core/Shortcuts/ShortcutRepairScanner.cs`, `Core/Uninstall/UninstallEngine.cs`, new `NativeMethods.txt`
  Acceptance: All `[DllImport]` declarations replaced by CsWin32-generated equivalents. NativeMethods.txt lists each API. Build clean with no hand-rolled structs.
  Complexity: L

- [ ] P1 — **Velopack auto-updater**
  Why: Current UpdateChecker only detects updates — user must manually download. Velopack provides delta auto-updates from GitHub Releases with PerMachine install mode.
  Evidence: Velopack 1.2.0 docs; DriverStoreExplorer ships self-update with SHA256 verification.
  Touches: `Core/Updates/UpdateChecker.cs`, `DeepPurge.App.csproj`, `.github/workflows/release.yml`, `Build.ps1`
  Acceptance: On update detection, user can click "Install Update" which downloads delta, verifies SHA256, and restarts the app. Release workflow produces Velopack artifacts.
  Complexity: M

- [ ] P1 — **Hunter Mode (drag-to-identify)**
  Why: Revo's signature UX feature. Drag crosshair onto any window to identify the owning program and jump to its uninstall entry. No OSS tool replicates this.
  Evidence: Revo Pro feature page; consistently praised in reviews as unique differentiator.
  Touches: New `App/Views/HunterWindow.xaml`, `App/ViewModels/MainViewModel.cs`, Win32 `WindowFromPoint` + `GetWindowThreadProcessId`
  Acceptance: User clicks "Hunter Mode" → overlay appears → drag crosshair onto any window → program identified → jump to its entry in the Programs panel.
  Complexity: M

### P2 — Quality, reliability, developer experience

- [ ] P2 — **Mutation testing on safety-critical code**
  Why: 111 tests for 15.8k LOC is thin. SafetyGuard and deletion logic are safety-critical — need verification that tests actually catch regressions.
  Evidence: Stryker.NET 4.14.2; mutation testing best practice for safety-critical paths.
  Touches: `tests/`, `.github/workflows/build.yml`
  Acceptance: Stryker runs in CI on `SafetyGuard.cs`, `SecureDelete.cs`, `UninstallEngine.cs`. Mutation score >80% on these files.
  Complexity: S

- [ ] P2 — **Snapshot testing for ViewModels and exports**
  Why: ViewModel state shapes and export formats should be regression-tested without hand-writing assertions.
  Evidence: Verify.Xunit 31.12.5; snapshot testing pattern.
  Touches: `tests/`, new snapshot `.verified.txt` files
  Acceptance: Verify tests for MainViewModel state transitions, GridExporter CSV/JSON output, ProgramExporter formats. Snapshot diffs caught in CI.
  Complexity: S

- [ ] P2 — **System Slimming module**
  Why: Wise's unique curated checklist of removable Windows components (wallpapers, sample media, IME packs, help files) with per-item sizes.
  Evidence: Wise Program Uninstaller feature; Sophia-Script implements similar tweaks.
  Touches: New `Core/Cleaning/SystemSlimmer.cs`, `App/ViewModels/MainViewModel.Extensions.cs`, `App/Views/MainWindow.xaml`
  Acceptance: New sidebar panel with checkboxes for ~15 removable Windows components. Each shows current size. Delete through SafetyGuard with dry-run support.
  Complexity: S

### P3 — Polish, differentiation

- [ ] P3 — **Health dashboard**
  Why: CCleaner/IObit pattern. Aggregate system hygiene score across Leftovers, Privacy, Disk Space, Startup Impact. One-click remediation entry point.
  Evidence: CCleaner Health Check; IObit Software Health (7-point analysis).
  Touches: New panel in `App/Views/MainWindow.xaml`, `App/ViewModels/MainViewModel.Extensions.cs`
  Acceptance: Dashboard panel shows 4 category scores (0-100), overall grade, and "Fix" buttons per category that navigate to the relevant cleanup panel.
  Complexity: M

- [ ] P3 — **Declarative cleaner format (JSON)**
  Why: Power users should be able to contribute cleaning rules without code changes. BleachBit's CleanerML and Winapp2.ini community model prove this works.
  Evidence: BleachBit CleanerML; Winapp2 repo (969 stars); Kudu uses "simple JSON files, readable and editable."
  Touches: New `Core/Cleaning/CleanerDefinition.cs` (parser + executor), DataPaths.Cleaners
  Acceptance: JSON cleaner files in DataPaths.Cleaners are parsed alongside winapp2.ini. Format supports detect/file/registry rules. Ships with 5 example cleaners.
  Complexity: L

- [ ] P3 — **Game platform detection (Steam/Epic/GOG)**
  Why: Game installations don't follow standard registry patterns. Steam, Epic, GOG use their own manifests.
  Evidence: BCU issue #912 (Epic Games uninstall); BCU's factory pattern for Steam/Scoop/Chocolatey discovery.
  Touches: `Core/Packages/PackageManagerScanner.cs` or new `Core/Packages/GamePlatformScanner.cs`
  Acceptance: Steam (libraryfolders.vdf), Epic (LauncherInstalled.dat), GOG (Galaxy DB) apps appear in the unified programs list with platform badges.
  Complexity: M

## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Portable app detection** — BCU-style folder scan for unregistered apps
- **Tray icon** — background scheduled cleaning with tray notifications
- **Registry ETW tracing** — add `Microsoft.Diagnostics.Tracing.TraceEvent` for
  real-time registry change capture alongside USN journal filesystem tracking
- **Android companion** — nope, scope creep, documented here only to flag it

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
