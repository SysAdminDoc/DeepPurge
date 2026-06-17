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
