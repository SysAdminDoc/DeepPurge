# Roadmap

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

## Actionable Items

- [ ] P2 — Enforce a capability-to-surface and documentation contract
  Why: Health Dashboard/System Slimming are advertised but unreachable, `ExpertMode` governs no behavior, some enrichment changes do not notify WPF, and architecture/status notes have drifted from implementation.
  Evidence: `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`; `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`; `src/DeepPurge.Core/Cleaning/SystemSlimmer.cs`; `src/DeepPurge.Core/App/AppSettings.cs`; `src/DeepPurge.App/Views/MainWindow.xaml`; `src/DeepPurge.Cli/Program.cs`; `README.md`; `CLAUDE.md`.
  Touches: capability registry, WPF/CLI bindings, observable program models, README/architecture/status docs, contract tests
  Acceptance: A test-generated matrix maps every advertised capability and setting to a reachable GUI/CLI surface or an explicit unsupported state; Health/System Slimming are wired safely or removed from claims; relevant model changes notify the UI; release validation fails on stale command, version, test-count, privilege, or capability documentation.
  Complexity: M
