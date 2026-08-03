# Research — DeepPurge
Date: 2026-07-29 — replaces all prior research.

## Executive Summary
DeepPurge is a local-first Windows 10/11 uninstall, cleanup, repair, and diagnostics workstation built on .NET 10 WPF with an elevated GUI and an as-invoker CLI. Its strongest shape is unusually broad local coverage—native and package-manager uninstall, remnants, browser and system cleanup, driver/startup/task management, install tracing, community cleaner definitions, previews, manifests, and portable distribution—without accounts or cloud dependencies. The highest-value direction is not more breadth: because the main GUI runs elevated and the product deletes user and system data, it must make target identity, privilege boundaries, provenance, recoverability, and partial failure trustworthy before adding parity features. The 2026-07-29 audit found six verified P0 paths capable of profile loss, wrong-target deletion, unsafe replay, or privilege escalation; those supersede cosmetic work and feature expansion.

Top opportunities, in priority order:

1. **Verified — make deletion handle-bound and reparse-safe.** `SafetyGuard` is not yet a complete choke point: file deletion skips path validation, protected descendants can pass, root reparse points are accepted, and path checks are vulnerable to target substitution.
2. **Verified — prevent Firefox profile deletion.** A missing extension package falls back to the profile directory, and removal recursively deletes the selected path; extension IDs also need canonical containment validation.
3. **Verified — bind registry recovery to the exact operation.** Recovery currently searches a user-writable backup directory by time and ignores the requested registry path, so it can import the wrong or attacker-planted `.reg` file while elevated.
4. **Verified — remove scheduled-task and executable search-order privilege boundaries.** A highest-privilege task runs a user-writable wrapper, while elevated package actions launch unqualified executables that Windows can resolve from writable locations.
5. **Verified — quarantine Install Monitor V2 replay until provenance is correct.** USN leaf names are fabricated into root paths, modified files are treated as installer-created, fallback baselines are captured after installation, and Sysmon enrichment lacks safe process/hive attribution.
6. **Verified — unify destructive outcomes and recovery policy.** Most cleanup pipelines bypass `UseRecycleBin`, several count failed deletes as successes, and manifests are written before an operation succeeds.
7. **Verified — distinguish uninstall capabilities and ownership.** Portable/game discoveries can report successful uninstall without removing anything, and leftover heuristics need conflict checks before attributing another app's files.
8. **Verified — add recoverable driver and non-file mutation transactions.** Driver removal lacks export, while firewall, PATH, service, autorun, and scheduled-task mutations bypass existing safety policies or lack rollback.
9. **Verified — make cleaner definitions and dependency inputs update-safe.** User-writable elevated rules need origin/schema/hash/rollback controls; the embedded SQLite line and .NET packages have 2026-07-29 maintenance updates, and the build does not enforce locked restore by default.
10. **Verified — expose degraded scans and reconcile advertised capabilities.** Swallowed scanner errors look like clean results, while Health Dashboard/System Slimming commands and some settings are not reachable or observable from the shipped surfaces.

## Product Map
- **Core workflows:** discover and uninstall installed/package-managed/portable/game applications; review and remove file/registry leftovers; preview and run junk/privacy/winapp2/custom cleanup; manage drivers, autoruns, services, tasks, shell entries, duplicates, and large files; trace installs and recover recorded deletions.
- **User personas:** Windows power users; repair and help-desk technicians; privacy-focused local users; administrators invoking the CLI through PowerShell, Task Scheduler, Intune, or SCCM-style tooling; maintainers producing portable GitHub/winget/Scoop artifacts.
- **Platforms and distribution:** Windows 10/11 x64; .NET 10 (`net10.0-windows10.0.17763.0`); elevated WPF GUI; as-invoker CLI; self-contained unsigned portable executables. ARM64 is not produced; publication, updater, localization, and rendered accessibility verification remain separately blocked.
- **Integrations and data flows:** HKLM/HKCU/HKU uninstall and cleanup keys; MSI and AppX metadata; WinVerifyTrust; winget/Scoop/Chocolatey; `pnputil`, DISM, `schtasks`, `reg.exe`, PowerShell, service/firewall/task APIs; browser profiles and SQLite databases; USN/Sysmon install evidence; winapp2/custom JSON rules; GitHub Releases; state under `DataPaths` in `%LocalAppData%\DeepPurge` or portable `Data/`.

