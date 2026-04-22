# Changelog

All notable changes to DeepPurge will be documented in this file.

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
