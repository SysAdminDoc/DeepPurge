# Changelog

All notable changes to DeepPurge will be documented in this file.

## [Unreleased]

### Fixed (P0 install-monitor safety)
- **Quarantined Install Monitor V2 replay** - Install traces now use an authoritative pre/post snapshot, capture installer identity before launch, classify created versus modified files, resolve USN parent FRNs without fabricating paths, correlate Sysmon registry events to the installer process tree/time window, preserve explicit HKU SIDs, and block legacy or diagnostic-only manifests from replay. Replay additionally requires a matching SHA256 and stable filesystem identity, and failed deletes no longer count as removed.

### Changed (P1 deletion contract)
- **Unified deletion outcomes and recovery provenance** - File cleanup now passes through one typed executor with preview, Recycle Bin (`IFileOperation` where available), permanent, secure, queued, skipped, failed, and cancelled outcomes. Summaries expose confirmed versus planned counts/bytes, cleanup callers report per-item failures consistently, and manifests record disposition/recoverability only after confirmed mutations.

### Changed (design)
- **DeepPurge Slate operations console** - The WPF shell now uses a bespoke graphite/cyan default theme, denser navigation, explicit local-data posture, searchable program inventory metrics, clearer uninstall hierarchy, responsive panel toolbars, trust/source badges, and calmer loading/status surfaces. Filled action buttons retain WCAG normal-text contrast across every bundled theme.
- **Accurate navigation and empty states** - Collection-backed empty states now update from observable counts, unavailable leftover deletion stays hidden until a scan enables it, and `--target` launches select the Forced Uninstall navigation item that matches the visible workspace.

### Added (P1 diagnostics)
- **Redacted support bundle export** - `deeppurgecli support-bundle --output <zip>` and a GUI About panel action create a privacy-safe diagnostic ZIP containing doctor results, app summary, package-source health, recent redacted activity/logs, and executable trust facts with a final redaction pass that scrubs all user-profile paths.

### Added (P3 health)
- **Actionable and trend-aware health results** - Each health category now exposes a `CommandTarget` for the relevant panel. Score history is stored as JSONL and the GUI/CLI shows whether the score improved, worsened, or stayed stable since the last check.

### Added (P2 privacy)
- **Domain-level cookie preservation** - Cookie whitelist now opens Chromium and Firefox SQLite cookie databases and deletes only non-whitelisted domains instead of skipping the entire database. Dry-run reports preserved/deleted counts per profile. Locked databases degrade with a clear reason. Backups are created before modification.

### Changed (P2 UX)
- **Inline risk preview replaces modal confirmations** - Driver removal, duplicate cleanup, winapp2 execution, bulk uninstall, and deletion manifest restore no longer use blocking MessageBox dialogs. Each action shows risk/count preview in the status bar and proceeds through existing dry-run/recovery paths. A contract test prevents reintroduction.

### Added (P2 browser security)
- **Browser extension permission risk labels** - Chromium and Firefox extension scans now extract manifest permissions and host_permissions, classify them (Low/Medium/High/Critical) for broad host access, sensitive APIs, native messaging, and background activity, and display risk level and labels in the GUI DataGrid.

### Added (P2 trust)
- **Online release checksum verifier** - `deeppurgecli verify-checksum` and a GUI About panel action fetch the latest release `SHA256SUMS.txt` from GitHub, parse the matching asset entry, and show explicit match/mismatch/unavailable status without auto-installing updates.

### Added (P1 trust)
- **Install-manifest replay identity guards** - install-trace manifests now stamp added files with replay identity data and forced-uninstall replay skips missing, changed-size, changed-timestamp, or SHA256-mismatched files instead of deleting by path alone.
- **Project-level dependency audit gate** - release validation now audits Core, App, CLI, and Tests project files directly for outdated and vulnerable NuGet packages without using the failing solution-level `dotnet list` path.
- **Per-item cleanup failure reasons** - Junk and Evidence cleanup summaries now preserve redacted skipped-item reasons, surface them in GUI status/activity history, print and serialize them from CLI clean runs, and test missing-file, protected-path, and command-failure reporting.

### Added (P2 package managers)
- **Source-native package-manager uninstall** - winget, Scoop, and Chocolatey managed rows now uninstall through strict source-specific command builders before falling back to registry uninstall strings. CLI `uninstall --dry-run` previews the exact native command, package-only synthetic rows can be removed without a registry uninstaller, and injection tests cover unsafe package IDs.
- **Release readiness validator** - `Build.ps1` now emits `SHA256SUMS.txt` and can validate version alignment, package release URLs, asset names, manifest hashes, and placeholder removal before publishing winget/Scoop manifests.
- **Package source diagnostics** - `deeppurgecli doctor` now reports winget, Scoop, and Chocolatey source health with versions, roots, package counts, parser status, and remediation hints. GUI enrichment surfaces degraded sources as a warning instead of silently falling back.
- **Shared external process runner** - Core command launch sites now use a runner with `ArgumentList`, timeout/cancellation handling, stdout/stderr caps, exit-status mapping, and redacted command rendering across package managers, repair, schedule, AppX, firewall, service, driver-store, autorun, registry backup/restore, evidence cleanup, system restore, and tray preview paths.
- **Versioned custom cleaner schema** - custom `*.cleaner.json` files can now use a `SchemaVersion: 1` document wrapper with `$schema`, `Rules`, and `Provenance`; legacy root arrays warn with migration guidance, future schema versions block predictably, and `deeppurgecli cleaners schema` prints or exports the bundled JSON schema.

### Added (P2 privacy)
- **Retention and scrub controls** - Settings / Privacy now configures log, activity, and deletion-manifest retention windows plus optional local-path redaction for reports. The GUI can prune expired data, and CLI `settings prune [--dry-run]` reports files, bytes, and activity entries affected.
- **Guarded settings import/export** - Settings files now export as schema-versioned documents with app version and timestamp metadata. CLI `settings import <path> --preview` reports a redacted summary and validation errors without writing, normal imports create rollback backups, and legacy raw `settings.json` files migrate forward under test coverage.
- **Static WPF accessibility/resource checks** - WPF contract tests now fail icon-only controls without automation metadata, missing shared focus indicators, missing empty/error/disabled-state copy, resource keys that drift from `Resources.resx`, and new hardcoded navigation labels without an explicit test allowlist.

### Fixed (P2 docs)
- **Local-only release documentation** - CONTRIBUTING, ARCHITECTURE, README, and packaging guidance now match xUnit v3, the local test suite, current release assets, SHA256 validation, and the absence of GitHub Actions workflows.

### Fixed (P2 reliability)
- **Deletion manifest concurrent access** - manifest writes now use shared file access so GUI/CLI readers and tests cannot briefly lock the daily JSONL file and drop a deletion record.

### Added (P1 safety)
- **Handle-bound registry rollback transactions** - destructive key/value cleanup now opens every registry component without following links, snapshots the exact held object, writes a versioned SHA-256-bound rollback artifact under an Administrators/SYSTEM-only `%ProgramData%` ACL, records write-ahead/final outcomes, and restores only after path, scope, metadata, owner, DACL, and hash validation. Legacy records remain readable but cannot discover or import nearby `.reg` files.
- **GUI deletion recovery panel** - the WPF Safety section now lists deletion manifests, previews valid JSONL entries, runs dry-run restore checks, shows registry/recoverable/unrecoverable counts, and opens the manifest/backup folders.
- **winapp2 update provenance** - `update-winapp2` and the GUI Community Cleaners panel now show local and remote commit/date/hash facts; successful updates save SHA256/byte metadata and back up the previous `winapp2.ini` before replacement.
- **Custom cleaner validation** - `deeppurgecli cleaners validate <file.cleaner.json>` now reports schema errors, unknown fields, risk level, expanded targets, registry scopes, estimates, and blocked rules. CLI cleaner runs skip invalid files, and the GUI Community Cleaners panel shows ready/blocked validation reports for local JSON cleaners.

