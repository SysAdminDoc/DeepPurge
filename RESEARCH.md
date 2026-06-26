# Research — DeepPurge

## Executive Summary

DeepPurge is a mature Windows uninstaller and system cleaner (C#/.NET 10 LTS, WPF + CLI, ~14,900 LOC, 171 tests, MIT) with the broadest feature set of any open-source tool in its category. It ships uninstall/batch/forced removal, leftover scanning (281-entry signature DB), junk/evidence/winapp2/custom-cleaner cleanup, MFT-based disk analysis, duplicate finder, driver store, startup impact, services, scheduled tasks, context menus, shortcuts, install monitoring (snapshot + USN journal), health scoring, game platform detection, portable app discovery, system slimming, orphan detection (signatures + BAM + Amcache), and browser extension management — all behind a safety-first architecture (SafetyGuard choke-point, child-reparse-safe deletion, locked-file recovery, Restart Manager integration).

The previous research pass (April 2026) identified 10 priority items; all 10 have been addressed. The highest-value direction now is **hardening the remaining raw destructive paths, closing competitive feature gaps exposed by the 2026 benchmark landscape, and improving accessibility**. The feature set already exceeds every free competitor — the focus should be accuracy, polish, and trust.

**Top 10 opportunities in priority order:**
1. Harden FirewallRuleScanner PowerShell command construction against injection via backtick/semicolon in rule names
2. Route AutorunScanner `DeleteAutorun` file operations through `SafeDeleteFile` for safety-guard validation
3. Fix ActivityLog.Prune() race condition — concurrent Record() and Prune() can lose entries
4. Add timeout and cancellation support to HealthScorer scan methods to prevent UI hangs
5. Make DeleteEmptyFolders_Click and services panel operations async to stop blocking the UI thread
6. Developer directory scanner — find and size `node_modules`, `venv`, `.gradle`, `bin/obj`, `target/` directories
7. Age-based file retention — "only clean files older than N days" per cleanup category
8. Right-click-to-exclude from scan results — add paths to SafetyGuard exclusion list inline
9. Migrate CommunityToolkit.Mvvm to partial properties pattern (8.4.x code fixer available)
10. WCAG 2.2 accessibility pass — Narrator automation names, 2px focus indicators, SystemColors mapping in HighContrast theme

## Product Map

- **Core workflows:** uninstall/batch/forced removal; leftover scan (Safe/Moderate/Advanced); junk/evidence/winapp2/custom-cleaner cleanup; disk/duplicate/empty-folder analysis; driver/startup/shortcut/service/task/orphan/repair/schedule/install-monitor/health/shell/history; portable/game/bundleware detection; system slimming; BAM/Amcache/prefetch remnant discovery.
- **User personas:** power users cleaning personal PCs; IT technicians with portable tools on USB; sysadmins scripting CLI/Intune/SCCM; privacy-focused users reviewing traces.
- **Platforms:** Windows 10/11 x64/ARM64, .NET 10 LTS (10.0.9), WPF GUI + headless CLI, self-contained single-file executables (~66 MB each), GitHub Releases, winget/Scoop manifests (not yet submitted).
- **Key integrations:** registry uninstall keys, winget/Scoop/Steam/Epic/GOG enrichment, winapp2.ini (4,000+ community rules), `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal (`FSCTL_ENUM_USN_DATA`), Restart Manager, GitHub Releases API.

## Competitive Landscape

**BCUninstaller** (v6.2, Apache 2.0, ~12k stars): Migrated to .NET 8. Added certificate/integrity columns and Scoop custom-path detection in v6.2. Still no dark mode (issue #228, 55 upvotes — DeepPurge's biggest advantage). 61.33% leftover accuracy in the 2026 Uninstalr benchmark — barely above Windows Settings (59.36%). Repo moved to new `BCUninstaller` org with two new maintainers. Learn from its modular architecture (UninstallTools lib + GUI + CLI + helper apps). Avoid its WinForms UI and Everything dependency.

**FluentCleaner** (v26.06.02, MIT, growing fast, launched April 2026): Most direct modern competitor — WinUI 3/.NET 10, cleaning-only (no uninstall). Global exclusions with right-click-to-exclude from results, AI-assisted cleaner creation (Groq), German localization, portable mode. Critical weakness: WinUI 3 requires Windows App SDK runtime (install failures on Win10/11), no Windows 10 support. Learn from right-click-to-exclude UX and settings export/import. DeepPurge's combined uninstaller + cleaner scope is the key differentiator.

**HiBit Uninstaller** (v4.0.10, freeware, 4.32 MB): 89.90% leftover accuracy — best free tool in the 2026 benchmark. Complete feature suite (batch uninstall, install monitor, registry cleaner, junk cleaner, startup manager, file shredder) in a tiny package. Closed-source. DeepPurge should target >90% accuracy to claim the best-free-uninstaller position.

**Uninstalr** (v3.0, freemium $39): 94.33% accuracy (self-published benchmark). Only tool with portable app detection (beyond DeepPurge). Full-system scan approach (all files + registry, not just Add/Remove Programs). Sets the accuracy ceiling. DeepPurge already has portable detection; the gap is leftover scan thoroughness.

**Revo Uninstaller Pro** (v5.5.0, $24.95/yr): Hunter Mode now supports UWP/MSIX apps — "drag crosshair to window, uninstall the owning app." Logs Database of community-shared install footprints. 63.05% accuracy — worse than HiBit free. Subscription pricing is a community pain point. Learn from the Logs Database concept (shared manifests for unmonitored installs).

**PrivaZer** (v4.0.123, donationware): Privacy-first cleaner with SSD-aware erasure (TRIM vs multi-pass), free-space trace scanning, and 100+ scan types. Zero telemetry. Learn from its transparency (every action documented for user audit). DeepPurge's SecureDelete already handles SSD detection.

**BleachBit** (v6.0.1 beta, GPL, 6k stars): CVE-2026-55567 (arbitrary file deletion during privileged cleaning) validates DeepPurge's SafetyGuard approach. v6.0.1 added developer directory scanning (`node_modules`, `venv`), DNS cache cleaning, and multi-profile browser support. Age-based deletion planned for v6.1. Learn from dev directory scanning. Avoid GPL entanglement.

**InstallerClean** (v1.9.2, MIT, 109 stars): MSI/MSP orphan cleanup via Windows Installer API — can reclaim 10-50 GB from `C:\Windows\Installer`. Best-in-class accessibility: Narrator support, Voice Access, keyboard-only operation, reduced-motion respect. Learn from its MSI/MSP approach and accessibility implementation.

## Security, Privacy, and Reliability

**Active codebase issues (verified June 2026):**
- [Verified] `FirewallRuleScanner.EscapePs()` at `src/DeepPurge.Core/Firewall/FirewallRuleScanner.cs:197` only escapes single quotes (`'` → `''`). PowerShell backticks, semicolons, and `$()` subexpressions in firewall rule names could enable command injection via `Remove-NetFirewallRule -Name '...'`.
- [Verified] `AutorunScanner.DeleteAutorun()` at `src/DeepPurge.Core/Startup/AutorunScanner.cs:424-427` uses raw `File.Delete(entry.Command)` and `File.Delete(disabled)` without routing through `SafetyGuard.SafeDeleteFile`. A malicious registry autorun entry pointing to a critical file would bypass the safety guard.
- [Verified] `ActivityLog.Prune()` at `src/DeepPurge.Core/Diagnostics/ActivityLog.cs:72-83` reads all lines, then writes back without holding the same `_lock` used by `Record()`. Entries appended between `ReadAllLines` and `WriteAllLines` are lost.
- [Verified] `HealthScorer` scan methods (`src/DeepPurge.Core/Diagnostics/HealthScorer.cs:39,68,97`) invoke expensive full-system scanners (`JunkFilesCleaner.ScanForJunk`, `EvidenceRemover.ScanAllTraces`) without timeout or cancellation support. If any scanner hangs, the dashboard hangs.
- [Verified] UI thread blocking: `DeleteEmptyFolders_Click` and services panel operations in `MainWindow.xaml.cs` run synchronous file/registry operations on the UI thread.
- [Verified] `PathCleaner` split inconsistency: scan path uses `StringSplitOptions.RemoveEmptyEntries` but clean path uses `StringSplitOptions.None`, potentially leaving stray semicolons in the PATH variable.
- [Verified] CLI `Program.cs:177` calls `ToastNotifier.ShowCleaningSummary()` — a WPF-coupled API from a headless CLI binary. Layering violation.

**Previously identified issues — now FIXED:**
- BrowserExtensionScanner raw Directory.Delete → fixed in commit `9ce8c61`
- Hardcoded v0.8.1 version strings → fixed in commit `9ce8c61`
- AppSettings atomic save → fixed in commit `9ce8c61`
- DeleteLargeFiles_Click raw File.Delete → now routes through SafeDeleteFile
- NuGet package alignment → all at 10.0.9, Test SDK at 18.7.0
- CLI help completeness → fixed in commit `4fae219`
- SecureDelete.Wipe logging → fixed in commit `dd92edc`

**External CVEs relevant to DeepPurge:**
- [Critical] CVE-2026-32177 — heap-based buffer overflow in WindowsDesktop/.NET WPF runtime (CVSS 7.3). Fixed in .NET 10.0.8+. DeepPurge builds against 10.0.9 which includes the fix. **Document 10.0.9 as minimum runtime requirement.**
- [External] CVE-2026-50656 (Windows Defender "RoguePlanet") — TOCTOU local privilege escalation in Defender's Malware Protection Engine. **Still UNPATCHED as of June 25, 2026.** DeepPurge's child-reparse-safe deletion mitigates the filesystem side.
- [External] CVE-2026-45490 (.NET SDK named pipe EoP) — affects build-time only. Ensure SDK is 10.0.109+.
- [External] CVE-2026-55567 (BleachBit arbitrary file deletion) — validates DeepPurge's SafetyGuard architecture.
- [External] Stryker.NET + xUnit v3 incompatibility (#3117) — remains OPEN. Must stay on xUnit 2.9.3.

**Recovery and rollback:** Restore points before uninstall; registry backups via BackupManager; dry-run on all delete paths; locked files queued for reboot deletion via Restart Manager; Recycle Bin default for file deletions.

## Architecture Assessment

- **Testing has improved but gaps remain.** 16 test files now cover more modules (up from 11). New tests added for CleanerDefinition, GamePlatformScanner, HealthScorer, AppSettings, DuplicateFinder, InstallSnapshotDiff, and WindowsRepairSanitiser. Safety-critical `SafeDeleteFile`/`SafeDeleteDirectory`/`SafeEnumerateFiles` primitives still have no dedicated unit tests.
- **NuGet versions are now aligned.** System.Management, System.ServiceProcess.ServiceController, and System.IO.Hashing all at 10.0.9. Test SDK at 18.7.0. CommunityToolkit.Mvvm at 8.4.2.
- **FormatBytes wrappers are cosmetic.** 6 `FormatBytes`/`FormatSize` methods across the codebase all delegate to `SizeFormatter.Format` — no logic duplication, just unnecessary wrappers. Low-priority cleanup.
- **MainViewModel** (923 + 645 = 1,568 lines across 2 partials) and **MainWindow.xaml.cs** (907 lines) remain large but stable. ViewModel decomposition correctly deferred to Roadmap_Blocked.
- **CommunityToolkit.Mvvm partial properties migration** available via 8.4.x code fixer. Replaces field-annotated `[ObservableProperty]` with partial property declarations. Improves AOT compatibility and is the recommended pattern going forward.
- **WPF .NET 10 Fluent styles** now cover DatePicker, GridSplitter, GroupBox, Hyperlink, Label, RichTextBox, TextBox. Could adopt for UI polish without a full WinUI rewrite.
- **Windows 11 25H2 impact:** WMIC removed by default — verified DeepPurge does NOT use `wmic.exe` (uses System.Management + PowerShell for WMI). PowerShell 2.0 removed — no dependency. No breaking impact.
- **CLI→ToastNotifier coupling** (`Program.cs:177`) calls WPF-coupled code from headless binary. Should abstract notification behind an interface.

## Rejected Ideas

- Generic registry cleaner — contradicts safety-first philosophy; Microsoft does not support registry cleaners. (Microsoft Support KB)
- Multi-pass DoD wipes — rejected by project policy; single-pass cryptographic random is sufficient for SSDs. (NIST SP 800-88r2)
- AI rule explanations — cloud API dependency contradicts zero-telemetry posture. FluentCleaner uses Groq. (FluentCleaner 26.06.02)
- WinUI rewrite — WPF theme system is adequate. .NET 10 Fluent styles narrow the gap further. (WPF .NET 10 release notes)
- Extension marketplace — supply-chain risk; local JSON cleaners fit better. (FluentCleaner extensions)
- Cross-platform support — Windows-only by design (registry, COM, drivers, Restart Manager). (Czkawka/BleachBit contrast)
- Software updater module — winget upgrade detection is sufficient. (IObit/Revo)
- Broad debloat presets — fragile service toggles conflict with safety philosophy. Win11Debloat owns this niche. (Win11Debloat 49.6k stars)
- Country-of-origin display — requires maintaining publisher-to-country database. Niche value. (Uninstalr 3.0)
- xUnit v3 migration — Stryker.NET #3117 still open; mutation score drops to 3%. Stay on v2.9.3. (Stryker.NET issues)
- `winget list --json` — closed as "not planned" by winget team (issue #4965). Text-table parsing remains necessary.
- Software permissions auditing — niche feature (only Wise/IObit offer it); requires maintaining an app-permissions database. Not core mission for a cleanup tool. (Wise 3.2.9)
- Notification blocker — OS-level responsibility (Windows Focus Assist, notification settings). Not a cleanup tool's job. (Wise 3.2.9)
- Crash analyzer — niche (only Ashampoo offers it). Windows Event Viewer and Reliability Monitor already serve this purpose. (Ashampoo 16)
- Community install footprint database — high infrastructure cost (hosting, moderation, trust model) for uncertain value. Revo Pro has this but scored only 63% accuracy anyway. Revisit if a lightweight approach emerges. (Revo Pro Logs Database)

## Sources

OSS competitors:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (v6.2, Jun 2026)
- https://github.com/Klocman/Bulk-Crap-Uninstaller/issues/228 (dark mode, 55 reactions)
- https://github.com/bleachbit/bleachbit (v6.0.1 beta, Jun 2026)
- https://github.com/builtbybel/FluentCleaner (v26.06.02, WinUI 3)
- https://github.com/MoscaDotTo/Winapp2 (restructured 2026, 27 files)
- https://github.com/raphire/win11debloat (49.6k stars, v2026.06.24)
- https://github.com/farag2/Sophia-Script-for-Windows (v7.1.6)
- https://github.com/lostindark/DriverStoreExplorer (v1.0.26)
- https://github.com/qarmin/czkawka (31.7k stars, v11.0.1)
- https://github.com/no-faff/InstallerClean (v1.9.2, accessibility-first)
- https://github.com/laurentiu021/SystemManager (C#/.NET 10/WPF, May 2026)

Commercial and benchmarks:
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.revouninstaller.com/products/revo-uninstaller-pro/ (v5.5.0)
- https://www.hibitsoft.ir/Uninstaller.html (v4.0.10, 89.90% accuracy)
- https://www.martau.com/document/total-uninstall.php (v7.6.2)
- https://www.iobit.com/en/advanceduninstaller.php (v15.4.0)
- https://privazer.com/en/changelog.php (v4.0.123)
- https://www.wisecleaner.com/wise-program-uninstaller.html (v3.2.9)
- https://www.ashampoo.com/en-us/uninstaller (v16)

Security:
- https://github.com/dotnet/announcements/issues/370 (CVE-2025-55247, Linux-only)
- https://github.com/dotnet/announcements/issues/403 (CVE-2026-45490, SDK named pipe)
- https://github.com/dotnet/announcements/issues/404 (CVE-2026-45491, Tar symlink)
- https://www.penligent.ai/hackinglabs/cve-2026-50656/ (RoguePlanet, unpatched)
- https://github.com/bleachbit/bleachbit/security/advisories/GHSA-j8jc-f6p7-55p8
- https://github.com/stryker-mutator/stryker-net/issues/3117 (xUnit v3 broken)
- https://github.com/dotnet/announcements (CVE-2026-32177, WPF heap overflow)

Platform and dependencies:
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10
- https://www.nuget.org/packages/System.IO.Hashing/10.0.9
- https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-840/
- https://github.com/microsoft/winget-cli/issues/4965

## Open Questions

None that block prioritization. All items are actionable with current information.
