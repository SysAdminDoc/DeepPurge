# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P2 — Parity, extensibility, and release truth

- [ ] P2 — Unify CLI app discovery with GUI package enrichment
  Why: `deeppurgecli list` and `uninstall` only use registry entries, while the GUI enriches with winget, Scoop, portable, and game-platform sources.
  Evidence: `src/DeepPurge.Cli/Program.cs` `CmdListAsync` and `CmdUninstallAsync`; `src/DeepPurge.Core/Packages/PackageManagerScanner.cs`; BCUninstaller multi-source discovery.
  Touches: `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.Core/Packages/PackageManagerScanner.cs`, `src/DeepPurge.Core/Models/InstalledProgram.cs`, `tests/DeepPurge.Tests/`
  Acceptance: CLI list can emit registry/package/portable/game entries with source and package id; uninstall can target supported package ids or return an explicit unsupported-source message; JSON/TSV output is covered by tests.
  Complexity: M

- [ ] P2 — Expose JSON cleaner definitions with schema validation
  Why: `CleanerDefinitionRunner` exists but has no GUI/CLI entry point, no schema, and no tests, making the changelogged custom cleaner surface effectively unreachable.
  Evidence: `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`; BleachBit CleanerML; winapp2 declarative cleaner ecosystem; FluentCleaner custom database support.
  Touches: `src/DeepPurge.Core/Cleaning/CleanerDefinition.cs`, `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.App/ViewModels/`, `src/DeepPurge.App/Views/MainWindow.xaml`, `tests/DeepPurge.Tests/`
  Acceptance: CLI supports `cleaners list|preview|run --dry-run`; GUI shows applicable custom cleaners with preview sizes and exclusion-aware details; invalid JSON/schema failures are surfaced; tests cover detect, files, registry, dry-run, and child-reparse behavior.
  Complexity: M

- [ ] P2 — Make Amcache/BAM remnant discovery truthful and wired
  Why: The parser checks for `Amcache.hve` but reads BAM registry data, and no production flow calls it, so the remnant-discovery claim is not observable.
  Evidence: `src/DeepPurge.Core/Registry/AmcacheParser.cs`; `src/DeepPurge.Cli/Program.cs` `CmdOrphans`; EricZimmerman AmcacheParser; Windows BAM forensic references.
  Touches: `src/DeepPurge.Core/Registry/AmcacheParser.cs`, `src/DeepPurge.Cli/Program.cs`, `src/DeepPurge.App/ViewModels/`, `tests/DeepPurge.Tests/`
  Acceptance: Either parse real `Amcache.hve` with fixture-backed behavior or rename/scope the feature to BAM execution evidence; `orphans` and GUI orphan scans can include the results; tests prove installed-program cross-reference and stale executable filtering.
  Complexity: M

- [ ] P2 — Wire existing `.resx` localization into UI text
  Why: Localization resources and generated accessors exist, but user-visible XAML/code-behind strings still bypass them, so the claimed i18n path is not observable.
  Evidence: `src/DeepPurge.App/Properties/Resources.resx`; `src/DeepPurge.App/Properties/Resources.Designer.cs`; `src/DeepPurge.App/Views/MainWindow.xaml`; BleachBit translation/localization practice.
  Touches: `src/DeepPurge.App/Properties/Resources.resx`, `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.App/ViewModels/`, `tests/DeepPurge.Tests/`
  Acceptance: Primary navigation, action buttons, dialog titles, and status copy bind to resources instead of literals; a culture-switch smoke test or generated-resource test proves the strings are consumed; missing-resource fallback remains English.
  Complexity: M


## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Tray icon** — background scheduled cleaning with tray notifications
- **Registry ETW tracing** — add `Microsoft.Diagnostics.Tracing.TraceEvent` for
  real-time registry change capture alongside USN journal filesystem tracking

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
