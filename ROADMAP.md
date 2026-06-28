# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Active items

No active implementation items.

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** - obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** - user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** - overkill for a local desktop tool
- **Cloud sync of settings** - privacy surface without clear value
- **MSIX distribution** - sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app

## Research-Driven Additions

- [ ] P0 — Harden GUI winget upgrade launch against command-line injection
  Why: The GUI starts an elevated command shell with a package id interpolated into the command string.
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs:1106-1110`; Microsoft `ProcessStartInfo.ArgumentList`; DriverStoreExplorer v1.0.26 WinGet update support.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml.cs`, package-manager launch tests or a small launch-command helper.
  Acceptance: Winget upgrades launch without `cmd.exe`; package ids outside a strict safe pattern are refused with a toast/log entry; tests cover quotes, spaces, shell metacharacters, and normal ids.
  Complexity: S

- [ ] P1 — Restore visible keyboard focus indicators in shared WPF styles
  Why: Shared styles remove focus visuals for GridSplitter and DataGridCell without an accessible replacement.
  Evidence: `src/DeepPurge.App/Themes/BaseStyles.xaml:237-247`, `src/DeepPurge.App/Themes/BaseStyles.xaml:549-560`; WCAG 2.2 Focus Appearance.
  Touches: `src/DeepPurge.App/Themes/BaseStyles.xaml`, theme dictionaries if focus brushes need per-theme tuning.
  Acceptance: Tab/arrow navigation shows a visible 2px-or-better focus treatment for buttons, inputs, grids, grid cells, and splitters in every DeepPurge theme; no interactive style nulls focus without replacement.
  Complexity: M

- [ ] P1 — Add GUI deletion-manifest recovery panel
  Why: Core and CLI can list, preview, dry-run, and restore deletion manifests, but GUI users cannot discover the rollback path.
  Evidence: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs:65-159`; `src/DeepPurge.Cli/Program.cs:835-870`; Revo/HiBit backup-and-restore trust patterns.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.App/ViewModels/MainViewModel*.cs`, `tests/DeepPurge.Tests/DeletionManifestTests.cs`.
  Acceptance: GUI lists available `deletions-*.jsonl` manifests, previews entries, runs dry-run restore, shows registry/restorable/unrecoverable counts, and opens backup/log locations without crashing on malformed manifests.
  Complexity: M

- [ ] P1 — Add GUI Settings and Privacy panel for existing AppSettings controls
  Why: Cookie whitelist, excluded paths, min-age defaults, expert mode, settings import/export, and program notes exist but are mostly CLI-only or hidden behind row actions.
  Evidence: `src/DeepPurge.Core/App/AppSettings.cs:6-13`; `src/DeepPurge.Cli/Program.cs:680-712`; BleachBit 6.0 Cookie Manager; FluentCleaner settings hotfix 26.06.04.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/ViewModels/MainViewModel*.cs`, `src/DeepPurge.Core/App/AppSettings.cs`, settings tests.
  Acceptance: GUI can view/edit cookie whitelist, excluded paths, min-age values, expert mode, and settings import/export; changes persist atomically; invalid paths/domains show inline errors; CLI settings still round-trip.
  Complexity: L

- [ ] P1 — Add winapp2 database provenance, backup, and rollback metadata
  Why: The updater overwrites the local cleaner database from a raw GitHub URL without recording commit SHA, SHA256, source date, or the previous file.
  Evidence: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs:39-55`; DriverStoreExplorer v1.0.26 SHA256/rollback update flow; FluentCleaner 26.06.04 database-update failure fix; `MoscaDotTo/Winapp2`.
  Touches: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`, `src/DeepPurge.Cli/Program.cs`, tests around update metadata.
  Acceptance: Each update stores source commit/date, SHA256, byte count, and previous-file backup; failed downloads preserve the previous database; GUI/CLI display local and remote provenance.
  Complexity: M

- [ ] P2 — Add local release and package-manifest readiness validator
  Why: Winget/Scoop manifests intentionally contain release-time placeholders, and docs drift has already left packaging guidance inconsistent.
  Evidence: `packaging/winget/SysAdminDoc.DeepPurge.yaml:39,46`; `packaging/scoop/deeppurge.json:14-25`; latest GitHub release assets `DeepPurge.exe`, `DeepPurgeCli.exe`, `SHA256SUMS.txt`.
  Touches: `Build.ps1`, `BUILD.bat` if needed, `packaging/`, `README.md`, validator tests.
  Acceptance: A local command validates version alignment, release URLs, asset names, SHA256SUMS, winget/Scoop hashes, and placeholder removal; failures explain the exact file/key to fix.
  Complexity: M

- [ ] P2 — Add log, activity, and deletion-manifest retention/scrub controls
  Why: Logs and manifests can contain program names and local paths, but users have no retention window or redaction control.
  Evidence: `src/DeepPurge.Core/App/DataPaths.cs:26-33`; activity/deletion manifest usage in `src/DeepPurge.Core/Diagnostics`; privacy-cleaner positioning from BleachBit/Revo/CCleaner.
  Touches: `src/DeepPurge.Core/Diagnostics`, `src/DeepPurge.Core/App/AppSettings.cs`, GUI Settings/Privacy panel, CLI `doctor` or `settings`, tests.
  Acceptance: Users can configure retention days for logs/activity/manifests and optionally scrub paths in reports/log exports; a prune command/panel action deletes expired files and reports bytes removed.
  Complexity: M

- [ ] P2 — Fix stale contribution, architecture, and packaging docs
  Why: Top-level docs still describe xUnit 2.9, GitHub Actions workflows, old test counts, and mismatched package placeholder text.
  Evidence: `CONTRIBUTING.md:28,40,97`; `ARCHITECTURE.md:12,154`; `packaging/README.md:32`; `.github/workflows` is absent.
  Touches: `README.md`, `CONTRIBUTING.md`, `ARCHITECTURE.md`, `packaging/README.md`.
  Acceptance: Docs match xUnit v3, local-only build/release flow, current test count, existing release assets, and current packaging placeholders; no current docs instruct contributors to use deleted workflows.
  Complexity: S

- [ ] P2 — Replace Disk Analyzer hardcoded system-drive label
  Why: The toolbar says `Scan C:\` even though DeepPurge already handles the system drive dynamically elsewhere.
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs:226-229`; prior dynamic-drive work in SafetyGuard/disk analyzer.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml.cs` or a small display helper with tests.
  Acceptance: Toolbar displays `Scan System Drive` or the actual runtime system drive; no UI string assumes `C:\`.
  Complexity: S

- [ ] P3 — Add About and update trust cues for hashes, signing, and release source
  Why: Releases ship SHA256SUMS and the app checks GitHub versions, but GUI users do not see local binary hash, signing status, or how to verify downloads.
  Evidence: DeepPurge v0.9.0 release assets; `src/DeepPurge.App/Views/MainWindow.xaml:1408-1429`; DriverStoreExplorer SHA256-verified update flow; BCU certificate/integrity columns.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`, `src/DeepPurge.Core/Security/DigitalSignatureInspector.cs`, update tests if helper logic is added.
  Acceptance: About/Updates shows current executable path, version, signing status, SHA256, latest release URL, and concise verification guidance without needing network for local facts.
  Complexity: M
