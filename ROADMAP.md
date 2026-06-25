# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P1 — High value, competitive differentiation

- [ ] P1 — **Target .NET 10 LTS + CommunityToolkit.Mvvm 8.4.2**
  Why: .NET 8 EOL Nov 2026. .NET 10 is LTS through Nov 2028. Toolkit 8.4.2 adds partial properties. Includes SearchValues SIMD, FrozenDictionary, native WPF Fluent theme.
  Evidence: .NET 10 release notes; .NET 9 STS EOL Nov 2026; breaking changes audit.
  Touches: All 4 `.csproj` files (TFM `net10.0-windows10.0.17763.0`), `CommunityToolkit.Mvvm` → 8.4.2, `System.IO.Hashing` → 10.0.9, ViewModels (partial property migration)
  Acceptance: Build succeeds on net10.0, all tests pass, ViewModels use `[ObservableProperty] public partial` syntax.
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

- [ ] P1 — **Expand leftover signature database to 200+ profiles**
  Why: Current 50 profiles cover common apps but benchmark accuracy requires broader coverage. Target: ≥85% accuracy in Uninstalr benchmark.
  Evidence: Uninstalr 2026 benchmark — HiBit 90%, Total Uninstall 86%; Revo Logs Database covers 12.5k programs. Also fix: duplicate Spotify entry.
  Touches: `Core/Data/leftover-signatures.json`, `Core/Data/LeftoverSignatureDb.cs`
  Acceptance: 200+ profiles. Matching algorithm enhanced to handle publisher-based grouping. Duplicate Spotify entry removed.
  Complexity: M

- [ ] P1 — **Portable app detection**
  Why: Only Uninstalr detects portable apps. All 8 other tested tools scored 0. Significant unmet demand.
  Evidence: Uninstalr 2026 benchmark — portable app detection section; BCU's folder scan concept.
  Touches: New `Core/Packages/PortableAppScanner.cs`, `Core/Packages/PackageManagerScanner.cs`
  Acceptance: Scan common portable locations for executables without matching registry entries. Show as "Portable" source in program list.
  Complexity: M

### P2 — Quality, reliability, developer experience

- [ ] P2 — **Mutation testing on safety-critical code**
  Why: 116 tests for ~14k LOC is thin. SafetyGuard and deletion logic are safety-critical — need verification that tests actually catch regressions.
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

- [ ] P2 — **winget COM API migration**
  Why: `winget list --output json` is not a supported CLI option. The `Microsoft.Management.Deployment` COM API is the official programmatic interface.
  Evidence: microsoft/winget-cli#4965 (feature request still open); winget export uses schema 2.0 JSON.
  Touches: `Core/Packages/PackageManagerScanner.cs`
  Acceptance: Use `winget export` for JSON package list or COM API for real-time queries. Remove `ParseWingetTable` fallback.
  Complexity: M

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

- [ ] P3 — **Expert/safe mode toggle**
  Why: BleachBit's expert mode hides dangerous operations from novice users. Reduces support burden and builds trust.
  Evidence: BleachBit 6.0 expert mode; FluentCleaner's anti-bloat philosophy.
  Touches: `App/ViewModels/MainViewModel.cs`, `App/Views/MainWindow.xaml`, `Core/App/DataPaths.cs` (persist setting)
  Acceptance: Default mode hides: secure delete toggle, advanced leftover scan, registry hunter, service deletion. Expert mode (toggle in settings) reveals all.
  Complexity: S

- [ ] P3 — **Junk growth history tracker**
  Why: Show users the trend of junk accumulation over time. Compelling retention feature.
  Evidence: FluentCleaner innovation — historical log of clean runs with bytes freed per date.
  Touches: `Core/Diagnostics/ActivityLog.cs` (already records operations), new chart panel in `App/Views/MainWindow.xaml`
  Acceptance: History panel shows a bar/line chart of bytes freed per clean run over time, sourced from ActivityLog JSONL data.
  Complexity: S

- [ ] P3 — **Orphan scan without prior uninstall**
  Why: Scan for remnants of programs already removed by other means. BCU's most-requested enhancement (#736). Ashampoo calls this "forensic analysis."
  Evidence: BCU #736 (automatic leftover scans without uninstallation); Ashampoo UnInstaller 16 forensic analysis.
  Touches: `Core/FileSystem/FileLeftoverScanner.cs`, `Core/Data/LeftoverSignatureDb.cs`, new UI panel
  Acceptance: User can trigger a system-wide orphan scan that uses the signature DB to find remnants of programs not currently installed. Results shown in a dedicated panel with confidence ratings.
  Complexity: M

## Research-Driven Additions (June 2026)

### P1 — High value, competitive parity

