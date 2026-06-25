# Roadmap

Living plan. Items sit here until they ship or get dropped; dropping is a valid outcome.
Blocked items live in `Roadmap_Blocked.md`.

## Research-Driven Additions

### P1 — High value, competitive differentiation




### P2 — Quality, reliability, developer experience




### P3 — Polish, differentiation








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
