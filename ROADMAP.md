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

### P1 — Trust and Safety

- [ ] P1 — **Cookie preservation whitelist for browser cleaning**
  Why: EvidenceRemover deletes all cookies wholesale; users lose saved logins. BleachBit 6.0.0's Cookie Manager is the competitive bar — every serious cleaner now offers selective cookie preservation.
  Evidence: BleachBit v6.0.0 Cookie Manager (bleachbit.org/news/bleachbit-600); no cookie/Cookie references anywhere in DeepPurge Core source.
  Touches: `src/DeepPurge.Core/Privacy/EvidenceRemover.cs`, `src/DeepPurge.Core/App/AppSettings.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.cs` (Evidence panel), `src/DeepPurge.Cli/Program.cs` (clean evidence command)
  Acceptance: Users can define a list of domains (e.g., `github.com`, `google.com`) in settings; Evidence cleaning skips cookies matching those domains. CLI supports `--keep-cookies domain1,domain2`. Settings export/import includes the whitelist.
  Complexity: M

- [ ] P1 — **Automated deletion rollback from DeletionManifest**
  Why: `DeletionManifest` (Core/Diagnostics/DeletionManifest.cs) records every deletion as JSONL but provides no restore path — the undo story is half-built. Winhance and Win-Debloat7 both have rollback. Files deleted to Recycle Bin can be restored; secure-deleted files cannot. Registry keys have BackupManager `.reg` exports.
  Evidence: DeletionManifest.cs is 46 lines with Record/RecordFile/RecordDirectory only — no List/Load/Restore methods. Competitors: Winhance Change History, Win-Debloat7 DPAPI rollback snapshots.
  Touches: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs` (add ListManifests, LoadManifest, RestoreManifest), `src/DeepPurge.Cli/Program.cs` (add `restore` command), `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs` (History panel restore button)
  Acceptance: `deeppurgecli restore --date 2026-06-26` reads the JSONL manifest for that date and attempts recovery: Recycle Bin restore for file deletions, `reg import` for backed-up registry keys. Reports what was restored vs unrecoverable (secure-deleted). GUI History panel shows a "Restore" button per session.
  Complexity: M

### P2 — Parity and Polish

- [ ] P2 — **xUnit v3 migration**
  Why: xUnit 3.2.2 is stable. Stryker.NET 4.14.2 MTP runner now supports xUnit v3 via `--test-runner mtp`, resolving the blocker (Stryker #3117). xUnit v3 drops Test SDK dependency, has native MTP support, and improves parallel execution. Staying on 2.9.x means missing out on the entire v3 ecosystem.
  Evidence: xunit.net/releases/v3/3.2.2 (stable Jan 2026); stryker-mutator.io/blog/stryker-net-mtp-runner (MTP runner preview); Stryker.NET 4.14.2 on NuGet (May 2026).
  Touches: `tests/DeepPurge.Tests/DeepPurge.Tests.csproj` (xunit 2.9.3 -> 3.2.2, drop Test SDK, update runner), `stryker-config.json` (add `--test-runner mtp`), all test files (minor API adjustments if any)
  Acceptance: `dotnet test` passes with xUnit 3.2.2. `dotnet stryker --test-runner mtp` produces a mutation report. CI workflow unchanged.
  Complexity: S

- [ ] P2 — **Digital signature column on installed programs list**
  Why: DeepPurge already runs WinVerifyTrust on autoruns and services (DigitalSignatureInspector). The main Programs DataGrid — the first thing users see — lacks this column. BCU v6.2 added certificate/integrity columns as a headline feature. Unsigned or revoked programs are a strong signal for bundleware/malware.
  Evidence: BCU v6.2 changelog (certificate/integrity columns); DeepPurge DigitalSignatureInspector exists but is only wired to AutorunScanner and ServiceScanner, not InstalledProgramScanner.
  Touches: `src/DeepPurge.Core/Registry/InstalledProgramScanner.cs` (enrich with signature check), `src/DeepPurge.Core/Models/InstalledProgram.cs` (add SignatureStatus property), `src/DeepPurge.App/Views/MainWindow.xaml` (Programs DataGrid column), `src/DeepPurge.App/ViewModels/MainViewModel.cs` (parallel sig check during initial scan)
  Acceptance: Programs DataGrid shows a Signature column (Signed/Unsigned/Revoked/Untrusted). Initial scan runs signature verification in parallel (8 workers, matching existing autorun pattern). CLI `list --json` includes signatureStatus field.
  Complexity: M

- [ ] P2 — **Per-program notes/tags**
  Why: IT technicians managing fleet PCs need to annotate programs ("keep for compliance", "remove after migration", "vendor required"). BCU v6.3 is adding Custom Notes per entry (#939). DeepPurge's sysadmin CLI positioning makes this particularly valuable.
  Evidence: BCU pull request #939 (Custom Notes feature, Jun 2026). No notes/tags infrastructure in DeepPurge AppSettings or InstalledProgram model.
  Touches: `src/DeepPurge.Core/App/AppSettings.cs` (add ProgramNotes dictionary), `src/DeepPurge.Core/Models/InstalledProgram.cs` (add Note property), `src/DeepPurge.App/Views/MainWindow.xaml` (Notes column + inline edit), `src/DeepPurge.Cli/Program.cs` (`list` output includes notes, `note` command to set/clear)
  Acceptance: Right-click a program row -> "Add Note" opens an inline text field. Notes persist in settings.json keyed by registry key name. `deeppurgecli note "Program Name" "keep for compliance"` sets a note. `deeppurgecli list --json` includes notes.
  Complexity: S

- [ ] P2 — **Copy scan results to clipboard**
  Why: Every scan panel (programs, junk, evidence, drivers, shortcuts, duplicates, orphans, startup impact) produces a result table with no way to copy it except via file export. A "Copy to Clipboard" button is a 5-minute quality-of-life improvement that reduces friction for bug reports, documentation, and sharing.
  Evidence: No Clipboard references in DeepPurge source. .NET 10 WPF has a new unified Clipboard API with JSON serialization support.
  Touches: `src/DeepPurge.App/ViewModels/MainViewModel.cs` and `MainViewModel.Extensions.cs` (add CopyToClipboard relay commands per panel), `src/DeepPurge.App/Views/MainWindow.xaml` (toolbar buttons)
  Acceptance: Each scan result panel has a "Copy" button in the toolbar. Copies TSV-formatted text to clipboard (same format as `deeppurgecli list` console output). Works on all 10+ scan panels.
  Complexity: S

- [ ] P2 — **WPF .NET 10 Fluent style adoption**
  Why: .NET 10 expanded Fluent theme support to DatePicker, GridSplitter, GroupBox, Hyperlink, Label, NavigationWindow, RichTextBox, TextBox, and GridView. These controls currently use the default WPF chrome, creating visual inconsistency with the themed panels. Adopting Fluent styles is free polish — no new dependencies, just XAML resource dictionary updates.
  Evidence: learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100 (Fluent styles for 9 new controls). Grid shorthand syntax also available.
  Touches: `src/DeepPurge.App/Themes/*.xaml` (resource dictionaries), `src/DeepPurge.App/Views/MainWindow.xaml` (adopt Grid shorthand where applicable)
  Acceptance: All TextBox, Label, GroupBox, and GridSplitter instances use Fluent styling that matches the active theme. No custom control template overrides needed for these controls.
  Complexity: S

- [ ] P2 — **DeletionManifest silent catch fix**
  Why: `DeletionManifest.Record` (line 33) has the one remaining empty `catch { }` after the 57-block cleanup. A manifest write failure is invisible — the user thinks a deletion was recorded when it wasn't. Should log via `Log.Warn`.
  Evidence: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs:33` — empty catch block.
  Touches: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs`
  Acceptance: Failed manifest writes log a warning via `Log.Warn`. No other behavioral change.
  Complexity: S

### P3 — Longer-term

- [ ] P3 — **Supplementary cleaner definitions for modern apps**
  Why: winapp2.ini last updated November 2025 (v251109, 7+ months stale). New apps (Claude Code, VS Code forks, modern Chromium browsers like Zen/Vivaldi) are not covered. BleachBit 6.0.1 beta added cleaners for Claude Code and VS Code forks. DeepPurge's custom JSON cleaner infrastructure is ready — it just needs new definitions.
  Evidence: github.com/MoscaDotTo/Winapp2 last release v251109 (Nov 10, 2025). BleachBit 6.0.1 beta changelog (new Claude Code, VS Code fork cleaners). DeepPurge CleanerDefinitionRunner already loads `*.cleaner.json` from DataPaths.Cleaners.
  Touches: `src/DeepPurge.Core/Cleaning/` (new `.cleaner.json` files for modern apps), packaging to bundle default cleaner definitions
  Acceptance: DeepPurge ships 15-20 bundled cleaner definitions for apps not in winapp2.ini (Claude Code, Cursor, Windsurf, Zen Browser, Arc, Notion, Obsidian, Discord, Slack, Teams, Figma, Docker Desktop, WSL caches). `deeppurgecli cleaners list` shows them. Definitions are loadable from DataPaths.Cleaners for community contributions.
  Complexity: M

- [ ] P3 — **Sysmon event log integration for install monitoring**
  Why: Windows 11 26H2 ships Sysmon as a built-in optional feature (disabled by default). When enabled, Sysmon logs process creation, file creation, registry modification, and network connections to the Windows Event Log. This could supplement USN journal tracking during install monitoring with registry change events that USN journal cannot capture.
  Evidence: bleepingcomputer.com/news/microsoft/microsoft-rolls-out-native-windows-11-sysmon-security-monitoring. Windows 11 26H2 ships late September 2026.
  Touches: `src/DeepPurge.Core/InstallMonitor/InstallSnapshotEngine.cs` (add Sysmon event log reader alongside USN journal), `src/DeepPurge.Core/Diagnostics/SelfTest.cs` (detect Sysmon availability)
  Acceptance: When Sysmon is enabled and `TraceInstallAsync` runs, registry changes are captured from Sysmon event logs in addition to filesystem changes from USN journal. Falls back gracefully when Sysmon is not installed. `deeppurgecli doctor` reports Sysmon status.
  Complexity: L

- [ ] P3 — **ReFS/exFAT graceful degradation in DuplicateFinder and FastDiskAnalyzer**
  Why: DuplicateFinder and FastDiskAnalyzer rely on NTFS-specific APIs (USN journal, MFT enumeration). ReFS is increasingly common on Storage Spaces and server volumes. exFAT is standard on USB/SD media. Both tools should detect the filesystem type and transparently fall back to the parallel `FindFirstFileExW` path without errors or user confusion.
  Evidence: DuplicateFinder.cs skips reparse points but doesn't detect filesystem type. FastDiskAnalyzer has a fallback path but error messaging may confuse users on non-NTFS volumes.
  Touches: `src/DeepPurge.Core/FileSystem/DuplicateFinder.cs`, `src/DeepPurge.Core/FileSystem/FastDiskAnalyzer.cs`
  Acceptance: DuplicateFinder and FastDiskAnalyzer detect volume filesystem type via `GetVolumeInformationW`. On non-NTFS volumes, they silently use the fallback enumeration path. Status bar shows "Scanning (fallback mode)" instead of an error.
  Complexity: S

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
