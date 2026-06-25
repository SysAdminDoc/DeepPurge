# Blocked Roadmap Items

Items moved here from ROADMAP.md because they require external resources,
credentials, or human judgment that cannot be resolved during autonomous
development passes.

## v0.9.x — stabilization

- [ ] **Submit winget manifest** to `microsoft/winget-pkgs`
      Blocked: needs a published GitHub Release with a stable download URL
      and ideally an Authenticode-signed binary.

- [ ] **Create `SysAdminDoc/scoop-bucket`** repo and commit the Scoop manifest
      Blocked: needs GitHub repo creation + a published release to populate
      the manifest's `url` and `hash` fields.

- [ ] **Obtain code-signing certificate** and wire `DEEPPURGE_CERT_PATH` /
      `DEEPPURGE_CERT_PASSWORD` secrets into the release workflow
      Blocked: requires purchase decision (EV vs OV), Azure Code Signing
      enrollment, and secret provisioning in GitHub Actions.

- [ ] **Real-world field test** of each v0.9 panel on a freshly-imaged
      Windows 11 VM
      Blocked: requires a clean VM image and manual human testing.

## v0.12 — accessibility + polish

- [ ] **Crowdin submission** for localization strings
      Blocked: requires Crowdin project creation and external contributor setup.
      The `.resx` infrastructure is already in place (`Properties/Resources.resx`).

## v1.0 — large features (need dedicated session + testing)

- [ ] **Velopack auto-updater**
      Blocked: requires Velopack NuGet integration, release workflow changes,
      and end-to-end testing of the download/apply/restart flow.
      `Core/Updates/UpdateChecker.cs` has the detection; Velopack adds the apply.

- [ ] **ETW registry monitoring for install tracking**
      Blocked: requires `Microsoft.Diagnostics.Tracing.TraceEvent` NuGet,
      kernel-level ETW session setup, and testing under real installer workloads.
      The USN journal + snapshot approach works for now.

- [ ] **CIM migration from System.Management (WMI)**
      Blocked: low-value for Windows-only app. System.Management works fine on
      Windows; CIM migration adds complexity without clear benefit. Revisit if
      the project ever targets cross-platform.

- [ ] **Hunter Mode (drag-to-identify)**
      Blocked: requires new WPF overlay window with Win32 `WindowFromPoint` +
      `GetWindowThreadProcessId` interop and visual/interactive testing.