### Added (premium polish)
- **Intentional empty states and accessible panel actions** - the WPF shell now uses shared empty-state cards for no-result/first-run states across Programs, Safety, System Tools, Winapp2, Schedule, Repair, and History panels. Generated toolbar actions now include tooltips and automation metadata, and Disk Analyzer no longer hardcodes `C:\` in its action label.
- **Settings / Privacy panel** - the GUI now exposes saved AppSettings controls for expert mode, cleanup age guardrails, cookie-domain preservation, excluded cleanup paths, and JSON import/export instead of leaving these controls CLI-only.
- **Scheduled-cleaning creation in the GUI** - the Scheduled Cleaning panel now creates and removes Task Scheduler jobs using constrained cleanup presets, validates HH:MM input inline, and no longer sends users to the CLI for the common workflow.
- **About trust cues** - the About / Updates panel now shows the running executable path, local signing status, SHA256 checksum, and release-verification guidance with refresh and copy actions.
- **Consistent validation feedback** - legacy WPF panel actions now pair selection and input failures with warning toasts instead of relying on status-bar-only messages.

### Fixed (P1 accessibility)
- **Keyboard focus visibility** - shared WPF styles now use a visible accent focus treatment for buttons, inputs, splitters, selectors, tabs, DataGrid cells, and sidebar navigation instead of nulling focus visuals.

### Removed (P1 safety)
- **Hidden free-space wipe primitive** - removed the unused public `SecureDelete.WipeFreeSpaceAsync` volume-fill API. DeepPurge now exposes selected-file and directory secure delete only.

### Fixed (P0 security)
- **Protected helper and original-user process execution** - Elevated launch sites now resolve Windows-owned helpers from absolute System32/Windows paths and reject current-directory, PATH, relative, reparse-point, and unknown bare-name substitutions. winget, Scoop, and Chocolatey resolve only from known install roots and run through a tested Explorer-token broker as the original non-elevated desktop user with separate executable/argument fields, bounded output, safe working directories, and doctor diagnostics. Registered uninstallers no longer fall back to `cmd.exe` for ambiguous paths.
- **Protected Task Scheduler 2.0 actions** - scheduled cleaning now copies the CLI into an administrator-owned, content-addressed ProgramData store, registers executable and arguments as separate Task Scheduler fields, protects task/folder DACLs against same-user edits, exposes principal/action trust diagnostics, and migrates legacy wrappers to disabled dry-run definitions without reading or executing wrapper contents.
- **Firefox extension package containment** - Firefox rows now carry exact profile/package provenance and removal eligibility. Missing, stale, built-in, system, temporary, rooted, traversal, ambiguous, reparse, and drifted entries fail closed; removal recomputes and validates the one direct `{id}` directory or `{id}.xpi` package while preserving profile data.
- **Handle-bound no-follow deletion** - File, tree, secure-erase, internal-artifact, and Recycle Bin workflows now validate final path, object identity, type, reparse status, and operation scope through Windows handles before mutation. Pinned delete handles block target replacement through disposition; protected descendants and root/child junctions fail closed; raw path-based `File.Delete`/`Directory.Delete` calls have been removed from production source.
- **Registry deletion safety funnel** - registry key/value deletes now use one backup-aware helper that validates SafetyGuard, skips registry symlinks, exports a `.reg` backup before deletion, and records deletion manifests only after successful cleanup.
- **Scheduled-cleaning wrapper hardening** - scheduled task wrappers now normalize safe CLI tokens and reject cmd.exe metacharacters, environment expansion, delayed expansion, quotes, and line breaks before writing highest-privilege `.cmd` jobs.
- **GUI winget upgrade launch hardening** - the Programs context-menu upgrade action now launches `winget.exe` directly through a strict package-id command builder instead of interpolating scanner data into `cmd.exe`. Unsafe IDs with spaces, quotes, shell metacharacters, CR/LF, environment expansion, or leading dashes are blocked with a toast and log entry.

### Added (roadmap continuation)
- **Tray icon for scheduled cleaning** — GUI now installs a Windows tray icon with Open, schedule status refresh, background dry-run clean preview, and Exit actions. Minimize/close hides DeepPurge to the tray without interrupting scheduled-cleaning visibility; the tray shows balloon notifications for schedule status and preview results.
- **Chocolatey package discovery** — `PackageManagerScanner` now queries `choco list --local-only --limit-output`, merges matching entries into the installed programs list, and injects Chocolatey-only entries when they are absent from the registry inventory. CLI `list --json` and TSV output include the resulting source/package metadata.
- **OEM bloat scoring** — installed programs now receive an `OemBloatScore` and reason string based on OEM publisher/name signals, support/trial utility terms, and driver/firmware suppression terms. GUI, CLI, CSV, HTML, and JSON exports expose the combined `Flags` value.
- **Orphaned Windows Installer package scan** — Junk Cleaner now scans `%WINDIR%\Installer` for old MSI/MSP files not referenced by active Windows Installer `LocalPackage` registry values. The category is deselected by default and routed through the existing SafetyGuard deletion pipeline.
- **Non-NTFS fallback detection** — `VolumeFileSystem` centralizes `GetVolumeInformationW` checks. `FastDiskAnalyzer` and `DuplicateFinder` report fallback enumeration on ReFS/exFAT/FAT32 or unknown filesystems instead of presenting the NTFS fast path as active.
- **Fluent-style WPF control polish** — shared theme styles now cover Label, GroupBox, GridSplitter, Hyperlink, RichTextBox, DatePicker, and GridView column headers using the active DeepPurge brush resources.

### Tests (roadmap continuation)
- **Package/discovery coverage** — added tests for Chocolatey `--limit-output` parsing, OEM bloat suppression for driver utilities, volume filesystem detection, and MSI/MSP package helper behavior. Test count: 220 → 226.

### Added (P1 trust and safety)
- **Cookie preservation whitelist** — `AppSettings.CookieWhitelist` lets users specify domains (e.g., `github.com`, `google.com`) to preserve during Evidence cleaning. When the whitelist is non-empty, browser cookie database files (Chrome, Edge, Brave, Firefox, Vivaldi, Opera) are skipped. New "Browser Cookies" scan category in EvidenceRemover auto-deselects when a whitelist is active. CLI: `deeppurgecli clean evidence --keep-cookies github.com,google.com`. Settings export/import includes the whitelist.
- **Automated deletion rollback from manifest** — `DeletionManifest` now supports `ListManifests()`, `LoadManifest(date)`, and `RestoreFromManifest(date, dryRun)`. Registry deletions are restored via `reg import` from BackupManager's `.reg` exports. Files are flagged for Recycle Bin recovery. Secure-deleted items are reported as unrecoverable. CLI: `deeppurgecli restore [--date YYYY-MM-DD] [--list] [--dry-run]`.
- **Per-program notes/tags** — `AppSettings.ProgramNotes` dictionary persists notes keyed by program name. `InstalledProgram.Note` property for VM binding. CLI: `deeppurgecli note "Program Name" "keep for compliance"` to set, `--clear` to remove. `list --json` includes notes.

### Added (P3 polish)
- **Activity history clipboard** — `CopyHistoryToClipboard` command added to History panel context menu. Total clipboard copy coverage is now 10 panels (Programs, Junk, Evidence, Autoruns, Services, Drivers, Startup Impact, Duplicates, Orphans, History).

### Fixed (P2 consistency)
- **Error logging parity between GUI and CLI** — `RunInitialScanAsync` catch handler now calls `Log.Error` before setting StatusText, matching all other VM error handlers. Previously the main scan was the only handler that set StatusText without logging.

### Added (P2 CLI)
- **`--json` output for 5 more CLI commands** — `shortcuts`, `snapshot trace`, `schedule list`, `clean`, and `winapp2` (partial) now support `--json` for machine-parseable scripting. Help text updated.

### Fixed (P2 UX)
- **Clipboard commands wired into XAML** — "Copy All to Clipboard" context menu items added to 8 DataGrid panels (Programs, Junk, Evidence, Autoruns, Services, Drivers, Startup Impact, Duplicates). All 9 `CopyXxxToClipboard` VM commands are now reachable via right-click.

### Tests (P1 coverage)
- **25 new tests for round 6 features** — `CookieWhitelistTests` (3 tests: IsCookiePath detection, full-path handling), `DeletionManifestTests` (6 tests: record types, manifest list/load/restore, empty-date fallback), `SysmonReaderTests` (6 tests: availability, path extraction, normalization, dedup, empty input, unavailable fallback), `ProgramNotesTests` (3 tests: notes round-trip, cookie whitelist round-trip, empty defaults). Test count: 195 → 220.

### Fixed (P1 reliability)
- **Replaced 8 remaining empty catch blocks** in `EvidenceRemover`, `SysmonReader`, `DeletionManifest` (2), and `DevDirectoryScanner` (4) with `Log.Warn` calls. All swallowed exceptions now leave a paper trail for field debugging.

### Fixed (P0 audit)
- **DeletionManifest now records registry deletions** — new `RecordRegistry(path, operation)` method wired into `Winapp2Parser`, `CleanerDefinition`, `UninstallEngine`, and `ContextMenuCleaner`. Registry entries now appear in the JSONL manifest, making `RestoreFromManifest` functional for registry operations.
- **Disk analyzer dynamic drive resolution** — `MainViewModel.ScanDiskAsync` now resolves the system drive via `Environment.SpecialFolder.Windows` instead of hardcoding `C:\`. Fixes disk analysis on non-C: Windows installations.
- **DeletionManifest.RecordFile empty catch replaced** with `Log.Warn` so failed size reads leave a paper trail.

### Added (P3 install monitoring)
- **Sysmon event log integration for install monitoring** — `SysmonReader` reads `Microsoft-Windows-Sysmon/Operational` event log for registry change events (IDs 12/13/14) during installer tracing. `TraceInstallV2Async` now captures Sysmon registry changes alongside USN journal filesystem changes, supplementing the before/after registry snapshot with real-time event data. Falls back gracefully when Sysmon is not installed. `deeppurgecli doctor` reports Sysmon availability.

### Added (P3 cleaner ecosystem)
- **16 bundled cleaner definitions for modern apps** — VS Code, Cursor, Windsurf, Discord, Slack, Microsoft Teams, Notion, Obsidian, Figma, Docker Desktop, Zen Browser, Arc Browser, Claude Desktop, WSL caches, Postman, Spotify. Auto-extracted to `DataPaths.Cleaners/bundled-modern-apps.cleaner.json` on first run. Supplements the stale winapp2.ini (last updated Nov 2025). Users can edit or delete the file.

### Added (P2 parity)
- **Copy scan results to clipboard** — 9 new `CopyXxxToClipboard` relay commands across all major scan panels (Programs, Junk, Evidence, Services, Autoruns, Drivers, Startup Impact, Duplicates, Orphans). Copies TSV-formatted text via `System.Windows.Clipboard`. Status bar confirms row count.
- **Digital signature column on installed programs list** — Programs DataGrid now shows a SIGNATURE column (Signed/Unsigned/Revoked/Untrusted/signer CN) via `DigitalSignatureInspector`. Runs WinVerifyTrust in parallel (8 workers) during initial scan, matching the existing autorun/service pattern. CLI `list --json` includes `signatureDisplay` field. Unsigned or revoked programs are a strong signal for bundleware.

### Changed (P2 ecosystem)
- **xUnit v3 migration** — test suite migrated from xUnit 2.9.3 to xUnit.v3 3.2.2. Visual Studio runner updated to 3.1.5. Verify.Xunit replaced with Verify.XunitV3 31.20.0. All 195 tests pass. Stryker.NET config updated for MTP runner compatibility.

### Fixed (P2 silent catch)
- **DeletionManifest logging** — replaced the one remaining empty `catch { }` in `DeletionManifest.Record` with `Log.Warn` so manifest write failures leave a paper trail.

### Fixed (audit pass 2)
- **HKCR registry hive support** — `CleanerDefinition.Execute` and `LeftoverSignatureDb.ScanForOrphans` now handle HKCR (ClassesRoot) registry paths, matching the detection methods that already supported it. Previously, cleaner rules referencing HKCR would pass applicability checks but silently skip registry cleanup.
- **Locked-file recovery wired into all delete paths** — `SafeDeleteFile` (with Restart Manager query and delete-on-reboot fallback) now used by JunkFilesCleaner, EvidenceRemover, CleanerDefinition, Winapp2Parser, DuplicateFinder, UninstallEngine, and InstallSnapshotEngine instead of raw `File.Delete`. Files locked by running processes are now diagnosed and queued for reboot cleanup instead of silently skipped.
- **Path exclusion list hardened** — `SafetyGuard.IsPathSafeToDelete` now catches `ArgumentException` from `Path.GetFullPath` on malformed exclusion paths in `settings.json`, preventing a corrupted config from breaking all safety checks.
- **Target path traversal guard** — `--target` argument in MainWindow rejects paths containing `..` segments before file existence checks.

### Changed (P2 localization)
- **Navigation labels wired to `.resx` resources** — 8 primary navigation labels in MainWindow.xaml now use `{x:Static props:Resources.Nav_*}` bindings instead of hardcoded strings. Adding a `Resources.de.resx` (or any other culture) file will produce a localized sidebar. `xmlns:props` namespace registered in XAML root.

### Added (P2 parity)
- **CLI app discovery unified with GUI enrichment** — `deeppurgecli list` and `uninstall` now call `PackageManagerScanner.EnrichAsync`, including winget, Scoop, portable app, and game-platform sources. List output includes source and package ID columns. Uninstall accepts package IDs in addition to display names and registry key names.
- **CLI `cleaners` command** — `deeppurgecli cleaners list|preview|run [--dry-run]` exposes custom JSON cleaner definitions through the CLI. Lists applicable rules, previews sizes, and runs with dry-run support.
- **BAM remnant discovery wired into orphan scan** — `deeppurgecli orphans --remnants` now includes BAM execution evidence from `AmcacheParser.FindRemnants` alongside signature-based remnant scanning.

### Fixed (P1 trust and recovery)
- **Registry symlink detection repaired** — `IsRegistrySymlink` now reads the key class via `RegQueryInfoKeyW` with a `StringBuilder` buffer instead of treating any API error as a symlink. Normal keys no longer produce false positives.
- **Locked-file recovery wired into delete flows** — `SafeDeleteFile` queries the Restart Manager for locking processes on sharing-violation errors and queues files for delete-on-reboot via `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` as a fallback. All `SafeDeleteDirectory` calls automatically benefit.
- **Shell context-menu `--target` path actionable** — `App.xaml.cs` now parses `--target <path>` from startup arguments. MainWindow navigates to the Forced Uninstall panel with the target name and path pre-populated. Invalid/missing targets show a recoverable warning toast.

### Fixed (P0 safety)
- **GUI junk cleanup routed through shared pipeline** — `CleanJunk_Click` in MainWindow now delegates to `MainViewModel.CleanJunkAsync` instead of deleting files directly. Dry Run, Secure Delete, progress reporting, cancellation, and ActivityLog recording are now honored for all GUI junk cleanup paths.
- **Child-reparse-safe recursive deletion** — New `SafetyGuard.SafeEnumerateFiles`, `SafeEnumerateDirectories`, and `SafeDeleteDirectory` primitives skip child junctions/symlinks during recursive operations. All destructive recursive callers updated: `SecureDelete.WipeDirectory`, `Winapp2Parser`, `CleanerDefinition`, `UninstallEngine.DeleteFileItem`, `EvidenceRemover`, `JunkFilesCleaner`, and `SystemSlimmer`. Prevents a junction under a safe directory from redirecting deletion into unrelated data.

### Fixed (audit pass)
- **CleanerDefinition path traversal hardening** — `DetectFile` paths with `..` segments are now rejected before environment variable expansion. Empty registry subkey names are blocked to prevent attempting hive-root deletion.
- **HealthScorer rounding** — `Math.Round` replaces integer truncation for overall score to prevent score-grade boundary misclassification (74.6 now rounds to 75 = B, not truncates to 74 = C).
- **LockedFileResolver bounds check** — Restart Manager array allocation capped at 1024 entries to prevent unbounded memory allocation from a malicious or corrupted RM response. MoveFileEx failure now logs the Win32 error code.
- **AmcacheParser resource leak and dead code** — Removed unused `Amcache.hve` path check (the parser reads BAM registry data, not the hive file). Fixed `arpKey` disposal with proper `using` statement. Added path-traversal guard (`..` rejection) on expanded registry values. Added null safety in `FindRemnants` LINQ predicate.
- **SystemSlimmer progress reporting** — Failed deletions now correctly report `Skipped = true` in progress callbacks instead of `false`.
- **chkdsk hardcoded C: drive** — `WindowsRepairEngine.ChkDsk` now resolves the system drive dynamically instead of assuming `C:`.
- **USN journal hardcoded C:\** — CLI and GUI install-monitor USN support checks now probe the actual system volume instead of hardcoded `C:\`.

### Changed
- **Target .NET 10 LTS** — All 4 projects migrated from `net8.0-windows10.0.17763.0` to `net10.0-windows10.0.17763.0`. .NET 10 is LTS through Nov 2028 (.NET 8 EOL was Nov 2026). CommunityToolkit.Mvvm upgraded from 8.2.2 to 8.4.2 (adds partial property support). CI workflows updated to .NET 10 SDK. Fixed SYSLIB0057: X509Certificate2 constructor replaced with X509CertificateLoader.

### Security
- **DLL search order hardening** — `SetDllDirectory("")` called in static constructor before any other code, removing the current directory from the DLL search path. Mitigates BleachBit-class CVE-2025-32780 DLL hijack attacks against elevated system utilities. `IncludeNativeLibrariesForSelfExtract` enabled in csproj.
- **Scheduled-task creation hardening (CVE-2025-33067)** — Tasks now run as the current interactive user instead of SYSTEM, mitigating the Batch Logon privilege escalation vector.

### Fixed
- **Dynamic path resolution in SafetyGuard and all cleaners** — Replaced 71 hardcoded `C:\` paths across 8 files (SafetyGuard, JunkFilesCleaner, FileLeftoverScanner, EvidenceRemover, InstallSnapshotEngine, ServiceScanner, FirewallRuleScanner, PathCleaner) with `Environment.GetFolderPath()` and `Environment.SystemDirectory`. Systems with Windows installed on a non-C: drive now have full safety protection and cleaner coverage. Two new test cases validate the dynamic resolution.
- **Replace 57 empty catch blocks with Log.Warn** — All `catch { }` blocks across 20 Core files replaced with `Log.Warn` (for non-fatal failures) or explanatory comments (for intentionally-silent catches in Log.cs, ActivityLog.cs, DataPaths.cs, etc.). Field debugging now has a paper trail for every swallowed exception.
- **Duplicate Spotify entry in leftover-signatures.json** — Removed duplicate Spotify entry (positions 9 and 32). Line 9 retained as it has more complete registry paths.
- **Version-aware shared-path protection in leftover scanner** — When two versions of the same program share an install parent directory (e.g., Blender 4.2 and 4.4 under "Blender Foundation"), the leftover scanner now detects the shared parent and downgrades confidence from Safe to Risky. Prevents accidental deletion of shared settings data (BCU #758).

### Added
- **Global path exclusion whitelist** — `AppSettings.ExcludedPaths` array is checked by `SafetyGuard.IsPathSafeToDelete` before any deletion. Paths in the exclusion list (persisted in `settings.json`) are treated as protected — all scanners and deletion pipelines skip them automatically.
- **Expert/safe mode toggle** — New `AppSettings` infrastructure (`settings.json` persisted via `DataPaths.Config`) with `ExpertMode` toggle on MainViewModel. Default mode can hide dangerous operations (secure delete, advanced scan, registry hunter, service deletion). Setting persists between sessions. Also adds `ExcludedPaths` array for future global path exclusion whitelist.
- **Bundleware / sideload detection** — Programs installed on the same day from a non-trusted publisher that appear as the sole representative of their publisher in that day's installs are flagged as `IsSuspectedBundleware`. Helps users identify software silently installed alongside other programs.
- **Game platform detection (Steam/Epic/GOG)** — New `GamePlatformScanner` parses Steam `appmanifest_*.acf` files across all library folders, Epic Games `*.item` manifests, and GOG Galaxy registry entries. Discovered games appear in the unified programs list with platform badges. Runs in parallel with winget/scoop/portable enrichment.
- **Health dashboard** — New `HealthScorer` assesses system hygiene across 4 categories (Junk Files, Privacy, Startup Impact, Disk Space) with 0-100 scores and A-F grade. VM commands: `RunHealthCheckCommand` with `HealthCategories`, `HealthOverallScore`, `HealthGrade` observable properties.
- **Declarative cleaner format (JSON)** — New `CleanerDefinitionRunner` loads `*.cleaner.json` files from `DataPaths.Cleaners`. Format supports detect (registry), detectFile, files (path + pattern + recurse + removeSelf), and registry rules. SafetyGuard enforcement, dry-run, and progress reporting.
- **Context menu shell integration** — New `ShellExtensionRegistrar` adds/removes a "Uninstall with DeepPurge" right-click menu entry for `.exe` files via HKCU registry. CLI: `deeppurgecli register-shell` / `unregister-shell`.
- **Amcache parsing for remnant discovery** — New `AmcacheParser` reads Windows BAM (Background Activity Moderator) data to find previously-executed binaries. `FindRemnants` cross-references against installed programs to discover orphaned executables.
- **ARM64 build target** — CI workflows (build.yml, release.yml) now use a matrix strategy to publish both `win-x64` and `win-arm64` single-file executables. GitHub Releases include both architectures with platform-suffixed filenames. `dotnet publish -r win-arm64` verified to produce a working binary.
- **System Slimming module** — New `SystemSlimmer` scans ~15 removable Windows components (wallpapers, sample media, help files, patch cache, delivery optimization, WER reports, font cache, log folders) with per-item sizes. Delete through SafetyGuard with dry-run and progress support. VM commands: `ScanSlimmableCommand`, `RunSlimCommand`.
- **Junk growth history tracker** — `ActivityLog.GetCleanHistory` aggregates cleanup runs into daily summaries (date, total bytes freed, run count) for trend visualization. VM exposes `CleanHistory` collection and `CleanHistorySummary` string.
- **Orphan scan without prior uninstall** — New `LeftoverSignatureDb.ScanForOrphans` method checks all 281 signature profiles against the current system to find remnants of programs that were previously uninstalled by other means. CLI: `deeppurgecli orphans --remnants`. Addresses BCU #736 and Ashampoo's "forensic analysis" feature.
- **Leftover signature database expanded to 281 profiles** — Added 231 new application profiles covering gaming, productivity, communication, development, security, media, system utilities, cloud, browsers, design, office, networking, VPN, password managers, and more. Up from 50 profiles.
- **Restart Manager locked-file detection** — New `LockedFileResolver` uses the Windows Restart Manager API (`rstrtmgr.dll`) to identify which processes hold locks on files that can't be deleted. Also provides `QueueDeleteOnReboot` via `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` for stubborn locked files.
- **Portable app detection** — New `PortableAppScanner` discovers standalone executables in `%USERPROFILE%\Desktop`, `%USERPROFILE%\Downloads`, `C:\PortableApps`, and removable drives. Apps are shown with a "Portable" source badge in the programs list. Runs in parallel with winget/scoop enrichment. Only Uninstalr previously offered this capability.
- **Install Monitor 2.0** — USN journal-based filesystem change tracking (`UsnJournalReader`) replaces the before/after snapshot walk. Catches every NTFS file create/modify/rename/delete during an installer run. Falls back to legacy snapshot diff on non-NTFS or when the journal is unavailable. CLI: `--legacy` flag forces the old path.
- **Install Monitor UI** — "Track This Installer" panel in the SYSTEM TOOLS section with program name, installer path, browse button, and trace workflow. Results display inline with upgrade-aware delta.
- **SpecialDetect browser detection** for winapp2.ini — `DET_CHROME`, `DET_FIREFOX`, `DET_OPERA`, `DET_EDGE`, `DET_THUNDERBIRD`, `DET_SAFARI`, `DET_SEAMONKEY`, `DET_WATERFOX`, `DET_PALE_MOON` are now evaluated against real registry keys instead of always returning "applicable". Unknown tokens remain permissive.
- **CSV / JSON export** on drivers, shortcuts, duplicates, and startup-impact panels via `--export <file> --format csv|json` CLI flags. New `GridExporter` in Core.
- **High Contrast theme** — WCAG AAA, pure black background with bright saturated accents (cyan/yellow/green/red). 9th theme in the theme picker.
- **Upgrade-aware snapshots** — `InstallDelta.RemovedFiles` and `RemovedRegistryKeys` now surfaced in both the GUI status line and CLI output. `IsUpgrade` flag labels upgrade vs fresh-install deltas.
- **Activity History tab** — structured JSONL activity log (`ActivityLog.cs`) records every cleanup/repair/snapshot/winapp2 operation. New "History" sidebar panel shows the last 200 entries with timestamp, operation type, summary, items, and bytes freed.
- **Intune/SCCM detection scripts** — `deeppurgecli detection-script --program "Name" [--export file.ps1]` generates a PowerShell detection script for Microsoft Intune or SCCM deployment.
- **Windows toast notifications** — `ToastNotifier` using `Microsoft.Toolkit.Uwp.Notifications`. Scheduled cleaning runs from the CLI now show a Windows toast with the cleanup summary.
- **Screen-reader narration** — `AutomationProperties.Name` and `.HelpText` on all v0.9 SYSTEM TOOLS panels (drivers, startup impact, shortcuts, duplicates, winapp2, repair, schedule, install monitor, about).
- **Localization infrastructure** — `Properties/Resources.resx` with top 20 UI strings and a strongly-typed `Resources.Designer.cs` accessor. Ready for Crowdin submission.

### Security hardening (research round 2)
- **CVE-2025-30399 mitigation** — `TargetLatestRuntimePatch` enabled via `Directory.Build.props` to pin .NET runtime ≥8.0.17.
- **DuplicateFinder thread-safety** — replaced `Dictionary` with `ConcurrentDictionary` for hash cache to prevent data corruption under concurrent scans.
- **Symlink/junction traversal guards** — all recursive deletion paths in FileLeftoverScanner, JunkFilesCleaner, and EvidenceRemover now check `FileAttributes.ReparsePoint` before traversal. `GetDirectorySize` uses `EnumerationOptions.AttributesToSkip`.
- **Registry symlink detection** — `SafetyGuard.IsRegistrySymlink()` checks for `REG_LINK` class before any registry write/delete in UninstallEngine. Prevents TOCTOU privilege escalation via registry symbolic links.
- **NuGet supply chain hardening** — `packages.lock.json` generated for all projects, CI uses `--locked-mode`, NuGet audit enabled at `moderate` level, package source mapping in `NuGet.Config`.
- **Silent catch logging** — 22 empty `catch { }` blocks in RegistryLeftoverScanner replaced with `Log.Warn()` calls for field debugging.
- **ManagementObject disposal** — WMI `ManagementObject` instances in SystemRestoreManager and SecureDelete now properly disposed via `using`.
- **Always-keep protection** — `ProtectedPrograms` persisted list excludes marked programs from batch uninstall. `IsProtected` flag on `InstalledProgram`.
- **External signature loading** — `LeftoverSignatureDb` now loads `*.signatures.json` files from `DataPaths.Cleaners` alongside the embedded database for community contributions.
- **Toast notification migration** — replaced deprecated `Microsoft.Toolkit.Uwp.Notifications` with direct WinRT `Windows.UI.Notifications` API. Removes transitive `System.Drawing.Common` 4.7.0 vulnerability.

### Changed
- TFM updated from `net8.0-windows` to `net8.0-windows10.0.17763.0` across all 4 projects to enable WinRT toast notification APIs.

### Dependencies
- Removed: `Microsoft.Toolkit.Uwp.Notifications 7.1.3` — deprecated, replaced with WinRT API.

### Research-driven additions (competitive analysis pass)
- **Leftover signature database** — embedded JSON database with 50 application profiles (Chrome, Firefox, Adobe, Steam, etc.) for known leftover paths. Signature-matched leftovers are flagged as Safe confidence before heuristic matching runs.
- **Administrator Protection (SMAA) readiness** — `UserIdentity` helper resolves the real interactive user's SID and LocalAppData even when running under Windows 11 SMAA elevation. InstalledProgramScanner and DataPaths use the real user's paths.
- **SafetyGuard path-traversal hardening** — paths containing `..` segments are rejected before normalization. 5 new test cases for traversal patterns.
- **Backup file validation** — `BackupManager` now validates registry backup content (non-empty, starts with `Windows Registry Editor Version 5.00`). Truncated backups log a warning instead of silently passing.
- **True disk footprint** — `InstalledProgram.ActualSizeBytes` computed by walking InstallLocation + AppData + ProgramData paths in parallel. Falls back to registry's EstimatedSizeKB.
- **Hash caching for duplicate finder** — persistent JSON cache keyed by (path, size, mtime). Second scans of the same directories are near-instant.
- **Configurable uninstall timeout** — default increased from 10 to 30 minutes. Settable via `UninstallEngine.UninstallerTimeout` and CLI `--timeout` flag.
- **Winget JSON output** — `PackageManagerScanner` tries `winget list --output json` first, falls back to fixed-width table parsing for older winget versions.
- **Orphaned Package Cache scanner** — `JunkFilesCleaner` scans `C:\ProgramData\Package Cache\` and flags entries whose parent product is no longer installed.
- **USB device history cleaner** — new trace category in `EvidenceRemover` for USBSTOR registry entries and SetupAPI logs.
- **Free space wipe** — `SecureDelete.WipeFreeSpaceAsync()` fills unallocated disk space with random data. Auto-detects SSD vs HDD via WMI MediaType.
- **Recently-installed highlighting** — programs installed in the last 7 days get an accent-colored left border in the Programs DataGrid.
- **IconExtractor WPF decoupling** — moved from Core to App. `InstalledProgram.Icon` changed to `object?`. Core.csproj no longer has `UseWPF=true`.

### Fixed
- `deeppurgecli doctor` now includes suggested fixes for actionable warning/failure paths, including missing system tools, inaccessible registry/shell roots, and unwritable data folders.

### Tests
- **Mutation testing infrastructure** — Stryker.NET 4.15.0 installed as local dotnet tool with `stryker-config.json` targeting SafetyGuard, SecureDelete, UninstallEngine, and DeleteOptions. Run via `dotnet stryker` from repo root.
- **Snapshot testing with Verify.Xunit** — Added Verify.Xunit 31.12.5. Two initial snapshot tests for ProgramExporter CSV and JSON output formats. Snapshot diffs caught automatically in CI.
- Expanded stabilization coverage for `DriverStoreScanner.ParseText`, `InstallSnapshotEngine.Diff`, and `WindowsRepairEngine` command sanitizers.

## [v0.9.0] — Ten-feature competitive pass + headless CLI

### Wide-net completion (post-audit hardening)
- **7 new GUI panels** under a "SYSTEM TOOLS" sidebar section: Driver Store, Startup Impact, Broken Shortcuts, Duplicate Files, Community Cleaners (winapp2), Repair Windows, Scheduled Cleaning, About / Updates. Each panel auto-scans on first navigation; confirmation dialogs gate destructive actions.
- **`deeppurgecli doctor`** — 14-check environment self-test (elevation, OS version, pnputil/schtasks/winget availability, WDI traces, DriverStore, registry access, log writability, snapshot dir, winapp2 cache). Exit 1 on any failure so CI can gate on it.
- **Unit test project** (`tests/DeepPurge.Tests`, xUnit) — **64 tests pass** covering UpdateChecker version-compare (regression tests for the 3-part-vs-4-part bug), Winapp2Parser bucket routing, StartupImpact thresholds, SafetyGuard block/allow lists, ScheduleManager name sanitisation, DataPaths resolution. Wired into the CI workflow.
- **GitHub Actions** — `.github/workflows/build.yml` (CI: build + test + artifact upload on every push/PR) + `.github/workflows/release.yml` (on tag push: build + test + SHA256 + release-asset upload of both exes).
- **winget + Scoop manifests** — `packaging/winget/SysAdminDoc.DeepPurge.yaml` (singleton manifest ready for `wingetcreate update`) + `packaging/scoop/deeppurge.json` (Scoop bucket manifest with autoupdate + pre-install portable-marker hook).
- **Authenticode signing** — `Build.ps1 -Sign` detects signtool.exe under the Windows SDK, supports PFX file + SecureString password, env-var (`DEEPPURGE_CERT_PATH`/`_PASSWORD`), or cert-store thumbprint. Signs both exes with SHA256 + RFC 3161 timestamp and verifies. Fails soft — unsigned builds still ship.
- **Install-manifest replay uninstall** — `MainViewModel.ForcedUninstallByManifestAsync(programName)` loads a previously-captured install delta and replays its deletions through `SafetyGuard` + `DeleteOptions`. Closes the "open-source Revo" loop between snapshot capture and uninstall.
- **3 new XAML value converters** in `Converters/V09Converters.cs`: `BytesToSizeConverter`, `BoolToOldBadgeConverter`, `PathListJoinConverter`.

### Core hardening (audit pass)

Pre-polish audit shipped the following fixes: UpdateChecker version-compare (3-part vs 4-part semver), ScheduleManager quote-escape (now uses per-job `.cmd` wrapper script, no inline quoting), StartupImpactCalculator (namespace-independent XML walk, multi-schema field lookup), Winapp2Parser (DetectOS / SpecialDetect / DetectFile / numbered Detect routed to correct buckets), ShortcutRepairScanner (dedicated STA thread, COM RCW release in `finally`, `SHFileOperation` Recycle Bin), DriverStoreScanner (schema-agnostic XML parsing via `LocalName`, OEM-codepage stdout, InvariantCulture date parse fallback), DuplicateFinder (`ArrayPool<byte>`, sort-safety for missing files), InstallSnapshotEngine (parallel roots via `Task.WhenAll`, gzipped snapshots, pruning to 3-per-program/30-global, atomic JSON write), WindowsRepairEngine (narrow font/icon cache deletes instead of `del /s`, correct console-encoding passthrough), DataPaths (error propagation on portable-enable failure), and the MainViewModel.Extensions HTTP work (shared `HttpClient` with 15s timeout, per-command try/catch with `Log.Error`). Plus a new `Core/Diagnostics/Log.cs` helper that rotates at 5 MB so swallowed exceptions leave a paper trail.

### Original research-driven feature pass

Research-driven feature pass against BCUninstaller, BleachBit, RAPR/DriverStoreExplorer, Czkawka, SophiApp, and the winapp2.ini community database. Every recommendation from the April 2026 competitive-research report landed.

### Added — Core services (`DeepPurge.Core`)
- **`App/DataPaths.cs`** — Single source of truth for per-user data location. Detects `DeepPurge.portable` next to the running exe and redirects every setting / backup / log / snapshot to `./Data/` beside the binary. BCU `PortableSettingsProvider` pattern. `BackupManager`, `ThemeManager`, and `App.xaml.cs` all migrated to use it.
- **`Drivers/DriverStoreScanner.cs`** — `pnputil /enum-drivers /format:xml` (with text-output fallback) parser. Computes FileRepository size per package, groups by `OriginalName`, flags non-latest versions as `IsOldVersion`. `DeleteAsync` routes through `pnputil /delete-driver` with `/force` option. Reference: `lostindark/DriverStoreExplorer` (RAPR).
- **`Startup/StartupImpactCalculator.cs`** — Parses `%SystemRoot%\System32\wdi\LogFiles\StartupInfo\Startup{SID}_*.xml` and classifies each process High / Medium / Low using Microsoft's documented thresholds (3 MB disk / 1000 ms CPU for High; 300 KB / 300 ms for Medium). Pure XML — no undocumented APIs.
- **`Repair/WindowsRepairEngine.cs`** — Wrapper for sfc / DISM (`ScanHealth`, `RestoreHealth`, `StartComponentCleanup`, `ResetBase`) / chkdsk / font & icon cache rebuild / `winget repair` / `msiexec /fa`. Live stdout streaming via `IProgress<string>`. Cancellable. Product-code and winget-ID sanitised.
- **`Shortcuts/ShortcutRepairScanner.cs`** — Walks Desktop + Start Menu (per-user + common) for `.lnk`, parses via `IShellLinkW` + `IPersistFile` COM, classifies Valid / Broken / Unresolved / MsiAdvertised / Store. `SLR_NO_UI` prevents "find target" prompts during bulk scan.
- **`Cleaning/Winapp2Parser.cs`** + `Winapp2Runner` — Parses community `winapp2.ini` cleaner database. Honours `Detect=` / `DetectFile=` applicability gating, `FileKey*` with `RECURSE` / `REMOVESELF` modifiers, `RegKey*` with SafetyGuard enforcement. Auto-downloads from `MoscaDotTo/Winapp2` on first run to `DataPaths.Cleaners`.
- **`FileSystem/DuplicateFinder.cs`** — Three-stage hash: size grouping → XXH3 first-MB head → XXH3 full for collisions. Uses `System.IO.Hashing.XxHash3` (new NuGet dep). Skips `FileAttributes.ReparsePoint` to avoid junction loops. Algorithm lifted from Czkawka.
- **`InstallMonitor/InstallSnapshotEngine.cs`** — **Flagship feature.** Pre/post snapshot diff of Program Files / ProgramData / LocalAppData / AppData + `HKLM\SOFTWARE`, `WOW6432Node`, `HKCU\SOFTWARE` (depth-3 subkey manifest). `TraceInstallAsync` launches an installer, waits for exit + 5s idle, snapshots again, persists the delta as `{name}.manifest.json`. `ReplayRemoveAsync` feeds the manifest back through SafetyGuard for exact-manifest forced uninstall. Closes the #1 feature gap vs Revo.
- **`Schedule/ScheduleManager.cs`** — Creates / lists / removes Task Scheduler jobs under `\DeepPurge\` via `schtasks.exe`. Runs as SYSTEM with highest privileges. `Create`, `Delete`, `List` operations.
- **`Updates/UpdateChecker.cs`** — Hits `GitHub /repos/{owner}/{repo}/releases/latest`, diffs semver, returns `UpdateInfo`. 8-second timeout. Never blocks startup.

### Added — Headless CLI (`DeepPurge.Cli`)
- New `DeepPurgeCli.exe` — separate project, `asInvoker` manifest so it's scriptable from Task Scheduler / PowerShell / cmd without a UAC prompt.
- Commands: `version`, `portable`, `list`, `uninstall`, `clean`, `repair`, `drivers`, `startup-impact`, `shortcuts`, `duplicates`, `snapshot trace`, `winapp2`, `schedule`, `check-update`.
- Exit codes follow BCU convention: `0` ok, `1` general fail, `2` bad args, `13` access denied, `1223` cancelled.

### Added — GUI (`DeepPurge.App`)
- `ViewModels/MainViewModel.Extensions.cs` — Partial class exposing the ten new Core services as `ObservableCollection`s + `[RelayCommand]` methods, ready for XAML panel binding. Async with `_dispatcher.Invoke` marshaling. Observable properties for badges, summaries, live output.

### Changed
- Version bumped `0.8.1` → `0.9.0` across `DeepPurge.Core.csproj`, `DeepPurge.App.csproj`, `DeepPurge.Cli.csproj`, `BUILD.bat`, `Build.ps1`, `README.md`, `App.xaml.cs`.
- `BackupManager.BackupRoot`, `ThemeManager.SettingsFile`, `App.CrashLogDir` now resolve through `DataPaths` — transparently honour portable-mode redirection.
- `Build.ps1` now publishes both `DeepPurge.exe` and `DeepPurgeCli.exe` into `build/`. Cleanup pass spares both exes; drops all side artifacts.
- Solution file adds the `DeepPurge.Cli` project entry + build configs.

### Dependencies
- New: `System.IO.Hashing 8.0.0` — for the duplicate finder's XXH3 hashing. No other new deps.

## [v0.8.1] — UX polish + WizTree-speed disk analyzer

### Added
- **Startup shows a real percentage** — the spinning circle on the loading screen is replaced by a big live "N%" readout plus a determinate progress bar. Each of the 11 scan phases ticks the bar as it finishes so the user can see what's happening instead of just a looping animation.
- **Disk Analyzer now uses WizTree's MFT technique** — new `FastDiskAnalyzer` reads the raw NTFS `$MFT` via `FSCTL_ENUM_USN_DATA` in one sequential sweep, then pulls sizes in a single `FSCTL_GET_NTFS_FILE_RECORD` pass. One warm volume handle replaces millions of random-seek `FindFirstFile` calls. Non-NTFS volumes fall back to a parallel `FindFirstFileExW` walk with the `FIND_FIRST_EX_LARGE_FETCH` hint and `FindExInfoBasic` (skips the 8.3 short-name lookup) — still materially faster than `Directory.EnumerateFiles`. Scan time appears in the status bar.
- **Registry Hunter rewritten along NirSoft RegScanner / Eric Zimmerman lines** — now scans HKLM, HKLM\\WOW6432Node, HKCU, and HKCR in parallel; adds a scope filter (Keys / Value names / Value data); adds optional compiled regex for pattern matching; streams a live hit counter to the UI every 32 matches. Same hit / depth / time caps as before so unbounded searches can't melt the process.

### Fixed
- **Uninstalled programs now disappear from the list immediately** after a successful uninstall. No need to hit Refresh to see the row go away; the underlying engine still honours the registry on rescan so broken-uninstaller cases don't pretend to succeed.

## [v0.8.0] — Competitive feature pass

Research-driven feature pass inspired by BCUninstaller, Revo Uninstaller, BleachBit, PrivaZer, and Sysinternals Autoruns.

### Added
- **Package manager detection (BCU-inspired)** — new `PackageManagerScanner` enriches the installed-programs list with `winget` metadata and injects Scoop apps that don't register with the Windows installer DB. Shows a "winget ↑" badge when an upgrade is available and exposes a context-menu "Upgrade via winget" action that shells out with the package ID.
- **Digital signature validation (Autoruns-inspired)** — new `DigitalSignatureInspector` wraps `WinVerifyTrust` (wintrust.dll) and runs across 8 parallel workers for each autorun/service entry. Every row now has a SIGNATURE column showing the signer's CN, `Unsigned`, `Untrusted`, `Revoked`, or blank when the binary is unreachable.
- **Bulk uninstall (BCU-inspired)** — `UninstallEngine.UninstallBatchAsync` uninstalls every checked program sequentially with silent flags. One restore point is created at the start of the batch (Windows throttles `SRSetRestorePoint`, so one per-program is a bad idea). Wired to a new "Uninstall Selected" button on the Programs toolbar and a context menu item. Confirmation modal warns before proceeding.
- **Silent-switch database (PatchMyPC-inspired)** — new `SilentSwitchDatabase` extends the old family heuristic with vendor-fingerprint overrides (`unins000.exe` → InnoSetup, `au_.exe` → NSIS, `Update.exe` → Squirrel) and flag tables. Used automatically in bulk mode.
- **Registry Hunter (Revo-inspired)** — new `RegistryHunter` walks HKLM, HKCU, and HKCR for arbitrary substrings with per-call hit / depth / time caps. Surfaced in a new sidebar panel with a search box + results grid.
- **Secure delete (BleachBit-inspired)** — new `SecureDelete` does a single-pass cryptographic-random overwrite + opaque rename + delete. Multi-pass DoD-style wipes are intentionally skipped (obsolete on SSDs, waste write cycles). Toggled via a status-bar checkbox that applies to junk, evidence, and leftover deletion.
- **Dry-run / Preview mode (BleachBit-inspired)** — new `DeleteOptions.DryRun` flag threads through every destructive pipeline. When enabled, scanners enumerate and size items but skip the actual delete. Status bar shows "Would free X" instead of "Freed X". Progress bar still animates so the user can confirm the preview ran.
- **Live progress bars for every long-running delete** — new `DeleteProgress` record + `IProgress<T>` wiring through junk cleaning, evidence cleaning, leftover deletion, and bulk uninstall. Status bar shows current item, `(n/total)`, and running byte count.
- **Registry Backups panel** — sidebar entry that opens `%LocalAppData%\DeepPurge\Backups\` so users can inspect, import, or prune the `.reg` exports the engine creates before every destructive registry op.

### Fixed (v0.7 follow-ups)
- **F1** Bare deletion loop moved out of the view into `JunkFilesCleaner.DeleteJunkSafe` with SafetyGuard enforcement, progress, and dry-run support. The view now just awaits the VM.
- **F2** Leftover deletion exposes full progress via `DeleteLeftoversAsync(..., DeleteOptions, IProgress<DeleteProgress>, CancellationToken)`. The old `(int, int)` overload is preserved for compatibility.
- **F3** Build.ps1 analyzer warnings fixed: `Ensure-DotNetSDK` → `Confirm-DotNetSDK` (approved PowerShell verb), unused `$cleanOutput` removed.
- **F4** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` verified clean — 66 MB single-file exe produced.
- **F5** Forced-scan leftover count is reported in the status bar + toast *before* delete so users see the blast radius; delete itself is already confirmation-gated via the Delete Selected button.

