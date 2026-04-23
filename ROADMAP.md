# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.

## v0.9.x — stabilization

- [ ] Submit winget manifest to `microsoft/winget-pkgs`
- [ ] Create `SysAdminDoc/scoop-bucket` repo and commit the Scoop manifest
- [ ] Obtain code-signing certificate and wire `DEEPPURGE_CERT_PATH` / `DEEPPURGE_CERT_PASSWORD`
      secrets into the release workflow
- [ ] Real-world field test of each v0.9 panel on a freshly-imaged Windows 11 VM
- [ ] Translate doctor warnings into suggested fixes where the fix is obvious
- [ ] Expand test coverage to `DriverStoreScanner.ParseText`, `InstallSnapshotEngine.Diff`,
      and `WindowsRepairEngine` sanitisers

## v0.10 — deeper install intelligence

- [ ] **Install monitor 2.0** — replace the curated-roots walk with USN journal
      + registry ETW so we catch every filesystem / registry change during the
      installer run instead of just the high-signal directories
- [ ] **Install monitor UI** — a "Track This Installer" button that wraps the
      trace-and-diff flow with progress, preview, and keep/discard
- [ ] **Upgrade-aware snapshots** — Diff.RemovedFiles wiring into the UI so
      patches and overwrites show up, not just fresh installs
- [ ] Parse `winapp2.ini` `SpecialDetect=DET_CHROME` etc. so applicability
      detection matches BleachBit for browser-specific rules

## v0.11 — reporting + automation

- [ ] **CSV / JSON export** on every panel with a grid (drivers, shortcuts,
      duplicates, startup impact) — sysadmin deliverable
- [ ] **Intune / SCCM detection scripts** generated from the CLI (`deeppurgecli
      detection-script --program X`) — enterprise deployment enabler
- [ ] **Daily digest** email/toast summarising scheduled-cleaning runs
- [ ] **History tab** showing prior uninstall / cleanup activity from the log

## v0.12 — accessibility + polish

- [ ] Localization: `.resx` + Crowdin submission for top 10 UI strings
- [ ] High-contrast theme pass
- [ ] Screen-reader narration on new v0.9 panels
- [ ] Replace in-window toast with Windows toast notifications for completed scans

## Ideas / not committed

Things worth considering but not on a timeline:

- **Chocolatey integration** — `choco list --local-only` merging into the installed
  programs list, analogous to the existing winget + scoop path
- **OEM bloat scoring** — heuristics (publisher=Dell/HP/Lenovo, install source=OEM)
  to recommend batch-uninstall candidates on factory images
- **Portable app detection** — BCU-style folder scan for unregistered apps
- **Tray icon** — background scheduled cleaning with tray notifications
- **Android companion** — nope, scope creep, documented here only to flag it

## What we will NOT ship

Explicit "no" list, so anyone proposing these doesn't waste effort:

- **Multi-pass DoD wipes** — obsolete on SSDs, wastes write cycles. Single-pass
  cryptographic random already covers the real threat model.
- **Keyboard shortcuts** — user preference (see global CLAUDE.md)
- **Feature flags / A-B gating** — overkill for a local desktop tool
- **Cloud sync of settings** — privacy surface without clear value
- **MSIX distribution** — sandboxes DeepPurge out of the HKLM autorun edits it
  needs to function; actively harmful for this app
