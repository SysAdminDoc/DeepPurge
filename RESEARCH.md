# Research — DeepPurge

## Executive Summary

DeepPurge is a mature Windows uninstaller and system cleaner (C#/.NET 10 LTS, WPF + CLI, ~22k LOC, 195 tests, MIT) with the broadest feature set of any open-source tool in its category — combining uninstall, cleanup, driver/startup/service management, install monitoring, and a headless CLI in a single binary. Six research rounds have shipped 40+ improvements. All round 5 items are confirmed shipped: slim build target, deletion manifest, CLI `--json` output, and winapp2 update command.

The competitive landscape continues to bifurcate: **consumer-facing tools** (WinUtil 56.4k stars, Win11Debloat 49.6k, Winhance 11.3k) dominate GitHub star counts but are PowerShell scripts or debloaters without uninstall depth. **Serious uninstallers** (BCU 19.9k, HiBit, Uninstalr) focus on removal accuracy but lack cleaning breadth. **Cleaners** (BleachBit 6.0.0, FluentCleaner 2.1k) lack uninstall capability. DeepPurge is the only open-source project that spans all three categories.

The highest-value direction now is **trust and safety hardening** — cookie preservation during browser cleaning, automated deletion rollback, and signature verification on the programs list — followed by **ecosystem modernization** (xUnit v3 migration now unblocked, WPF Fluent style adoption).

**Top 8 opportunities in priority order:**
1. Cookie preservation whitelist for browser cleaning (BleachBit parity, high user trust)
2. Automated deletion rollback from DeletionManifest (undo story is write-only today)
3. Promote MSI/MSP orphan cleanup from Ideas to committed (InstallerClean proved the approach)
4. xUnit v3 migration (Stryker.NET MTP runner now supports it — blocker resolved)
5. Digital signature column on installed programs list (BCU v6.2 parity)
6. Per-program notes/tags for IT technician workflows (BCU v6.3 in dev)
7. WPF .NET 10 Fluent style adoption for 9 newly-supported controls (free polish)
8. Copy scan results to clipboard across all panels

## Product Map

- **Core workflows:** uninstall/batch/forced/manifest-replay removal; leftover scan (Safe/Moderate/Advanced); junk/evidence/winapp2/custom-cleaner/dev-directory cleanup with age-based retention; disk/duplicate(files+dirs)/empty-folder analysis; driver/startup/shortcut/service/task/orphan/repair/schedule/install-monitor/health/shell/history management; portable/game/bundleware/BAM/Amcache detection; system slimming; right-click-to-exclude; settings export/import; deletion manifest logging.
- **User personas:** power users cleaning personal PCs; IT technicians deploying portable tools on USB; sysadmins scripting CLI via Intune/SCCM/Task Scheduler; privacy-focused users reviewing digital traces.
- **Platforms:** Windows 10/11 x64/ARM64, .NET 10 LTS (10.0.9), WPF GUI + headless CLI (22 commands), self-contained (~66 MB) + framework-dependent slim (~2-5 MB) single-file builds, GitHub Releases with SHA256SUMS, winget/Scoop manifests (not yet submitted).
- **Key integrations:** registry uninstall keys, winget/Scoop/Steam/Epic/GOG enrichment, winapp2.ini (3,715 rules), `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal, NTFS MFT (`FSCTL_ENUM_USN_DATA`), Restart Manager, WinVerifyTrust, GitHub Releases API, Windows toast notifications.

## Competitive Landscape

**WinUtil** (56.4k stars, MIT, PowerShell): The largest project in the Windows optimization space. 30M+ runs, 200+ contributors, custom ISO builder. PowerShell-only with no binary distribution. DeepPurge's WPF GUI and compiled native performance are clear advantages. Avoid competing on debloat scope — WinUtil owns that niche.

**BCUninstaller** (19.9k stars, Apache 2.0, C#/WinForms/.NET 6): Under active development by new maintainer org. v6.2 added certificate/integrity columns and install-date fallback. Custom Notes feature (#939) in development for v6.3. Still no dark mode after 10 years. DeepPurge should match the certificate column on the programs list (already on autoruns/services). Learn from BCU's MSI component enumeration and plugin-based detection architecture.

**BleachBit** (6k stars, GPL-3.0, Python): v6.0.0 was the biggest release in years — Cookie Manager (whitelist sites during cleaning), Expert Mode, Vivaldi/Zen browser cleaners, deeper Chromium cleaning (component cache, shader cache, DIPS, IndexedDB). v6.0.1 beta fixes an arbitrary file deletion security bug during privileged cleaning (validates DeepPurge's SafetyGuard approach). The Cookie Manager is the standout feature DeepPurge lacks — users lose all saved logins during evidence cleaning without it.

**FluentCleaner** (2.1k stars, MIT, C#/WinUI 3/.NET 10): Cleaning-only (no uninstall). Multi-database approach: winapp2.ini + winapp3.ini (aggressive) + winappx (AppX bloatware). AI-assisted cleaner creation via Groq. WinUI 3 deployment failures remain the most common issue. DeepPurge's WPF deployment simplicity and combined uninstaller+cleaner scope are key differentiators.

**Winhance** (11.3k stars, PolyForm Shield — NOT open source): Builder Mode for deployment configs, Change History logging, 29 languages. 90K+ downloads for latest release. PolyForm Shield license restricts competitive use — DeepPurge's MIT is a genuine differentiator. Avoid scope creep into ISO customization and debloat.

**InstallerClean** (109 stars, MIT, C#/WPF/.NET 10): Focused tool for orphaned MSI/MSP files in `%WINDIR%\Installer`. Queries Windows Installer API to identify unregistered files. Can recover 1-100+ GB on heavily patched systems. Nearly identical tech stack to DeepPurge. The approach is proven and ready to integrate — DeepPurge should promote this from Ideas to committed.

**Win11Debloat** (49.6k stars, MIT, PowerShell): Added WhatIf dry-run mode in June 2026 — DeepPurge already has this. GPO domain policy override warning is a nice touch for enterprise deployments. PowerShell-only, no GUI.

**Uninstalr** (v3.0, $39 perpetual): 94.33% accuracy in self-published benchmark (March 2026). Free version has no feature restrictions. Sets the accuracy ceiling for the category. DeepPurge's open-source transparency is the counter-positioning.

## Security, Privacy, and Reliability

**All round 5 codebase items — FIXED:**
- Slim build target → framework-dependent publish added (commit `b43be56`)
- Deletion manifest (`deletions.jsonl`) → DeletionManifest.Record/RecordFile/RecordDirectory (commit `b43be56`)
- CLI `--json` output mode → list and orphans commands support `--json` (commit `b43be56`)
- Winapp2 staleness check → `update-winapp2 [--check-only]` CLI command (commit `b43be56`)

**Remaining architectural gaps:**
- `DeletionManifest` is write-only (`src/DeepPurge.Core/Diagnostics/DeletionManifest.cs`). Records every deletion as JSONL but provides no `Restore()`, `ListManifests()`, or CLI surface. The undo story is half-built — manifests exist but aren't actionable without manual JSON parsing.
- Browser cleaning (`EvidenceRemover`) has no cookie preservation — all cookies are deleted wholesale. No grep hits for "cookie" or "Cookie" in the Core project. Users lose all saved logins during evidence cleanup. BleachBit's Cookie Manager is the competitive bar.
- `DeletionManifest.Record` has an empty `catch { }` block (line 33) — the one remaining silent catch after the 57-block cleanup. Should log to prevent invisible manifest write failures.
- Activity log (`DataPaths.Logs`) may contain PII (file paths, registry keys, program names). No documented retention policy or scrubbing mechanism. Not a vulnerability, but a data-hygiene gap.

**External CVEs (current as of June 2026 — no new advisories since round 5):**
- CVE-2026-45490 — .NET SDK named pipe EoP. Build-time only. Fixed in SDK 10.0.109+.
- CVE-2026-45491 — System.Formats.Tar symlink traversal. Not relevant (DeepPurge doesn't extract archives).
- CVE-2026-45591 — ASP.NET Core SignalR DoS. Not relevant.
- CVE-2026-32177 — WPF heap overflow. Fixed in .NET 10.0.8+; DeepPurge at 10.0.9.
- BleachBit v6.0.1 beta fixed an arbitrary file deletion bug during privileged cleaning (no CVE assigned). Validates DeepPurge's SafetyGuard choke-point design.

**Recovery and rollback:** Restore points before uninstall; registry backups via BackupManager; dry-run on all delete paths; DeletionManifest JSONL recording (write-only — no automated restore); locked files queued for reboot via Restart Manager; Recycle Bin default; right-click-to-exclude; settings export/import.

## Architecture Assessment

- **Testing at 195 tests across 19 files.** No stubs or `NotImplementedException` found. Coverage gaps remain for RegistryLeftoverScanner, FileLeftoverScanner, BrowserExtensionScanner, ServiceScanner, UninstallEngine — all require live system state. Stryker.NET 4.14.2 MTP runner now supports xUnit v3 (`--test-runner mtp`), resolving the migration blocker (Stryker #3117). xUnit v3 3.2.2 is stable.
- **NuGet packages are current.** System.Management/ServiceProcess/IO.Hashing 10.0.9, CommunityToolkit.Mvvm 8.4.2 (no newer release), Test SDK 18.7.0, xUnit 2.9.3. No updates available except the xUnit 2→3 major version.
- **WPF .NET 10 expanded Fluent styles** to DatePicker, GridSplitter, GroupBox, Hyperlink, Label, NavigationWindow, RichTextBox, TextBox, GridView. DeepPurge could adopt these for incremental visual polish without custom control templates. New `ColumnDefinitions="..."` shorthand syntax available for cleaner XAML.
- **Windows 11 26H2** ships built-in Sysmon (disabled by default). Could supplement USN journal for registry change tracking during install monitoring. WinUI Run dialog and redesigned context menus are opt-in, no impact on DeepPurge's shell integration. Low Latency Profile and nested context menus do not add cleanup APIs.
- **Winapp2.ini last updated November 2025** (v251109, 3,715 entries). 7+ months stale. FluentCleaner adopted a multi-database approach (winapp2/winapp3/winappx). DeepPurge should consider maintaining supplementary definitions for modern apps not covered by the community database.
- **Self-contained build size (~66 MB) mitigated** by the slim framework-dependent build target (`build/slim/`, ~2-5 MB) shipped in round 5. No further action needed.
- **Zero-dependency Core library** (only System.* packages from the runtime). This is a significant architectural strength — no NuGet supply-chain risk beyond the runtime itself.
- **MainViewModel is 1,708 lines across 2 partials.** ViewModel decomposition remains blocked (requires visual testing of XAML binding changes). Manageable for now.

## Rejected Ideas

*Carried forward from previous rounds (still valid):*
- Generic registry cleaner — contradicts safety-first philosophy. (Microsoft Support KB)
- Multi-pass DoD wipes — project policy; single-pass sufficient. (NIST SP 800-88r2)
- Free-space wiping on SSDs — wears flash, TRIM renders ineffective. (PrivaZer v4.0.123)
- AI-assisted cleaner creation — cloud dependency contradicts zero-telemetry. (FluentCleaner/Groq)
- WinUI 3 rewrite — WPF adequate; WinUI 3 has deployment friction. (FluentCleaner #18, #40)
- Extension marketplace — supply-chain risk. (FluentCleaner extensions)
- Cross-platform — Windows-only by design. (Czkawka/BleachBit contrast)
- Software updater — winget upgrade detection sufficient. (IObit v15.4.0)
- Broad debloat presets — conflicts with safety philosophy. (Win11Debloat 49.6k, Winhance 11.2k own this niche)
- Country-of-origin display — publisher-to-country database maintenance. (Uninstalr 3.0)
- Community install footprint database — infrastructure cost, uncertain value. (Revo Pro 63% accuracy)
- App relocation between drives — different product category. (Ashampoo 16)
- Crash analyzer — Windows Event Viewer serves this. (Ashampoo 16)
- ISO/autounattend.xml customization — scope creep. (Winhance Builder Mode)
- Policy-based MSIX removal — Enterprise/Education only. (Windows 11 25H2)
- Floating toolbar / Widgets — consumer pattern, conflicts with admin positioning. (PC Manager v3.21)
- Video/audio duplicate detection — separate tool territory. (Czkawka v11)
- Declarative tweak system — debloat/tweaker, not uninstaller. (Win11Debloat, Sophia Script)
- BleachBit CleanerML format — INI (winapp2.ini) is the de facto standard. (BleachBit docs)

*New rejections from round 6:*
- Cross-platform Electron rewrite — overhead vs native C# performance, contradicts single-binary philosophy. (Kudu, Sparkle both have 250-1.8k stars vs DeepPurge's native perf advantage)
- Malware scanning integration — out of scope, Windows Defender/third-party AV handles this. (Kudu bundles 70+ signatures but it's a different product category)
- System monitoring dashboard — real-time CPU/RAM/network/disk is Task Manager territory. (Kudu, Sparkle feature this but it's tangential to cleanup)
- GPO domain policy override warnings — enterprise-only scenario, adds complexity without broad user value. (Win11Debloat 2026.06.24 added this but it's niche)
- Winapp3.ini (aggressive) database support — "aggressive" rules contradict safety-first philosophy. (FluentCleaner supports this but it's the opposite of DeepPurge's SafetyGuard approach)
- EXIF metadata stripping — separate tool territory, not cleanup. (Czkawka v11 added this)
- Video re-encoding/optimization — not cleanup. (Czkawka v11 added this)
- Config-export-for-fleet-deployment (Builder Mode) — interesting but scope creep into deployment tooling. (Winhance)

## Sources

OSS competitors:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (v6.2, Jun 2026, custom notes #939 in dev)
- https://github.com/bleachbit/bleachbit (v6.0.0 stable, v6.0.1 beta Jun 2026)
- https://github.com/builtbybel/FluentCleaner (v26.06.02, 2,070+ stars)
- https://github.com/builtbybel/FluentTweaker (3,158 stars)
- https://github.com/memstechtips/Winhance (v26.06.12, 11,252 stars)
- https://github.com/raphire/win11debloat (49.6k stars, v2026.06.24)
- https://github.com/ChrisTitusTech/winutil (56.4k stars, v26.06.23)
- https://github.com/ravendevteam/talon (1,930 stars, v2026.6.5.17)
- https://github.com/qarmin/czkawka (31.7k stars, v11.0.1, GTK deprecated for Krokiet/Slint)
- https://github.com/lostindark/DriverStoreExplorer (v1.0.26)
- https://github.com/no-faff/InstallerClean (v1.9.2, MIT, C#/WPF/.NET 10)
- https://github.com/farag2/Sophia-Script-for-Windows (v7.1.5, 8.9k stars)
- https://github.com/MoscaDotTo/Winapp2 (v251109, 3,715 entries, last update Nov 2025)
- https://github.com/adventdevinc/kudu (268 stars, Electron, new entrant)
- https://github.com/thedogecraft/sparkle (1.8k stars, Electron, new entrant)

Commercial and benchmarks:
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.hibitsoft.ir/Uninstaller.html (v4.0.10, 89.90% accuracy)
- https://www.ashampoo.com/en-us/uninstaller (v16, Forensic Analysis)

Security and platform:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://github.com/dotnet/announcements/issues/403 (CVE-2026-45490)
- https://xunit.net/releases/v3/3.2.2 (xUnit 3.2.2 stable)
- https://stryker-mutator.io/blog/stryker-net-mtp-runner/ (MTP runner supports xUnit v3)
- https://www.nuget.org/packages/CommunityToolkit.Mvvm (8.4.2, no newer)
- https://www.bleepingcomputer.com/news/microsoft/microsoft-rolls-out-native-windows-11-sysmon-security-monitoring/

Windows 11:
- https://www.pcworld.com/article/3063498/windows-11-26h2-is-coming-meet-all-the-new-features.html
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/get-ready-for-windows-11-version-26h2/4529367

Community:
- https://www.windowscentral.com/microsoft/windows-11/fluent-cleaner-may-be-the-best-ccleaner-alternative-for-windows-11-users
- https://www.bleachbit.org/news/bleachbit-600 (Cookie Manager, Expert Mode)
- https://www.bleachbit.org/news/bleachbit-601-beta (security fix for privileged file deletion)

## Open Questions

None that block prioritization. All items are actionable with current information.