### Changed
- `InstalledProgram.SourceDisplay` now shows `winget ↑` when an upgrade is available from a package manager, trumping the raw registry hive label.
- `AutorunScanner` populates Publisher from the certificate subject when the registry omits it — matches how Autoruns presents unsigned vendor binaries.
- `ServiceScanner` now resolves `\SystemRoot\` and relative `system32\...` paths before signature check, eliminating false "Missing" reports on core Windows services.
- Default initial-scan no longer auto-selects orphaned services or tasks; bulk-operation opt-in is now explicit.

### Removed
- Nothing removed. All v0.7 APIs retained (with additive overloads).

## [v0.7.0] — Production hardening pass

### Fixed (critical)
- **UninstallEngine.BuildUninstallerStartInfo** — rewrote the command parser. The previous `ParseCommand` returned the *entire* command as `FileName` when the exe token had no backslash (e.g. `unins000.exe /S`), which caused `Process.Start` to fail for most NSIS/InnoSetup uninstallers. Unquoted paths with spaces now route through `cmd /c` so Windows parses them correctly.
- **AutorunScanner.DisableAutorun** — "Disable" previously deleted the Run value outright, so disabling an autorun entry and closing DeepPurge lost the command forever. Now uses the `StartupApproved\Run` flag pattern (same mechanism as Task Manager's Startup tab) so disable is truly reversible.
- **EvidenceRemover** — removed Jump-Lists double-counting: `ScanRecentDocuments` no longer enumerates the same `AutomaticDestinations` files that `ScanJumpLists` manages as a directory.
- **ServiceScanner.IsOrphanedService** — no longer flags legit system services with NT-style paths (`\SystemRoot\...`, `system32\...`) as orphaned. Resolves against `%SystemRoot%` before `File.Exists`.
- **IconExtractor** cache keys now use `\0` separators so paths containing `|` cannot collide.

### Fixed (high)
- **ScheduledTaskScanner** — removed dead code; `Get-ScheduledTaskInfo` now receives both `-TaskName` and `-TaskPath`; DateTime fields render correctly across PowerShell versions.
- **BackupManager** — registry paths are strictly validated before being passed to `reg.exe export` (defense in depth against injection via weird key names); filenames are sanitized.
- **WindowsAppManager** — `PackageFullName` is validated against a tight charset before being embedded in a PowerShell `Remove-AppxPackage` command.
- **MainViewModel** — dispatcher is now resolved via `Application.Current.Dispatcher` so the VM is constructible outside the UI thread; icon back-fill has its own cancellation token and is cancelled on refresh/close.
- **MainWindow** reuses the shared `UninstallEngine` from the VM instead of spawning fresh instances per click (removes leaked event subscriptions).
- **App.xaml.cs** wires up `DispatcherUnhandledException`, `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` — crashes now write a log to `%LocalAppData%\DeepPurge\Logs\` and the app survives dispatcher exceptions.

### Added (v0.7)
- Five new themes to match the README claim: Catppuccin Mocha (dark default), OLED Black, Dracula, Nord Polar, GitHub Dark. Theme choice persists to `%LocalAppData%\DeepPurge\theme.txt`.

## [v0.3.0]
- ci: add build and release workflow
- Initial uploaded drop

## Roadmap archive — 2026-08-10 — ROADMAP.md

<details>
<summary>Original roadmap snapshot</summary>

```markdown
# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Active items

