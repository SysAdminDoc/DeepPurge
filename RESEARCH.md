# Research — DeepPurge

## Executive Summary

DeepPurge is a mature Windows uninstaller and system cleaner (C#/.NET 10 LTS, WPF + CLI, ~15k LOC, 195 tests, MIT) with the broadest feature set of any open-source tool in its category. Five research rounds have shipped 30+ improvements. All 8 items from round 4 are confirmed shipped: CLI export path validation, ActivityLog read locking, initial scan 60s timeout, EmptyFolderScanner SafetyGuard routing, dynamic user-agent versioning, settings export/import CLI, and 15 SafetyGuard deletion primitive tests.

The competitive landscape has shifted: **Winhance** (11,252 stars, WinUI 3) emerged as a major player with Builder Mode config export, Change History logging, and 29-language support. **FluentCleaner** continues explosive growth (2,070+ stars, Windows Central coverage as "best CCleaner alternative"). **Microsoft PC Manager v3.21** adds deep cleanup with Widgets integration — first-party overlap growing. The Uninstalr 2026 benchmark confirmed most uninstallers "barely do anything" — only 3 of 9 tools scored >85% accuracy.

The highest-value direction now is **deployment story and machine-parseable scripting** — the features that differentiate DeepPurge for IT/sysadmin adoption over the fast-growing consumer-focused competitors.

**Top 4 opportunities in priority order:**
1. Framework-dependent "slim" build producing ~2-5 MB executables (vs ~66 MB self-contained)
2. Granular deletion manifest (`deletions.jsonl`) for IT audit trails and post-mortem review
3. CLI `--json` stdout output mode for machine-parseable scripting
4. Winapp2.ini staleness check + `update-winapp2` CLI command

## Product Map

- **Core workflows:** uninstall/batch/forced removal; leftover scan (Safe/Moderate/Advanced); junk/evidence/winapp2/custom-cleaner/dev-directory cleanup with age-based retention; disk/duplicate(files+dirs)/empty-folder analysis; driver/startup/shortcut/service/task/orphan/repair/schedule/install-monitor/health/shell/history; portable/game/bundleware detection; system slimming; BAM/Amcache/prefetch remnant discovery; right-click-to-exclude; settings export/import.
- **User personas:** power users cleaning personal PCs; IT technicians deploying portable tools on USB; sysadmins scripting CLI via Intune/SCCM/Task Scheduler; privacy-focused users reviewing traces.
- **Platforms:** Windows 10/11 x64/ARM64, .NET 10 LTS (10.0.9), WPF GUI + headless CLI, self-contained single-file executables (~66 MB each), GitHub Releases with SHA256SUMS, winget/Scoop manifests (not yet submitted).
- **Key integrations:** registry uninstall keys, winget/Scoop/Steam/Epic/GOG enrichment, winapp2.ini (3,715+ community rules), `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal, Restart Manager, GitHub Releases API.

## Competitive Landscape

**Winhance** (v26.06.12, MIT, 11,252 stars, launched Jan 2025): The breakout hit of the Windows optimization space. WinUI 3 with Builder Mode (create configs/autounattend.xml without changing current PC), Change History logging for every tweak, 29-language localization, app install/removal, power plan management, Explorer customization. 90K+ downloads for latest release. Key differentiator: "configure without applying" then export for fleet deployment. DeepPurge should learn from the config-export-for-fleet-deployment pattern. Avoid scope creep into ISO customization.

**FluentCleaner** (v26.06.02, MIT, ~2,070 stars, launched April 2026): Growing fastest among pure cleaners. WinUI 3/.NET 10, cleaning-only (no uninstall). Windows Central featured it as "best CCleaner alternative." Global exclusions, AI-assisted cleaner creation, localization, settings export/import. Critical weakness: WinUI 3 runtime deployment failures remain the most common issue. DeepPurge's combined uninstaller + cleaner scope and WPF deployment simplicity are key differentiators. The FluentTweaker sibling (3,158 stars) signals ecosystem ambitions.

**BCUninstaller** (v6.2, Apache 2.0, ~19.9k stars): Under new `BCUninstaller` org with fresh maintainers. v6.2 added certificate/integrity columns, invalid-uninstaller view presets, install-date fallback via `Directory.GetLastWriteTime`, improved Scoop detection. Still no dark mode after 10 years (#228, 46 comments). Plugin-based detection architecture (separate detectors for MSI, Scoop, Chocolatey, Steam, Store). Learn from MSI component enumeration in v6.1.

**Uninstalr** (v3.0, $39 perpetual): Self-published 2026 benchmark: 94.33% accuracy (23/406 leftovers). Detects 15 app types. Free version has no feature restrictions — Pro adds support only. Sets accuracy ceiling. DeepPurge's open-source transparency is the counter-positioning.

**HiBit Uninstaller** (v4.0.10, freeware): 89.90% in Uninstalr benchmark. 30% faster leftover search in v4.0. 10+ bundled companion tools. Closed-source.

**Ashampoo UnInstaller** (v16, $12/yr 3 devices or $20 one-time): Major upgrade. Forensic Analysis creates uninstall logs for pre-installed apps retroactively. Crash Analyzer parses Windows Event Viewer. App relocation between drives. 50% faster cleaning. Registry Optimizer 2 with "Super Safe Mode." Installation monitoring claimed 10x faster.

**BleachBit** (v6.0.0, GPL, ~6k stars): Cookie manager GUI, deeper Chromium/Firefox cleaning, Vivaldi/Zen browsers. CVE-2026-55567 validates SafetyGuard approach. Active Weblate translations (505 strings, 99%+ Russian). Community reports clipboard cleaning deleting source files (#2135) — safety gap.

**Microsoft PC Manager** (v3.21.7.0, free): Floating toolbar, deep cleanup algorithm, Widgets integration, real-time network speed. Entirely free, no upsell. Growing first-party overlap with third-party cleanup tools.

## Security, Privacy, and Reliability

**All round 4 codebase issues — FIXED:**
- CLI `--export` path traversal → fixed with `ValidateExportPath()` (commit `5aecc74`)
- ActivityLog.LoadRecent() read lock → fixed (commit `5aecc74`)
- Initial scan timeout → 60s CancellationTokenSource (commit `5aecc74`)
- EmptyFolderScanner SafeDeleteDirectory → routed through SafetyGuard (commit `5aecc74`)
- HealthScorer drive fallback → already used `SpecialFolder.Windows` (confirmed correct)
- Hardcoded user-agent → dynamic from assembly version (commit `237816e`)
- SafetyGuard deletion primitive tests → 15 tests added (commit `237816e`)

**Remaining raw File.Delete calls (all acceptable in context):**
- `Log.cs:30` — log rotation, own files
- `SelfTest.cs:95` — probe file, own temp file
- `WindowsRepairEngine.cs:179,214` — cache files in system dirs, controlled repair context
- `BackupManager.cs:102` — own backup files
- `ScheduleManager.cs:84` — wrapper script cleanup

**External CVEs (current as of June 2026):**
- CVE-2026-45490 — .NET SDK named pipe EoP. Build-time only, fixed in SDK 10.0.109/10.0.301.
- CVE-2026-45491 — System.Formats.Tar symlink traversal. Not relevant.
- CVE-2026-45591 — ASP.NET Core SignalR DoS. Not relevant.
- CVE-2026-40372 — ASP.NET Core DataProtection CVSS 9.1. Not relevant (no ASP.NET Core).
- CVE-2026-32177 — WPF heap overflow. Fixed in .NET 10.0.8+; DeepPurge at 10.0.9.
- CVE-2026-50656 — Windows Defender TOCTOU (RoguePlanet). Still unpatched. DeepPurge's reparse-point-safe deletion mitigates.

**Recovery and rollback:** Restore points before uninstall; registry backups via BackupManager; dry-run on all delete paths; locked files queued for reboot deletion via Restart Manager; Recycle Bin default; right-click-to-exclude for false positives; settings export/import for config preservation.

## Architecture Assessment

- **Testing at 195 tests across 19 files.** Round 4 added SafetyGuardDeletionTests (15 tests). Coverage gaps remain for RegistryLeftoverScanner, FileLeftoverScanner, BrowserExtensionScanner, ServiceScanner, UninstallEngine — all require live system state or complex fixtures.
- **NuGet packages are current.** All at latest stable: System.Management/ServiceProcess/IO.Hashing 10.0.9, CommunityToolkit.Mvvm 8.4.2, Test SDK 18.7.0, xUnit 2.9.3. No updates available.
- **xUnit v3 (3.2.2 stable)** now has native Microsoft Testing Platform support and no longer requires Test SDK. Migration still blocked by Stryker.NET #3117.
- **WPF .NET 10** Fluent styles expanded to DatePicker, GridSplitter, GroupBox, Hyperlink, Label, RichTextBox, TextBox. HighContrast crash fixed. Performance improvements in XAML parsing and font rendering. Could adopt for incremental polish.
- **Windows 11 26H2** confirmed as enablement package on 25H2. Low Latency Profile, nested context menus, WinUI-based Run window. No new cleanup APIs. 25H2 added policy-based MSIX removal by PFN (Enterprise/Education only).
- **Self-contained build size (~66 MB)** is a deployment friction point. InstallerClean's triple distribution model (self-contained 65MB / framework-dependent 2MB / CLI) shows the pattern. Framework-dependent builds assume runtime is installed but dramatically reduce download size.
- **CLI output is human-readable only.** No `--json` flag for machine parsing. BCU console, Czkawka CLI, and winget all support structured output. Gap for sysadmin scripting workflows.
- **Winapp2.ini auto-downloads on first run** but has no update-after-first-run mechanism. FluentCleaner and BleachBit both auto-update their cleaner databases.

## Rejected Ideas

*Carried forward from previous rounds (still valid):*
- Generic registry cleaner — contradicts safety-first philosophy. (Microsoft Support KB)
- Multi-pass DoD wipes — project policy; single-pass sufficient. (NIST SP 800-88r2)
- Free-space wiping — wears SSDs, TRIM renders ineffective. (PrivaZer v4.0.123)
- AI-assisted cleaner creation — cloud dependency contradicts zero-telemetry. (FluentCleaner/Groq)
- WinUI rewrite — WPF adequate; WinUI 3 has deployment friction. (FluentCleaner #18, #40)
- Extension marketplace — supply-chain risk. (FluentCleaner extensions)
- Cross-platform — Windows-only by design. (Czkawka/BleachBit contrast)
- Software updater — winget upgrade detection sufficient. (IObit v15.4.0)
- Broad debloat presets — conflicts with safety philosophy. (Win11Debloat 49.6k stars, Winhance 11.2k stars own this niche)
- Country-of-origin display — publisher-to-country database maintenance. (Uninstalr 3.0)
- xUnit v3 migration — Stryker.NET #3117 still open. (Stryker.NET issues)
- Community install footprint database — infrastructure cost, uncertain value. (Revo Pro 63% accuracy)
- Multi-profile browser cleaning — already implemented. (Verified in codebase)

*New rejections from round 5:*
- App relocation between drives — different product category, not cleanup. (Ashampoo 16)
- Crash analyzer — Windows Event Viewer serves this. (Ashampoo 16)
- ISO/autounattend.xml customization — scope creep beyond cleanup/uninstall. (Winhance Builder Mode)
- Policy-based MSIX removal integration — Enterprise/Education only, Group Policy dependency. (Windows 11 25H2)
- Floating toolbar / Widgets integration — consumer UI pattern, conflicts with admin-tool positioning. (Microsoft PC Manager v3.21)
- Video/audio duplicate detection — separate tool territory. (Czkawka v11 video checker)
- Declarative tweak system — debloat/tweaker, not uninstaller. (Win11Debloat, Sophia Script)
- BleachBit CleanerML format support — INI (winapp2.ini) is the de facto standard; adding a second format increases complexity without clear benefit. (BleachBit CleanerML docs)

## Sources

OSS competitors:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (v6.2, Jun 2026)
- https://github.com/bleachbit/bleachbit (v6.0.0, Apr 2026)
- https://github.com/builtbybel/FluentCleaner (v26.06.02, 2,070+ stars)
- https://github.com/builtbybel/FluentTweaker (3,158 stars)
- https://github.com/memstechtips/Winhance (v26.06.12, 11,252 stars)
- https://github.com/raphire/win11debloat (49.6k stars, v2026.06.24)
- https://github.com/ravendevteam/talon (1,930 stars, v2026.6.5.17)
- https://github.com/qarmin/czkawka (31.7k stars, v11.0.1)
- https://github.com/lostindark/DriverStoreExplorer (v1.0.26)
- https://github.com/no-faff/InstallerClean (v1.9.2, triple distribution)
- https://github.com/farag2/Sophia-Script-for-Windows (v7.1.6)
- https://github.com/MoscaDotTo/Winapp2 (v251109, CCleaner 7 flavor added)

Commercial and benchmarks:
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.revouninstaller.com/revo-uninstaller-pro-full-version-history/ (v5.5.0)
- https://www.hibitsoft.ir/Uninstaller.html (v4.0.10, 89.90% accuracy)
- https://www.martau.com/uninstaller-buy.php (v7.6.2, EUR 49.95)
- https://www.iobit.com/en/advanceduninstaller.php (v15.4.0, $19.99/yr)
- https://www.ashampoo.com/en-us/uninstaller (v16, Forensic Analysis)
- https://privazer.com/en/changelog.php (v4.0.123)
- https://www.ccleaner.com/ccleaner/plans (v7.08, reputation issues)
- https://pcmanager.microsoft.com/en-us (v3.21.7.0, deep cleanup)

Security and platform:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://github.com/microsoft/winget-cli/releases (v1.28)
- https://github.com/dotnet/announcements/issues/403 (CVE-2026-45490)
- https://github.com/stryker-mutator/stryker-net/issues/3117
- https://www.windowscentral.com/microsoft/windows-11/fluent-cleaner-may-be-the-best-ccleaner-alternative-for-windows-11-users

Windows 11:
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/dynamically-remove-apps-from-managed-windows-11-devices/4516291
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/get-ready-for-windows-11-version-26h2/4529367

## Open Questions

None that block prioritization. All items are actionable with current information.
