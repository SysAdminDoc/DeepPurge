# Packaging DeepPurge

DeepPurge publishes portable Windows executables through GitHub Releases. The Scoop manifest in this folder is the supported package-manager definition.

## Release checklist

1. Run `Build.ps1 -Sign` locally. A Release build performs locked restore, all tests, and the project-level NuGet dependency audit before it publishes anything.
2. Confirm `build\DeepPurge.exe`, `build\DeepPurgeCli.exe`, and `build\SHA256SUMS.txt` are present.
3. Copy the two generated SHA256 values into `packaging\scoop\deeppurge.json` for the matching release URLs.
4. Run `Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath build\SHA256SUMS.txt` and fix every reported key.
5. Tag the release with `git tag v0.9.2` and push the tag.
6. Create the GitHub Release and attach both executables plus `SHA256SUMS.txt`.

Run `Build.ps1 -AuditDependenciesOnly` when you only need the dependency gate.

## Scoop

The manifest at `packaging\scoop\deeppurge.json` installs both executables. It exposes `DeepPurgeCli.exe` on `PATH`, adds a DeepPurge shortcut, and creates the `DeepPurge.portable` marker so app data stays inside Scoop's managed directory.

Before copying the manifest into a bucket, verify that its version, URLs, and hashes match the published release. The release validator checks the GUI hash first and the CLI hash second.

## Code signing

`Build.ps1 -Sign` accepts a PFX path and secure password, environment variables, or a Current User certificate-store thumbprint. It applies SHA256 Authenticode signatures with RFC 3161 timestamps and verifies both executables after signing.

If no suitable certificate is available, don't present the artifacts as signed. Windows SmartScreen may warn users on first launch.
