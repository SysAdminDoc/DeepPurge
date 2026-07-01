# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Active items

No active implementation items.

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** - obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** - user preference
- **Feature flags / A-B gating** - overkill for a local desktop tool
- **Cloud sync of settings** - privacy surface without clear value
- **MSIX distribution** - sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app

## Research-Driven Additions

- [ ] P3 — Make health results actionable and trend-aware
  Why: HealthScorer already returns category actions, but the app does not turn them into panel-specific next steps or retain lightweight trends comparable to CCleaner/Microsoft PC Manager health surfaces.
  Evidence: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`; CCleaner Health Check docs; Microsoft PC Manager positioning.
  Touches: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`, `src/DeepPurge.Core/Diagnostics/ActivityLog.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`, `src/DeepPurge.App/Views/MainWindow.xaml`, `tests/DeepPurge.Tests/HealthScorerTests.cs`.
  Acceptance: each health category exposes a command target for the relevant panel/dry-run action, recent score history is stored locally with existing retention/redaction rules, and GUI/CLI show whether the score improved, worsened, or stayed stable.
  Complexity: M

