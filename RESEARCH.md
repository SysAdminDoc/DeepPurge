# Research — DeepPurge

## Executive Summary

DeepPurge is a mature Windows uninstaller and system cleaner (C#/.NET 10 LTS, WPF + CLI, ~15k LOC, 180 tests, MIT) with the broadest feature set of any open-source tool in its category. It ships uninstall/batch/forced removal, leftover scanning (281-entry signature DB), junk/evidence/winapp2/custom-cleaner/dev-directory cleanup with age-based retention, MFT-based disk analysis, duplicate finder (files + directories), driver store, startup impact, services, scheduled tasks, context menus, shortcuts, install monitoring (snapshot + USN journal), health scoring, game platform detection (Steam/Epic/GOG), portable app discovery, system slimming, orphan detection (signatures + BAM + Amcache + prefetch), browser extension management across all profiles, and right-click-to-exclude — all behind a safety-first architecture (SafetyGuard, child-reparse-safe deletion, locked-file recovery, Restart Manager). All 10 items from the previous (round 3) research pass have shipped.

The highest-value direction now is **fixing the remaining safety/reliability gaps in the CLI and infrastructure code, then pursuing the IT-deployment and trust-building features** that differentiate DeepPurge from the fast-growing FluentCleaner ecosystem. The feature set exceeds every free and most paid competitors — the focus should be trust, correctness, and deployment story.

**Top 8 opportunities in priority order:**
1. Validate CLI `--export` paths against path traversal before writing
2. Add lock around `ActivityLog.LoadRecent()` to prevent concurrent read/write corruption
3. Add timeout to initial scan so the UI doesn't hang indefinitely on startup
4. Route `EmptyFolderScanner.DeleteEmptyFolders` through `SafetyGuard.SafeDeleteDirectory` instead of raw `Directory.Delete`
5. Fix `HealthScorer` system-drive fallback to use actual Windows installation drive instead of hardcoded `C:\`
6. Make user-agent version string dynamic from assembly version instead of hardcoded "0.9"
7. Add settings export/import CLI commands for IT deployment configuration sharing
8. Add unit tests for `SafeDeleteFile`/`SafeDeleteDirectory`/`SafeEnumerateFiles` safety primitives

## Product Map

- **Core workflows:** uninstall/batch/forced removal; leftover scan (Safe/Moderate/Advanced); junk/evidence/winapp2/custom-cleaner/dev-directory cleanup with age-based retention; disk/duplicate(files+dirs)/empty-folder analysis; driver/startup/shortcut/service/task/orphan/repair/schedule/install-monitor/health/shell/history; portable/game/bundleware detection; system slimming; BAM/Amcache/prefetch remnant discovery; right-click-to-exclude from scan results.
- **User personas:** power users cleaning personal PCs; IT technicians deploying portable tools on USB; sysadmins scripting CLI via Intune/SCCM/Task Scheduler; privacy-focused users reviewing traces.
- **Platforms:** Windows 10/11 x64/ARM64, .NET 10 LTS (10.0.9), WPF GUI + headless CLI, self-contained single-file executables (~66 MB each), GitHub Releases with SHA256SUMS, winget/Scoop manifests (not yet submitted).
- **Key integrations:** registry uninstall keys, winget/Scoop/Steam/Epic/GOG enrichment, winapp2.ini (4,000+ community rules), `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal (`FSCTL_ENUM_USN_DATA`), Restart Manager, GitHub Releases API.

## Competitive Landscape

**BCUninstaller** (v6.2, Apache 2.0, ~19.9k stars): Migrated to .NET 8. v6.2 added certificate/integrity columns, invalid-uninstaller view presets, improved Scoop custom-path detection. v6.1 added MSI component enumeration (detects all installed files for Msiexec uninstallers — reduces false orphans). Still no dark mode (issue #228, 46+ comments). BCU's top request after 10 years is dark theme — DeepPurge ships 9 themes dark-first. Learn from MSI component enumeration and view presets. Avoid WinForms lock-in.

**FluentCleaner** (v26.06.02, MIT, ~2,070 stars, launched April 2026): Most dangerous emerging competitor — WinUI 3/.NET 10, cleaning-only (no uninstall). Growing explosively (2,070 stars in 2.5 months). Global exclusions with right-click-to-exclude, AI-assisted cleaner creation (Groq), localization with language switcher, settings export/import, portable mode. Critical weakness: WinUI 3 runtime deployment failures (#18, #40 — most common issues), no Windows 10 support. Same developer launched FluentTweaker (3,158 stars, Jan 2026) — building a "Fluent ecosystem." If they add an uninstaller module, they become a direct competitor with modern UI and momentum. Learn from settings export/import. DeepPurge's combined uninstaller + cleaner scope and WPF's deployment simplicity are key differentiators.

**HiBit Uninstaller** (v4.0.10, freeware, closed-source): 30% faster leftover search in v4.0. Extensive free companion tools (Process Manager, Services Manager, Context Menu Manager, Startup Manager, Shortcut Fixer, Registry Cleaner, Junk Cleaner, Empty Folder Cleaner, File Shredder, Duplicate Finder, Disk Analyzer, Connection Manager). Closed-source limits inspection. Benchmark accuracy varies by test.

**Uninstalr** (v3.0, freemium $39): Self-published 2026 benchmark shows 94.33% accuracy across 4 test apps. Detects 15 app types including NirLauncher portables and drive-root apps. v3.0 improved portable detection 23%. Free version has no feature restrictions — Pro adds priority support only. Sets the accuracy ceiling but benchmarks are not independently verified.

**Revo Uninstaller Pro** (v5.5.0, $24.95/yr): Logs Database now 67,266 logs covering 12,582 programs. Hunter Mode extended to UWP/MSIX apps. Real-time installation monitor. Subscription model is a community pain point. 63.05% accuracy in Uninstalr benchmark — worse than free tools.

**BleachBit** (v6.0.1 beta, GPL, ~6k stars): CVE-2026-55567 (arbitrary file deletion during privileged cleaning) reinforces the importance of SafetyGuard-style validation. v6.0.1 added multi-profile browser cleaning, dev directory scanning (node_modules, venv), DNS cache cleaner, Claude Code cleaner. Age-based deletion planned for v6.1 — DeepPurge already shipped this.

**PrivaZer** (v4.0.123, donationware): Storage-type-aware secure erasure (SSD vs HDD algorithm selection), free-space wiping, USB history cleanup. Privacy-first positioning. DeepPurge's SecureDelete already handles SSD detection.

**InstallerClean** (v1.9.2, MIT, 109 stars): MSI/MSP orphan cleanup — can reclaim 5-20+ GB from `C:\Windows\Installer`. Best-in-class accessibility: Narrator, Voice Access, JAWS, keyboard-only, reduced-motion respect, screen reader automation names. Same stack as DeepPurge (C#/.NET 10/WPF). SHA-256 sidecar files + VirusTotal links in every release. Learn from accessibility implementation and MSI/MSP orphan approach.

## Security, Privacy, and Reliability

**Active codebase issues (verified June 2026):**
- [Verified] CLI `--export` path traversal: Multiple `File.WriteAllText(exportPath, ...)` and `GridExporter.Export*()` calls in `src/DeepPurge.Cli/Program.cs` (lines 250, 278, 298, 337, 489) accept user-provided paths from `a.GetOption("export")` without validation. A path like `--export ..\..\windows\system32\evil.txt` writes outside the intended directory. Mitigated by CLI's `asInvoker` manifest (no elevation), but still a correctness issue.
- [Verified] `ActivityLog.LoadRecent()` at `src/DeepPurge.Core/Diagnostics/ActivityLog.cs:40` calls `File.ReadAllLines()` without acquiring `_lock`. Concurrent `Record()` or `Prune()` (which do hold `_lock`) can produce partial reads or file-in-use errors.
- [Verified] `EmptyFolderScanner.DeleteEmptyFolders` at `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs:85` uses raw `Directory.Delete(folder.Path, recursive: false)` instead of `SafetyGuard.SafeDeleteDirectory`. While `IsPathSafeToDelete` is checked on line 83, the deletion itself bypasses reparse-point guards and locked-file recovery.
- [Verified] `HealthScorer` at `src/DeepPurge.Core/Diagnostics/HealthScorer.cs:169` falls back to `@"C:\"` when `Path.GetPathRoot()` returns null. Should use the actual Windows installation drive. Rare edge case but incorrect on systems where Windows is on D:\ or another drive.
- [Verified] Hardcoded user-agent `"DeepPurge/0.9"` at `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs:40`. Should use assembly version to stay current across version bumps.
- [Verified] `MainWindow.xaml.cs:53` — `RunInitialScanAsync()` has no timeout. If any scanner hangs (e.g., WMI timeout on degraded system), the window stays in loading state indefinitely with no recovery path.

**Previously identified issues — now FIXED (round 3 → round 4):**
- FirewallRuleScanner injection → fixed via `-EncodedCommand` base64 encoding (commit `6b4d41e`)
- AutorunScanner SafeDeleteFile routing → fixed (commit `6b4d41e`)
- ActivityLog.Prune() race condition → fixed with `_lock` (commit `6b4d41e`)
- HealthScorer async + 30s timeout → fixed (commit `c443f55`)
- Async UI operations (large-file delete) → fixed (commit `c443f55`)
- PathCleaner split inconsistency → fixed (commit `c443f55`)
- CLI→ToastNotifier decoupling → fixed (commit `c443f55`)
- Dev directory scanner → shipped (commit `38aff54`)
- Age-based file retention → shipped (commit `38aff54`)
- Right-click-to-exclude → shipped (commit `38aff54`)

**External CVEs relevant to DeepPurge:**
- [Current] CVE-2026-45490 — .NET SDK named pipe EoP (Windows-only). Affects build-time `dotnet.exe workload` command. Fixed in SDK 10.0.109/10.0.301. Build environment concern only.
- [Current] CVE-2026-45491 — System.Formats.Tar symlink traversal. Not relevant (DeepPurge doesn't extract tar archives).
- [Current] CVE-2026-45591 — ASP.NET Core SignalR/Blazor DoS. Not relevant (no ASP.NET Core).
- [Prior] CVE-2026-32177 — WPF heap overflow, fixed in .NET 10.0.8+. DeepPurge at 10.0.9.
- [Prior] CVE-2026-50656 — Windows Defender TOCTOU (RoguePlanet), still unpatched. DeepPurge's reparse-point-safe deletion mitigates.
- [External] CVE-2026-55567 — BleachBit arbitrary file deletion during privileged cleaning. Validates SafetyGuard architecture.

**Recovery and rollback:** Restore points before uninstall; registry backups via BackupManager; dry-run on all delete paths; locked files queued for reboot deletion via Restart Manager; Recycle Bin default; right-click-to-exclude for false positive management.

## Architecture Assessment

- **Testing improved to 180 tests across 18 files.** Round 3 added ActivityLog, FirewallEscape tests. Coverage gaps remain for the safety-critical primitives: `SafeDeleteFile`, `SafeDeleteDirectory`, `SafeEnumerateFiles` have no dedicated unit tests. These are the foundation of every destructive operation.
- **NuGet packages are current.** System.Management/ServiceProcess/IO.Hashing at 10.0.9. CommunityToolkit.Mvvm at 8.4.2 with partial properties migration complete. Test SDK at 18.7.0. xUnit at 2.9.3 (v3 blocked by Stryker.NET #3117).
- **WPF .NET 10** added Fluent styles for DatePicker, GridSplitter, GroupBox, Hyperlink, Label, RichTextBox, TextBox. HighContrast crash fix. RecognizesAccessKey added. DeepPurge could adopt these for incremental polish.
- **Windows 11 26H2** confirmed as enablement-package update on 25H2 codebase. No new cleanup APIs. Dynamic app removal policy (Enterprise/Education) allows PFN-based MSIX removal. winget 1.29 adds source priority system. Microsoft PC Manager growing as first-party overlap.
- **Winapp2.ini ecosystem** unchanged — INI format, CCleaner 7 broke compatibility, last release v251109 (Nov 2025). FluentCleaner now consumes it natively. No format migration.
- **European Accessibility Act** enforcement began June 28, 2025. EN 301 549 maps WCAG to desktop software. Increases importance of the WCAG 2.2 pass in Roadmap_Blocked.
- **Raw File.Delete calls** remain in 5 locations outside SafetyGuard: Log.cs (rotation, acceptable), SelfTest.cs (probe, acceptable), EmptyFolderScanner.cs (should route through SafetyGuard), WindowsRepairEngine.cs (cache files in system dirs — controlled context), BackupManager.cs (own backup files — acceptable).

## Rejected Ideas

- Generic registry cleaner — contradicts safety-first philosophy. (Microsoft Support KB)
- Multi-pass DoD wipes — rejected by project policy; single-pass sufficient for SSDs. (NIST SP 800-88r2)
- Free-space wiping — aggressive operation (writes hundreds of GB, wears SSDs, TRIM renders it ineffective on modern drives). Same rationale as multi-pass DoD rejection. (PrivaZer v4.0.123)
- AI-assisted cleaner creation — cloud API dependency contradicts zero-telemetry posture. (FluentCleaner/Groq)
- WinUI rewrite — WPF theme system is adequate; WinUI 3 has serious deployment friction (FluentCleaner #18, #40). .NET 10 Fluent styles narrow the gap. (WPF .NET 10 release notes)
- Extension marketplace — supply-chain risk; local JSON cleaners fit better. (FluentCleaner extensions)
- Cross-platform support — Windows-only by design. (Czkawka/BleachBit contrast)
- Software updater module — winget upgrade detection is sufficient. (IObit v15.4.0)
- Broad debloat presets — fragile service toggles conflict with safety philosophy. Win11Debloat owns this niche. (Win11Debloat 49.6k stars)
- Country-of-origin display — requires maintaining publisher-to-country database. (Uninstalr 3.0)
- xUnit v3 migration — Stryker.NET #3117 still open. Stay on v2.9.3. (Stryker.NET issues)
- `winget list --json` — closed as "not planned." Text-table parsing remains necessary. (winget-cli #4965)
- Software permissions auditing — niche, requires app-permissions database. (Wise 3.2.9)
- Notification blocker — OS-level responsibility. (Wise 3.2.9)
- Crash analyzer — niche; Windows Event Viewer serves this. (Ashampoo 16)
- Community install footprint database — high infrastructure cost, uncertain value. Revo Pro has this but scored only 63% accuracy. (Revo Pro Logs Database)
- Video optimizer / EXIF remover — separate tool territory, not cleanup. (Czkawka v11)
- DNS-over-HTTPS configuration — OS-level responsibility. (Sophia Script v7.1.6)
- Driver updater — different product category, security risk. (CCleaner v7.08)
- Multi-profile browser cleaning — already implemented. Both BrowserExtensionScanner and JunkFilesCleaner scan Default + Profile * directories. (Verified in codebase)

## Sources

OSS competitors:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (v6.2, Jun 2026)
- https://github.com/bleachbit/bleachbit (v6.0.1 beta, Jun 2026)
- https://github.com/builtbybel/FluentCleaner (v26.06.02, 2,070 stars)
- https://github.com/builtbybel/FluentTweaker (3,158 stars, Jan 2026)
- https://github.com/raphire/win11debloat (49.6k stars, v2026.06.24)
- https://github.com/qarmin/czkawka (31.7k stars, v11.0.1)
- https://github.com/lostindark/DriverStoreExplorer (v1.0.26, self-update)
- https://github.com/no-faff/InstallerClean (v1.9.2, accessibility-first)
- https://github.com/farag2/Sophia-Script-for-Windows (v7.1.6)
- https://github.com/MoscaDotTo/Winapp2 (v251109, CCleaner 7 incompatible)

Commercial and benchmarks:
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.revouninstaller.com/revo-uninstaller-pro-full-version-history/ (v5.5.0)
- https://www.hibitsoft.ir/Uninstaller.html (v4.0.10)
- https://www.martau.com/document/total-uninstall.php (v7.6.2)
- https://www.iobit.com/en/advanceduninstaller.php (v15.4.0)
- https://privazer.com/en/changelog.php (v4.0.123)
- https://www.ashampoo.com/en-us/uninstaller (v16, Forensic Analysis)
- https://www.ccleaner.com/ccleaner/version-history (v7.08, trust deficit)
- https://pcmanager.microsoft.com/en-us (Microsoft PC Manager)

Security:
- https://github.com/dotnet/announcements/issues/403 (CVE-2026-45490)
- https://github.com/dotnet/announcements/issues/404 (CVE-2026-45491)
- https://github.com/dotnet/announcements/issues/405 (CVE-2026-45591)
- https://github.com/bleachbit/bleachbit/security/advisories/GHSA-j8jc-f6p7-55p8
- https://github.com/stryker-mutator/stryker-net/issues/3117

Platform:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://github.com/microsoft/winget-cli/releases (v1.29.280)
- https://accessibilityinsights.io/
- https://commission.europa.eu/strategy-and-policy/policies/justice-and-fundamental-rights/disability/european-accessibility-act-eaa_en

## Open Questions

None that block prioritization. All items are actionable with current information.
