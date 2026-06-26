# Changelog

All notable changes to DeepPurge will be documented in this file.

## [Unreleased]

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