No active implementation items.

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** - obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** - user preference
- **Feature flags / A-B gating** - overkill for a local desktop tool
- **Cloud sync of settings** - privacy surface without clear value
- **MSIX distribution** - sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app

## Research-Driven Additions

### P0

- [ ] P0 — Quarantine Install Monitor V2 replay until provenance is trustworthy
  Why: USN leaf names are converted to incorrect root paths, volume-wide modifications are treated as installer-created, fallback snapshots occur after installation, and Sysmon events are not safely attributed.
  Evidence: `src/DeepPurge.Core/InstallMonitor/UsnJournalReader.cs`; `src/DeepPurge.Core/InstallMonitor/InstallSnapshotEngine.cs`; `src/DeepPurge.Core/InstallMonitor/SysmonReader.cs`; Windows `USN_RECORD_V3` documentation; cited academic uninstaller/remover study.
  Touches: `src/DeepPurge.Core/InstallMonitor/`, manifest schemas, trace/replay WPF and CLI, injected install-monitor tests
  Acceptance: Until a new eligible manifest is produced, tracing falls back to the pre/post V1 path and V2 data is diagnostic-only; the replacement resolves parent FRNs, captures pre-launch identity, correlates the installer process tree/time window, distinguishes create from modify, preserves HKU SID/hive, and permits replay only for created objects whose identity still matches.
  Complexity: L

