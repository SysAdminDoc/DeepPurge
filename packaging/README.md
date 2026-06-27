# Packaging

Package-manager manifests and publish checklists for DeepPurge releases.

## Release workflow

1. Run `BUILD.bat` or `Build.ps1` locally and verify both published executables.
2. Generate `SHA256SUMS.txt` locally for `DeepPurge.exe` and `DeepPurgeCli.exe`.
3. Tag the release: `git tag v0.9.0 && git push --tags`.
4. Create or update the GitHub Release with `gh release create` / `gh release upload` and attach both executables plus `SHA256SUMS.txt`.
5. Copy the SHA256 values into the manifests below.

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

## Chocolatey

Runtime Chocolatey discovery is built into DeepPurge through `choco list --local-only --limit-output`. A Chocolatey package manifest is still optional distribution work; template it with `choco new deeppurge` if a release needs Chocolatey installation support.

## Code signing

See `Build.ps1 -Sign` for the Authenticode signing pass. Requires `DEEPPURGE_CERT_PATH` and `DEEPPURGE_CERT_PASSWORD` environment variables (or a pre-loaded personal-store cert). Without signing, Windows SmartScreen will warn users on first run.
