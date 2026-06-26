# Research — DeepPurge

## Executive Summary

DeepPurge is a mature Windows uninstaller and system cleaner (C#/.NET 10 LTS, WPF + CLI, 17.8k LOC, 134 tests, MIT) with unusually broad feature coverage for an open-source tool: uninstall/batch/forced, leftover scanning (281-entry signature DB), junk/evidence/winapp2 cleanup, MFT-based disk analysis, duplicate finder, driver store, startup impact, services, scheduled tasks, context menus, shortcuts, install monitoring (snapshot + USN journal), health scoring, game platform detection, portable app discovery, custom JSON cleaners, system slimming, orphan detection (signatures + BAM), and browser extension management — all with a safety-first architecture (SafetyGuard choke-point, child-reparse-safe deletion, locked-file recovery). Recent rapid development (30+ commits) shipped major safety primitives and competitive-parity features.

The highest-value direction is **consolidation and trust**: fixing the remaining raw destructive paths that bypass SafetyGuard, aligning NuGet dependencies with the .NET 10 TFM, expanding test coverage past 19%, correcting the stale version strings in the UI, and documenting shipped features. No major new features are needed — the feature set already exceeds every free competitor.

**Top 10 opportunities in priority order:**
1. Fix BrowserExtensionScanner using raw `Directory.Delete(true)` — last recursive destructive path bypassing SafeDeleteDirectory
2. Fix hardcoded `v0.8.1` version strings in MainWindow.xaml (lines 278, 1594)
3. Add thread-safe save in AppSettings (concurrent writes corrupt settings.json)
4. Route remaining raw File.Delete calls in GUI destructive paths through SafeDeleteFile (DeleteLargeFiles_Click, EmptyFolderScanner)
5. Align System.Management / System.ServiceProcess.ServiceController / System.IO.Hashing NuGet versions with .NET 10 TFM
6. Add missing CLI help entries for `register-shell`, `unregister-shell`, `cleaners`, `orphans --remnants`
7. Add tests for SafeDeleteFile/SafeDeleteDirectory/SafeEnumerateFiles (safety-critical code)
8. Add tests for new modules: CleanerDefinition, GamePlatformScanner, PortableAppScanner, HealthScorer, AppSettings
9. Update README to document shipped features: portable app detection, game platforms, health dashboard, system slimming, bundleware detection, expert mode, custom cleaners, BAM remnants, shell integration
10. Add failure logging to SecureDelete.Wipe to diagnose partial-overwrite states

## Product Map
- Core workflows: uninstall/batch/forced removal; leftover scan and selective deletion; junk/evidence/winapp2/custom-cleaner cleanup; disk/duplicate/empty-folder analysis; driver/startup/shortcut/service/task/orphan/repair/schedule/install-monitor/health/shell/history flows; portable/game/bundleware detection; system slimming; BAM remnant discovery.
- User personas: power users cleaning personal PCs; IT technicians with portable tools on USB; sysadmins scripting CLI/Intune/SCCM; privacy-focused users reviewing traces.
- Platforms: Windows 10/11 x64/ARM64, .NET 10 LTS, WPF GUI + headless CLI, self-contained GitHub release assets, winget/Scoop manifests (not yet submitted), unsigned.
- Key integrations: registry uninstall keys/HKU profiles, winget/Scoop/Steam/Epic/GOG enrichment, winapp2.ini, `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal, Restart Manager, GitHub Releases API, local logs/backups/settings.

## Competitive Landscape

**BCUninstaller** (v6.2, Apache 2.0, ~7.5k stars): Migrated to .NET 8. Added certificate/integrity columns and Scoop custom-path detection. Still no dark mode (issue #228, 46 reactions). 61.33% leftover accuracy in the 2026 Uninstalr benchmark — barely above Windows Settings (59.36%). Learn from its shared-settings protection (BCU#758, addressed in DeepPurge). Avoid its WinForms architecture and Everything dependency.

**BleachBit** (v6.0.0, GPL, 6k stars): CVE-2026-55567 arbitrary file deletion during privileged cleaning validates DeepPurge's SafetyGuard approach. Python/GTK feels non-native on Windows. Winapp2 community database was restructured in 2026 (27 smaller files instead of monolith). Learn from CleanerML extensibility. Avoid GPL code reuse.

**FluentCleaner** (v26.06.02, MIT, 2.1k stars, launched April 2026): The most direct modern competitor — WinUI 3/.NET 10, growing rapidly. Global exclusions, German localization, AI-assisted cleaner creation (Groq), ARM64 native. However, it is a **cleaner only** — no uninstall capability. DeepPurge's combined uninstaller + cleaner scope is the key differentiator. Watch for feature parity in cleaning quality.

**HiBit Uninstaller** (v4.0.10, freeware): 89.90% leftover accuracy in the 2026 benchmark — best free tool. Complete feature suite (process manager, services, shortcuts, registry cleaner, file shredder). Main weaknesses: closed-source, non-modern UI. DeepPurge should target exceeding 90% accuracy to claim the best-free-uninstaller position.

**Revo Uninstaller Pro** (v5.5.0, $24.95/yr): Hunter Mode now supports Windows apps. Subscription pricing is a community pain point. The Delphi codebase is aging. 63.05% accuracy in 2026 benchmark — worse than HiBit free.

**Uninstalr** (v3.0, freemium $39 one-time): 94.33% accuracy (self-published benchmark). Only tool with portable app detection (NirLauncher). Country-of-origin display. Improved Steam/Epic/GOG detection (23% faster). Sets the accuracy ceiling, but benchmarks are self-published.

**Total Uninstall** (v7.6.2, $29.95 one-time): 85.96% accuracy. Graphical tree view of install changes is the gold standard for traced uninstalls. DeepPurge's snapshot + USN journal approach is competitive with this.

**Win11Debloat** (49.6k stars, PowerShell): Added WhatIf dry-run mode and granular AI/telemetry controls (Copilot, Recall). Massive community adoption validates demand for UWP/Store app removal. DeepPurge already handles this via WindowsAppManager.

## Security, Privacy, and Reliability

- [Verified] `BrowserExtensionScanner.RemoveExtension` at `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs:235` still uses raw `Directory.Delete(ext.Path, true)` and `File.Delete(ext.Path)` — last recursive destructive path bypassing `SafeDeleteDirectory`/`SafeDeleteFile`. A junction under a browser extension directory could redirect deletion.
- [Verified] `DeleteLargeFiles_Click` at `src/DeepPurge.App/Views/MainWindow.xaml.cs:561` uses raw `File.Delete(f.Path)` instead of `SafetyGuard.SafeDeleteFile`. Passes `IsPathSafeToDelete` but skips locked-file recovery (Restart Manager query + delete-on-reboot fallback).
- [Verified] `EmptyFolderScanner.DeleteEmptyFolders` at `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs:83` calls `Directory.Delete(folder.Path, recursive: false)` without checking `SafetyGuard.IsPathSafeToDelete`. An adversary could add content between scan and delete to redirect the operation.
- [Verified] `AppSettings.Save()` at `src/DeepPurge.Core/App/AppSettings.cs:14` has no synchronization. Concurrent `Save()` calls can corrupt `settings.json`.
- [Verified] `SecureDelete.Wipe` at `src/DeepPurge.Core/Safety/SecureDelete.cs:68` catches all exceptions and returns `false` without logging. A file partially overwritten but not deleted is left in a corrupted state with no diagnostic trail.
- [Verified] XAML version strings at `src/DeepPurge.App/Views/MainWindow.xaml:278` and `:1594` show `v0.8.1` while the actual version is 0.9.0 — user-visible misinformation. The About panel (line 1377) correctly binds to `AppVersionDisplay`.
- [Verified] CLI `PrintHelp()` at `src/DeepPurge.Cli/Program.cs:675` documents 15 of 19 available commands — `register-shell`, `unregister-shell`, `cleaners`, and `orphans --remnants` are undiscoverable.
- [Verified] NuGet packages `System.Management` (8.0.0), `System.ServiceProcess.ServiceController` (8.0.1), and `System.IO.Hashing` (8.0.0) are mismatched with the project's `net10.0` TFM. Latest available: 10.0.9 for all three. Version mismatch can cause subtle runtime behavior differences.
- [Corrected] CVE-2025-55247: The official Microsoft advisory ([dotnet/announcements#370](https://github.com/dotnet/announcements/issues/370)) classifies this as a **Linux-only Denial of Service** via predictable MSBuild temp directories — NOT a Windows privilege escalation as previously reported in this file. Third-party databases (SentinelOne, Windows Forum) incorrectly described it as a symlink/junction EoP on Windows. **The existing P2 roadmap item to verify this CVE can be dropped — it is irrelevant to Windows.**
- [External] CVE-2026-50656 (Windows Defender "RoguePlanet", June 2026) — local privilege escalation via TOCTOU race in Defender's Malware Protection Engine. Path-redirection strategy exploiting Defender file processing. **Still UNPATCHED as of June 25, 2026.** Functional exploit code assessed as available. DeepPurge creates/deletes files in directories that Defender scans — environmental risk. DeepPurge's child-reparse-safe deletion mitigates the file-system side of this attack class.
- [External] CVE-2026-33825 (Windows Defender "BlueHammer") — same TOCTOU class as RoguePlanet. **Patched** via Defender engine update.
- [External] CVE-2026-45490 (.NET SDK named pipe EoP, June 2026, CVSS 7.8) — affects build-time `dotnet.exe workload` command, not DeepPurge runtime. Ensure SDK is updated to .NET 10.0.9+.
- [External] CVE-2026-55567 (BleachBit arbitrary file deletion during privileged cleaning) — validates DeepPurge's SafetyGuard approach as a necessary defense against the exact attack class BleachBit failed to prevent.
- [External] Stryker.NET + xUnit v3 incompatibility — issue #3117 (mutation score drops from 100% to 3%) remains OPEN. #3629 closed as duplicate. MTP runner is still preview-only. Must stay on xUnit v2 for mutation testing.
- Recovery and rollback: restore points created before uninstall; registry backups via BackupManager; dry-run available on all delete paths; locked files queued for reboot deletion.

## Architecture Assessment

- **Testing gap is the top structural risk.** 11 test files cover 11 of 57 Core modules (19%). Recent features shipped without any tests: CleanerDefinition, GamePlatformScanner, PortableAppScanner, SystemSlimmer, HealthScorer, AmcacheParser, LockedFileResolver, ShellExtensionRegistrar, AppSettings. The safety-critical `SafeEnumerateFiles`/`SafeDeleteDirectory`/`SafeDeleteFile` primitives have zero unit tests.
- **NuGet version drift.** Three `System.*` packages are at 8.0.x while the project targets `net10.0`. `Microsoft.NET.Test.Sdk` is at 17.11.1 (latest: 18.7.0). Aligning these reduces the risk of subtle runtime behavior differences and picks up performance improvements in System.IO.Hashing's XXH3 implementation.
- **6 duplicate FormatSize/FormatBytes implementations** across InstalledProgram.cs, EvidenceRemover.cs, MainViewModel.cs, MainViewModel.Extensions.cs, Program.cs, and ToastNotifier.cs. A shared utility would reduce maintenance surface.
- **MainViewModel monolith** (1773 lines across 2 partials) and **MainWindow.xaml.cs** (1039 lines) remain large but functionally stable. ViewModel decomposition is correctly deferred to Roadmap_Blocked (needs visual testing).
- **README documentation drift** is significant. 8+ shipped features have no README documentation: portable app detection, game platform scanning, health dashboard, system slimming, bundleware/sideload detection, expert/safe mode, custom JSON cleaners, BAM remnant discovery, shell context-menu integration.
- **CLI help text drift**: 4 commands undocumented in `--help` output.
- The safety primitive architecture (SafetyGuard → SafeDeleteFile/SafeDeleteDirectory → LockedFileResolver) is well-designed. The remaining gaps are BrowserExtensionScanner (recursive) and two GUI paths that use raw File.Delete/Directory.Delete instead of the safe primitives.
- **Winget JSON output unavailable.** `winget list --json` was closed as "not planned" (GitHub issue #4965). The current text-table parsing approach (keyed off column positions) will remain necessary. `winget export -o` produces JSON but captures a different data set (packages by source, not the installed-programs view).

## Rejected Ideas
- Generic registry cleaner — contradicts safety-first philosophy; Microsoft explicitly does not support registry cleaners. (Microsoft Support policy KB)
- Multi-pass DoD wipes — already rejected by project policy; single-pass is sufficient for SSD threat model. (NIST SP 800-88r2)
- AI rule explanations — external API dependency contradicts zero-telemetry posture. FluentCleaner uses Groq for this, but requires cloud dependency. (FluentCleaner 26.06.02)
- WinUI rewrite — WPF theme system is adequate. FluentCleaner proves WinUI 3 looks good, but WPF's .NET 10 Fluent style expansion narrows the gap. (WPF .NET 10 release notes)
- Extension marketplace — trust/supply-chain risk; local JSON cleaners are better fit. (FluentCleaner extensions)
- Cross-platform support — Windows-only by design (registry, COM, drivers, Restart Manager). (Czkawka/BleachBit contrast)
- Software updater module — winget upgrade detection is enough. (IObit/Revo)
- Broad debloat presets — fragile service toggles conflict with conservative safety philosophy. Win11Debloat (49.6k stars) owns this niche. (Win11Debloat/Sophia)
- Country-of-origin display — Uninstalr v3.0 feature. Niche value; requires maintaining a publisher-to-country database. Not worth the maintenance cost for a cleaning tool. (Uninstalr 3.0 changelog)
- xUnit v3 migration — Stryker.NET #3117 still open; mutation score drops to 3% on v3. Stay on xUnit 2.9.3 until Stryker ships a fix. (Stryker.NET issues)
- `winget list --json` — closed as "not planned" by winget team (issue #4965). Continue text-table parsing or explore `winget export` JSON format for enrichment. (winget-cli issues)

## Sources

OSS competitors:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller (v6.2, Jun 2026)
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/tag/v6.2
- https://github.com/Klocman/Bulk-Crap-Uninstaller/issues/228 (dark mode, 46 reactions)
- https://github.com/bleachbit/bleachbit (v6.0.0, security vuln CVE-2026-55567)
- https://github.com/builtbybel/FluentCleaner (v26.06.02, WinUI 3 cleaner)
- https://github.com/MoscaDotTo/Winapp2 (restructured 2026, 27 files)
- https://github.com/raphire/win11debloat (49.6k stars, v2026.06.24)
- https://github.com/farag2/Sophia-Script-for-Windows (v7.1.5)
- https://github.com/lostindark/DriverStoreExplorer (v1.0.26, Smart Cleanup)
- https://github.com/qarmin/czkawka (31.7k stars, GTK deprecated, Krokiet active)
- https://github.com/no-faff/InstallerClean (WPF/.NET 10, MSI/MSP orphan cleanup)

Commercial and benchmarks:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/ (v5.5.0, May 2026)
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/ (94.33% accuracy)
- https://uninstalr.com/changelog/ (v3.0, Apr 2026)
- https://www.hibitsoft.ir/Uninstaller.html (v4.0.10, 89.90% accuracy)
- https://www.martau.com/document/total-uninstall.php (v7.6.2, 85.96% accuracy)

Security:
- https://github.com/dotnet/announcements/issues/370 (CVE-2025-55247, Linux-only DoS)
- https://github.com/dotnet/announcements/issues/403 (CVE-2026-45490, SDK named pipe)
- https://github.com/dotnet/announcements/issues/404 (CVE-2026-45491, TarFile symlink)
- https://www.penligent.ai/hackinglabs/cve-2026-50656/ (RoguePlanet, unpatched)
- https://www.picussecurity.com/resource/blog/bluehammer-redsun-windows-defender-cve-2026-33825-zero-day-vulnerability-explained
- https://github.com/bleachbit/bleachbit/security/advisories/GHSA-j8jc-f6p7-55p8 (CVE-2025-32780)
- https://github.com/stryker-mutator/stryker-net/issues/3117 (xUnit v3 still broken)
- https://cybersecuritynews.com/windows-disk-cleanup-tool-vulnerability-exploited/

Platform and dependencies:
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100 (.NET 10 WPF)
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10 (.NET 10 breaking changes)
- https://www.nuget.org/packages/System.IO.Hashing/10.0.9
- https://www.nuget.org/packages/System.Management/10.0.9
- https://www.nuget.org/packages/Prefetch (v2026.5.2, Eric Zimmerman)
- https://github.com/microsoft/winget-cli/issues/4965 (list --json, closed not planned)
- https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-840/

## Open Questions
None that block prioritization. The CVE-2025-55247 correction (Linux-only, not Windows) simplifies the existing P2 roadmap item — it can be dropped without replacement.
