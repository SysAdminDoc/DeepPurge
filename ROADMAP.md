# Roadmap

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Actionable Items

- [ ] P0 — Quarantine Install Monitor V2 replay until provenance is trustworthy
  Why: USN leaf names are converted to incorrect root paths, volume-wide modifications are treated as installer-created, fallback snapshots occur after installation, and Sysmon events are not safely attributed.
  Evidence: `src/DeepPurge.Core/InstallMonitor/UsnJournalReader.cs`; `src/DeepPurge.Core/InstallMonitor/InstallSnapshotEngine.cs`; `src/DeepPurge.Core/InstallMonitor/SysmonReader.cs`; Windows `USN_RECORD_V3` documentation; cited academic uninstaller/remover study.
  Touches: `src/DeepPurge.Core/InstallMonitor/`, manifest schemas, trace/replay WPF and CLI, injected install-monitor tests
  Acceptance: Until a new eligible manifest is produced, tracing falls back to the pre/post V1 path and V2 data is diagnostic-only; the replacement resolves parent FRNs, captures pre-launch identity, correlates the installer process tree/time window, distinguishes create from modify, preserves HKU SID/hive, and permits replay only for created objects whose identity still matches.
  Complexity: L

- [ ] P1 — Centralize deletion policy, outcomes, and success-only provenance
  Why: `UseRecycleBin`, dry-run, secure mode, result counts, and manifests mean different things across pipelines, so permanent or failed operations can be reported as recoverable success.
  Evidence: `DeleteOptions`; `UninstallEngine`; `DuplicateFinder`; `EmptyFolderScanner`; `SystemSlimmer`; `CleanerDefinition`; `Winapp2Parser`; `MainWindow.xaml.cs`; Sifty/Mole recoverable-default and audit designs.
  Touches: `src/DeepPurge.Core/Safety/`, every destructive Core pipeline, WPF/CLI result surfaces, activity/recovery manifests, tests
  Acceptance: One typed executor exposes preview, Recycle Bin, permanent, secure, queued, skipped, failed, and cancelled outcomes; ordinary user-file cleanup defaults to `IFileOperation` recycling where supported; only confirmed operations enter manifests/counts; all existing deletion callers and per-item cleanup-failure details use the same contract.
  Complexity: XL

- [ ] P1 — Add ownership-conflict gates to leftover attribution
  Why: Name/publisher/install-location heuristics can attribute shared or adversarial paths to the wrong program and offer unrelated data for deletion.
  Evidence: `src/DeepPurge.Core/FileSystem/FileLeftoverScanner.cs`; `src/DeepPurge.Core/Registry/RegistryLeftoverScanner.cs`; `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`; cited academic uninstaller/remover study.
  Touches: file/registry leftover scanners, installed-product index, MSI/package ownership adapters, candidate evidence models, preview WPF/CLI, tests
  Acceptance: Candidates carry each supporting signal and conflicts against other installed products/components; paths claimed by another product, Windows, or weak single-source metadata are protected/review-only; crafted metadata pointing at another app or Windows directory never becomes auto-removable.
  Complexity: L

- [ ] P1 — Route non-file administrative mutations through reversible safety policy
  Why: Firewall, PATH, service/autorun, and scheduled-task actions bypass existing guards, omit backups/refresh notifications, or expose unsupported mutations.
  Evidence: `src/DeepPurge.Core/Firewall/FirewallRuleScanner.cs`; `src/DeepPurge.Core/Shell/PathCleaner.cs`; `src/DeepPurge.Core/Startup/AutorunScanner.cs`; `SafetyGuard.cs`; scheduled-task action handlers.
  Touches: safety and operation-ledger services, firewall/PATH/autorun/service/task modules, WPF/CLI actions, tests
  Acceptance: Every mutation runs a matching protection rule, records before/after state and rollback, reports exact outcome, sends required system refresh notifications, and disables UI/CLI actions for unsupported source types; protected services/rules/PATH entries cannot be changed through production code.
  Complexity: L

- [ ] P1 — Model removal capability and uninstaller trust explicitly
  Why: Portable/game discoveries can report successful uninstall without an action, while selected HKCU/HKU uninstall strings cross into the elevated process without visible trust facts.
  Evidence: `src/DeepPurge.Core/Packages/PortableAppScanner.cs`; `src/DeepPurge.Core/Packages/GamePlatformScanner.cs`; `src/DeepPurge.Core/Uninstall/UninstallEngine.cs`; `src/DeepPurge.App/app.manifest`; BCUninstaller capability/risk patterns.
  Touches: installed-program models/scanners, `UninstallEngine`, signature/path-owner inspection, WPF/CLI program rows and results, tests
  Acceptance: Each row declares `NativeUninstaller`, `PackageManager`, `PortableFolder`, `GameLauncher`, or `Unsupported` capability with source identity and trust facts; unsupported actions are disabled; no skipped action reports success; executable path, owner/publisher, arguments, and risk are reviewable before elevated execution.
  Complexity: M

