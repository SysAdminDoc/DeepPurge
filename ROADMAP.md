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

- [ ] P2 — Implement domain-level cookie preservation
  Why: The current cookie whitelist preserves all browser cookie databases when any domain is listed, while BleachBit/CCleaner-style behavior preserves selected cookies and removes the rest.
  Evidence: `src/DeepPurge.Core/Privacy/EvidenceRemover.cs`; `src/DeepPurge.Core/App/AppSettings.cs`; CCleaner cookie manager docs; BleachBit cleaner research.
  Touches: `src/DeepPurge.Core/Privacy`, `src/DeepPurge.Core/App/AppSettings.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.cs`, `src/DeepPurge.App/Views/MainWindow.xaml`, `tests/DeepPurge.Tests/CookieWhitelistTests.cs`.
  Acceptance: dry-run reports preserved/deleted cookie counts per supported browser profile; destructive mode backs up SQLite cookie DBs, deletes only non-whitelisted domains where safe, and degrades with a clear locked-file reason when a browser holds the DB.
  Complexity: L

- [ ] P2 — Replace destructive modal confirmations with inline risk preview
  Why: Driver removal, duplicate cleanup, winapp2 execution, bulk uninstall, and deletion restore still use blocking MessageBox confirmations instead of inline preview, toast, and recovery affordances.
  Evidence: `src/DeepPurge.App/Views/MainWindow.xaml.cs`; current deletion manifests/dry-run/recovery surfaces; local UX rules.
  Touches: `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/Views/MainWindow.xaml.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.cs`, `tests/DeepPurge.Tests/WpfPolishContractTests.cs`.
  Acceptance: destructive GUI handlers no longer call `MessageBox.Show` for user confirmation; each action shows risk/count preview in its panel, runs through existing dry-run/recovery paths where available, and reports completion or failure by toast/activity log.
  Complexity: M

- [ ] P2 — Add browser extension permission risk labels
  Why: DeepPurge lists and removes browser extensions but does not classify broad host, background, native messaging, or sensitive API permissions.
  Evidence: `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs`; Chrome extension permissions reference; IObit browser-extension cleanup positioning.
  Touches: `src/DeepPurge.Core/Browsers/BrowserExtensionScanner.cs`, `src/DeepPurge.App/Views/MainWindow.xaml`, `src/DeepPurge.App/ViewModels/MainViewModel.cs`, `tests/DeepPurge.Tests`.
  Acceptance: Chromium and Firefox scans extract manifest permissions/host permissions where available, assign risk labels for broad/sensitive access, display those labels in GUI/exports, and tests cover benign, broad host, and native-messaging examples.
  Complexity: M

- [ ] P3 — Make health results actionable and trend-aware
  Why: HealthScorer already returns category actions, but the app does not turn them into panel-specific next steps or retain lightweight trends comparable to CCleaner/Microsoft PC Manager health surfaces.
  Evidence: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`; CCleaner Health Check docs; Microsoft PC Manager positioning.
  Touches: `src/DeepPurge.Core/Diagnostics/HealthScorer.cs`, `src/DeepPurge.Core/Diagnostics/ActivityLog.cs`, `src/DeepPurge.App/ViewModels/MainViewModel.Extensions.cs`, `src/DeepPurge.App/Views/MainWindow.xaml`, `tests/DeepPurge.Tests/HealthScorerTests.cs`.
  Acceptance: each health category exposes a command target for the relevant panel/dry-run action, recent score history is stored locally with existing retention/redaction rules, and GUI/CLI show whether the score improved, worsened, or stayed stable.
  Complexity: M