## Competitive Landscape
- **BCUninstaller:** does broad source discovery, batch uninstall, leftovers, filtering, export, verification, and user keep-rules well. DeepPurge should learn from explicit capability/risk presentation, dry-run verification, presets, and protected items; it should avoid copying BCU's full configurability before destructive contracts are coherent.
- **BleachBit:** does category preview, visible cleaner semantics, cookie preservation, and mature rule testing well. DeepPurge should learn from its conservative release response to junction/symlink deletion risk; it should avoid path-based deletion that assumes a validated name still identifies the same object.
- **Sifty and Mole:** make Trash/Recycle Bin the default, preserve audit history, expose machine-readable output, and protect sensitive paths. DeepPurge should adopt the same recoverable-default and exact-result discipline; it should avoid turning aggressive deletion into the default merely because elevation is available.
- **Czkawka:** does staged duplicate identification, reference folders, keeper choice, exclusions, saved selections, and large-set UX well. DeepPurge should revalidate identity immediately before removal and let users choose the retained copy; it should avoid the present age-only automatic keeper policy.
- **DriverStoreExplorer:** does focused driver inventory, selection, export/backup, and package removal well. DeepPurge should export each selected driver package before deletion and attach it to recovery provenance; it should avoid broad driver-health claims without device-state validation.
- **Winapp2 and Kudu:** demonstrate the reach of community-maintained cleaner rules and simple JSON rule ecosystems. DeepPurge should add last-known-good rollback, origin/hash visibility, update diffs, and preserved-data regression cases; it should avoid a remote executable plugin marketplace inside an elevated process.
- **UniGetUI and winget:** do source-native package identity, operation history, queued actions, import/export, and package-manager diagnostics well. DeepPurge should keep source-native actions and trusted executable resolution; it should avoid expanding into a general package-manager frontend.
- **Revo Uninstaller Pro and Ashampoo UnInstaller:** make monitored installs, before/after snapshots, restore points, full registry backup, log import/export, and backup management commercially valuable. DeepPurge should make its manifests exact and replay eligibility conservative; it should avoid claiming precision from volume-wide activity that cannot be attributed to the installer.

## Security, Privacy, and Reliability

