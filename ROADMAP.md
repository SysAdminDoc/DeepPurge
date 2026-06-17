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

## Research-Driven Additions (Round 2)

### P0 — Security hardening (act now)

- [ ] P0 — **Pin .NET runtime ≥8.0.17 for CVE-2025-30399**
  Why: Untrusted search path RCE affects .NET 8.0 ≤8.0.16. DeepPurge runs elevated — this is a privilege escalation vector.
  Evidence: NVD CVE-2025-30399 (CVSS 7.5); .NET security advisory June 2025.
  Touches: All 4 `.csproj` files — add `<RuntimeFrameworkVersion>8.0.17</RuntimeFrameworkVersion>` or target net10.0
  Acceptance: `dotnet --info` shows runtime 8.0.17+. CI build pins the minimum version.
  Complexity: S

- [ ] P0 — **Fix DuplicateFinder._cache thread-safety**
  Why: `_cache` dictionary at `Core/FileSystem/DuplicateFinder.cs:41` is accessed without synchronization. Concurrent `FindAsync` calls corrupt the cache.
  Evidence: Code review — `Dictionary<string, HashCacheEntry>` with no lock around reads/writes in TryGetCachedHead, TryGetCachedFull, UpdateCache.
  Touches: `Core/FileSystem/DuplicateFinder.cs`
  Acceptance: Replace `Dictionary` with `ConcurrentDictionary` or add lock around all cache access. Verify with concurrent scan test.
  Complexity: S

- [ ] P0 — **Symlink/junction traversal guard in deletion paths**
  Why: File deletion in FileLeftoverScanner, JunkFilesCleaner, EvidenceRemover does not check for reparse points before recursive deletion. BleachBit shipped a CVE-class fix for this exact pattern. DuplicateFinder.SafeEnumerate already has the guard but other scanners don't.
  Evidence: BleachBit 6.0 release notes (symlink/junction safety); FluentCleaner reparse point skip.
  Touches: `Core/FileSystem/FileLeftoverScanner.cs`, `Core/FileSystem/JunkFilesCleaner.cs`, `Core/Privacy/EvidenceRemover.cs`
  Acceptance: All recursive directory deletion checks `FileAttributes.ReparsePoint` before traversal. Add test with a junction-loop directory structure.
  Complexity: S

- [ ] P0 — **Registry symlink detection before writes**
  Why: Elevated process writes to enumerated registry keys without checking for symlinks. Unprivileged attacker can redirect writes to critical system keys via registry symbolic links. CVE-2025-6231 and CVE-2026-20815 exploited this exact pattern.
  Evidence: Security research — registry TOCTOU privilege escalation; Project Zero Windows Administrator Protection bypass.
  Touches: `Core/Registry/RegistryLeftoverScanner.cs`, `Core/Uninstall/UninstallEngine.cs`
  Acceptance: Before any registry write/delete, verify key is not a symlink (REG_LINK class check). Fail-closed: if check fails, skip the key and log warning.
  Complexity: S

- [ ] P0 — **NuGet supply chain hardening**
  Why: No lock file, no audit, no package source mapping. Dependency confusion and typosquatting attacks possible.
  Evidence: .NET supply chain security best practices; NuGet audit (NU1901-NU1904 warnings).
  Touches: All `.csproj` files (add `RestorePackagesWithLockFile`), `NuGet.Config` (add `packageSourceMapping`), `.github/workflows/build.yml` (add `--locked-mode`)
  Acceptance: `packages.lock.json` committed. CI build uses `--locked-mode`. `NuGetAudit` enabled at `moderate` level. Package sources mapped explicitly.
  Complexity: S

### P1 — Accuracy + platform currency

- [ ] P1 — **Expand leftover signature database to 200+ profiles**
  Why: Current 50 profiles cover common apps but benchmark accuracy requires broader coverage. Target: ≥85% accuracy (≤61 leftovers out of 406 artifacts) to reach top-3 in Uninstalr benchmark.
  Evidence: Uninstalr 2026 benchmark — HiBit 90%, Total Uninstall 86%; Revo Logs Database covers 12.5k programs. Also fix: duplicate Spotify entry in current DB.
  Touches: `Core/Data/leftover-signatures.json`, `Core/Data/LeftoverSignatureDb.cs`
  Acceptance: 200+ profiles. Matching algorithm enhanced to handle publisher-based grouping (e.g., all Adobe products share Adobe paths). Duplicate Spotify entry removed.
  Complexity: M

