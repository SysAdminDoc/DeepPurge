# Packaging

Package-manager manifests and publish checklists for DeepPurge releases.

## Release workflow

1. Run `Build.ps1 -Test -Sign` locally and verify `build\DeepPurge.exe`, `build\DeepPurgeCli.exe`, and `build\SHA256SUMS.txt`.
2. Copy the generated SHA256 values into the winget and Scoop manifests for the exact assets being released.
3. Run `Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath build\SHA256SUMS.txt`; fix every reported file/key before publishing.
4. Tag the release: `git tag v0.9.0 && git push --tags`.
5. Create or update the GitHub Release with `gh release create` / `gh release upload` and attach both executables plus `SHA256SUMS.txt`.

## winget

`packaging/winget/SysAdminDoc.DeepPurge.yaml` is a singleton manifest. To submit:

```powershell
wingetcreate update SysAdminDoc.DeepPurge --version 0.9.0 --urls https://github.com/SysAdminDoc/DeepPurge/releases/download/v0.9.0/DeepPurge.exe
```

The tool will split the singleton into the required three-file form (Version / Installer / DefaultLocale) and open a PR against `microsoft/winget-pkgs`.

## Scoop

`packaging/scoop/deeppurge.json` is ready for a personal bucket:

```powershell
scoop bucket add sysadmindoc https://github.com/SysAdminDoc/scoop-bucket
scoop install sysadmindoc/deeppurge
```

Before committing to the bucket, run `Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath <release SHA256SUMS.txt>` and confirm every Scoop URL/hash pair matches the checksum file (GUI first, CLI second).

The `pre_install` hook drops a `DeepPurge.portable` marker so the app redirects all state to `$dir/Data/` — matches Scoop's user-scope philosophy.

## Chocolatey

Runtime Chocolatey discovery is built into DeepPurge through `choco list --local-only --limit-output`. A Chocolatey package manifest is still optional distribution work; template it with `choco new deeppurge` if a release needs Chocolatey installation support.

## Code signing

See `Build.ps1 -Sign` for the Authenticode signing pass. Requires `DEEPPURGE_CERT_PATH` and `DEEPPURGE_CERT_PASSWORD` environment variables (or a pre-loaded personal-store cert). Without signing, Windows SmartScreen will warn users on first run.