### P1

- [ ] P1 — Centralize deletion policy, outcomes, and success-only provenance
  Why: `UseRecycleBin`, dry-run, secure mode, result counts, and manifests mean different things across pipelines, so permanent or failed operations can be reported as recoverable success.
  Evidence: `DeleteOptions`; `UninstallEngine`; `DuplicateFinder`; `EmptyFolderScanner`; `SystemSlimmer`; `CleanerDefinition`; `Winapp2Parser`; `MainWindow.xaml.cs`; Sifty/Mole recoverable-default and audit designs.
  Touches: `src/DeepPurge.Core/Safety/`, every destructive Core pipeline, WPF/CLI result surfaces, activity/recovery manifests, tests
  Acceptance: One typed executor exposes preview, Recycle Bin, permanent, secure, queued, skipped, failed, and cancelled outcomes; ordinary user-file cleanup defaults to `IFileOperation` recycling where supported; only confirmed operations enter manifests/counts; all existing deletion callers and per-item cleanup-failure details use the same contract.
  Complexity: XL

- [ ] P1 — Add ownership-conflict gates to leftover attribution
  Why: Name/publisher/install-location heuristics can attribute shared or adversarial paths to the wrong program and offer unrelated data for deletion.
  Evidence: `src/DeepPurge.Core/FileSystem/FileLeftoverScanner.cs`; `src/DeepPurge.Core/Registry/RegistryLeftoverScanner.cs`; `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`; cited academic uninstaller/remover study.
  Touches: file/registry leftover scanners, installed-product index, MSI/package ownership adapters, candidate evidence models, preview WPF/CLI, tests
  Acceptance: Candidates carry each supporting signal and conflicts against other installed products/components; paths claimed by another product, Windows, or weak single-source metadata are protected/review-only; crafted metadata pointing at another app or Windows directory never becomes auto-removable.
  Complexity: L