- [ ] P1 — **Wire .resx localization into XAML and code-behind**
  Why: `Properties/Resources.resx` has 20 UI strings with `Resources.Designer.cs` accessor but zero references in XAML or C#. The CHANGELOG claims "Ready for Crowdin submission" but strings aren't consumed. Localization is dead infrastructure.
  Evidence: `grep 'x:Static.*Resources\.' Views/` → 0 matches. `grep 'Properties\.Resources\.' App/` → 0 matches.
  Touches: `App/Views/MainWindow.xaml` (replace ~100 hardcoded string literals with `{x:Static}` bindings), `App/Views/MainWindow.xaml.cs` (programmatic strings), `App/Properties/Resources.resx` (expand from 20 to ~150 strings)
  Acceptance: All user-visible strings in XAML and code-behind reference `Resources.Designer.cs`. Adding a `Resources.de.resx` file produces a German UI.
  Complexity: M

- [ ] P1 — **ARM64 build target**
  Why: Windows on ARM is growing (Surface Pro, Snapdragon X Elite/Plus, Qualcomm Oryon). DeepPurge only publishes `win-x64`. P/Invoke-heavy code (MFT structs, USN journal, COM IShellLinkW) needs ARM64 validation.
  Evidence: BCU issue #841 (ARM64 request). No `win-arm64` in any csproj or workflow.
  Touches: All 4 `.csproj` files, `.github/workflows/build.yml`, `.github/workflows/release.yml`, `BUILD.bat`, `Build.ps1`. P/Invoke struct validation in `FastDiskAnalyzer.cs`, `UsnJournalReader.cs`, `ShortcutRepairScanner.cs`.
  Acceptance: `dotnet publish -r win-arm64` produces a working single-file exe. CI publishes both `win-x64` and `win-arm64` artifacts. GitHub Release includes both.
  Complexity: M

- [ ] P1 — **Version-aware shared-path protection in leftover scanner**
  Why: Uninstalling one version of multi-version software (e.g., Blender 4.4) can destroy shared settings used by another version (Blender 4.2). The leftover scanner doesn't distinguish version-specific from shared paths.
  Evidence: BCU #758 (Blender settings data loss). Uninstalr's relevance-filtering avoids this by attributing files to specific installs.
  Touches: `Core/FileSystem/FileLeftoverScanner.cs`, `Core/Registry/RegistryLeftoverScanner.cs`, `Core/Data/LeftoverSignatureDb.cs`
  Acceptance: Before flagging a leftover path, the scanner checks if any other installed program shares the same parent directory (via `InstalledProgramScanner`). Shared paths are downgraded from Safe to Risky confidence. Test case: two versions of same app installed, uninstall one, verify shared paths are not flagged as Safe.
  Complexity: M

- [ ] P1 — **Restart Manager locked-file detection**
  Why: Uninstallers frequently fail because files are locked by running processes. The Windows Restart Manager API (`rstrtmgr.dll`) can identify which processes hold locks and optionally gracefully shut them down.
  Evidence: BCU #129 (delete locked files on reboot). Restart Manager API docs (`RmStartSession`, `RmRegisterResources`, `RmGetList`).
  Touches: New `Core/FileSystem/LockedFileResolver.cs`, `Core/Uninstall/UninstallEngine.cs`
  Acceptance: When a file deletion fails with `IOException` (sharing violation), DeepPurge identifies the locking process by name/PID and offers: (1) close the process, (2) queue for delete-on-reboot via `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`, (3) skip. CLI uses `--force-close` flag.
  Complexity: M

- [ ] P1 — **ETW registry monitoring for install tracking**
  Why: Current install monitor captures filesystem changes (USN journal) but not registry changes in real-time. The `Microsoft.Diagnostics.Tracing.TraceEvent` NuGet library can capture every registry create/set/delete via the kernel ETW provider, closing the snapshot-vs-journal gap for registry.
  Evidence: `KernelTraceEventParser.Keywords.Registry` (0x00020000). Events: RegistryCreate, RegistrySetValue, RegistryDelete. Filters by installer PID.
  Touches: New `Core/InstallMonitor/RegistryEtwTracer.cs`, `Core/InstallMonitor/InstallSnapshotEngine.cs`, `DeepPurge.Core.csproj` (add `Microsoft.Diagnostics.Tracing.TraceEvent` NuGet)
  Acceptance: `TraceInstallAsync` captures both filesystem (USN journal) and registry (ETW) changes during install. Registry diff is precise (only installer-PID operations), not snapshot-based. CLI `--legacy` flag falls back to snapshot.
  Complexity: L

### P2 — Quality, reliability, developer experience

