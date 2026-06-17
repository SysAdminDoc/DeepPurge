# Research — DeepPurge

## Executive Summary

DeepPurge is a mature, safety-first Windows uninstaller and system cleaner (C#/.NET 8, WPF, 15.8k LOC, 111 tests) with competitive feature parity against BCUninstaller and partial parity against Revo Pro. Its strongest assets are the centralized SafetyGuard validation layer, USN journal-based install monitoring, and WizTree-speed MFT disk analysis — all production-grade. The codebase is clean, well-structured (Core/App/CLI split), and ships both a GUI and headless CLI.

The highest-value direction is **leftover detection accuracy** — the Uninstalr 2026 benchmark shows most tools (including BCU at 61.3%) find fewer than 65% of leftovers. A curated signature database of known leftover paths per application would be the single highest-ROI feature. Secondary priorities: prepare for Windows 11 Administrator Protection (SMAA registry isolation breaks HKCU-based scanning), upgrade to .NET 9 for performance/tooling gains, and add hash caching to the duplicate finder for instant re-scans.

**Top 10 opportunities in priority order:**
1. Leftover signature database (known paths per app) — accuracy from 60% to 90%+
2. Administrator Protection readiness — HKCU→HKEY_USERS\{SID} migration
3. True disk footprint per program (replace inaccurate EstimatedSizeKB)
4. Orphaned artifact scanner (services, tasks, firewall rules, PATH entries)
5. .NET 9 upgrade + CommunityToolkit.Mvvm 8.4.2 partial properties
6. CsWin32 type-safe PInvoke (eliminate hand-rolled struct alignment risks)
7. Hash caching for duplicate finder (instant re-scans)
8. Velopack auto-updater with delta updates from GitHub Releases
9. System Slimming module (curated Windows component removal)
10. Free space wipe with storage-type awareness (HDD vs SSD)

## Product Map

- **Core workflows:** Uninstall (single/batch/forced) → leftover scan → backup → delete; System cleanup (junk/evidence/winapp2); Disk analysis (MFT/duplicate/empty); System management (drivers/startup/services/tasks/shortcuts); Install monitoring (USN journal + snapshot)
- **User personas:** Power user cleaning up a personal PC; IT technician servicing client machines (portable mode); sysadmin scripting cleanup via CLI/Intune
- **Platforms:** Windows 10/11 x64, .NET 8, self-contained single-file executables (~66 MB each)
- **Distribution:** GitHub Releases (GUI + CLI), winget/Scoop manifests staged but not yet submitted
- **Key integrations:** winget (enrichment + upgrade detection), Scoop (filesystem scan), winapp2.ini (community cleaner database), pnputil (driver store), schtasks (scheduled cleaning), Windows toast notifications

## Competitive Landscape

**Bulk-Crap-Uninstaller (BCU)** — 19.7k stars, Apache 2.0, .NET 8 WinForms. The dominant OSS uninstaller. Factory pattern for app discovery (registry, Store, Steam, Scoop, Chocolatey as separate factories) is architecturally superior to DeepPurge's monolithic InstalledProgramScanner. Learn from: multi-source detection, view presets (orphaned/invalid), app rating system. Avoid: WinForms UI (universally criticized as "Christmas tree on LSD"), 8-minute startup time, 61.3% leftover accuracy.

**Revo Uninstaller Pro** — $10-25/yr. Hunter Mode (drag-crosshair-to-identify) is a unique UX differentiator worth replicating. Pre-built Logs Database of known leftover paths is the key accuracy advantage. Learn from: Hunter Mode, signature database, multi-level backup. Avoid: dated UI, only 63% accuracy in benchmarks despite commercial claims.

**Czkawka/Krokiet** — 31.5k stars, MIT/GPL, Rust. Hash caching with (path, size, mtime) invalidation makes second duplicate scans instant. Reference folder concept (mark "keep" vs "search" directories) prevents accidental deletion of master copies. Learn from: hash caching, reference folders, group selection strategies. Avoid: scope expansion into image/video/music similarity (different audience).

**PrivaZer** — Free, donation-supported. Deepest privacy cleaning available: MFT/FAT table entries, free space residuals, USB device history, storage-type-aware overwrite (HDD/SSD/USB auto-detection). Learn from: free space wipe, USB device history cleanup, smart selective cleanup. Avoid: 2-10 hour initial scan times, forensic-grade MFT manipulation (too risky for safety-first tool).

**CCleaner** — $0-65/yr. Health Check dashboard (aggregate score across Privacy/Space/Speed/Security) is excellent onboarding UX. Application-specific cleaning rules as plugins. Learn from: Health Check dashboard concept, scheduled cleaning. Avoid: telemetry, ads, bundleware — CCleaner's trust collapse is the #1 reason users seek alternatives.

**Wise Program Uninstaller** — Free. Three-mode uninstall (Safe/Force/Custom-folder) where Custom lets users point at any folder to uninstall unlisted software. System Slimming module with curated Windows component removal. Learn from: Custom-folder uninstall, System Slimming. Avoid: mediocre scanning accuracy (62.8%).

**DriverStoreExplorer (RAPR)** — 11k stars, GPL-2.0. IDriverStore interface with multiple backend implementations (native API, DISM, PnPUtil) with graceful fallback. Learn from: multi-API abstraction pattern. Feature-complete for its scope; DeepPurge's existing DriverStoreScanner already covers this.

## Security, Privacy, and Reliability

**Administrator Protection (SMAA) — CRITICAL, Verified:** Windows 11 is rolling out a new elevation model where `HKCU` in elevated processes maps to a System Managed Administrator Account, not the real user. InstalledProgramScanner.cs reads `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` — this will return the wrong data under SMAA. DataPaths.cs uses `%LocalAppData%` which will resolve to the SMAA profile, not the user's. Fix: obtain the real user's SID from the launching process token and access `HKEY_USERS\{UserSID}` instead.

**SafetyGuard path traversal — Likely:** `SafetyGuard.IsPathSafeToDelete()` uses `Path.GetFullPath()` for normalization but does not explicitly reject `..` segments before normalization. A crafted path like `C:\Users\Public\..\..\Windows\System32\config\SAM` would normalize to a protected path and be caught, but the normalization itself could have edge cases with junction points. Add explicit `..` rejection before normalization.

**SecureDelete slack space — Verified:** The wipe algorithm overwrites file content but not cluster slack (unused bytes in the last cluster). On NTFS with 4KB clusters, a 1-byte file leaves 4095 bytes of potentially recoverable slack. Acceptable for the threat model (not forensic-grade), but should be documented.

**UninstallEngine timeout — Verified:** 10-minute hardcoded timeout in `RunUninstallerAsync`. Visual Studio uninstaller, Oracle products, and some enterprise MSIs routinely exceed this. Should be configurable per-program or with a generous default (30 minutes).

**Winget table parsing fragility — Verified:** `PackageManagerScanner.ParseWingetTable()` relies on header line column positions. Winget 1.8+ supports `--output json`. Migration eliminates a class of silent parse failures.

**Backup validation gap — Verified:** `BackupManager.BackupRegistryKey()` runs `reg.exe export` but only checks the exit code, not that the file contains valid .reg content. A zero-byte or truncated backup would pass validation.

## Architecture Assessment

**Module boundaries:** Core has a WPF dependency via `IconExtractor` (returns `ImageSource`). This prevents Core from being consumed by non-WPF hosts (e.g., a future web API or service). Extract icon data as byte arrays and let the consumer convert to ImageSource.

**InstalledProgramScanner monolith:** All program sources (HKLM, HKCU, HKU, WoW64) are handled in one static method. BCU's factory pattern (one class per source: registry, Store, Steam, Scoop) is more extensible and testable. Consider splitting into `IAppDiscoveryFactory` implementations.

**MainViewModel size:** The two partials total 1,581 lines. The v0.9 extensions partial already has 10 feature areas. Consider extracting per-panel ViewModels (DriverPanelViewModel, DuplicatePanelViewModel, etc.) that the MainViewModel composes.

**Test coverage gaps:** 111 tests for 15.8k LOC (ratio: 1 test per 143 lines). Safety-critical code (SafetyGuard, SecureDelete, UninstallEngine) has reasonable coverage, but PackageManagerScanner, BrowserExtensionScanner, EvidenceRemover, and ContextMenuCleaner have zero tests. The duplicate finder has tests but no mutation testing to verify the tests actually catch regressions.

**Missing: integration tests.** No tests verify the CLI commands end-to-end. No tests verify panel switching or ViewModel→View binding. Xunit.StaFact 3.0.13 enables WPF-thread testing.

## Rejected Ideas

- **App relocation between drives** (Ashampoo) — Extremely complex (registry, COM, shortcuts, services). High breakage risk. Out of scope for removal-focused tool.
- **Software Updater module** (IObit/CCleaner) — Duplicates winget's job. DeepPurge already detects winget upgrades.
- **Generic registry cleaner** (CCleaner) — Multiple forum sources confirm no legitimate performance benefit. Risk of breaking apps. DeepPurge should only clean registry tied to specific uninstalled programs.
- **Bundleware/PUP detection database** (IObit) — Requires maintaining malware-like signatures. Better left to Defender. False positive risk.
- **Similar image/video/music finder** (Czkawka) — Scope creep. Different audience.
- **Cross-platform** (BleachBit) — DeepPurge is Windows-specific by design (registry, services, drivers).
- **MFT/FAT table entry cleanup** (PrivaZer) — Raw disk manipulation. Too risky for safety-first tool.
- **Real-time file system watcher** (WinDirStat 2.x) — Not aligned with on-demand cleanup model.
- **Cookie whitelist manager** (BleachBit 6.0) — Too browser-specific. Evidence removal already covers this.
- **XAML Islands / WinUI 3 controls** — Airspace issues, deployment complexity. Native WPF Fluent theme in .NET 9 is sufficient.
- **xUnit v3 migration** — Still in pre-release. Wait for stable.

## Sources

**OSS Competitors:**
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/bleachbit/bleachbit
- https://github.com/qarmin/czkawka
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/windirstat/windirstat
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/Raphire/Win11Debloat
- https://github.com/memstechtips/Winhance
- https://github.com/farag2/Sophia-Script-for-Windows

**Commercial Competitors:**
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.iobit.com/en/advanceduninstaller.php
- https://www.ashampoo.com/en-us/uninstaller
- https://geekuninstaller.com/
- https://www.ccleaner.com/
- https://privazer.com/
- https://www.jam-software.com/treesize
- https://www.wisecleaner.com/wise-program-uninstaller.html

**Benchmarks & Reviews:**
- https://www.uninstalr.com/benchmark (2026 leftover detection benchmark, 13 tools)

**Platform & Ecosystem:**
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90
- https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0
- https://devblogs.microsoft.com/dotnet/announcing-the-dotnet-community-toolkit-840/
- https://github.com/microsoft/CsWin32
- https://docs.velopack.io/packaging/overview
- https://blogs.windows.com/windowsdeveloper/2025/05/19/enhance-your-application-security-with-administrator-protection/
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://www.nuget.org/packages/System.IO.Hashing
- https://github.com/VerifyTests/Verify
- https://www.nuget.org/packages/dotnet-stryker
- https://www.nuget.org/packages/Xunit.StaFact
- https://www.nuget.org/packages/velopack

## Open Questions

1. **Leftover signature database format** — Should this be a shipped JSON file, a downloadable community database (like winapp2.ini), or both? The choice affects maintenance burden and community contribution model.
2. **Administrator Protection timeline** — Microsoft temporarily disabled SMAA in retail channels (June 2026). When it re-enables, DeepPurge's HKCU-based scanning will silently return wrong results. Priority depends on Microsoft's rollout schedule.
3. **Velopack PerMachine + requireAdministrator interaction** — Does Velopack's PerMachine install mode work correctly when the app manifest requires admin elevation? Needs live testing.
