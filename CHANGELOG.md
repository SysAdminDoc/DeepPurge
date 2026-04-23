# Changelog

All notable changes to DeepPurge will be documented in this file.

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