- [ ] P2 — **ViewModel decomposition — extract per-panel ViewModels**
  Why: MainViewModel is 1,666 lines across 2 partials with 15+ feature areas. Cognitive load, merge conflicts, and testability all suffer. MainWindow code-behind is 1,044 lines with similar monolith issues.
  Evidence: `wc -l MainViewModel.cs MainViewModel.Extensions.cs` → 1,060 + 606.
  Touches: `App/ViewModels/MainViewModel.cs`, `App/ViewModels/MainViewModel.Extensions.cs`, new per-panel VM files (DriverPanelViewModel, DuplicatePanelViewModel, etc.), `App/Views/MainWindow.xaml.cs`
  Acceptance: MainViewModel composes per-panel VMs. Each panel VM is independently testable. MainViewModel drops below 400 lines.
  Complexity: L

- [ ] P2 — **Global path exclusion whitelist**
  Why: Users need to protect specific directories from all scans/cleanups (e.g., custom data directories inside AppData, development environments). No mechanism exists.
  Evidence: FluentCleaner's global exclusion whitelist. BCU supports exclusions. No exclusion logic in DeepPurge's SafetyGuard or scanners.
  Touches: `Core/App/DataPaths.cs` (persist exclusion list), `Core/Safety/SafetyGuard.cs` (check before delete), `Core/FileSystem/FileLeftoverScanner.cs`, `Core/FileSystem/JunkFilesCleaner.cs`, `Core/Privacy/EvidenceRemover.cs`
  Acceptance: Users can add paths to an exclusion list (persisted in `DataPaths.Config`). All scanners and deletion pipelines skip excluded paths. CLI supports `--exclude <path>`.
  Complexity: M

- [ ] P2 — **Amcache parsing for remnant discovery**
  Why: `Amcache.hve` tracks every executed binary with SHA1, publisher, install date, and paths. Cross-referencing against installed programs reveals remnant executables that survive uninstall — enabling forensic-style orphan detection without prior monitoring.
  Evidence: Ashampoo's "forensic analysis" feature. `InventoryApplication` and `InventoryApplicationFile` registry paths inside Amcache. P/Invoke via `offreg.dll` (`OROpenHive`/`OREnumKey`).
  Touches: New `Core/Registry/AmcacheParser.cs`, `Core/FileSystem/FileLeftoverScanner.cs`
  Acceptance: Scan parses `Amcache.hve` to find executables associated with an uninstalled program. Results feed into the leftover scanner as high-confidence matches.
  Complexity: M

- [ ] P2 — **CIM migration from System.Management (WMI)**
  Why: `System.Management` (WMI) is deprecated. CIM via `Microsoft.Management.Infrastructure` is faster, scales better, and is the recommended path for .NET 8+. Current WMI usage: SystemRestoreManager, SecureDelete (SSD detection).
  Evidence: Microsoft deprecation guidance. `Win32_Product` triggers MSI reconfiguration on query.
  Touches: `Core/Safety/SystemRestoreManager.cs`, `Core/Safety/SecureDelete.cs`, `DeepPurge.Core.csproj` (replace `System.Management` with `Microsoft.Management.Infrastructure`)
  Acceptance: All WMI calls replaced with CIM equivalents. `Win32_Product` never queried. Build warning-free.
  Complexity: S

### P3 — Polish, differentiation

- [ ] P3 — **Context menu shell integration (right-click uninstall)**
  Why: Right-click any executable in Explorer → "Uninstall with DeepPurge" resolves the "which entry is this program?" problem without Hunter Mode's complexity.
  Evidence: BCU #331 (context menu integration). Standard UX pattern in Revo, IObit, HiBit.
  Touches: New `Core/Shell/ShellExtensionRegistrar.cs`, `App/Views/MainWindow.xaml.cs` (handle deep-link args), CLI (handle `--target <path>` arg)
  Acceptance: `deeppurgecli register-shell` adds a context menu entry for `.exe` files. Right-click → "Uninstall with DeepPurge" opens the GUI with the program pre-selected. `deeppurgecli unregister-shell` removes it.
  Complexity: M

- [ ] P3 — **Bundleware / sideload detection**
  Why: Programs silently installed alongside other software (toolbar bundling, browser hijacker sideloading) are a distinct scan category. Users don't know they exist until they see symptoms.
  Evidence: IObit's bundleware scanner (paywalled). Uninstalr blog calls out the irony of IObit bundling iTop VPN.
  Touches: `Core/Registry/InstalledProgramScanner.cs` (flag programs with `InstallDate` matching another program's install within ±5 minutes), `Core/Models/InstalledProgram.cs` (add `IsBundleware` flag)
  Acceptance: Programs installed within 5 minutes of another program's install get flagged as "Possibly bundled" in the UI. No false positives for system updates.
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

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
