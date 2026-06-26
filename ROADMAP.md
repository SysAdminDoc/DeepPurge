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

### P0 — Safety and trust

- [ ] P0 — Harden FirewallRuleScanner PowerShell injection
  Why: `EscapePs()` only escapes single quotes; backticks, semicolons, and `$()` in rule names can inject commands
  Evidence: `src/DeepPurge.Core/Firewall/FirewallRuleScanner.cs:197` — compare with BleachBit CVE-2026-55567 (arbitrary deletion via privileged cleaning)
  Touches: `FirewallRuleScanner.cs` (EscapePs method + all PowerShell command construction)
  Acceptance: Rule names containing `'; Remove-Item C:\Windows -Recurse; '` are safely escaped; unit test proves it
  Complexity: S

- [ ] P0 — Route AutorunScanner file deletion through SafeDeleteFile
  Why: `DeleteAutorun()` uses raw `File.Delete(entry.Command)` — a malicious registry autorun entry pointing to a critical file bypasses SafetyGuard
  Evidence: `src/DeepPurge.Core/Startup/AutorunScanner.cs:424-427`
  Touches: `AutorunScanner.cs` (DeleteAutorun method, lines 424-427 and DisableAutorun line 391)
  Acceptance: Deleting an autorun entry whose Command points to a SafetyGuard-protected path is blocked
  Complexity: S

- [ ] P0 — Fix ActivityLog.Prune() race condition
  Why: Prune reads all lines then writes back without holding `_lock`; entries appended by concurrent `Record()` calls between Read and Write are lost
  Evidence: `src/DeepPurge.Core/Diagnostics/ActivityLog.cs:72-83` — `Record()` uses `_lock` (line 23) but `Prune()` does not
  Touches: `ActivityLog.cs` (Prune method)
  Acceptance: Concurrent Record + Prune calls in a test never lose entries
  Complexity: S

### P1 — Reliability and quality

- [ ] P1 — Add timeout and cancellation to HealthScorer
  Why: `AssessJunk()`, `AssessPrivacy()`, `AssessStartup()` invoke full-system scanners without timeout; if any scanner hangs, the health dashboard hangs indefinitely
  Evidence: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs:39,68,97` — no CancellationToken parameter
  Touches: `HealthScorer.cs` (all Assess methods), `MainViewModel.Extensions.cs` (health dashboard command)
  Acceptance: Health scan completes or times out within 30s; UI remains responsive during scan
  Complexity: M

- [ ] P1 — Async UI operations in MainWindow code-behind
  Why: `DeleteEmptyFolders_Click` and services panel operations run synchronous file/registry I/O on the UI thread, causing freezes on large datasets
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs` — synchronous loops with `Directory.Delete`, `SafeDeleteFile`, service disable calls
  Touches: `MainWindow.xaml.cs` (DeleteEmptyFolders_Click, services panel handlers)
  Acceptance: UI stays responsive during bulk empty-folder deletion and service operations; progress bar updates during execution
  Complexity: M

- [ ] P1 — Fix PathCleaner split inconsistency
  Why: Scan path splits PATH with `RemoveEmptyEntries` but clean path splits with `None`, potentially leaving stray semicolons in the registry value
  Evidence: `src/DeepPurge.Core/Shell/PathCleaner.cs` — compare scan vs clean split options
  Touches: `PathCleaner.cs` (RemoveOrphanedEntries method)
  Acceptance: After removing orphaned PATH entries, the resulting PATH string has no leading, trailing, or consecutive semicolons
  Complexity: S

- [ ] P1 — Decouple CLI from ToastNotifier
  Why: `Program.cs:177` calls `ToastNotifier.ShowCleaningSummary()` — a WPF-coupled API from the headless CLI binary; breaks layering and will fail silently if WPF assemblies aren't loaded
  Evidence: `src/DeepPurge.Cli/Program.cs:177`
  Touches: `Program.cs` (clean command handler), potentially add `INotifier` interface in Core
  Acceptance: CLI clean command reports summary to stdout without referencing any WPF type
  Complexity: S

### P2 — Competitive features

- [ ] P2 — Developer directory scanner
  Why: `node_modules`, `venv`, `.gradle`, `bin/obj/`, `target/`, `.next`, `__pycache__` directories consume tens of GB on developer machines; BleachBit 6.0.1 added this and it's a top community request
  Evidence: BleachBit v6.0.1 changelog; r/sysadmin and r/windows threads requesting dev cleanup
  Touches: New `Core/FileSystem/DevDirectoryScanner.cs`, wire into CLI (`deeppurgecli clean dev`) and GUI (Cleanup panel)
  Acceptance: Scan finds and sizes all matching directories under a given root; delete with SafetyGuard enforcement; dry-run support
  Complexity: M