### Verified defects and scoped validation needs
- **Wrong-target file deletion:** `src/DeepPurge.Core/Safety/SafetyGuard.cs` documents protected directories as recursive boundaries but only repeats equality checks; `SafeDeleteFile` does not call `IsPathSafeToDelete`, and `SafeDeleteDirectory` accepts a root reparse point. `src/DeepPurge.Core/Safety/SecureDelete.cs` has the same root issue, while `src/DeepPurge.Core/FileSystem/EmptyFolderScanner.cs` recursively follows reparse points. String validation followed by a later path delete leaves a time-of-check/time-of-use window analogous to BleachBit CVE-2026-55567.
- **Firefox profile loss:** `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs` resolves a missing Firefox extension directory/XPI to `profileDir`, then `RemoveExtension` recursively deletes `ext.Path`. The add-on ID is used in `Path.Combine` without rejecting rooted/traversal input. A stale, built-in, system, or malicious metadata entry can therefore target the profile that holds bookmarks, passwords, history, and cookies.
- **Registry recovery poisoning and false provenance:** `src/DeepPurge.Core/Registry/RegistryDeletion.cs` creates an exact backup, but `src/DeepPurge.Core/Diagnostics/DeletionManifest.cs` stores no backup path/hash and `FindMatchingBackup` ignores `registryPath`, selecting the newest `.reg` in a broad time window under user-writable `DataPaths`. The elevated GUI can import an unrelated or planted file. File operations are also recorded before success, permanent deletion is sometimes labeled “Check Recycle Bin,” and secure wipes are omitted.
- **Incorrect registry-link detection:** `SafetyGuard.IsRegistrySymlink` interprets a non-empty registry key class as a symbolic link by reflecting a private handle. Windows registry links instead require link-specific open/create semantics and a `SymbolicLinkValue` of type `REG_LINK`; ordinary key classes are not proof of a link. Deletion can follow the link before this check.
- **Scheduled-task privilege escalation:** `src/DeepPurge.Core/Schedule/ScheduleManager.cs` registers a current-user, highest-run-level task whose action is a `.cmd` wrapper in user-writable `%LocalAppData%\DeepPurge\Config`. Another unelevated process for that user can replace the wrapper before the scheduled elevated run.
- **Privileged executable search-order hijacking:** `src/DeepPurge.Core/Packages/PackageManagerScanner.cs` and `PackageManagerCommandBuilder.cs` launch unqualified `winget.exe`, `choco.exe`, and `cmd.exe` from the elevated GUI. Windows executable search considers locations that can be influenced by the working directory or `PATH`; trusted helpers require resolved absolute paths and ownership/location validation.
- **Unsafe Install Monitor V2 evidence:** `src/DeepPurge.Core/InstallMonitor/UsnJournalReader.cs` receives a leaf name plus parent file-reference number but fabricates `<volume>:\<leaf>` without resolving the parent. `InstallSnapshotEngine.cs` treats volume-wide “Created” and “Modified” records as installer-added files and, when USN is unavailable, captures both fallback snapshots after the installer exits. Replay can target an unrelated file. **Likely:** `SysmonReader.cs` reads positional event properties incorrectly; **Verified:** it does not constrain the installer process tree/end time and rewrites every `HKU\<SID>` path to HKCU. The mapping needs spec-backed tests before it can be trusted.
- **Inconsistent deletion semantics and false success:** `DeleteOptions.UseRecycleBin` is honored chiefly by uninstall leftovers; junk, evidence, empty folders, duplicate/large-file removal, system slimming, winapp2, and custom cleaners normally delete permanently. `DuplicateFinder`, `EmptyFolderScanner`, `SystemSlimmer`, and GUI large-file deletion include paths that ignore a false result or selected dry-run/secure/recycle settings and still count success.
- **False uninstall success and command trust:** portable and game scanners create `InstalledProgram` rows without a native identity or uninstall command, but `src/DeepPurge.Core/Uninstall/UninstallEngine.cs` can mark the skipped action successful. Separately, HKCU/HKU uninstall strings are user-controlled commands executed by the elevated GUI after selection; the UI does not expose path owner/publisher/risk before execution.
- **Heuristic ownership ambiguity:** `FileLeftoverScanner.BuildCrossReference` excludes exact names/folders found in another uninstall entry, but still treats the target's declared install location as a guaranteed match and silently degrades if cross-reference enumeration fails; `RegistryLeftoverScanner` has no equivalent product cross-reference. Neither establishes MSI/component/package ownership. The cited academic uninstaller study demonstrates that malicious or stale install metadata can steer heuristic removers toward unrelated applications or Windows directories.
- **Duplicate identity drift:** `src/DeepPurge.Core/FileSystem/DuplicateFinder.cs` does not re-stat/re-hash immediately before deletion, counts some failed deletes, and uses an implicit “older copy” keeper without per-group or reference-folder selection. A file changed after scanning can be deleted on stale evidence.
- **Irrecoverable administrative mutations:** `DriverStoreScanner` deletes packages without first exporting them. Production firewall and PATH mutations bypass the corresponding `SafetyGuard` checks; PATH edits have no backup or `WM_SETTINGCHANGE`; autorun actions can disable protected services; scheduled-task rows are exposed where toggle/delete handlers do not support them.
- **Silent partial scans:** scheduled-task, firewall, PATH, autorun, context-menu, health, and other scanners swallow enumeration/process errors or return empty collections. A blocked source is therefore indistinguishable from a clean system in the GUI, CLI JSON, logs, and support bundle.
- **Dependency maintenance exposure:** on 2026-07-29, `dotnet list package --vulnerable --no-restore` reported no known advisory for the resolved graph. However, the repository resolves `Microsoft.Data.Sqlite` 10.0.9, SQLitePCLRaw 3.0.3 / SQLite 3.50.4.5, and .NET 10.0.9 libraries while 10.0.10 and SQLite 3.53.4-backed packages are available. SQLite documents CVE-2026-11822 in crafted FTS5 databases. **Needs live validation:** whether DeepPurge's browser queries reach the vulnerable path. An elevated app opening user-writable browser databases should still avoid retaining the older engine without reason.

### Missing guardrails
- One operation-scoped destructive executor should bind the opened object identity, final normalized path, no-follow policy, allowed roots, requested disposition, and typed result. Validation must occur on the handle used to delete, not on a string checked earlier.
- Trusted external tools need a single resolver that returns absolute paths from protected locations and rejects current-directory/PATH shims. Third-party uninstall commands need a separate, visible trust decision rather than being treated as OS helpers.
- Every candidate needs explicit capability and provenance: source adapter, native action type, identity evidence, conflicts, risk, and whether removal is supported. “Discovered” must not imply “uninstallable.”
- Scanners need `ScanResult<T>`-style contracts carrying items, failed sources, warnings, duration, and degraded state through WPF, CLI JSON, activity history, and support diagnostics.
- Cleaner definitions need a versioned data-only contract with origin, content hash, safe-root expansion, update diff, invalid-rule quarantine, preserved-data regression fixtures, and last-known-good rollback.

