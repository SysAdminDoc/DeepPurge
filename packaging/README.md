# Packaging

Package-manager manifests and publish checklists for DeepPurge releases.

## Release workflow

1. Tag the release: `git tag v0.9.0 && git push --tags`
2. GitHub Actions (`.github/workflows/release.yml`) publishes `DeepPurge.exe`, `DeepPurgeCli.exe`, and `SHA256SUMS.txt` as release assets.
3. Copy the SHA256 from `SHA256SUMS.txt` into the manifests below.

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

Before committing to the bucket, replace `<<REPLACE_WITH_RELEASE_ARTIFACT_SHA256>>` with the two real SHA256 hashes from `SHA256SUMS.txt` (GUI first, CLI second).

The `pre_install` hook drops a `DeepPurge.portable` marker so the app redirects all state to `$dir/Data/` — matches Scoop's user-scope philosophy.

## Chocolatey (optional)

Not shipped in v0.9.0. If you want it: `choco new deeppurge` and template off the portable installer type.

## Code signing

See `Build.ps1 -Sign` for the Authenticode signing pass. Requires `DEEPPURGE_CERT_PATH` and `DEEPPURGE_CERT_PASSWORD` environment variables (or a pre-loaded personal-store cert). Without signing, Windows SmartScreen will warn users on first run.