- [ ] P1 — Route non-file administrative mutations through reversible safety policy
  Why: Firewall, PATH, service/autorun, and scheduled-task actions bypass existing guards, omit backups/refresh notifications, or expose unsupported mutations.
  Evidence: `src/DeepPurge.Core/Firewall/FirewallRuleScanner.cs`; `src/DeepPurge.Core/Shell/PathCleaner.cs`; `src/DeepPurge.Core/Startup/AutorunScanner.cs`; `SafetyGuard.cs`; scheduled-task action handlers.
  Touches: safety and operation-ledger services, firewall/PATH/autorun/service/task modules, WPF/CLI actions, tests
  Acceptance: Every mutation runs a matching protection rule, records before/after state and rollback, reports exact outcome, sends required system refresh notifications, and disables UI/CLI actions for unsupported source types; protected services/rules/PATH entries cannot be changed through production code.
  Complexity: L

- [ ] P1 — Model removal capability and uninstaller trust explicitly
  Why: Portable/game discoveries can report successful uninstall without an action, while selected HKCU/HKU uninstall strings cross into the elevated process without visible trust facts.
  Evidence: `src/DeepPurge.Core/Packages/PortableAppScanner.cs`; `src/DeepPurge.Core/Packages/GamePlatformScanner.cs`; `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`; `src/DeepPurge.App/app.manifest`; BCUninstaller capability/risk patterns.
  Touches: installed-program models/scanners, `UninstallEngine`, signature/path-owner inspection, WPF/CLI program rows and results, tests
  Acceptance: Each row declares `NativeUninstaller`, `PackageManager`, `PortableFolder`, `GameLauncher`, or `Unsupported` capability with source identity and trust facts; unsupported actions are disabled; no skipped action reports success; executable path, owner/publisher, arguments, and risk are reviewable before elevated execution.
  Complexity: M

