# Roadmap

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Actionable Items

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
