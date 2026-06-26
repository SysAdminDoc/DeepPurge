# Research — DeepPurge

## Executive Summary
DeepPurge is a Windows-only, administrator-oriented uninstaller and system cleaner with a broad WPF surface, a headless CLI, safety gating, install snapshots, winapp2 support, driver/startup/shortcut/duplicate panels, and recent net10/ARM64/security work. The strongest direction is not broader feature sprawl; it is making the newest safety and integration surfaces trustworthy end-to-end. Top opportunities: route all GUI cleanup through the same `DeleteOptions` pipeline as CLI cleanup; centralize recursive delete/wipe walking so child junctions/symlinks cannot escape a safe root; repair registry-link detection; remove remaining fixed-drive assumptions; wire locked-file recovery where deletes fail; make the shell context menu launch real work; bring CLI discovery up to GUI parity; expose custom JSON cleaners with schema/tests; make Amcache/BAM discovery truthful; and align build/docs/packaging with .NET 10 and ARM64.

## Product Map
- Core workflows: uninstall/batch/forced removal; leftover scan and selective deletion; junk/evidence/winapp2 cleanup; disk, duplicate, empty-folder, driver, startup, shortcut, service, task, orphan, repair, schedule, install-monitor, health, shell, and history flows.
- User personas: power users cleaning personal Windows PCs; IT technicians running portable tools; sysadmins scripting CLI/Intune/SCCM checks; privacy-focused users reviewing traces before deletion.
- Platforms and distribution: Windows 10/11, `net10.0-windows10.0.17763.0`, WPF GUI plus CLI, self-contained GitHub release assets, winget/Scoop manifests, unsigned by default.
- Key integrations and data flows: registry uninstall keys/HKU profiles, winget/Scoop/portable/game enrichment, winapp2.ini, `pnputil`, `schtasks`, SFC/DISM/chkdsk, USN journal install tracing, Restart Manager, GitHub Releases, local logs/backups/settings.

## Competitive Landscape
- BCUninstaller: strong bulk uninstall, orphan discovery, and multi-source app detection. Learn from its source-adapter model and community-reported failure modes such as shared Blender settings deletion and locked-file delete-on-reboot requests. Avoid external helper startup stalls like its Everything Search issue.
- BleachBit and winapp2: strong preview/delete workflow, CleanerML extensibility, and broad community cleaner coverage. Learn from declarative cleaner validation and user review before delete. Avoid GPL code reuse and overly aggressive rules without global exclusions.
- FluentCleaner: modern OSS cleaner built around winapp2 databases, global exclusions, and custom databases/extensions. Learn from right-click exclusion UX and transparent database management. Avoid AI explanations or remote service dependencies that contradict DeepPurge's local, zero-telemetry posture.
- Revo Uninstaller Pro and Total Uninstall: commercial benchmarks for install monitoring, forced uninstall, backup/restore, logs, and monitored-program diffs. Learn from monitored install visualizations and recovery flows. Avoid paywall-driven bloat and marketing claims without observable evidence.
- Uninstalr: useful benchmark pressure around leftover attribution and portable/previously removed app detection. Learn from evidence-first removal previews. Treat self-published benchmarks as directional, not authoritative.
- Win11Debloat and Sophia Script: strong Windows tuning adoption through auditable scripts and revert guidance. Learn from rollback-first changes and Intune-friendly automation. Avoid broad service-debloat presets that conflict with DeepPurge's conservative safety philosophy.
- DriverStoreExplorer and Czkawka: focused tools with clear bounded scope: driver-store cleanup and fast duplicate/empty/similar-file scanning. Learn from tight domain workflows. Avoid expanding DeepPurge into unrelated media-similarity cleanup.

## Security, Privacy, and Reliability
- [Verified] GUI junk cleanup bypasses the shared safe-delete pipeline. `src/DeepPurge.App/Views/MainWindow.xaml.cs` `CleanJunk_Click` calls `Directory.Delete(file.Path, true)` and `File.Delete(file.Path)` directly, ignoring footer `DryRunEnabled`, `SecureDeleteEnabled`, progress, activity log, cancellation, and `JunkFilesCleaner.DeleteJunkSafe` in `src/DeepPurge.App/ViewModels/MainViewModel.cs`.
- [Verified] Recursive destructive paths still rely on `SearchOption.AllDirectories` or `Directory.Delete(..., recursive:true)` in multiple modules (`SecureDelete.cs`, `Winapp2Parser.cs`, `CleanerDefinition.cs`, `SystemSlimmer.cs`, `EvidenceRemover.cs`, `JunkFilesCleaner.cs`, `UninstallEngine.cs`, `BrowserExtensionScanner.cs`). Root-level `IsReparsePoint` checks do not protect child junctions under an otherwise safe directory.
- [Verified] Registry symbolic-link protection is not implemented according to the comment. `SafetyGuard.IsRegistrySymlink` calls `RegQueryInfoKeyW` with no class buffer and returns `result != 0`; Microsoft documents `RegQueryInfoKey` class output and `RegOpenKeyEx` `REG_OPTION_OPEN_LINK` as the mechanisms relevant to registry links.
- [Verified] Locked-file recovery exists but is not integrated. `LockedFileResolver.QueueDeleteOnReboot` and `GetLockingProcesses` are unused outside `LockedFileResolver.cs`, so failed file deletes do not offer process attribution or delayed delete despite the changelog claim.
- [Verified] Context-menu shell integration writes `"DeepPurge.exe" --target "%1"` in `ShellExtensionRegistrar.cs`, but `App.xaml.cs` never reads `StartupEventArgs.Args` and `MainWindow` has no target-selection path.
- [Verified] Some fixed-drive assumptions remain after the broad dynamic path pass: `WindowsRepairEngine.ResolveCommand` uses `chkdsk.exe C: /scan`; `DeepPurge.Cli.Program.CmdSnapshotAsync` and `MainViewModel.Extensions` probe `UsnJournalReader.IsSupported(@"C:\")`.
- Missing guardrails: destructive operations need one file-tree primitive, one registry-link primitive, one failed-delete recovery path, and tests that prove dry-run/delete/secure-delete behavior is identical across GUI and CLI.
- Recovery and rollback needs: use existing backups/restore points for uninstall flows, add user-visible locked-file recovery choices, and require custom cleaners to support dry-run, schema validation, and exclusion review before execution.