- [ ] P1 — Revalidate duplicate identity and require an explicit keeper policy
  Why: Duplicate groups can drift between hashing and deletion, and the implicit age-based keeper gives users no per-group or reference-folder control.
  Evidence: `src/DeepPurge.Core/FileSystem/DuplicateFinder.cs`; Czkawka reference-folder, keeper, and saved-selection patterns.
  Touches: duplicate models/scanner/deleter, duplicate WPF/CLI, operation results, tests
  Acceptance: Every candidate is re-statted and fully re-hashed immediately before removal; any changed group aborts safely; users can select the keeper or a protected reference folder; no group is deleted without one retained identity; failed/skipped counts are exact.
  Complexity: M

- [ ] P1 — Export and bind a rollback package before driver deletion
  Why: DeepPurge removes driver-store packages without the export/backup workflow exposed by Windows and DriverStoreExplorer.
  Evidence: `src/DeepPurge.Core/Drivers/DriverStoreScanner.cs`; DriverStoreExplorer; Microsoft PnPUtil and DISM driver-export documentation.
  Touches: driver scanner/removal service, backup and operation manifests, driver WPF/CLI, tests
  Acceptance: Each selected driver is exported before deletion, the exported files and INF identity are hashed into the operation ledger, export failure blocks removal, protected/excluded packages remain pinned, and the UI/CLI exposes a tested reinstall/rollback command.
  Complexity: M