- [ ] P1 — Revalidate duplicate identity and require an explicit keeper policy
  Why: Duplicate groups can drift between hashing and deletion, and the implicit age-based keeper gives users no per-group or reference-folder control.
  Evidence: `src/DeepPurge.Core/FileSystem/DuplicateFinder.cs`; Czkawka reference-folder, keeper, and saved-selection patterns.
  Touches: duplicate models/scanner/deleter, duplicate WPF/CLI, operation results, tests
  Acceptance: Every candidate is re-statted and fully re-hashed immediately before removal; any changed group aborts safely; users can select the keeper or a protected reference folder; no group is deleted without one retained identity; failed/skipped counts are exact.
  Complexity: M

- [ ] P1 — Export and bind a rollback package before driver deletion
  Why: DeepPurge removes driver-store packages without the export/backup workflow exposed by Windows and DriverStoreExplorer.
  Evidence: `src/DeepPurge.Core/Drivers/DriverStoreScanner.cs`; DriverStoreExplorer; Microsoft PnPUtil and DISM driver-export documentation.
  Touches: driver scanner/removal service, backup and operation manifests, driver WPF/CLI, tests
  Acceptance: Each selected driver is exported before deletion, the exported files and INF identity are hashed into the operation ledger, export failure blocks removal, protected/excluded packages remain pinned, and the UI/CLI exposes a tested reinstall/rollback command.
  Complexity: M

- [ ] P1 — Make cleaner-definition updates diffable and rollback-safe
  Why: Elevated user-writable winapp2/JSON rules can regress into deleting configuration or package-manager state, as demonstrated by the cited winapp2 corrections.
  Evidence: `src/DeepPurge.Core/Cleaning/Winapp2Updater.cs`; `src/DeepPurge.Core/Cleaning/Winapp2Parser.cs`; `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`; Winapp2 PRs 1004 and 945; Kudu data-rule design.
  Touches: cleaner schemas/loaders/updater, `DataPaths.Cleaners`, validation CLI, cleaner preview WPF, regression fixtures
  Acceptance: Every rule set records schema, origin, version, SHA-256, and trust state; updates show expanded-target diffs, quarantine invalid/unsafe rules, preserve a last-known-good version, and pass fixtures proving protected app settings and the winget pin database survive.
  Complexity: M

- [ ] P1 — Propagate typed partial-scan and degraded-source results
  Why: Swallowed enumeration/process errors currently look identical to zero findings and make the GUI, CLI JSON, logs, and support data overstate confidence.
  Evidence: scheduled-task, firewall, PATH, autorun, context-menu, health, package-enrichment, and initial-scan code paths; UniGetUI source diagnostics and Mole JSON/result patterns.
  Touches: shared scan contracts, affected scanners, `MainViewModel`, CLI JSON/text, activity log, doctor/support bundle, tests
  Acceptance: Each multi-source scan returns items plus failed sources, warnings, duration, cancellation, and degraded status; one source failure does not discard successful peers; all user and diagnostic surfaces distinguish clean, partial, failed, timed-out, and cancelled states.
  Complexity: M

- [ ] P1 — Pin dependency/toolchain inputs and close the SQLite maintenance gap
  Why: The build permits unlocked restore and mutable SDK bootstrap, source mapping is incomplete, and the elevated process retains an older SQLite engine despite the cited SQLite 3.53.4 and .NET 10.0.10 maintenance/security releases.
  Evidence: `Build.ps1`; `NuGet.Config`; project and lock manifests; SQLite CVE/release pages; SQLitePCLRaw 3.0.5; .NET 10.0.10 release notes; NuGet locked-restore/source-mapping documentation.
  Touches: project package references, all `packages.lock.json`, `NuGet.Config`, `Build.ps1`, SDK bootstrap/version files, release validation tests/docs
  Acceptance: Microsoft.Data.Sqlite/.NET libraries resolve to 10.0.10 and SQLitePCLRaw to a 3.53.4-backed release; all packages map to explicit trusted sources; restore runs locked by default and fails on drift; SDK/bootstrap content is version/hash pinned; the default release path runs tests, dependency audit, and validation offline from the lock/cache.
  Complexity: M

- [ ] P2 — Enforce a capability-to-surface and documentation contract
  Why: Health Dashboard/System Slimming are advertised but unreachable, `ExpertMode` governs no behavior, some enrichment changes do not notify WPF, and architecture/status notes have drifted from implementation.
  Evidence: `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`; `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`; `src/DeepPurge.Core/Cleaning/SystemSlimmer.cs`; `src/DeepPurge.Core/App/AppSettings.cs`; `src/DeepPurge.App/Views/MainWindow.xaml`; `src/DeepPurge.Cli/Program.cs`; `README.md`; `CLAUDE.md`.
  Touches: capability registry, WPF/CLI bindings, observable program models, README/architecture/status docs, contract tests
  Acceptance: A test-generated matrix maps every advertised capability and setting to a reachable GUI/CLI surface or an explicit unsupported state; Health/System Slimming are wired safely or removed from claims; relevant model changes notify the UI; release validation fails on stale command, version, test-count, privilege, or capability documentation.
  Complexity: M