### Recovery and rollback
- Deletion and registry manifests need schema versions, exact backup identity and hash, success/failure outcome, original object identity, operation mode, and legacy read-only migration. No recovery action should discover a backup by timestamp.
- Recycle Bin through `IFileOperation` should be the default for ordinary user-file cleanup when supported; permanent and secure modes should be explicit. Aborted/locked/unsafe/missing outcomes must not increase deleted counts.
- Driver removal should export the selected package with `pnputil /export-driver` or DISM, hash it, and record a reinstall path before `/delete-driver`.
- Firewall, PATH, service, autorun, and task changes need before/after state plus an executable rollback action in the same operation ledger.

## Architecture Assessment
- **Destructive boundary:** the documented `SafetyGuard`/`DeleteOptions` architecture is correct in intent but not in enforcement. Introduce a narrow handle-bound file executor and a separate transactional non-file mutation coordinator, then migrate callers rather than adding more booleans or direct `File.Delete`/process calls.
- **Evidence boundary:** install tracing, uninstall discovery, leftovers, duplicates, cleaner rules, and recovery all need a shared concept of immutable candidate identity plus evidence. This is more valuable than adding another scanner because it lets every surface explain why an object is eligible and detect drift before mutation.
- **Observability/result boundary:** replace booleans, swallowed exceptions, and count-only summaries with typed per-item and aggregate outcomes. The 2026-07-29 cleanup-failure reason model is a useful start, but it covers only junk/evidence paths and should be extended through WPF, CLI JSON, activity history, logs, doctor, and support-bundle contracts.
- **UI boundary:** `src/DeepPurge.App/Views/MainWindow.xaml` (about 2,181 lines), `MainWindow.xaml.cs` (about 1,329), and the two `MainViewModel` partials (about 2,550 combined) remain coupled. The separately blocked ViewModel decomposition should not be duplicated, but new work should add Core contracts and thin bindings rather than more code-behind deletion logic.
- **Accessibility and localization:** `src/DeepPurge.App/Themes/BaseStyles.xaml` and the nine color dictionaries yield `DangerButton` white-on-red combinations below 4.5:1 in static measurement; Arctic has additional normal-text failures. Major controls in `src/DeepPurge.App/Views/MainWindow.xaml` lack automation names, and status/toast/loading surfaces lack live semantics; `tests/DeepPurge.Tests/WpfPolishContractTests.cs` does not compute contrast or cover all affected controls. These findings strengthen the existing blocked WCAG/rendered-QA and `.resx` items; they are not new roadmap entries because visual/Narrator validation is already tracked.
- **Capability reachability:** Health Dashboard and System Slimming commands/collections are advertised but have no shipped XAML or CLI consumer; `ExpertMode` does not govern behavior, and some package-enrichment properties do not notify the UI. A generated capability-to-surface contract should make README claims, commands, settings, and tests fail together when they drift.
- **Build and dependency boundary:** `Build.ps1` does not run tests/release validation by default, restore is not locked, SDK bootstrap executes a mutable remote `dotnet-install.ps1`, and `NuGet.Config` source mapping omits SQLitePCLRaw/SourceGear patterns. Pinning toolchain inputs and enforcing lock/source-map integrity is more urgent than adding a hosted release workflow.
- **Offline/resilience and upgrade strategy:** core use remains local and offline; recoverable deletion, last-known-good cleaner rules, and lock/cache-verifiable builds strengthen that posture. Keep the updater check-only and ship unsigned checksum-verifiable artifacts until the separately blocked apply/restart updater can receive end-to-end testing; do not duplicate that blocked work.
- **Test gaps:** add deterministic junction/target-swap tests; Firefox containment/profile-preservation tests; exact registry backup/legacy manifest tests; scheduled-task DACL/action tests; trusted-executable shadowing tests; injected USN/Sysmon adapters; duplicate drift tests; driver export rollback tests; partial-scan propagation tests; cleaner preserved-data fixtures; and a source-to-surface documentation contract. Elevated rendered QA remains blocked rather than silently assumed.

