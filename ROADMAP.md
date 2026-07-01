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

