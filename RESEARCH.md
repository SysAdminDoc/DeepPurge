# Research - DeepPurge

## Executive Summary
DeepPurge v0.9.0 is a Windows 10/11 cleanup and uninstall workstation: WPF GUI, headless CLI, portable data paths, centralized deletion gates, rollback primitives, package-manager enrichment, install tracing, custom/winapp2 cleaners, driver/startup/service/task tooling, and local release packaging. The strongest current shape is safety-first local administration; the highest-value direction is closing trust gaps in command execution, registry rollback, cleaner provenance, and package-manager workflows before adding new cleanup domains. Priority opportunities: 1. harden scheduled-job wrapper arguments; 2. harden GUI winget upgrade launch; 3. route cleaner registry deletes through a backup/symlink-safe helper; 4. restore visible WPF focus indicators; 5. expose deletion-manifest recovery in the GUI; 6. expose settings/privacy controls in the GUI; 7. add winapp2 provenance and rollback; 8. add cleaner-definition validation; 9. add source-native package-manager uninstall; 10. fix stale release/docs/package guidance.

## Product Map
- Core workflows: uninstall/bulk/forced uninstall; leftover registry/file cleanup; junk/evidence/system-slimming cleanup; winapp2/custom/bundled cleaners; driver store, startup, services, scheduled tasks, shell context menus, browser extensions, duplicate files, disk analysis, repair, install monitoring, update checks, history, and scheduled cleaning.
- User personas: Windows power users, IT technicians with portable USB workflows, admins scripting via CLI/Task Scheduler/Intune/SCCM, and privacy-focused users who need dry-run, rollback, and local-only operation.
- Platforms and distribution: Windows 10/11 x64 and ARM64; .NET 10 WPF GUI with `requireAdministrator`; CLI with `asInvoker`; self-contained single-file builds; GitHub Releases, winget singleton manifest, Scoop manifest, optional Authenticode signing.
- Key integrations and data flows: HKLM/HKCU/HKCR uninstall and cleanup keys, MSI metadata, WinVerifyTrust, winget/Scoop/Chocolatey/Steam/Epic/GOG enrichment, `pnputil`, `schtasks`, `reg.exe`, PowerShell appx/task/firewall commands, SFC/DISM/chkdsk, Restart Manager, USN/MFT scanning, GitHub Releases API, raw GitHub winapp2.ini download, `%LocalAppData%\DeepPurge` or portable `Data\`.

## Competitive Landscape
- BCUninstaller: broad uninstall/remnant depth, invalid-uninstaller handling, certificate/integrity columns, Scoop path handling, and resilient exports. DeepPurge should keep matching trust metadata and loader resilience; avoid plugin/theme/localization sprawl until the core GUI recovery flows are complete.
- BleachBit: strongest cleaner-specific privacy UX, Cookie Manager, cleaner-definition heritage, and explicit warnings around wipe-free-space behavior. DeepPurge should make cookie/history retention controls discoverable and avoid promoting free-space fills.
- Revo/HiBit/Ashampoo/IObit commercial uninstallers: traced installs, forced uninstall, backup/restore, software health, browser extension cleanup, and scheduled cleaning are table-stakes UX. DeepPurge has many primitives already; GUI discoverability and reliable rollback are the main gaps.
- DriverStoreExplorer: shows the right release-trust pattern for an admin tool: WinGet support, hashes, verified updates, and rollback. DeepPurge should borrow hash/provenance cues for releases and winapp2 updates without adding a self-applying updater path.
- FluentCleaner: modern cleaner UX, database updates, settings, translations, and recent hotfixes around settings/update failure. DeepPurge should learn from failure-state handling; WPF remains the better portable admin stack.
- UniGetUI/winget/Scoop/Chocolatey: users expect source-native install, update, and uninstall once package-manager identity is shown. DeepPurge currently enriches rows and launches winget upgrades but should remove synthetic/source-managed apps through their native managers with strict argument construction.
- WinUtil/Win11Debloat/Winhance: dominate broad Windows optimization with presets, WhatIf/dry-run, import/export, and rollback requests. DeepPurge should reuse the preset/preview lesson for scheduled cleaning but avoid becoming a general tweak or ISO customization tool.
- Czkawka/winapp2 ecosystem: scan-state clarity, exclusions, cleaner-rule provenance, and local definition validation matter when users trust third-party or editable cleanup rules.

## Security, Privacy, and Reliability
- Verified P0: `src/DeepPurge.Core/Schedule/ScheduleManager.cs:120-129` writes `ScheduleJob.CliArguments` verbatim into a `.cmd` wrapper. The comment says arguments are encoded, but `--args "clean junk & powershell ..."` remains batch syntax in a highest-privilege scheduled job. BatBadBut-class batch escaping issues and Microsoft Task Scheduler ExecAction argument separation support replacing this with tokenized arguments, a constrained preset, or a non-batch job runner.
- Verified P0: `src/DeepPurge.App/Views/MainWindow.xaml.cs:1106-1110` launches `cmd.exe /k winget upgrade --id "{p.PackageId}" ...` with package id text sourced from scanner data. Use `ProcessStartInfo.ArgumentList` or strict package-id validation and avoid shell interpolation.
- Verified P0/P1: `src/DeepPurge.Core/Cleaning/Winapp2Parser.cs:279-288`, `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs:163-172`, and `src/DeepPurge.Core/Shell/ContextMenuCleaner.cs:75-80` delete registry trees directly and record manifests, but do not consistently export backups first or check `SafetyGuard.IsRegistrySymlink` like `UninstallEngine.DeleteRegistryItem`. `DeletionManifest.RestoreFromManifest` relies on backup files, so registry rollback is incomplete for cleaner/context-menu paths.
- Verified P1: custom cleaner JSON is loaded from `DataPaths.Cleaners` without a schema/version/provenance surface in `CleanerDefinitionRunner.LoadAll`; SafetyGuard limits deletion, but there is no `cleaners validate` command to reject suspicious `RemoveSelf`, HKLM/HKCR, broad wildcard, malformed expansion, or unrecognized fields before users run third-party rules.
- Verified P1: `src/DeepPurge.App/Themes/BaseStyles.xaml:237-247` and `:549-560` set `FocusVisualStyle="{x:Null}"` for shared GridSplitter/DataGridCell styles without a visible replacement, conflicting with WCAG 2.2 focus appearance.
- Verified P1: `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs:65-159` and `src/DeepPurge.Cli/Program.cs:835-870` can list/load/dry-run/restore deletion manifests, but no GUI panel exposes this recovery path.
- Verified P1/P2: logs, activity history, deletion manifests, notes, cookie whitelist, excluded paths, and min-age defaults are persisted through `DataPaths`, `ActivityLog`, `DeletionManifest`, and `AppSettings`, but GUI users lack retention, scrub, import/export, and settings editing controls.
- Verified P2: synthetic Scoop/Chocolatey rows and winget identities are added in `PackageManagerScanner`, and CLI uninstall accepts package IDs, but there is no source-native uninstall path for package-manager-only rows. This creates a product mismatch with UniGetUI and the package-manager docs.
- Verified P2: `Winapp2Updater.UpdateAsync` downloads raw `Winapp2.ini` and overwrites the local file after only a size check. It should store source commit/date, SHA256, byte count, and previous-file backup.
- Verified P2: packaging docs and manifests still contain release placeholders and stale workflow/test wording; `dotnet list package --outdated` and `dotnet list package --vulnerable --include-transitive` both reported no package updates or known vulnerable packages.

## Architecture Assessment
- Command execution remains the riskiest boundary. External invocations are spread across GUI, CLI, ScheduleManager, BackupManager, repair, appx, firewall, package-manager, and uninstall code; new work should add small command-builder helpers with tests rather than patching string arguments ad hoc.
- Registry deletion needs a single helper that performs safety check, symlink check, pre-delete backup, actual delete, deletion-manifest record, and structured logging. `UninstallEngine` already approximates this; cleaner and shell paths should reuse the same behavior.
- Core and CLI are ahead of the GUI for trust controls: deletion restore, settings import/export, cookie whitelist, excluded paths, min-age controls, program notes, JSON output, doctor checks, and winapp2 updating need discoverable GUI surfaces.
- `MainWindow.xaml` and `MainWindow.xaml.cs` remain broad user-facing boundaries. Until the blocked ViewModel decomposition is safe to visually test, prefer helper-first changes with unit tests and small XAML additions.
- Cleaner ecosystem support is useful but should stay local and auditable: JSON schema, validation, provenance, dry-run diff, and per-rule risk labels fit the project; arbitrary remote cleaner marketplaces do not.
- Tests are strong around parser/safety primitives and xUnit v3 is current, but gaps remain around scheduled wrapper generation, command-line metacharacters, registry rollback helper behavior, cleaner validation, and package-manager-native uninstall command construction.

## Rejected Ideas
- User-facing free-space wipe - current direction should retire or quarantine `SecureDelete.WipeFreeSpaceAsync`; NIST media sanitization and BleachBit SSD warnings point to device secure erase, TRIM, encryption, or physical destruction instead.
- WinUI 3 rewrite - FluentCleaner shows active Windows App SDK/settings/update churn; WPF is still the better portable elevated-admin binary stack here.
- Self-applying updater - blocked separately; hash/provenance/About cues are enough for now.
- Code-signing certificate work - blocked on purchase/enrollment; do not duplicate it in active roadmap items.
- Full WCAG certification and full `.resx` XAML localization - already blocked for visual/Narrator/localization wiring passes; keep only bounded focus repair active.
- Plugin marketplace or arbitrary remote cleaner marketplace - supply-chain risk is too high for an elevated deletion tool; local JSON cleaners plus validation fit better.
- Broad debloat presets, ISO customization, policy tweak engine, or fleet dashboard - WinUtil/Win11Debloat/Winhance own that category and it conflicts with DeepPurge's cleanup/uninstall workstation posture.
- Mobile, Linux, or macOS ports - the product depends on Windows registry, MSI, AppX, shell, service, driver store, Task Scheduler, and Win32 APIs.
- Cloud sync, telemetry, accounts, or multi-user collaboration - adds data movement to a privacy-first local tool.

## Sources
OSS and adjacent projects:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/tag/v6.2
- https://github.com/bleachbit/bleachbit/releases/tag/v6.0.0
- https://docs.bleachbit.org/doc/shred-files-and-wipe-disks.html
- https://github.com/builtbybel/FluentCleaner/releases/tag/26.06.04
- https://github.com/lostindark/DriverStoreExplorer/releases/tag/v1.0.26
- https://github.com/marticliment/UniGetUI
- https://github.com/qarmin/czkawka/releases/tag/11.0.1
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/ChrisTitusTech/winutil/releases/tag/26.06.23
- https://github.com/Raphire/Win11Debloat/releases/tag/2026.06.24

Commercial and community:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://www.hibitsoft.ir/Uninstaller.html
- https://support.ashampoo.com/hc/en-us/articles/28056212092818-UnInstaller-16-Manual
- https://www.iobit.com/en/advanceduninstaller.php
- https://uninstalr.com/blog/windows-uninstaller-performance-comparison-2026/
- https://www.reddit.com/r/windows/comments/15ncnwf/i_compared_all_windows_uninstallers_and_the/

Standards and platform APIs:
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist
- https://learn.microsoft.com/en-us/windows/win32/taskschd/execaction
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create
- https://learn.microsoft.com/en-us/windows/package-manager/winget/uninstall
- https://docs.chocolatey.org/en-us/choco/commands/uninstall/
- https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexa
- https://learn.microsoft.com/en-us/windows/win32/rstmgr/restart-manager-portal
- https://nvd.nist.gov/vuln/detail/CVE-2024-24576
- https://csrc.nist.gov/pubs/sp/800/88/r2/final
- https://www.w3.org/TR/WCAG22/#focus-appearance

Dependencies and advisories:
- https://devblogs.microsoft.com/dotnet/dotnet-and-dotnet-framework-june-2026-servicing-updates/
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://xunit.net/releases/v3/3.2.2
- https://stryker-mutator.io/blog/stryker-net-mtp-runner/

## Open Questions
None that block prioritization or implementation.
