# Research — DeepPurge

## Executive Summary
DeepPurge is a safety-first Windows cleanup and uninstall workstation tool for Windows 10/11: .NET 10 WPF GUI, headless CLI, portable/local data paths, centralized SafetyGuard deletion gates, registry/file rollback primitives, dry-run flows, install tracing, package-manager enrichment, custom/winapp2 cleaner support, local packaging manifests, and no telemetry. Verified current code shows the prior research roadmap was mostly harvested into active work already; the highest-value direction is now trust completion and policy cleanup rather than adding broad new domains. Top opportunities, in priority order: 1. harden GUI winget launches; 2. restore visible WPF focus indicators; 3. expose deletion-manifest recovery in the GUI; 4. surface AppSettings privacy controls in the GUI; 5. add winapp2 provenance/backups; 6. retire the hidden free-space wipe primitive; 7. add release/package readiness validation; 8. add log/activity retention controls; 9. fix stale docs; 10. add About/update trust cues.

## Product Map
- Core workflows: uninstall/bulk/forced uninstall; leftover registry/file cleanup; junk/evidence/system-slimming cleanup; winapp2/custom/bundled cleaners; drivers/startup/services/tasks/shortcuts/duplicates/disk/repair/schedule/history/install-monitor/package-manager panels.
- User personas: Windows power users, IT technicians running portable USB workflows, admins scripting via CLI/Intune/SCCM/Task Scheduler, privacy-focused users who need visible safety, rollback, and local-only operation.
- Platforms and distribution: Windows 10/11 x64 and ARM64, .NET 10 SDK/runtime, WPF GUI with `requireAdministrator`, CLI with `asInvoker`, self-contained and slim publish paths, GitHub Releases with `SHA256SUMS.txt`, local winget/Scoop manifests awaiting external submission.
- Key integrations and data flows: HKLM/HKCU/HKU uninstall keys, MSI metadata, WinVerifyTrust, winget/Scoop/Chocolatey/Steam/Epic/GOG enrichment, `pnputil`, `schtasks`, `reg.exe`, SFC/DISM/chkdsk, Restart Manager, USN/MFT scanning, GitHub Releases API, raw GitHub winapp2.ini download, `%LocalAppData%\DeepPurge` or portable `Data\`.

## Competitive Landscape
- BCUninstaller: strong uninstall/remnant depth; v6.2 added invalid-uninstaller presets, certificate/integrity columns, Scoop path detection, startup/load guards, and export hardening. DeepPurge should keep matching trust metadata and loader resilience; avoid BCU's localization/theme debt and plugin complexity.
- BleachBit: strongest cleaner UX signal with 6.0 Cookie Manager, deeper browser cleaning, and explicit warnings around wipe-empty-space behavior. DeepPurge should keep privacy-preserving controls discoverable and avoid turning free-space fill into a prominent feature.
- FluentCleaner: modern WinUI 3 cleaner with database updates, translation workflow, settings, and active hotfixes for settings/database-update crashes. DeepPurge should learn from failed-update state handling; avoid WinUI deployment friction and cloud/AI cleaner generation.
- DriverStoreExplorer: v1.0.26 added WinGet update support and SHA256-verified in-place self-update with rollback. DeepPurge should borrow provenance/hash/rollback cues for database updates and About/update trust, while leaving self-applying updates in the blocked list.
- Revo/HiBit/IObit/Ashampoo commercial uninstallers: traced install, forced uninstall, backup/restore, install monitoring, browser extension management, and health/software-updater surfaces are table stakes. DeepPurge already has many of these primitives; GUI discoverability and release trust lag behind the commercial UX.
- WinUtil/Win11Debloat/Winhance: dominate Windows optimization attention with presets, WhatIf/dry-run, drift detection, rollback requests, and deployment config. DeepPurge should keep the narrower cleanup/uninstall workstation posture and avoid broad tweak/ISO/debloat expansion.
- Czkawka/Krokiet and duplicate-file adjacent tools: show value in scan-state clarity, exclusions, exportability, and explicit backend/platform notes. DeepPurge should borrow scan-state and exclusion UX patterns where they match local cleanup workflows.
- winapp2 ecosystem: remains the de facto cleaner-definition corpus and is already integrated; DeepPurge needs local provenance, hashes, and previous-file backups because `Winapp2Updater` overwrites the database directly.

## Security, Privacy, and Reliability
- Verified bug/risk: `src/DeepPurge.App/Views/MainWindow.xaml.cs:1106-1110` launches `cmd.exe /k winget upgrade --id "{p.PackageId}" ...` with a package id sourced from scanner data. Use `ProcessStartInfo.ArgumentList` or strict package-id validation and avoid shell interpolation for admin-launched upgrades.
- Verified accessibility risk: `src/DeepPurge.App/Themes/BaseStyles.xaml:237-247` and `:549-560` set `FocusVisualStyle="{x:Null}"` for GridSplitter/DataGridCell without a visible replacement. This is a bounded focus-indicator fix, separate from the blocked full WCAG/Narrator pass.
- Verified recovery gap: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs:65-159` and `src/DeepPurge.Cli/Program.cs:835-870` can list/load/dry-run/restore deletion manifests, but no GUI panel exposes this recovery flow.
- Verified privacy gap: `src/DeepPurge.Core/App/DataPaths.cs:26-33`, `ActivityLog`, and `DeletionManifest` store local paths/program names without retention or scrub controls. A privacy cleaner should let users prune operational history.
- Verified provenance gap: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs:39-55` downloads raw winapp2.ini and overwrites the local file after only a size check, with no commit SHA, SHA256, previous-file backup, or visible source metadata.
- Verified policy gap: `src/DeepPurge.Core/Safety/SecureDelete.cs:104-158` still exposes public `WipeFreeSpaceAsync`, and `CHANGELOG.md:164` records free-space wipe as shipped, while current product direction rejects SSD free-space wiping because it creates heavy writes and weaker guarantees than device secure erase/TRIM/encryption workflows.
- Verified UX truth bug: `src/DeepPurge.App/Views/MainWindow.xaml.cs:226-229` displays `Scan C:\` even though disk scanning resolves the system drive dynamically elsewhere.
- Verified docs drift: `CONTRIBUTING.md:28,40,97`, `ARCHITECTURE.md:12,154`, and `packaging/README.md:32` still mention xUnit 2.9, deleted GitHub Actions workflows, old test counts, and placeholder text that does not match current manifests.

## Architecture Assessment
- Core/CLI are ahead of GUI for trust controls: settings show/import/export, cookie whitelist, min-age, excluded paths, program notes, deletion restore, JSON outputs, and doctor checks are scriptable but not centrally discoverable in WPF.
- `MainWindow.xaml` and `MainWindow.xaml.cs` remain the broadest user-facing boundary; active GUI additions should be small, testable helper-first changes until the blocked ViewModel decomposition can be visually tested.
- `AppSettings.Save()` writes through a temp file (`src/DeepPurge.Core/App/AppSettings.cs:19-31`), so a GUI privacy/settings panel can reuse existing persistence safely.
- Tests cover many parser/safety primitives (`CookieWhitelistTests`, `ProgramNotesTests`, `DeletionManifestTests`, `SafetyGuardTests`, xUnit v3), but GUI automation is absent; new GUI features should isolate launch/provenance/settings logic behind unit-testable helpers.
- Dependency posture is clean: `dotnet list package --outdated` reported no updates, and `dotnet list package --vulnerable --include-transitive` reported no vulnerable packages. NuGet audit and lock files are enabled in `Directory.Build.props`.
- Category coverage: security, privacy, accessibility, observability, testing, docs, distribution/packaging, offline/update resilience, migration, and upgrade trust have active recommendations. i18n, plugin ecosystem, mobile/cross-platform, and multi-user collaboration are rejected or already blocked below.

## Rejected Ideas
- User-facing free-space wipe — `SecureDelete.WipeFreeSpaceAsync` should be retired or quarantined, not promoted; NIST media sanitization, Microsoft TRIM behavior, and BleachBit's SSD warning point users toward device secure erase, encryption, TRIM, or physical destruction for media sanitization.
- WinUI 3 rewrite — FluentCleaner still shows Windows App SDK/deployment and settings crash churn; WPF remains the simpler portable admin-binary stack.
- Self-applying Velopack/updater work — already parked in `Roadmap_Blocked.md`; add provenance and trust cues now, not a second updater path.
- Code-signing certificate work — already blocked on purchase/enrollment; do not duplicate it in active roadmap items.
- Full WCAG 2.2 certification — already blocked pending contrast-theme and Narrator testing; keep only the bounded shared focus-style repair active.
- Full `.resx` XAML localization wiring — already blocked; adding another i18n item would duplicate `Roadmap_Blocked.md`.
- Plugin marketplace or arbitrary remote cleaner marketplace — external executable/delete rules create supply-chain risk; current local JSON cleaner and signature-file support is enough.
- Broad debloat presets, ISO customization, or policy tweak engine — WinUtil/Win11Debloat/Winhance own that category and it conflicts with DeepPurge's removal/cleanup safety posture.
- Mobile, Linux, or macOS ports — the product depends on Windows registry, MSI, shell, WMI/service, driver store, and Win32 APIs.
- Cloud sync, telemetry, or team dashboard — adds accounts and data movement to a local-first privacy tool.

## Sources
OSS and adjacent projects:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/tag/v6.2
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/923
- https://github.com/bleachbit/bleachbit/releases/tag/v6.0.0
- https://docs.bleachbit.org/doc/shred-files-and-wipe-disks.html
- https://github.com/builtbybel/FluentCleaner/releases/tag/26.06.04
- https://github.com/lostindark/DriverStoreExplorer/releases/tag/v1.0.26
- https://github.com/Raphire/Win11Debloat/releases/tag/2026.06.24
- https://github.com/ChrisTitusTech/winutil/releases/tag/26.06.23
- https://github.com/qarmin/czkawka/releases/tag/11.0.1
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/no-faff/InstallerClean

Commercial and community:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.iobit.com/en/advanceduninstaller.php
- https://support.ashampoo.com/hc/en-us/articles/28056212092818-UnInstaller-16-Manual
- https://www.hibitsoft.ir/Uninstaller.html
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.reddit.com/r/windows/comments/15ncnwf/i_compared_all_windows_uninstallers_and_the/

Standards and platform APIs:
- https://csrc.nist.gov/pubs/sp/800/88/r2/final
- https://learn.microsoft.com/en-us/powershell/module/storage/optimize-volume
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexa
- https://learn.microsoft.com/en-us/windows/win32/rstmgr/restart-manager-portal
- https://learn.microsoft.com/en-us/windows/package-manager/winget/
- https://www.w3.org/TR/WCAG22/#focus-appearance

Dependencies and advisories:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://xunit.net/releases/v3/3.2.2
- https://stryker-mutator.io/blog/stryker-net-mtp-runner/

## Open Questions
None that block prioritization or implementation.
