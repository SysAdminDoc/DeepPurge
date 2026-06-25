# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P1 — High value, competitive differentiation


- [ ] P1 — **CsWin32 type-safe PInvoke**
  Why: Hand-rolled PInvoke in FastDiskAnalyzer (MFT structs), UsnJournalReader, SecureDelete, ShortcutRepairScanner, UninstallEngine risks struct alignment bugs. CsWin32 generates correct marshaling from official SDK metadata.
  Evidence: Microsoft.Windows.CsWin32 0.3.296; CsWin32 GitHub; hand-rolled `USN_RECORD_V2` with `Pack=1` in FastDiskAnalyzer.
  Touches: `Core/FileSystem/FastDiskAnalyzer.cs`, `Core/InstallMonitor/UsnJournalReader.cs`, `Core/Safety/SecureDelete.cs`, `Core/Shortcuts/ShortcutRepairScanner.cs`, `Core/Uninstall/UninstallEngine.cs`, new `NativeMethods.txt`
  Acceptance: All `[DllImport]` declarations replaced by CsWin32-generated equivalents. NativeMethods.txt lists each API. Build clean with no hand-rolled structs.
  Complexity: L



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


### P2 — Quality, reliability, developer experience

- [ ] P2 — **ViewModel decomposition — extract per-panel ViewModels**
  Why: MainViewModel is 1,666 lines across 2 partials with 15+ feature areas. Cognitive load, merge conflicts, and testability all suffer. MainWindow code-behind is 1,044 lines with similar monolith issues.
  Evidence: `wc -l MainViewModel.cs MainViewModel.Extensions.cs` → 1,060 + 606.
  Touches: `App/ViewModels/MainViewModel.cs`, `App/ViewModels/MainViewModel.Extensions.cs`, new per-panel VM files (DriverPanelViewModel, DuplicatePanelViewModel, etc.), `App/Views/MainWindow.xaml.cs`
  Acceptance: MainViewModel composes per-panel VMs. Each panel VM is independently testable. MainViewModel drops below 400 lines.
  Complexity: L


- [ ] P2 — **Amcache parsing for remnant discovery**
  Why: `Amcache.hve` tracks every executed binary with SHA1, publisher, install date, and paths. Cross-referencing against installed programs reveals remnant executables that survive uninstall — enabling forensic-style orphan detection without prior monitoring.
  Evidence: Ashampoo's "forensic analysis" feature. `InventoryApplication` and `InventoryApplicationFile` registry paths inside Amcache. P/Invoke via `offreg.dll` (`OROpenHive`/`OREnumKey`).
  Touches: New `Core/Registry/AmcacheParser.cs`, `Core/FileSystem/FileLeftoverScanner.cs`
  Acceptance: Scan parses `Amcache.hve` to find executables associated with an uninstalled program. Results feed into the leftover scanner as high-confidence matches.
  Complexity: M


### P3 — Polish, differentiation

- [ ] P3 — **Context menu shell integration (right-click uninstall)**
  Why: Right-click any executable in Explorer → "Uninstall with DeepPurge" resolves the "which entry is this program?" problem without Hunter Mode's complexity.
  Evidence: BCU #331 (context menu integration). Standard UX pattern in Revo, IObit, HiBit.
  Touches: New `Core/Shell/ShellExtensionRegistrar.cs`, `App/Views/MainWindow.xaml.cs` (handle deep-link args), CLI (handle `--target <path>` arg)
  Acceptance: `deeppurgecli register-shell` adds a context menu entry for `.exe` files. Right-click → "Uninstall with DeepPurge" opens the GUI with the program pre-selected. `deeppurgecli unregister-shell` removes it.
  Complexity: M



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
