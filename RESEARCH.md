# Research — DeepPurge

## Executive Summary

DeepPurge is a production-grade Windows uninstaller and system cleaner (C#/.NET 8 WPF, 13.5k LOC, 116 tests, MIT) with strong safety fundamentals (centralized SafetyGuard, USN journal install monitoring, WizTree-speed MFT disk analysis) and a dual GUI+CLI architecture. The 2026 Uninstalr benchmark reveals that most uninstallers — including market leaders — find fewer than 65% of leftovers; DeepPurge's signature database (50 profiles) puts it ahead of BCU (61%) and Revo Pro (63%) but well behind the top 3 (Uninstalr 94%, HiBit 90%, Total Uninstall 86%). The highest-value direction is **leftover detection accuracy to 90%+**, which requires expanding the signature database and adding portable app detection. Secondary priorities: hardening against CVE-2025-30399 (.NET RCE), fixing a thread-safety bug in DuplicateFinder, migrating to .NET 10 LTS, and adding runtime safety features (symlink traversal guards, registry symlink detection, supply chain hardening).

**Top 10 opportunities in priority order:**
1. Pin .NET runtime ≥8.0.17 to close CVE-2025-30399 untrusted-path RCE
2. Fix DuplicateFinder._cache thread-safety (concurrent access causes data corruption)
3. Add symlink/junction traversal guards in file deletion paths (BleachBit CVE-class fix)
4. Add registry symlink detection before writes (TOCTOU/privilege escalation vector)
5. Enable NuGet lock file + audit + package source mapping (supply chain hardening)
6. Expand leftover signature database to 200+ profiles for benchmark accuracy ≥85%
7. Add portable app detection (only Uninstalr does this; 8/9 tools score 0)
8. Migrate to .NET 10 LTS (net10.0-windows10.0.17763.0) — .NET 8 EOL Nov 2026
9. Migrate deprecated Microsoft.Toolkit.Uwp.Notifications to Windows App SDK toast API
10. Add "always keep" protection flag per program (BCU #935, table-stakes safety UX)

## Product Map

- **Core workflows:** Uninstall (single/batch/forced) → leftover scan → backup → delete; System cleanup (junk/evidence/winapp2); Disk analysis (MFT/duplicate/empty); System management (drivers/startup/services/tasks/shortcuts); Install monitoring (USN journal + snapshot)
- **User personas:** Power user cleaning a personal PC; IT technician servicing client machines (portable mode); sysadmin scripting cleanup via CLI/Intune
- **Platforms:** Windows 10/11 x64, .NET 8 (net8.0-windows10.0.17763.0), self-contained 66MB single-file executables
- **Distribution:** GitHub Releases (GUI + CLI), winget/Scoop manifests staged but not yet submitted
- **Key integrations:** winget (enrichment + upgrade detection), Scoop (filesystem scan), winapp2.ini (community cleaners), pnputil (drivers), schtasks (scheduled cleaning), Windows toast notifications

## Competitive Landscape

**Uninstalr** (benchmark leader, 94.3% accuracy) — Detects 15 app source types including Steam/Epic/GOG/PortableApps. Portable app detection is unique — all 8 other tested tools scored 0. Real-time space calculation corrects Windows's inaccurate size data. Learn from: portable app detection, per-path exclusion preview, 15-source detection. Avoid: closed-source model limits community trust.

**HiBit Uninstaller** (89.9% accuracy, free) — Complete tool suite including Process Manager, Context Menu Manager, Scheduled Task Manager, Empty Folder Cleaner, all in one package. Second-place benchmark accuracy while being completely free. Learn from: comprehensive tool breadth in a single app. Avoid: no open-source transparency.

**BleachBit** (GPL, v6.0.0 April 2026) — Cookie Manager for selective retention is the #1 requested cleaner feature. Expert mode toggle hides dangerous operations from novice users. Symlink/junction safety guards prevent CVE-class bugs in deletion paths. RFC 3161 timestamped code signing. Learn from: expert mode toggle, symlink safety, CleanerML extensibility. Avoid: GPL license incompatibility (DeepPurge is MIT).

**BCUninstaller** (19.7k stars, Apache 2.0) — Factory pattern for multi-source app discovery (registry, Store, Steam, Scoop, Chocolatey). Top enhancement requests: "always keep" flag (#935), automatic orphan scan without uninstall (#736), context menu manager (#756). Learn from: factory pattern architecture, community-driven enhancement requests. Avoid: unsigned binaries, 61% benchmark accuracy, 2+ year release gap creating trust erosion.

**FluentCleaner** (WinUI 3, ~2k stars) — Junk growth history tracker showing trend over time. AI-powered rule explanations via Groq/Llama. Global path exclusion whitelist. Anti-bloat philosophy: "fewer features, honest features." Learn from: junk growth tracking, global exclusions, anti-bloat messaging. Avoid: WinUI 3 dependency adds complexity without proportional benefit for system utilities.

**Win11Debloat** (22k+ stars) — Automatic detection of previously applied tweaks with one-click revert. SYSTEM account mode for Intune/SCCM deployment. 80+ AppX removals including Copilot/Recall/Bing. Learn from: tweak detection + revert pattern, Intune-first enterprise design. Avoid: PowerShell-only architecture limits extensibility.

**Revo Pro** ($25/yr, 63% accuracy) — Logs Database (67k trace logs for 12.5k programs) is the accuracy differentiator. Hunter Mode (drag-crosshair-to-identify) is iconic UX. Learn from: Hunter Mode, pre-built trace database. Avoid: paid model with worse benchmark accuracy than free alternatives; closed database.

## Security, Privacy, and Reliability

**CVE-2025-30399 — CRITICAL (Verified):** Untrusted search path RCE in .NET 8.0 ≤8.0.16. DeepPurge runs elevated — this is a privilege escalation vector. Pin runtime to 8.0.17+ via `<RuntimeFrameworkVersion>` or migrate to .NET 10. Source: NVD.

**DuplicateFinder thread-safety — HIGH (Verified):** `_cache` dictionary at `Core/FileSystem/DuplicateFinder.cs:41` is a `Dictionary<string, HashCacheEntry>` accessed without synchronization. Concurrent calls to `FindAsync` (e.g., from UI re-scan while a scan is running) can corrupt the cache. Source: Code review.

**Registry symlink attacks — HIGH (Likely):** Elevated process writes to enumerated registry keys without checking for symlinks. An unprivileged user can create registry symlinks redirecting writes to critical system locations (e.g., service ImagePath). CVE-2025-6231 (Lenovo Vantage) and CVE-2026-20815 (Windows CamSvc) exploited this exact pattern. Fix: check for `REG_LINK` class via `NtQueryKey` before writes in `RegistryLeftoverScanner.cs`, `UninstallEngine.cs`. Source: Security research agent.

**Symlink/junction traversal in deletion — HIGH (Likely):** File deletion paths (`FileLeftoverScanner`, `JunkFilesCleaner`, `EvidenceRemover`) do not check for reparse points before recursive deletion. `DuplicateFinder.SafeEnumerate` correctly skips reparse points, but this guard is not present in all deletion paths. BleachBit shipped a CVE-class fix for this exact issue. Source: BleachBit 6.0 release notes.

**88 silent catch blocks — MEDIUM (Verified):** `RegistryLeftoverScanner.cs` alone has 16+ `catch { }` blocks with no logging. Field debugging is impossible when errors are silently swallowed. Source: Code review.

**ManagementObject disposal leak — MEDIUM (Verified):** `SystemRestoreManager.cs` line 48: `ManagementObject` instances in foreach loop are never disposed. WMI COM objects leak if not disposed. Source: Code review.

**Microsoft.Toolkit.Uwp.Notifications deprecated — MEDIUM (Verified):** Package 7.1.3 is in archived/unmaintained repo. No security patches issued since archive. Migration path: Windows App SDK `AppNotificationManager`. Source: NuGet.

**Leftover signature database duplicate — LOW (Verified):** `Data/leftover-signatures.json` has Spotify listed twice (entries 9 and 32). Source: Code review.

**Supply chain gaps — MEDIUM (Verified):** No NuGet lock file (`packages.lock.json`), no `NuGetAudit`, no package source mapping. Dependency confusion/typosquatting possible. Source: Security research.

## Architecture Assessment

**InstalledProgramScanner monolith:** `Core/Registry/InstalledProgramScanner.cs` handles all sources (HKLM/HKCU/HKU/WoW64) in one static method. BCU's factory pattern (one class per source) is more extensible and testable. Refactor into `IAppDiscoveryFactory` implementations.

**MainViewModel size:** Two partials total ~1,580 lines with 10+ feature areas. Extract per-panel ViewModels (DriverPanelViewModel, DuplicatePanelViewModel, etc.) that MainViewModel composes.

**Test coverage:** 116 tests for 13.5k LOC (1:116 ratio). **11 destructive-operation classes have zero tests:** UninstallEngine, FileLeftoverScanner, RegistryLeftoverScanner, SecureDelete, BackupManager, EvidenceRemover, ContextMenuCleaner, RegistryHunter, ServiceScanner, ScheduledTaskScanner, InstalledProgramScanner. SafetyGuard and parser tests are solid; deletion orchestration is untested.

**No external signature loading:** `LeftoverSignatureDb` loads only from embedded resource. No mechanism for users to add custom signatures or for the community to contribute profiles without recompilation.

**winget integration fragility:** `PackageManagerScanner.cs` still tries `winget list --output json` which is not a supported option as of mid-2026. The correct programmatic API is `Microsoft.Management.Deployment` COM or `winget export` for JSON. Source: .NET ecosystem research.

## Rejected Ideas

- **Multi-pass DoD wipes** (PrivaZer) — Obsolete on SSDs; wastes write cycles. Already in project's "will not ship" list.
- **Software Updater module** (IObit) — Scope creep; winget already handles this. DeepPurge should detect upgrades, not perform them.
- **Generic registry cleaner** (CCleaner) — No legitimate performance benefit; risk of breaking apps. Only clean registry tied to specific uninstalled programs.
- **MFT/FAT table entry cleanup** (PrivaZer) — Raw disk manipulation too risky for safety-first tool.
- **Cross-platform support** (BleachBit) — DeepPurge is Windows-specific by design (registry, services, drivers).
- **Video/image similarity detection** (Czkawka) — Scope creep beyond system cleanup; different audience.
- **AI-powered rule explanations** (FluentCleaner) — External API dependency contradicts zero-telemetry philosophy. Source: FluentCleaner Groq integration.
- **Country of origin display** (Uninstalr) — Politically charged feature with accuracy concerns. Source: Uninstalr features page.
- **MSIX distribution** — Already in "will not ship" list; sandboxes out HKLM autorun edits.
- **xUnit v3 migration** — Stryker.NET 4.14.2 has known compatibility issues with xUnit v3 (stryker-net#3117). Stay on v2 until resolved.

## Sources

**Benchmarks:**
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/

**OSS Competitors:**
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/bleachbit/bleachbit (v6.0.0 release notes)
- https://github.com/qarmin/czkawka
- https://github.com/windirstat/windirstat
- https://github.com/Raphire/Win11Debloat
- https://github.com/builtbybel/FluentCleaner
- https://github.com/farag2/Sophia-Script-for-Windows
- https://github.com/lostindark/DriverStoreExplorer

**Commercial Competitors:**
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.iobit.com/product-manuals/iu-help/
- https://www.ashampoo.com/en-us/uninstaller
- https://geekuninstaller.com/
- https://www.ccleaner.com/

**Platform & Ecosystem:**
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0
- https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-840/
- https://github.com/microsoft/CsWin32
- https://docs.velopack.io/packaging/overview
- https://blogs.windows.com/windowsdeveloper/2025/05/19/enhance-your-application-security-with-administrator-protection/
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://www.nuget.org/packages/velopack

**Security:**
- https://nvd.nist.gov/vuln/detail/CVE-2025-30399
- https://github.com/stryker-mutator/stryker-net/issues/3117
- https://github.com/microsoft/win32metadata/issues/427

## Open Questions

1. **winget COM API adoption** — Should DeepPurge use `Microsoft.Management.Deployment` COM interop instead of CLI parsing? The COM API is the official programmatic interface but adds COM registration complexity. Needs live testing with the CLI's `asInvoker` manifest.
2. **.NET 10 vs .NET 9** — .NET 10 is LTS (supported through 2028); .NET 9 STS ends Nov 2026. The ROADMAP currently targets .NET 9 — should it skip to .NET 10 directly?
3. **Leftover signature contribution model** — Should external signatures load from `DataPaths.Cleaners` (community-contributed JSON files) alongside the embedded database? This affects the P1 accuracy improvement path.