## Rejected Ideas
- **Multi-pass/free-space wiping** — rejected from NIST storage-sanitization guidance and existing project philosophy; it adds SSD wear and a misleading guarantee without fixing target identity.
- **One-click “Fix All” or unattended heuristic deletion** — rejected despite IObit/CCleaner commercial prevalence; the verified attribution and recovery gaps make additional automation unsafe.
- **Full package-manager frontend** — rejected from UniGetUI/winget comparison; DeepPurge should invoke native actions for identities it already discovers, not duplicate package browsing and source management.
- **Remote executable plugin marketplace** — rejected from cleaner/plugin ecosystem research; loading third-party code into an elevated process creates disproportionate supply-chain risk. Versioned data-only cleaner definitions remain appropriate.
- **Broad debloat/tweak/policy dashboard or AI cleanup** — rejected from WinUtil/Winhance market signal; it dilutes the uninstall/cleanup workstation and makes destructive decisions harder to explain and test.
- **Cloud sync, telemetry accounts, mobile clients, Linux/macOS ports, or multi-user collaboration** — rejected because Windows registry, Task Scheduler, driver store, service, AppX, and WPF dependencies make these poor fits for the local privacy-oriented product.
- **MSIX packaging** — rejected because sandboxing/identity changes conflict with required HKLM, driver, service, task, and shell operations; the existing portable model is the compatible distribution path.
- **Authenticode certificate acquisition or signing as a release gate** — rejected because the governing `AGENTS.md` instructions prohibit software signing; publish unsigned artifacts with checksums where permitted.
- **New roadmap entries for localization, WCAG/Narrator/rendered-theme QA, VM field validation, publication, Velopack, ViewModel decomposition, CsWin32, winget COM, ETW/CIM, Hunter Mode, or benchmark work** — rejected as duplicates, not as product directions; each already exists in `Roadmap_Blocked.md`.

## Sources

### OSS competitors and adjacent projects
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases/tag/v6.2
- https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/issues/947
- https://github.com/bleachbit/bleachbit
- https://www.bleachbit.org/news/bleachbit-602
- https://github.com/qarmin/czkawka
- https://github.com/qarmin/czkawka/issues/1992
- https://github.com/lostindark/DriverStoreExplorer
- https://github.com/Devolutions/UniGetUI/releases/tag/v2026.2.6
- https://github.com/MoscaDotTo/Winapp2/pull/1004
- https://github.com/MoscaDotTo/Winapp2/pull/945
- https://github.com/memstechtips/Winhance/releases
- https://github.com/ChrisTitusTech/winutil
- https://github.com/Vortrix5/sifty
- https://github.com/tw93/Mole/releases
- https://github.com/AdventDevInc/kudu
- https://github.com/alienator88/Pearcleaner
- https://github.com/microsoft/winget-cli/releases/tag/v1.29.280

### Commercial products
- https://www.revouninstaller.com/products/revo-uninstaller-pro/
- https://support.ashampoo.com/hc/en-us/articles/28056212092818-UnInstaller-16-Manual
- https://www.iobit.com/en/advanceduninstaller.php
- https://geekuninstaller.com/download
- https://www.ccleaner.com/ccleaner/plans
- https://www.martau.com/

### Community and research
- https://marcusbotacin.github.io/files/isc_uninstallers.pdf
- https://github.com/topics/windows-debloat
- https://project-awesome.org/r/awesome-windows
- https://fmhy.net/system-tools
- https://www.reddit.com/r/Windows11/comments/1qshxpa/uninstall_leaves_things/
- https://www.reddit.com/r/software/comments/1t0rvoe/is_there_an_uninstaller_that_actually_cleans/
- https://stackoverflow.com/questions/30067976/programmatically-uninstall-a-software-using-c-sharp
- https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data
- https://firefox-source-docs.mozilla.org/toolkit/mozapps/extensions/addon-manager/SystemAddons.html

### Standards, platform APIs, dependencies, and security
- https://github.com/bleachbit/bleachbit/security/advisories/GHSA-vcjw-px28-5w94
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlea
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessa
- https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iexecaction
- https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
- https://learn.microsoft.com/en-us/windows/win32/taskschd/task-security-hardening
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rrp/d5ce9dcc-1f90-4f5a-b076-cc1d2c9b4195
- https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regopenkeyexa
- https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v3
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14
- https://learn.microsoft.com/en-us/windows/apps/develop/input/text-scaling
- https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes
- https://learn.microsoft.com/en-us/accessibility-tools-docs/items/wpf/control_automationproperties
- https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files
- https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping
- https://csrc.nist.gov/pubs/sp/800/88/r2/final
- https://www.sqlite.org/cves.html
- https://www.sqlite.org/releaselog/3_53_4.html
- https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5
- https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.10/10.0.10.md

## Open Questions
None. The 2026-07-29 repository state and public evidence are sufficient to prioritize the actionable work; hardware-, credential-, and human-visual-validation dependencies remain explicitly isolated in `Roadmap_Blocked.md`.