- [ ] P2 — Age-based file retention
  Why: "Delete temp files only if older than N days" is the most-requested safety feature across BleachBit (#1957, 6 reactions), Reddit, and forum threads; prevents cleaning files an active process still needs
  Evidence: BleachBit issue #1957 (scheduled for 6.1.0); community complaints about losing recent temp files
  Touches: `JunkFilesCleaner.cs`, `EvidenceRemover.cs`, `Winapp2Runner` — add `MinAgeDays` parameter to clean pipelines; `AppSettings` for per-category defaults
  Acceptance: Junk clean with 7-day retention skips files modified within the last 7 days; configurable per category in settings
  Complexity: M

- [ ] P2 — Right-click-to-exclude from scan results
  Why: FluentCleaner's UX pattern — when viewing leftover/junk scan results, right-clicking a path to permanently whitelist it saves users from repeatedly deselecting the same false positive
  Evidence: FluentCleaner v26.06.01 (global exclusions with right-click); SafetyGuard exclusion infrastructure already exists in AppSettings.ExcludedPaths
  Touches: `MainWindow.xaml` (DataGrid context menus for leftover/junk/evidence panels), `MainWindow.xaml.cs` or `MainViewModel.cs` (command to add path to ExcludedPaths)
  Acceptance: Right-clicking a scan result and choosing "Exclude" adds the path to AppSettings.ExcludedPaths; future scans skip it
  Complexity: S

- [ ] P2 — Duplicate directory detection
  Why: Czkawka's most-requested feature (19+14 upvotes across #676 and #1182, still unshipped); folder-level "these two directories are 97% identical" is more actionable than file-level duplicates for cleanup
  Evidence: Czkawka issues #676, #1182; no competitor ships this
  Touches: `Core/FileSystem/DuplicateFinder.cs` (add directory-level grouping after file-level hash pass)
  Acceptance: Given two directories with identical contents, they appear as a duplicate group with size and match percentage
  Complexity: L

### P3 — Forward-looking

- [ ] P3 — CommunityToolkit.Mvvm partial properties migration
  Why: 8.4.x ships a one-click VS code fixer to migrate `[ObservableProperty]` field annotations to partial property declarations — the recommended pattern going forward, improves AOT compatibility
  Evidence: CommunityToolkit.Mvvm 8.4.0 announcement; 16 new analyzers enforce proper usage
  Touches: `MainViewModel.cs`, `MainViewModel.Extensions.cs` — all `[ObservableProperty]` fields
  Acceptance: All observable properties use partial property pattern; no analyzer warnings; build clean
  Complexity: M

- [ ] P3 — WCAG 2.2 accessibility pass
  Why: InstallerClean (same stack — WPF/.NET 10) ships Narrator support, Voice Access, reduced-motion, and keyboard-only operation. DeepPurge's HighContrast theme exists but doesn't map to SystemColors or meet the 2px/3:1 focus indicator requirement
  Evidence: InstallerClean v1.9.0 accessibility release notes; WCAG 2.2 SC 1.4.11 and SC 2.4.11
  Touches: HighContrast theme ResourceDictionary (map brushes to SystemColor*Color), all interactive controls (automation names, focus indicators), MainWindow (reduced-motion check via `SystemParameters`)
  Acceptance: App is fully operable via keyboard and Narrator in all four Windows contrast themes; focus indicators meet 2px/3:1 contrast
  Complexity: L

- [ ] P3 — Publish independent accuracy benchmark
  Why: Uninstalr's 2026 benchmark is the industry standard — BCU scored 61.33%, HiBit 89.9%, Uninstalr 94.33%. Publishing DeepPurge's results with transparent methodology (same test apps, screen recordings) builds credibility and identifies scan gaps
  Evidence: Uninstalr 2026 benchmark methodology; DeepPurge's leftover scanner + signature DB + install monitor should exceed 90%
  Touches: No code changes — test methodology document + results. May drive accuracy improvements in `RegistryLeftoverScanner`, `FileLeftoverScanner`, `LeftoverSignatureDb`
  Acceptance: Reproducible benchmark against the same 8 test apps shows DeepPurge's leftover accuracy > 90%
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
