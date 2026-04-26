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

## Open-Source Research (Round 2)

### Related OSS Projects
- **Klocman/Bulk-Crap-Uninstaller** — https://github.com/Klocman/Bulk-Crap-Uninstaller — Reference implementation for bulk uninstall + leftover scanner with Very-Good/Good/Questionable confidence ratings. Apache 2.0.
- **bleachbit/bleachbit** — https://github.com/bleachbit/bleachbit — GPL-v3 system cleaner; open cleaner-definition format (`.xml`) that third parties extend.
- **BleachBit-Software winapp2** — https://github.com/MoscaDotTo/Winapp2 — Community-maintained database of 1400+ CCleaner-format cleaning rules, consumable by BleachBit.
- **adileo/squirreldisk** — https://github.com/adileo/squirreldisk — Rust cross-platform disk analyzer; visual tree-map UI reference.
- **WinDirStat/windirstat** — https://github.com/windirstat/windirstat — Classic treemap; 2024 rewrite parallelized the scan.
- **henrypp/registrar** — https://github.com/henrypp/registrar — Small OSS Autoruns alternative for startup/run-key management.
- **Sysinternals/Autoruns** — Reference (closed) for the exhaustive autorun surface enumeration — worth matching feature-for-feature.
- **paolomainardi/czkawka** / **qarmin/czkawka** — https://github.com/qarmin/czkawka — Rust multi-tool for duplicates, empty folders, broken symlinks, big-files; directly comparable to DeepPurge Cleanup tabs.

### Features to Borrow
- Three-tier leftover confidence rating ("Very Good / Good / Questionable") surfaced inline in the scan UI — borrow from `Bulk-Crap-Uninstaller`.
- Winapp2.ini ingestion so DeepPurge inherits 1400+ community-curated cleaning rules on day one — borrow from `Winapp2` format (BleachBit also consumes it).
- Open XML cleaner-definition format so power users can ship their own definitions — borrow from `bleachbit/cleanerml`.
- Certificate-verify pass on installer executables before running them (flag tampering) — borrow from `Bulk-Crap-Uninstaller` verify-certificates feature.
- Portable-app detection (apps never registered in the uninstall registry) — borrow from `Bulk-Crap-Uninstaller`.
- Command-line mode with export-program-list and silent uninstall selectors for Intune/SCCM deployment — borrow from `Bulk-Crap-Uninstaller` CLI.
- Broken context-menu-entry cleaner as a dedicated tab — borrow from `Bulk-Crap-Uninstaller`'s post-uninstall pass.
- Duplicate/empty-folder/broken-symlink sweep alongside other Cleanup tasks — borrow from `czkawka`.

### Patterns & Architectures Worth Studying
- `Bulk-Crap-Uninstaller`'s uninstall-system adapters (NSIS, InnoSetup, MSI, Squirrel, Chocolatey, Scoop, winget, Steam, UWP) as a plugin family — replicate instead of hardcoding silent flags.
- `BleachBit`'s declarative cleaner XML + preview/dry-run separation from the executor — already partly in DeepPurge; the XML format makes it extensible.
- `Winapp2` repo model: data-only community repo (no code) auto-consumed by the app — low-maintenance way to keep cleaning rules fresh.
- `czkawka` pipelining: cache-by-size → cache-by-hash-prefix → full hash, mirroring DeepPurge's progressive pipeline (same pattern family as DuplicateFF in this repo family).

## Implementation Deep Dive (Round 3)

### Reference Implementations to Study
- **bleachbit/winapp2.ini** — https://github.com/bleachbit/winapp2.ini — canonical BleachBit-compatible winapp2 fork; parse `SpecialDetect=DET_CHROME` etc. for app applicability (the planned v0.10 item)
- **MoscaDotTo/Winapp2** — https://github.com/moscadotto/winapp2 — upstream community-maintained set; broader than BleachBit's fork, use as secondary sync source
- **BleachBit docs winapp2ini.html** — https://docs.bleachbit.org/doc/winapp2ini.html — environment-variable expansion (`%LocalAppData%`, `%CommonAppData%`), `RECURSE` and `REMOVESELF` semantics; required to parse entries correctly
- **Microsoft.Diagnostics.Tracing.TraceEvent** — https://github.com/microsoft/perfview/tree/main/src/TraceEvent — the C# TraceEvent NuGet reference; use for `Microsoft-Windows-Kernel-File` + `Microsoft-Windows-Kernel-Registry` providers
- **Sysinternals Procmon/Procmon24 ETW manifest** — https://learn.microsoft.com/en-us/sysinternals/downloads/procmon — Process Monitor's filtering and column semantics are the UX reference for the "Track This Installer" flow
- **dotnet/runtime FileSystemWatcher + USN journal** — https://github.com/dotnet/runtime/issues/24079 — discussion + links for why `FileSystemWatcher` misses events; USN `FSCTL_QUERY_USN_JOURNAL` is the only reliable path
- **chentsulin/electron-react-boilerplate AutoUpdater refs** — not applicable; instead see **velopack/velopack** — https://github.com/velopack/velopack — for signed MSIX/winget release wiring; copy the CI `Sign.ps1` pattern