- [ ] P1 — **Portable app detection**
  Why: Only Uninstalr detects portable apps (Notepad++ Portable, Brave Portable). All 8 other tested tools scored 0. Significant unmet demand.
  Evidence: Uninstalr 2026 benchmark — portable app detection section; BCU's folder scan concept in Ideas list.
  Touches: New `Core/Packages/PortableAppScanner.cs`, `Core/Packages/PackageManagerScanner.cs`
  Acceptance: Scan common portable locations (Desktop, Downloads, USB roots, PortableApps.com folder) for executables without matching registry entries. Show as "Portable" source in program list.
  Complexity: M

- [ ] P1 — **Target .NET 10 LTS instead of .NET 9 STS**
  Why: .NET 9 STS ends Nov 2026 (too soon). .NET 10 is LTS through Nov 2028. CommunityToolkit.Mvvm 8.4.2 partial properties require .NET 9+ SDK but target any runtime. Includes all .NET 9 benefits plus WPF improvements.
  Evidence: .NET 10 release notes; .NET 9 STS EOL Nov 2026; breaking changes audit (empty Grid defs, DynamicResource crashes, P/Invoke search path changes).
  Touches: All 4 `.csproj` files — TFM `net10.0-windows10.0.17763.0`. Review P/Invoke DLL loading paths for single-file compatibility.
  Acceptance: Build succeeds on net10.0, all tests pass. Note: this supersedes the existing ".NET 9" ROADMAP item — implementer should do .NET 10 directly.
  Complexity: M

- [ ] P1 — **Migrate toast notifications from deprecated package**
  Why: `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 is archived/unmaintained. No security patches. Windows App SDK `AppNotificationManager` is the supported replacement.
  Evidence: NuGet package status (archived); Microsoft migration guidance.
  Touches: `Core/Diagnostics/ToastNotifier.cs`, `Core/DeepPurge.Core.csproj`
  Acceptance: Toast notifications work on Windows 10 1809+ and Windows 11. Remove `Microsoft.Toolkit.Uwp.Notifications` NuGet reference.
  Complexity: S

### P2 — Reliability + developer experience

- [ ] P2 — **Add logging to silent catch blocks in RegistryLeftoverScanner**
  Why: 16+ `catch { }` blocks with no logging make field debugging impossible. 88 total silent catches across Core.
  Evidence: Code review — `Core/Registry/RegistryLeftoverScanner.cs` has the highest density.
  Touches: `Core/Registry/RegistryLeftoverScanner.cs`, other Core files with silent catches
  Acceptance: All catch blocks in RegistryLeftoverScanner call `Log.Warn()` with the exception message. Other high-density files (FileLeftoverScanner) follow suit.
  Complexity: S

- [ ] P2 — **Fix ManagementObject disposal leak**
  Why: `SystemRestoreManager.cs` line 48: WMI `ManagementObject` instances in foreach are never disposed. COM object leak.
  Evidence: Code review — foreach loop over `ManagementObjectSearcher.Get()` without disposal.
  Touches: `Core/Safety/SystemRestoreManager.cs`
  Acceptance: ManagementObject instances disposed after use. Verify no WMI handle leaks under repeated calls.
  Complexity: S

- [ ] P2 — **"Always keep" protection flag per program**
  Why: Users want to protect critical apps from accidental batch uninstall. Table-stakes safety UX.
  Evidence: BCU issue #935 (most-requested safety feature); Revo Pro has similar "exclude" feature.
  Touches: `Core/Models/InstalledProgram.cs`, `Core/App/DataPaths.cs` (persist list), `App/Views/MainWindow.xaml` (context menu + visual indicator)
  Acceptance: Right-click → "Always Keep" marks a program. Marked programs are excluded from batch uninstall and show a lock icon. Persisted in DataPaths.Config.
  Complexity: S

- [ ] P2 — **winget COM API migration**
  Why: `winget list --output json` is not a supported CLI option. Current fallback to table parsing is fragile. The `Microsoft.Management.Deployment` COM API is the official programmatic interface.
  Evidence: microsoft/winget-cli#4965 (feature request still open); winget export uses schema 2.0 JSON.
  Touches: `Core/Packages/PackageManagerScanner.cs`
  Acceptance: Use `winget export` for JSON package list or COM API for real-time queries. Remove `ParseWingetTable` fallback. Add test with sample JSON output.
  Complexity: M

- [ ] P2 — **External leftover signature loading**
  Why: Current DB is embedded-resource only — no way for users or community to contribute profiles without recompilation.
  Evidence: RESEARCH.md architecture assessment; BleachBit CleanerML community model; Winapp2 repo (969 stars).
  Touches: `Core/Data/LeftoverSignatureDb.cs`, `Core/App/DataPaths.cs`
  Acceptance: On startup, scan `DataPaths.Cleaners/*.signatures.json` and merge with embedded DB. User-contributed files take precedence over embedded entries for the same app name.
  Complexity: S

### P3 — Polish + differentiation

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
