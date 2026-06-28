# Research — DeepPurge

## Executive Summary
DeepPurge is a safety-first Windows cleanup and uninstall workstation tool: .NET 10, WPF GUI, headless CLI, portable/local data paths, registry/file rollback primitives, dry-run flows, winapp2/community cleaner support, package-manager enrichment, and local-only release packaging. Verified current code shows that prior roadmap ideas have shipped since the last research file: xUnit v3, cookie whitelist plumbing, program notes, installed-program signature column, modern bundled cleaners, JSON CLI expansion, deletion manifest restore in Core/CLI, tray scheduled cleaning, and package-manager fallback flags. The highest-value direction is now trust completion rather than feature breadth: harden admin command launches, surface existing recovery/settings controls in the GUI, make update/database provenance visible, restore accessible focus indicators, and repair docs/release truth. Top opportunities: 1. harden GUI winget launch; 2. restore shared WPF focus indicators; 3. add GUI deletion-manifest recovery; 4. add GUI settings/privacy controls for existing AppSettings; 5. add winapp2 provenance/backups; 6. add release/package manifest validator; 7. add log retention/scrub controls; 8. fix stale docs; 9. replace hardcoded system-drive copy; 10. add About/update trust cues.

## Product Map
- Core workflows: uninstall/bulk/forced uninstall; leftover registry/file cleanup; junk/evidence cleaning; winapp2/custom/bundled cleaners; drivers/startup/services/tasks/shortcuts/duplicates/disk/repair/schedule/history/install-monitor/package-manager panels.
- User personas: Windows power users, IT technicians with portable USB workflows, admins scripting via CLI/Intune/SCCM/Task Scheduler, privacy-focused users who need visible safety and rollback.
- Platforms and distribution: Windows 10/11 x64 and ARM64, .NET 10 WPF GUI plus CLI, self-contained and slim framework-dependent binaries, GitHub Releases with SHA256SUMS, local winget/Scoop manifests not yet submitted.
- Key integrations and data flows: HKLM/HKCU/HKU uninstall keys, MSI metadata, WinVerifyTrust, winget/Scoop/Steam/Epic/GOG enrichment, pnputil/schtasks/reg.exe/SFC/DISM/chkdsk, Restart Manager, USN/MFT scanning, GitHub Releases API, raw GitHub winapp2.ini download, `%LocalAppData%\DeepPurge` or portable `Data\`.

## Competitive Landscape
- BCUninstaller: strong uninstall/remnant depth; v6.2 added invalid-uninstaller presets, certificate/integrity columns, Scoop path detection, and hardening for startup/uninstall-list loading. DeepPurge should keep matching safety metadata and list robustness; avoid BCU's long-standing dark-mode/localization UI debt.
- BleachBit: strongest cleaner UX signal in 6.0 with Cookie Manager, deeper Chromium/Firefox cleaning, Vivaldi/Zen cleaners, and a 6.0.1 beta privileged-delete security fix. DeepPurge already shipped cookie whitelist plumbing; learn from making privacy-preserving choices discoverable in the GUI and continue treating delete chokepoints as security-critical.
- FluentCleaner: modern WinUI 3 cleaner with database-update UX, settings, localization, and fresh visual language; its 26.06.04 hotfix fixed settings-page and failed-database-update crashes. DeepPurge should learn from update-state resilience and translation workflow; avoid WinUI deployment churn and AI/cloud cleaner generation.
- DriverStoreExplorer: v1.0.26 added WinGet update support plus SHA256-verified in-place self-update with rollback. DeepPurge should borrow provenance/hash/rollback cues for downloaded databases and release trust surfaces; self-applying updates remain blocked separately.
- Revo/HiBit/Uninstalr commercial uninstallers: traced uninstall, forced uninstall, backup/restore, and accuracy messaging are category trust primitives. DeepPurge's CLI/core manifest restore is a strength; the GUI still needs parity.
- WinUtil/Win11Debloat/Winhance: dominate Windows optimization attention with scripts, presets, WhatIf/dry-run, deployment config, and broad debloat posture. DeepPurge should keep the safety-first workstation-cleaner position and avoid broad debloat/ISO customization already rejected by project policy.
- Czkawka and analogous duplicate/media cleaners: show high polish around scan/export/filter flows and cross-platform duplicate workflows. DeepPurge should borrow scan-state clarity and export consistency where it fits; media optimization and cross-platform work do not match the Windows-admin product.
- winapp2 community database: remains the de facto Windows cleaner-definition corpus and is already integrated. DeepPurge should add local provenance/backup metadata because the current updater writes raw downloaded content directly to `DataPaths.Cleaners`.

## Security, Privacy, and Reliability
- Verified bug/risk: `src/DeepPurge.App/Views/MainWindow.xaml.cs:1106-1110` launches `cmd.exe /k winget upgrade --id "{p.PackageId}" ...` with a package id sourced from scanner data. Use `ProcessStartInfo.ArgumentList` or tight package-id validation and avoid command-shell interpolation for admin-launched package upgrades.
- Verified accessibility risk: `src/DeepPurge.App/Themes/BaseStyles.xaml:237-247` and `:549-560` set `FocusVisualStyle="{x:Null}"` for GridSplitter/DataGridCell without a visible replacement. This is a bounded focus-indicator bug, separate from the blocked full WCAG/Narrator pass.
- Verified recovery gap: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs:65-159` and `src/DeepPurge.Cli/Program.cs:835-870` can list/load/dry-run/restore deletion manifests, but no GUI panel exposes this recovery flow; GUI rollback trust is weaker than CLI/Core capability.
- Verified privacy gap: `src/DeepPurge.Core/App/DataPaths.cs:26-33` centralizes logs/settings, and activity/deletion manifests record program names and paths, but there is no retention or scrub setting. Privacy tooling should let users prune or redact local operational history.
- Verified provenance gap: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs:39-55` downloads raw winapp2.ini and overwrites the local file after a size check, without storing commit SHA, SHA256, previous-file backup, or visible source metadata.
- Verified UX truth bug: `src/DeepPurge.App/Views/MainWindow.xaml.cs:226-229` displays `Scan C:\` even though system-drive handling elsewhere is dynamic. Non-C Windows installs see misleading copy.
- Verified docs drift: `CONTRIBUTING.md:28,40,97`, `ARCHITECTURE.md:12,154`, and `packaging/README.md:32` still mention xUnit 2.9, GitHub Actions workflows, old test counts, and a placeholder that does not match current manifests.
- Missing guardrails: no local validator proves packaging placeholder replacement, GitHub release asset/hash consistency, README/test/doc truth, or release manifest readiness before shipping.

## Architecture Assessment
- Core and CLI are ahead of GUI for several trust controls: settings export/import/show, cookie whitelist/min-age/excluded paths/program notes, deletion restore, JSON outputs, and doctor checks are scriptable but not centrally discoverable in WPF.
- `MainWindow.xaml` plus `MainWindow.xaml.cs` remains the broadest user-facing boundary, so small GUI parity items should be added surgically before any larger ViewModel decomposition already parked in `Roadmap_Blocked.md`.
- `AppSettings.Save()` writes through a temp file (`src/DeepPurge.Core/App/AppSettings.cs:19-31`), so a GUI settings/privacy panel can reuse existing persistence without inventing storage.
- Tests cover newer primitives (`CookieWhitelistTests`, `ProgramNotesTests`, `DeletionManifestTests`, xUnit v3), but there is no rendered GUI automation harness; new GUI features should isolate logic in testable helpers and visually verify manually.
- Documentation and packaging are now the largest non-code trust gap. `.github/workflows` is gone, but top-level docs still describe CI/release automation that no longer exists.
- Category coverage: security, privacy, accessibility, observability, testing, docs, distribution, offline/update resilience, and upgrade trust are active recommendations; i18n, plugin ecosystem, mobile/cross-platform, multi-user collaboration, and large migration work are consciously rejected or already blocked below.

## Rejected Ideas
- WinUI 3 rewrite — FluentCleaner still shows settings/update crash churn; WPF remains simpler for a portable admin binary.
- Self-applying Velopack/updater work — already parked in `Roadmap_Blocked.md`; add trust cues and provenance now, not a second updater path.
- Code-signing certificate work — already blocked on purchase/enrollment; do not create duplicate roadmap items.
- Full WCAG 2.2 certification item — already blocked pending contrast-theme and Narrator testing; keep only the bounded shared focus-style fix active.
- Full XAML `.resx` localization wiring — already blocked; adding another i18n item would duplicate `Roadmap_Blocked.md`.
- Plugin marketplace or arbitrary cleaner marketplace — external executable/delete rules add supply-chain risk; current local JSON cleaner/signature files are sufficient.
- Multi-user collaboration or cloud team dashboard — DeepPurge already scans relevant local machine/user hives where applicable, but shared state would add accounts, sync, and privacy burden to a local workstation tool.
- Broad debloat presets, ISO customization, or policy-based tweak engine — WinUtil/Win11Debloat/Winhance own that category and it conflicts with DeepPurge's removal/cleanup safety posture.
- Mobile, Linux, or macOS ports — the product depends on Windows registry, MSI, shell, WMI/service, driver store, and Win32 APIs.
- Cloud sync/telemetry/community footprint database — adds privacy and infrastructure burden without matching local-first positioning.
- Multi-pass DoD wipes and SSD free-space wiping — already rejected in `ROADMAP.md` because they are harmful or obsolete for the target threat model.

## Sources
OSS and adjacent projects:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/tag/v6.2
- https://github.com/bleachbit/bleachbit/releases/tag/v6.0.0
- https://www.bleachbit.org/news/bleachbit-600
- https://www.bleachbit.org/news/bleachbit-601-beta
- https://github.com/builtbybel/FluentCleaner/releases/tag/26.06.04
- https://github.com/lostindark/DriverStoreExplorer/releases/tag/v1.0.26
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/Raphire/Win11Debloat/pull/611
- https://github.com/ChrisTitusTech/winutil
- https://github.com/no-faff/InstallerClean
- https://github.com/qarmin/czkawka
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/941

Commercial and benchmarks:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.hibitsoft.ir/Uninstaller.html
- https://www.ccleaner.com/ccleaner/features
- https://www.ashampoo.com/en-us/uninstaller

Standards and platform APIs:
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist
- https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexa
- https://learn.microsoft.com/en-us/windows/win32/rstmgr/restart-manager-portal
- https://learn.microsoft.com/en-us/windows/package-manager/winget/
- https://www.w3.org/TR/WCAG22/#focus-appearance

Dependencies, advisories, and community:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://xunit.net/releases/v3/3.2.2
- https://stryker-mutator.io/blog/stryker-net-mtp-runner/
- https://www.windowscentral.com/microsoft/windows-11/fluent-cleaner-may-be-the-best-ccleaner-alternative-for-windows-11-users

## Open Questions
None that block prioritization or implementation.
