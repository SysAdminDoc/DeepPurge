# Research — DeepPurge

## Executive Summary
DeepPurge is a Windows 10/11 local-first cleanup, uninstall, diagnostics, and recovery workstation built on .NET 10 WPF plus an as-invoker CLI. Its strongest current shape is safety-first admin cleanup: dry-run flows, SafetyGuard path blocking, package-manager enrichment, winapp2/custom cleaner validation, deletion manifests, release checksums, signature facts, and 321 passing xUnit tests are already in place. The highest-value direction is trust depth: preserve recovery integrity, make failures/support data easier to act on, tighten release/security gates, and close privacy features where competitors are more precise. Top opportunities in priority order: install-manifest identity guards, deterministic dependency audit gating, cleanup failure reporting, redacted support bundles, domain-level cookie preservation, inline destructive-action risk previews, release checksum verification, doc/test-count truth checks, extension permission risk labels, and more actionable health scoring.

## Product Map
- Core workflows: program uninstall and forced uninstall; leftover file/registry cleanup; junk, duplicate, browser, evidence, winapp2, and custom-cleaner cleanup; driver/startup/service/task/shell cleanup; scheduled cleaning; health checks; install tracing; deletion recovery; release/update trust checks.
- User personas: Windows power users, repair technicians, privacy-focused local users, admins scripting cleanup through CLI/Task Scheduler/Intune-style deployment, and maintainers preparing local GitHub/winget/Scoop releases.
- Platforms and distribution: Windows 10/11, .NET 10, elevated WPF GUI, as-invoker CLI, self-contained x64 portable release assets, GitHub Releases with `SHA256SUMS.txt`, optional Authenticode signing, draft winget/Scoop manifests.
- Key integrations and data flows: HKLM/HKCU uninstall keys, MSI metadata, WinVerifyTrust, winget/Scoop/Chocolatey inventory, `pnputil`, `schtasks`, `reg.exe`, PowerShell AppX/firewall/task commands, USN/Sysmon install tracing, browser profile stores, winapp2.ini, GitHub Releases API, `%LocalAppData%\DeepPurge` or portable `Data\`.

## Competitive Landscape
- BCUninstaller: strong broad uninstall inventory, remnant cleanup, source filtering, exports, and user-requested "always keep" and self-verification ideas. DeepPurge should learn from its safety controls and export depth; avoid cloning its full plugin/theme/config surface before core trust gaps close.
- Revo, Ashampoo UnInstaller, HiBit, and IObit: traced installs, forced uninstall, leftovers, restore points/backups, software-health surfaces, and browser extension cleanup are table-stakes in commercial uninstallers. DeepPurge should make its install manifests safer and its recovery/support flows clearer; avoid paywall-style feature fragmentation.
- BleachBit and CCleaner: mature cleaner UX emphasizes per-category preview, cookie preservation, visible errors, and user-understandable privacy choices. DeepPurge should move from whole cookie-database skips to domain-level preservation and expose per-item cleanup failures; avoid unsafe wipe promises.
- UniGetUI: sets expectations that displayed winget/Scoop/Chocolatey identity maps to native package-manager actions and health reporting. DeepPurge should continue source-native uninstall/update integration without becoming a full package-manager frontend.
- DriverStoreExplorer: demonstrates a narrow admin surface with provenance cues and release artifacts users can verify. DeepPurge should extend its local SHA256/signature facts into an online release-checksum verifier; avoid self-applying updates until the blocked updater work is testable.
- Czkawka: shows strong duplicate-finder expectations around progress, exclusions, contrast, and large-file feedback. DeepPurge already has duplicate safety tests; it should add clearer failed-item and scan-state reporting where destructive cleanup can partially succeed.
- WinUtil, Win11Debloat, Winhance, and Microsoft PC Manager: prove demand for presets, dry-run/WhatIf, health checks, and rollback messaging. DeepPurge should keep the actionable health/status model but reject broad Windows tweak dashboards.

## Security, Privacy, and Reliability
- Verified: project-level `dotnet list ... package --outdated` and `--vulnerable --include-transitive` checks for Core/App/Cli/Tests returned no package updates or known vulnerable packages on 2026-06-30, but solution-level `dotnet list DeepPurge.sln ...` failed during restore with `Cannot create a file when that file already exists`; release gating needs a deterministic project-level audit path.
- Verified: `src/DeepPurge.Core/InstallMonitor/InstallSnapshotEngine.cs` records `SnapshotEntry(Path, SizeBytes, LastWriteUtc)` and `ReplayRemoveAsync` deletes current paths after `SafetyGuard.IsPathSafeToDelete`, but it does not verify that the file still matches the captured manifest identity before deletion.
- Verified: `src/DeepPurge.Core/Privacy/EvidenceRemover.cs` skips all cookie database files when `AppSettings.CookieWhitelist` is non-empty; this preserves logins but does not implement competitor-style per-domain cookie retention.
- Verified: `src/DeepPurge.Core/Privacy/EvidenceRemover.cs` increments skipped counts for cleanup exceptions without preserving per-item reasons for the GUI/CLI; BleachBit users explicitly ask for easier access to errors.
- Verified: `.github/ISSUE_TEMPLATE/bug_report.yml` asks users to manually paste `deeppurgecli doctor` and log lines, while `SelfTest`, `PrivacyRedactor`, release trust facts, and package-source health already provide enough primitives for a redacted support bundle.
- Verified: destructive GUI paths in `src/DeepPurge.App/Views/MainWindow.xaml.cs` still use modal `MessageBox.Show` confirmation for driver removal, duplicate cleanup, winapp2 execution, bulk uninstall, and deletion-manifest restore; the app has to preserve safety while moving to inline risk preview, toast, and recovery.
- Verified: `dotnet test tests/DeepPurge.Tests/DeepPurge.Tests.csproj --no-restore -c Release --nologo --verbosity minimal` passed 321 tests with 0 warnings on 2026-06-30.
- Verified: README says 321 tests, while `CONTRIBUTING.md`, `ARCHITECTURE.md`, and `CHANGELOG.md` still contain 301/301+ test-count claims; release readiness should prevent stale trust claims.

## Architecture Assessment
- `DeepPurge.Core` has the right ownership for the next trust improvements: manifest identity, package/source diagnostics, cleanup error models, support-bundle creation, cookie database handling, release checksum comparison, and browser extension risk classification should live there with tests before WPF wiring.
- `MainWindow.xaml` and `MainWindow.xaml.cs` remain large, sensitive user-facing boundaries. Until the blocked ViewModel decomposition is safe to verify, UI changes should be narrow panels/commands backed by static WPF contract tests.
- Persistence is already centralized through `DataPaths`, `AppSettings`, `ActivityLog`, deletion manifests, snapshots, and logs. New support-bundle and health-history work should reuse these seams and the existing `PrivacyRedactor` rather than inventing new storage.
- Release packaging is close but still manually fragile: `Build.ps1`, package locks, NuGet audit settings, package manifests, version strings, `SHA256SUMS.txt`, and docs need one local validation path that avoids the current solution-level audit failure.
- Test coverage is strong for parsers, safety, release readiness, settings import/export, package commands, deletion recovery, and static WPF polish. Gaps remain around install-manifest replay identity, per-item cleanup failures, support bundle redaction, cookie DB mutation/locked-file degradation, browser extension permission risk, and doc truth gates.

## Rejected Ideas
- Free-space wipe resurrection — NIST SP 800-88r2 and SSD behavior favor encryption, device sanitize/secure erase, or physical destruction over repeated free-space writes; DeepPurge should keep its existing "no" stance.
- WinUI 3 rewrite — FluentCleaner shows modern UI demand, but DeepPurge's WPF admin surface and blocked ViewModel-decomposition note identify the real maintainability path.
- Full package-manager replacement — UniGetUI already owns that space; DeepPurge should only act natively on package identities it already displays.
- Remote cleaner/plugin marketplace — winapp2/BleachBit show the value of cleaner definitions, but an elevated deletion tool should stay with local, schema-validated, provenance-visible cleaners.
- Cloud sync, accounts, telemetry, mobile, Linux, macOS, or multi-user collaboration — these conflict with the local Windows-admin/privacy product shape and platform-specific APIs.
- Broad debloat, ISO customization, policy dashboard, or AI automation — WinUtil/Winhance/UniGetUI demand is real, but these would dilute the cleanup/uninstall workstation.
- Embedded registry editor — BCUninstaller issue signal exists, but direct editing adds high corruption risk; DeepPurge should keep preview/delete/recovery workflows instead.
- Code-signing certificate, winget/Scoop publication, full localization, full WCAG/Narrator pass, Velopack, ETW/CIM/COM migrations, Hunter Mode, elevated rendered QA, and independent VM benchmark — already tracked as blocked in `Roadmap_Blocked.md`.

## Sources
OSS and adjacent projects:
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/935
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/923
- https://github.com/bleachbit/bleachbit
- https://github.com/bleachbit/bleachbit/issues/2150
- https://docs.bleachbit.org/
- https://github.com/Devolutions/UniGetUI
- https://github.com/Devolutions/UniGetUI/issues/5004
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/qarmin/czkawka
- https://github.com/qarmin/czkawka/issues/1963
- https://github.com/MoscaDotTo/Winapp2
- https://github.com/builtbybel/FluentCleaner
- https://github.com/ChrisTitusTech/winutil
- https://github.com/memstechtips/Winhance

Commercial and community:
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://support.ashampoo.com/hc/en-us/articles/28056212092818-UnInstaller-16-Manual
- https://www.hibitsoft.ir/Uninstaller.html
- https://www.iobit.com/en/advanceduninstaller.php
- https://support.ccleaner.com/s/article/what-is-health-check
- https://support.ccleaner.com/s/article/how-do-i-manage-cookies-in-ccleaner-for-windows
- https://pcmanager.microsoft.com/
- https://www.reddit.com/r/windows/comments/15ncnwf/i_compared_all_windows_uninstallers_and_the/

Standards, platform, and dependency references:
- https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
- https://developer.chrome.com/docs/extensions/reference/permissions-list
- https://learn.microsoft.com/en-us/windows/package-manager/winget/uninstall
- https://docs.chocolatey.org/en-us/choco/commands/uninstall/
- https://github.com/ScoopInstaller/Scoop/wiki/Commands
- https://csrc.nist.gov/pubs/sp/800/88/r2/ipd

## Open Questions
None that block prioritization. Elevated rendered QA, signing, publication, full localization, and external VM benchmarking remain correctly blocked in `Roadmap_Blocked.md`.
