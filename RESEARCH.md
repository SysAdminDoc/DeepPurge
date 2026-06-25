# Research — DeepPurge

## Executive Summary

DeepPurge is a production-grade Windows uninstaller and system cleaner (C#/.NET 8 WPF, ~31k LOC, 130 tests, MIT) with strong safety fundamentals (centralized SafetyGuard, USN journal install monitoring, WizTree-speed MFT disk analysis) and a dual GUI+CLI architecture. Its install-monitor flagship (USN journal + snapshot diff) is a genuine differentiator that only commercial tools like Total Uninstall and Revo Pro match — and DeepPurge does it open-source.

The 2026 Uninstalr benchmark reveals most uninstallers find fewer than 65% of leftovers. DeepPurge's signature database (50 profiles) puts it ahead of BCU (61%) and Revo Pro (63%) but well behind the leaders (Uninstalr 94%, HiBit 90%, Total Uninstall 86%). The accuracy gap is addressable: Uninstalr achieves 94% through relevance-filtered remnant attribution (knowing which files belong to which program), not brute-force scanning.

**Critical finding: SafetyGuard and 5 other files hardcode 71 `C:\` paths. Systems with Windows on a different drive bypass all safety protections and all cleaners miss their targets. This is a P0 security bug.**

**Top 10 opportunities in priority order:**
1. Replace 71 hardcoded `C:\` paths with dynamic resolution via `Environment.GetFolderPath()` / `%SystemRoot%`
2. Expand leftover signature database to 200+ profiles with publisher-based grouping
3. Add portable app detection (Uninstalr's unique capability; 8/9 competitors score 0)
4. Wire existing .resx localization infrastructure into XAML (20 strings exist, 0 consumed)
5. Migrate to .NET 10 LTS before .NET 8 EOL (Nov 2026)
6. Add version-aware shared-path protection to prevent deleting shared settings (BCU #758)
7. Add ARM64 build target for Windows on ARM (Surface Pro, Snapdragon X Elite)
8. Replace 57 remaining empty `catch { }` blocks with `Log.Warn()` for field debugging
9. Implement Hunter Mode (drag-crosshair-to-identify) — Revo's signature UX with no OSS equivalent
10. Add delete-on-reboot for locked files via `PendingFileRenameOperations`

## Product Map

- **Core workflows:** Uninstall (single/batch/forced/manifest-replay) → leftover scan (signature-matched + heuristic) → backup → delete; System cleanup (junk/evidence/winapp2); Disk analysis (MFT-speed/duplicate/empty); System management (drivers/startup/services/tasks/shortcuts/orphans/firewall/PATH); Install monitoring (USN journal + snapshot diff)
- **User personas:** Power user cleaning a personal PC; IT technician on USB stick (portable mode); sysadmin scripting via CLI/Intune/SCCM; privacy-conscious user removing traces
- **Platforms:** Windows 10/11 x64 only. .NET 8 (`net8.0-windows10.0.17763.0`), self-contained ~71MB single-file executables. No ARM64 target.
- **Distribution:** GitHub Releases (GUI + CLI). winget/Scoop manifests staged but unsubmitted. No code-signing certificate.
- **Key integrations:** winget (enrichment + upgrade), Scoop (filesystem scan), winapp2.ini (2,500+ community cleaners), pnputil (drivers), schtasks (scheduled cleaning), Windows toast notifications, GitHub Releases API (update checker)

## Competitive Landscape

**Uninstalr** (benchmark leader, 94% accuracy, free, closed-source) — Built by Macecraft (jv16 PowerTools, 20+ years). Achieves accuracy through relevance-filtered remnant attribution, not brute-force scanning. Detects 15 app source types including portable apps (unique — 8/9 competitors score 0). Learn: relevance filtering, portable app detection. Avoid: closed-source model, self-published benchmark.

**BCUninstaller** (19.9k stars, Apache 2.0, C# .NET 8 WinForms) — Factory pattern for multi-source discovery (Registry/Store/Steam/Scoop/Chocolatey). Top community requests: dark mode (#228, 7+ years open — DeepPurge advantage), standalone orphan scan (#736), version-aware shared-path protection (#758), delete-on-reboot (#129). Learn: factory architecture, community engagement. Avoid: WinForms UI debt, 2+ year release gaps eroding trust.

**Win11Debloat** (49.6k stars, MIT, PowerShell) — Most popular project in the space by far. Key innovation: auto-detect previously applied tweaks with one-click revert. Learn: undo/revert pattern, Intune-first enterprise design, massive GitHub traction from PowerShell accessibility. Avoid: PowerShell-only limits extensibility.

**Revo Pro** ($25/yr, 63% accuracy) — Hunter Mode (drag-crosshair-to-identify) is iconic UX with no OSS equivalent. Logs Database (67k trace logs for 12.5k programs) is their accuracy moat. Learn: Hunter Mode UX. Avoid: paid model with worse benchmark accuracy than free alternatives.

**Total Uninstall** ($30 lifetime, 86% benchmark accuracy) — Gold standard for install monitoring. Tree-view snapshot diff visualization is the UX benchmark for this feature. Learn: diff visualization, backup/restore per program. Avoid: no free tier limits adoption.

**BleachBit** (6k stars, GPL, Python/GTK) — Cookie manager with selective retention is #1 requested cleaner feature. Expert mode toggle (hide dangerous ops from novices). Symlink safety guards are a CVE-class fix. Learn: expert mode, symlink safety, CleanerML extensibility. Avoid: GPL incompatibility (DeepPurge is MIT), Python performance.

**FluentCleaner** (2k stars, WinUI 3) — Only 2 months old, 2k stars shows demand for modern-looking cleaners. AI-powered rule explanations (Groq/Llama). Global path exclusion whitelist. Junk growth history tracker. Learn: global exclusions, junk history tracking, modern UI expectations. Avoid: WinUI 3 dependency adds complexity.

**Wise Program Uninstaller** (free) — System Slimming module removes Windows built-in bloat (language packs, optional features, sample media). Distinct from third-party uninstall but serves same user intent. Learn: system slimming category.

## Security, Privacy, and Reliability

**Hardcoded `C:\` paths — CRITICAL (Verified):** 71 occurrences across 6 files (`SafetyGuard.cs`, `JunkFilesCleaner.cs`, `FileLeftoverScanner.cs`, `EvidenceRemover.cs`, `InstallSnapshotEngine.cs`, `SecureDelete.cs`). SafetyGuard's protected-directory list, all junk/evidence scanner paths, and install-monitor roots assume `C:\Windows`, `C:\Users`, `C:\ProgramData`. Systems with Windows on D:\ or other drives bypass all safety protections entirely. The fix pattern already exists in the codebase — `BrowserExtensionScanner`, `ShortcutRepairScanner`, `AutorunScanner`, `ServiceScanner`, `DriverStoreScanner`, and `PathCleaner` all correctly use `Environment.GetFolderPath()`.

**Silent catch blocks — HIGH (Verified):** 57 empty `catch { }` blocks remain across 20 Core files after the round-2 fix that addressed 22 in RegistryLeftoverScanner. 204 total catch blocks — 28% are silent. Field debugging is impossible when errors are silently swallowed. Pattern: replace with `catch (Exception ex) { Log.Warn(...); }`.

**Duplicate leftover signature — LOW (Verified):** `Data/leftover-signatures.json` has Spotify listed at both position 9 and position 32. Causes double-matching.

**Localization infrastructure unwired — MEDIUM (Verified):** `Properties/Resources.resx` contains 20 UI strings with `Resources.Designer.cs` accessor. Zero references in XAML (`{x:Static}`) or code-behind (`Properties.Resources.`). The infrastructure was built but never connected. CHANGELOG claims "Ready for Crowdin submission" but strings aren't consumed.

**No ARM64 build — MEDIUM:** Only `win-x64` RID published. Windows on ARM (Surface Pro X, Snapdragon X Elite/Plus, Qualcomm Oryon) is growing. The codebase has native P/Invoke (MFT structs, USN journal) that may need ARM64 validation.

**Stale ROADMAP entry — LOW (Verified):** "Orphaned artifact scanner" (P1) shipped in commit `ce5382f` but remains in ROADMAP.md as incomplete.

## Architecture Assessment

**SafetyGuard `C:\` assumption:** The central safety choke-point hardcodes `C:\Windows`, `C:\Users`, `C:\Program Files`, etc. Must be migrated to `Environment.GetFolderPath(SpecialFolder.Windows)`, `Environment.SystemDirectory`, `Environment.GetEnvironmentVariable("SystemRoot")`, etc. This is the highest-priority fix in the codebase.

**MainViewModel monolith:** Two partials total ~1,666 lines with 15+ feature areas. Extract per-panel ViewModels (DriverPanelViewModel, DuplicatePanelViewModel, etc.) that MainViewModel composes via dependency injection or manual construction. MainWindow code-behind (1,044 lines) should similarly delegate panel-specific logic.

**InstalledProgramScanner monolith:** Single static method handles all registry sources (HKLM/HKCU/HKU/WoW64). BCU's factory pattern (one class per source) is more extensible and testable. Refactor into `IAppDiscoverySource` implementations.

**LeftoverSignatureDb matching:** Uses `string.Contains` for alias matching — coarse, no publisher-based grouping, no fuzzy matching. Uninstalr's relevance-filtering approach (attribution, not proximity) produces dramatically better results (26 vs 6,972 leftovers).

**Test coverage:** 130 tests in 868 lines for ~31k LOC. 10 test files covering parsers, sanitizers, and SafetyGuard. **Zero tests for destructive operations:** UninstallEngine, FileLeftoverScanner, RegistryLeftoverScanner, SecureDelete, BackupManager, EvidenceRemover, ContextMenuCleaner, ServiceScanner, ScheduledTaskScanner, InstalledProgramScanner. Testing philosophy correctly avoids mocking Windows, but safety-critical logic (SafetyGuard path validation, DeleteOptions threading) could have more unit coverage.

**winget integration fragility:** `PackageManagerScanner.cs` tries `winget list --output json` which is not a supported CLI option. The correct programmatic API is `Microsoft.Management.Deployment` COM or `winget export` for JSON.

## Rejected Ideas

- **Multi-pass DoD wipes** (PrivaZer) — Obsolete on SSDs; wastes write cycles. Already in project's "will not ship" list.
- **Software Updater module** (IObit) — Scope creep; winget handles this. DeepPurge detects upgrades, not performs them.
- **Generic registry cleaner** (CCleaner) — No legitimate performance benefit; Microsoft's official stance is against them. Only clean registry tied to specific uninstalled programs. Source: Microsoft Q&A, HowToGeek.
- **MFT/FAT table entry cleanup** (PrivaZer) — Raw disk manipulation too risky for safety-first tool.
- **Cross-platform support** (BleachBit) — Windows-specific by design (registry, services, drivers, COM).
- **Video/image similarity detection** (Czkawka) — Scope creep beyond system cleanup; different audience.
- **AI-powered rule explanations** (FluentCleaner) — External API dependency contradicts zero-telemetry philosophy. Source: FluentCleaner Groq integration.
- **Country of origin display** (Uninstalr) — Politically charged feature with accuracy concerns.
- **MSIX distribution** — Sandboxes out HKLM autorun edits; actively harmful for this app.
- **xUnit v3 migration** — Stryker.NET compatibility issues remain (stryker-net#3117). Stay on v2.
- **WinUI 3 migration** — WPF is sufficient for a system utility; WinUI 3 adds complexity without proportional benefit. FluentCleaner proves WinUI 3 works but DeepPurge's WPF theme system already delivers dark/light/HC.
- **Tray icon / background daemon** — Scope creep toward "always-running" software users are trying to remove. Scheduled tasks via CLI are the right pattern.

## Sources

**Benchmarks:**
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://uninstalr.com/blog/uninstalr-2-0-or-why-making-this-windows-software-uninstaller-was-the-hardest-thing-i-have-ever-done/

**OSS Competitors:**
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/bleachbit/bleachbit
- https://github.com/qarmin/czkawka
- https://github.com/Raphire/Win11Debloat
- https://github.com/builtbybel/FluentCleaner
- https://github.com/farag2/Sophia-Script-for-Windows
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/MoscaDotTo/Winapp2

**Commercial Competitors:**
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.ashampoo.com/en-us/uninstaller
- https://uninstalr.com/
- https://www.martau.com/document/total-uninstall.php
- https://www.ccleaner.com/
- https://www.wisecleaner.com/wise-program-uninstaller.html
- https://www.hibitsoft.ir/Uninstaller.html
- https://privazer.com/en/

**Community Signal:**
- https://github.com/Klocman/Bulk-Crap-Uninstaller/issues/228 (dark mode, 7yr)
- https://github.com/Klocman/Bulk-Crap-Uninstaller/issues/758 (shared-path data loss)
- https://github.com/Klocman/Bulk-Crap-Uninstaller/discussions/287 (orphan scan)
- https://learn.microsoft.com/en-us/answers/questions/5854721/ (registry cleaners harmful)

**Platform & Ecosystem:**
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0
- https://github.com/microsoft/CsWin32
- https://docs.velopack.io/packaging/overview
- https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-840/

**Security:**
- https://nvd.nist.gov/vuln/detail/CVE-2025-30399
- https://github.com/dotnet/core/blob/main/release-notes/8.0/cve.md
- https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-45491
- https://www.cvedetails.com/cve/CVE-2025-32780/ (BleachBit DLL hijack)
- https://gbhackers.com/windows-task-scheduler-flaw/ (CVE-2025-33067)
- https://cyberpress.org/regpwn-vulnerability/ (RegPwn registry symlink)
- https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-88r2.pdf

## Open Questions

1. **Relevance-filtered remnant attribution** — Uninstalr achieves 94% accuracy by attributing files to specific programs (not just scanning nearby paths). What heuristics can DeepPurge use? Options: registry installer metadata (InstallSource, InstallLocation, DisplayIcon paths), installer family fingerprinting (NSIS/InnoSetup/MSI log locations), and cross-referencing the signature DB. This is the core technical investment for accuracy parity.
2. **winget COM API viability** — `Microsoft.Management.Deployment` COM requires specific registration. Does it work from an `asInvoker` CLI exe? Needs live testing.
3. **ARM64 P/Invoke compatibility** — MFT structs (`USN_RECORD_V2` with `Pack=1`) and COM interop (IShellLinkW) in FastDiskAnalyzer and ShortcutRepairScanner may need ARM64 validation. Unknown risk without hardware.