- [ ] P1 — Make cleaner-definition updates diffable and rollback-safe
  Why: Elevated user-writable winapp2/JSON rules can regress into deleting configuration or package-manager state, as demonstrated by the cited winapp2 corrections.
  Evidence: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs`; `src/DeepPurge.Core/Cleaning/Winapp2Parser.cs`; `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`; Winapp2 PRs 1004 and 945; Kudu data-rule design.
  Touches: cleaner schemas/loaders/updater, `DataPaths.Cleaners`, validation CLI, cleaner preview WPF, regression fixtures
  Acceptance: Every rule set records schema, origin, version, SHA-256, and trust state; updates show expanded-target diffs, quarantine invalid/unsafe rules, preserve a last-known-good version, and pass fixtures proving protected app settings and the winget pin database survive.
  Complexity: M

- [ ] P1 — Propagate typed partial-scan and degraded-source results
  Why: Swallowed enumeration/process errors currently look identical to zero findings and make the GUI, CLI JSON, logs, and support data overstate confidence.
  Evidence: scheduled-task, firewall, PATH, autorun, context-menu, health, package-enrichment, and initial-scan code paths; UniGetUI source diagnostics and Mole JSON/result patterns.
  Touches: shared scan contracts, affected scanners, `MainViewModel`, CLI JSON/text, activity log, doctor/support bundle, tests
  Acceptance: Each multi-source scan returns items plus failed sources, warnings, duration, cancellation, and degraded status; one source failure does not discard successful peers; all user and diagnostic surfaces distinguish clean, partial, failed, timed-out, and cancelled states.
  Complexity: M

- [ ] P1 — Pin dependency/toolchain inputs and close the SQLite maintenance gap
  Why: The build permits unlocked restore and mutable SDK bootstrap, source mapping is incomplete, and the elevated process retains an older SQLite engine despite the cited SQLite 3.53.4 and .NET 10.0.10 maintenance/security releases.
  Evidence: `Build.ps1`; `NuGet.Config`; project and lock manifests; SQLite CVE/release pages; SQLitePCLRaw 3.0.5; .NET 10.0.10 release notes; NuGet locked-restore/source-mapping documentation.
  Touches: project package references, all `packages.lock.json`, `NuGet.Config`, `Build.ps1`, SDK bootstrap/version files, release validation tests/docs
  Acceptance: Microsoft.Data.Sqlite/.NET libraries resolve to 10.0.10 and SQLitePCLRaw to a 3.53.4-backed release; all packages map to explicit trusted sources; restore runs locked by default and fails on drift; SDK/bootstrap content is version/hash pinned; the default release path runs tests, dependency audit, and validation offline from the lock/cache.
  Complexity: M

### P2

- [ ] P2 — Enforce a capability-to-surface and documentation contract
  Why: Health Dashboard/System Slimming are advertised but unreachable, `ExpertMode` governs no behavior, some enrichment changes do not notify WPF, and architecture/status notes have drifted from implementation.
  Evidence: `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`; `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`; `src/DeepPurge.Core/Cleaning/SystemSlimmer.cs`; `src/DeepPurge.Core/App/AppSettings.cs`; `src/DeepPurge.App/Views/MainWindow.xaml`; `src/DeepPurge.Cli/Program.cs`; `README.md`; `CLAUDE.md`.
  Touches: capability registry, WPF/CLI bindings, observable program models, README/architecture/status docs, contract tests
  Acceptance: A test-generated matrix maps every advertised capability and setting to a reachable GUI/CLI surface or an explicit unsupported state; Health/System Slimming are wired safely or removed from claims; relevant model changes notify the UI; release validation fails on stale command, version, test-count, privilege, or capability documentation.
  Complexity: M
```

</details>