## Architecture Assessment
- The biggest boundary issue is business logic in `MainWindow.xaml.cs`. Junk cleanup and several panel actions still perform destructive work in code-behind while safer ViewModel/Core paths exist.
- `SafetyGuard` is a policy checker, not a deletion primitive. A `SafeFileTree` or similar Core service should own enumeration, child reparse skipping, dry-run sizing, secure delete, recycle/delete-on-reboot fallback, and progress reporting.
- CLI discovery is behind the GUI. `CmdListAsync` and `CmdUninstallAsync` use only `InstalledProgramScanner.GetAllInstalledPrograms()`, while the GUI enriches through `PackageManagerScanner.EnrichAsync`; README promises a full headless surface.
- New custom-cleaner and forensic-discovery surfaces are incomplete. `CleanerDefinitionRunner` is not exposed in GUI/CLI/tests, and `AmcacheParser` checks for `Amcache.hve` but reads BAM registry paths and is not called by the orphan flow.
- Release truth is drifting: project files target .NET 10, but `Build.ps1`, `BUILD.bat`, `README.md`, `CONTRIBUTING.md`, `CLAUDE.md`, and `.github/workflows/codeql.yml` still reference .NET 8 or x64-only assumptions; XAML splash/sidebar text still shows `v0.8.1`.
- Testing gaps: existing roadmap items already cover mutation and snapshot tests. New tests should focus on GUI dry-run wiring, child reparse traversal, registry-link detection, failed-delete recovery, shell `--target`, custom cleaner schema/execution, and Amcache/BAM fixtures.
- Accessibility, i18n, observability, and packaging: High Contrast and AutomationProperties exist; `.resx` infrastructure exists but is unwired and should become real UI bindings; logs/doctor/crash logs exist but need failed-delete visibility; packaging needs .NET 10/ARM64 truth.
- Plugin ecosystem, offline resilience, multi-user, and migration: prefer local JSON cleaner definitions over a marketplace; keep all cleaners offline-first; preserve HKCU/HKU awareness rather than adding multi-user administration UX; track net10 support through the Microsoft lifecycle page.

## Rejected Ideas
- Generic registry cleaner (Microsoft support policy) — contradicts the safety-first philosophy; keep registry deletion tied to specific uninstall/remnant evidence.
- Multi-pass DoD-style wipes (NIST SP 800-88r2, BleachBit SSD guidance) — already rejected by project policy; single-pass/secure erase guidance is enough for this app's threat model.
- AI rule explanations (FluentCleaner discussion) — external API dependency and user distrust do not fit a local privacy tool.
- WinUI rewrite (FluentCleaner) — DeepPurge's WPF theme system is adequate; the gaps are integration and safety, not framework choice.
- Full extension marketplace (FluentCleaner extensions) — creates trust and supply-chain surface; local signed/schema-validated cleaner files are a better fit.
- Broad debloat/service-disabling presets (Win11Debloat/Sophia ecosystem) — useful adjacent domain, but DeepPurge should remain cleanup/uninstall/repair focused and avoid fragile Windows feature toggles.
- Cross-platform or mobile support (BleachBit/Czkawka contrast) — DeepPurge depends on Windows registry, services, drivers, COM, Restart Manager, and admin flows.
- Software updater module (IObit/Revo) — winget upgrade detection/actions are enough; a parallel updater increases maintenance and trust risk.

## Sources
OSS:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/758
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/129
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/832
- https://github.com/bleachbit/bleachbit
- https://docs.bleachbit.org/cml/cleanerml.html
- https://docs.bleachbit.org/doc/winapp2ini.html
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/builtbybel/FluentCleaner
- https://github.com/builtbybel/FluentCleaner/releases
- https://github.com/raphire/win11debloat
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/EricZimmerman/AmcacheParser

Commercial and community:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.revouninstaller.com/revo-uninstaller-pro-full-version-history/
- https://www.martau.com/document/total-uninstall.php
- https://www.iobit.com/product-manuals/iu-help/
- https://uninstalr.com/blog/comparing-windows-uninstallers-and-making-uninstalr/
- https://github.com/TemporalAgent7/awesome-windows-privacy

Platform, security, and dependencies:
- https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points
- https://learn.microsoft.com/en-us/windows/win32/fileio/symbolic-links
- https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeyexa
- https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regqueryinfokeya
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rrp/d5ce9dcc-1f90-4f5a-b076-cc1d2c9b4195
- https://learn.microsoft.com/en-us/windows/package-manager/winget/
- https://github.com/microsoft/winget-cli
- https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core
- https://learn.microsoft.com/en-us/dotnet/core/rid-catalog
- https://support.microsoft.com/en-us/topic/microsoft-support-policy-for-the-use-of-registry-cleaning-utilities-0485f4df-9520-3691-2461-7b0fd54e8b3a
- https://csrc.nist.gov/pubs/sp/800/88/r2/final

## Open Questions
None that block prioritization. ARM64 runtime behavior and registry-link fixture creation still need implementation-time validation on Windows.