### Known Pitfalls from Similar Projects
- **FileSystemWatcher drops events under load** — https://github.com/dotnet/runtime/issues/24079 — buffer overruns silently produce `InternalBufferOverflowException`; never rely on it for install tracing, use USN journal
- **USN journal wrap-around** — https://learn.microsoft.com/en-us/windows/win32/fileio/walking-a-buffer-of-change-journal-records — default journal is tiny and can wrap mid-install; grow to ≥64MB via `FSCTL_CREATE_USN_JOURNAL` before tracing
- **Registry ETW requires admin + SeSystemProfilePrivilege** — https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-a-nt-kernel-logger-session — the tracing session won't start under a standard user; surface a UAC prompt, don't silently fall back
- **winapp2 entries can delete user data if parsed wrong** — https://wp.bleachbit.org/forums/reply/winapp2-ini-2/ — `RECURSE` on a wrongly expanded variable can nuke profile dirs; always sandbox-test entries on a junction before shipping
- **Driver store parsing regressions** — Windows 11 24H2 changed `pnputil /enum-drivers` output; the `DriverStoreScanner.ParseText` tests listed in v0.9.x must lock both 23H2 and 24H2 fixtures
- **MSIX + PSGallery dual packaging** — https://learn.microsoft.com/en-us/windows/msix/overview — MSIX runs in a container; ETW collection and USN reads are blocked from inside MSIX; ship MSIX for the UI and a separate admin-elevated CLI for tracing
- **VirusTotal false positives on installer monitors** — ETW + USN tools get flagged as rootkits; submit to Defender + common AVs with "Microsoft Partner Portal" submission before GA

### Library Integration Checklist
- **.NET 9** WPF — target `net9.0-windows`, `UseWPF=true`; build with `PublishReadyToRun=true` for cold-start; framework-dependent per stack-csharp.md guidance
- **Microsoft.Diagnostics.Tracing.TraceEvent 3.2+** (NuGet) — API entry `TraceEventSession("DeepPurgeInstallMonitor")` with `EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | Registry)`; gotcha: must run as admin, and `Stop()` + `Dispose()` or the session leaks across reboots in the registry
- **PInvoke on `DeviceIoControl` + `FSCTL_QUERY_USN_JOURNAL`** — no good official wrapper; use `Windows.Win32` CsWin32 generator (https://github.com/microsoft/CsWin32); gotcha: struct alignment differs between 32/64-bit, force x64-only
- **winapp2.ini parser** — no good .NET lib; hand-written INI parser (the file is non-standard — allows duplicate keys); gotcha: environment variable expansion must happen per-user, don't cache across profiles
- **ManagedBass or NAudio** not needed; pure .NET/WPF
- **Microsoft.Wim.dll** (via WIM API) — for snapshotting Program Files for v0.10 diff flow; alternative: VSS (Volume Shadow Copy) via `AlphaVSS` NuGet; gotcha: VSS requires `SeBackupPrivilege` — elevate explicitly
- **Authenticode signing** — `signtool.exe` from Windows SDK 10.0.26100+; pass `/fd SHA256 /td SHA256 /tr http://timestamp.digicert.com`; gotcha: EV certs for MSIX are on Azure Code Signing now (free for OSS via the Microsoft program)
- **winget manifest schema 1.6** — https://github.com/microsoft/winget-pkgs/tree/master/doc/manifest/schema/1.6.0 — CI-validate with `winget validate --manifest`; `AppsAndFeaturesEntries` must match the installed registry for upgrade detection

