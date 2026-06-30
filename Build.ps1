<#
.SYNOPSIS
    DeepPurge Build Script v0.9.0
    Compiles the project into single portable .exe files (GUI + CLI)

.DESCRIPTION
    Automatically installs .NET 10 SDK if needed, then builds self-contained
    single-file portable executables. No Visual Studio required.

.NOTES
    Run from the DeepPurge project root directory.
    Output: build\DeepPurge.exe (GUI)  +  build\DeepPurgeCli.exe (CLI)
#>

param(
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipClean,
    [switch]$OpenOutput,
    # Run the xUnit suite after build, before publish. Release builds
    # should always use -Test; dev-inner-loop builds can skip.
    [switch]$Test,
    # ── Signing (optional) ──────────────────────────────────────────
    # Pass -Sign to Authenticode-sign the two published exes. Certificate
    # source is auto-detected in this order:
    #   1. -CertPath <.pfx>   + -CertPassword (or $env:DEEPPURGE_CERT_PASSWORD)
    #   2. $env:DEEPPURGE_CERT_PATH + $env:DEEPPURGE_CERT_PASSWORD
    #   3. -CertThumbprint <SHA1> pointing at a cert in CurrentUser\My
    # Only used for official releases — day-to-day dev builds skip signing.
    [switch]$Sign,
    [string]$CertPath,
    [securestring]$CertPassword,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$ValidateRelease,
    [switch]$ValidateReleaseOnly,
    [switch]$AuditDependenciesOnly,
    [string]$ReleaseChecksumsPath
)

$ErrorActionPreference = "Continue"
$ProjectRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($ProjectRoot)) { $ProjectRoot = Get-Location }

$BuildDir = Join-Path $ProjectRoot "build"
$SolutionFile = Join-Path $ProjectRoot "DeepPurge.sln"
$AppProject = Join-Path $ProjectRoot "src\DeepPurge.App\DeepPurge.App.csproj"
$CliProject = Join-Path $ProjectRoot "src\DeepPurge.Cli\DeepPurge.Cli.csproj"
$CoreProject = Join-Path $ProjectRoot "src\DeepPurge.Core\DeepPurge.Core.csproj"
$TestsProject = Join-Path $ProjectRoot "tests\DeepPurge.Tests\DeepPurge.Tests.csproj"

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Cyan
Write-Host "    DeepPurge Build Script v0.9.0" -ForegroundColor Cyan
Write-Host "  ============================================" -ForegroundColor Cyan
Write-Host ""

# ── Authenticode signing helper ───────────────────────────────
# Locates signtool.exe and a certificate (in priority order:
#   1. -CertPath + -CertPassword
#   2. env DEEPPURGE_CERT_PATH + DEEPPURGE_CERT_PASSWORD
#   3. -CertThumbprint in CurrentUser\My
# ) then signs each exe with SHA256 + RFC 3161 timestamping. Throws
# on failure so the caller can decide whether to ship unsigned.
function Get-SignTool {
    $candidates = @(
        (Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    )
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkRoot) {
        # Prefer the newest SDK build. signtool lives under <version>\<arch>\signtool.exe.
        Get-ChildItem $sdkRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
            $candidates += (Join-Path $_.FullName "x64\signtool.exe")
            $candidates += (Join-Path $_.FullName "x86\signtool.exe")
        }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    throw "signtool.exe not found. Install the Windows 10/11 SDK."
}

function Invoke-Signing {
    param([string[]]$ExePaths)

    $signtool = Get-SignTool

    # Resolve cert source.
    $pfxPath = $CertPath
    if ([string]::IsNullOrEmpty($pfxPath) -and -not [string]::IsNullOrEmpty($env:DEEPPURGE_CERT_PATH)) {
        $pfxPath = $env:DEEPPURGE_CERT_PATH
    }
    $pfxSecure = $CertPassword
    if (-not $pfxSecure -and -not [string]::IsNullOrEmpty($env:DEEPPURGE_CERT_PASSWORD)) {
        $pfxSecure = ConvertTo-SecureString -String $env:DEEPPURGE_CERT_PASSWORD -AsPlainText -Force
    }

    foreach ($exe in $ExePaths) {
        if (-not (Test-Path $exe)) { continue }

        if (-not [string]::IsNullOrEmpty($pfxPath) -and (Test-Path $pfxPath)) {
            # PFX-file path. signtool accepts the password as plaintext on its
            # command line — we decode from SecureString only right here.
            $pfxPlain = ''
            if ($pfxSecure) {
                $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pfxSecure)
                try { $pfxPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
                finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
            }
            $signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256",
                          "/f", $pfxPath)
            if ($pfxPlain) { $signArgs += @("/p", $pfxPlain) }
            $signArgs += $exe
            & $signtool @signArgs
        }
        elseif (-not [string]::IsNullOrEmpty($CertThumbprint)) {
            & $signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 /sha1 $CertThumbprint $exe
        }
        else {
            throw "No cert source: pass -CertPath + -CertPassword, -CertThumbprint, or set DEEPPURGE_CERT_PATH + DEEPPURGE_CERT_PASSWORD."
        }
        if ($LASTEXITCODE -ne 0) { throw "signtool failed on $exe (exit $LASTEXITCODE)" }

        # Verify the freshly-applied signature.
        & $signtool verify /pa /q $exe
        if ($LASTEXITCODE -ne 0) { throw "signature verify failed on $exe" }
    }
}

# ── Locate or Install .NET 10 SDK ──────────────────────────────
function Add-ReleaseValidationFailure {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [Parameter(Mandatory=$true)][string]$Message
    )

    if ($null -eq $script:ReleaseValidationFailures) {
        $script:ReleaseValidationFailures = [System.Collections.Generic.List[string]]::new()
    }
    $script:ReleaseValidationFailures.Add("${Key}: $Message") | Out-Null
}

function Get-CsprojVersion {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Key
    )

    if (-not (Test-Path $Path)) {
        Add-ReleaseValidationFailure $Key "file is missing"
        return $null
    }

    try {
        [xml]$projectXml = Get-Content $Path -Raw
        $version = $projectXml.Project.PropertyGroup |
            ForEach-Object { $_.Version } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($version)) {
            Add-ReleaseValidationFailure $Key "Version is missing"
            return $null
        }
        return $version.Trim()
    } catch {
        Add-ReleaseValidationFailure $Key "could not parse project XML: $_"
        return $null
    }
}

function Assert-ReleaseValue {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [AllowNull()][string]$Actual,
        [Parameter(Mandatory=$true)][string]$Expected
    )

    if ($Actual -ne $Expected) {
        Add-ReleaseValidationFailure $Key "expected '$Expected', found '$Actual'"
    }
}

function Assert-NoReleasePlaceholders {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Content
    )

    $lines = $Content -split "\r?\n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "PLACEHOLDER|REPLACE_WITH|<<") {
            Add-ReleaseValidationFailure "${Path}:$($i + 1)" "release placeholder remains"
        }
    }
}

function Get-ReleaseChecksumPath {
    if (-not [string]::IsNullOrWhiteSpace($ReleaseChecksumsPath)) {
        $resolved = Resolve-Path $ReleaseChecksumsPath -ErrorAction SilentlyContinue
        if ($resolved) { return $resolved.Path }
        return $ReleaseChecksumsPath
    }
    return (Join-Path $BuildDir "SHA256SUMS.txt")
}

function Read-ReleaseChecksums {
    param([Parameter(Mandatory=$true)][string]$Path)

    $checksums = @{}
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        Add-ReleaseValidationFailure "SHA256SUMS.txt" "checksum file is missing at '$Path'"
        return $checksums
    }

    $lineNumber = 0
    foreach ($line in Get-Content $Path) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch "^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>[^\\/:*?`"<>|\r\n]+)$") {
            Add-ReleaseValidationFailure "${Path}:$lineNumber" "expected '<64-char sha256>  <asset name>'"
            continue
        }
        $checksums[$matches.name.Trim()] = $matches.hash.ToUpperInvariant()
    }

    if ($checksums.Count -eq 0) {
        Add-ReleaseValidationFailure "SHA256SUMS.txt" "checksum file contains no assets"
    }

    return $checksums
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory=$true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "").ToUpperInvariant()
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Write-Sha256Sums {
    param([Parameter(Mandatory=$true)][string[]]$ArtifactPaths)

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($artifact in $ArtifactPaths) {
        if (-not (Test-Path $artifact)) {
            Add-ReleaseValidationFailure $artifact "release artifact is missing"
            continue
        }
        $hash = Get-FileSha256Hex $artifact
        $name = Split-Path $artifact -Leaf
        $lines.Add("$hash  $name") | Out-Null
    }

    $checksumPath = Join-Path $BuildDir "SHA256SUMS.txt"
    Set-Content -Path $checksumPath -Value $lines -Encoding ASCII
    Write-Host "  [OK] SHA256SUMS.txt generated" -ForegroundColor Green
    return $checksumPath
}

function Get-ReleaseAssetName {
    param([Parameter(Mandatory=$true)][string]$Url)

    $cleanUrl = ($Url -split "#", 2)[0]
    try {
        return [Uri]::UnescapeDataString(([Uri]$cleanUrl).Segments[-1])
    } catch {
        return [IO.Path]::GetFileName($cleanUrl)
    }
}

function Assert-ReleaseAsset {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$ManifestHash,
        [Parameter(Mandatory=$true)][hashtable]$Checksums,
        [Parameter(Mandatory=$true)][string]$Version
    )

    $expectedPrefix = "https://github.com/SysAdminDoc/DeepPurge/releases/download/v$Version/"
    if (-not $Url.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Add-ReleaseValidationFailure $Key "release URL must start with '$expectedPrefix'"
    }

    $assetName = Get-ReleaseAssetName $Url
    if ([string]::IsNullOrWhiteSpace($assetName)) {
        Add-ReleaseValidationFailure $Key "could not resolve asset name from URL"
        return
    }

    if (-not $Checksums.ContainsKey($assetName)) {
        Add-ReleaseValidationFailure $Key "asset '$assetName' is missing from SHA256SUMS.txt"
        return
    }

    $expectedHash = $Checksums[$assetName]
    if ($ManifestHash.ToUpperInvariant() -ne $expectedHash) {
        Add-ReleaseValidationFailure $Key "hash for '$assetName' must be '$expectedHash'"
    }
}

function Assert-LocalArtifactsMatchChecksums {
    param([Parameter(Mandatory=$true)][hashtable]$Checksums)

    foreach ($entry in $Checksums.GetEnumerator()) {
        $artifactPath = Join-Path $BuildDir $entry.Key
        if (-not (Test-Path $artifactPath)) {
            Add-ReleaseValidationFailure "build\$($entry.Key)" "checksum entry has no matching local artifact"
            continue
        }

        $actual = Get-FileSha256Hex $artifactPath
        if ($actual -ne $entry.Value) {
            Add-ReleaseValidationFailure "build\$($entry.Key)" "SHA256SUMS.txt has '$($entry.Value)', local file is '$actual'"
        }
    }
}

function Invoke-ReleaseReadinessValidation {
    $script:ReleaseValidationFailures = [System.Collections.Generic.List[string]]::new()

    Write-Host ""
    Write-Host "  [*] Validating release and package manifests..." -ForegroundColor Yellow

    $appVersion = Get-CsprojVersion $AppProject "src/DeepPurge.App/DeepPurge.App.csproj:Version"
    $coreVersion = Get-CsprojVersion $CoreProject "src/DeepPurge.Core/DeepPurge.Core.csproj:Version"
    $cliVersion = Get-CsprojVersion $CliProject "src/DeepPurge.Cli/DeepPurge.Cli.csproj:Version"
    if (-not [string]::IsNullOrWhiteSpace($appVersion)) {
        Assert-ReleaseValue "src/DeepPurge.Core/DeepPurge.Core.csproj:Version" $coreVersion $appVersion
        Assert-ReleaseValue "src/DeepPurge.Cli/DeepPurge.Cli.csproj:Version" $cliVersion $appVersion
    }

    $readmePath = Join-Path $ProjectRoot "README.md"
    $readme = if (Test-Path $readmePath) { Get-Content $readmePath -Raw } else { "" }
    if ($readme -match "version-v(?<version>\d+\.\d+\.\d+)") {
        Assert-ReleaseValue "README.md:version badge" $matches.version $appVersion
    } else {
        Add-ReleaseValidationFailure "README.md:version badge" "version badge is missing"
    }

    $buildScript = Get-Content (Join-Path $ProjectRoot "Build.ps1") -Raw
    if ($buildScript -match "Build Script v(?<version>\d+\.\d+\.\d+)") {
        Assert-ReleaseValue "Build.ps1:Build Script version" $matches.version $appVersion
    } else {
        Add-ReleaseValidationFailure "Build.ps1:Build Script version" "script version banner is missing"
    }

    $buildBatPath = Join-Path $ProjectRoot "BUILD.bat"
    $buildBat = if (Test-Path $buildBatPath) { Get-Content $buildBatPath -Raw } else { "" }
    if ($buildBat -match "Builder v(?<version>\d+\.\d+\.\d+)") {
        Assert-ReleaseValue "BUILD.bat:title version" $matches.version $appVersion
    } else {
        Add-ReleaseValidationFailure "BUILD.bat:title version" "title version is missing"
    }

    $checksumPath = Get-ReleaseChecksumPath
    $checksums = Read-ReleaseChecksums $checksumPath
    if ([string]::IsNullOrWhiteSpace($ReleaseChecksumsPath)) {
        Assert-LocalArtifactsMatchChecksums $checksums
    }

    $wingetPath = Join-Path $ProjectRoot "packaging\winget\SysAdminDoc.DeepPurge.yaml"
    if (Test-Path $wingetPath) {
        $winget = Get-Content $wingetPath -Raw
        Assert-NoReleasePlaceholders "packaging/winget/SysAdminDoc.DeepPurge.yaml" $winget

        if ($winget -match "(?m)^PackageVersion:\s*(?<version>\S+)") {
            Assert-ReleaseValue "packaging/winget/SysAdminDoc.DeepPurge.yaml:PackageVersion" $matches.version $appVersion
        } else {
            Add-ReleaseValidationFailure "packaging/winget/SysAdminDoc.DeepPurge.yaml:PackageVersion" "value is missing"
        }

        $urls = @([regex]::Matches($winget, "(?m)^\s*InstallerUrl:\s*(?<url>\S+)") | ForEach-Object { $_.Groups["url"].Value })
        $hashes = @([regex]::Matches($winget, "(?m)^\s*InstallerSha256:\s*(?<hash>\S+)") | ForEach-Object { $_.Groups["hash"].Value })
        if ($urls.Count -ne $hashes.Count) {
            Add-ReleaseValidationFailure "packaging/winget/SysAdminDoc.DeepPurge.yaml:Installers" "InstallerUrl count ($($urls.Count)) does not match InstallerSha256 count ($($hashes.Count))"
        }
        for ($i = 0; $i -lt [Math]::Min($urls.Count, $hashes.Count); $i++) {
            Assert-ReleaseAsset "packaging/winget/SysAdminDoc.DeepPurge.yaml:Installers[$i].InstallerSha256" $urls[$i] $hashes[$i] $checksums $appVersion
        }
    } else {
        Add-ReleaseValidationFailure "packaging/winget/SysAdminDoc.DeepPurge.yaml" "file is missing"
    }

    $scoopPath = Join-Path $ProjectRoot "packaging\scoop\deeppurge.json"
    if (Test-Path $scoopPath) {
        $scoopContent = Get-Content $scoopPath -Raw
        Assert-NoReleasePlaceholders "packaging/scoop/deeppurge.json" $scoopContent
        try {
            $scoop = $scoopContent | ConvertFrom-Json
            Assert-ReleaseValue "packaging/scoop/deeppurge.json:version" $scoop.version $appVersion
            $expectedHashUrl = 'https://github.com/SysAdminDoc/DeepPurge/releases/download/v$version/SHA256SUMS.txt'
            Assert-ReleaseValue "packaging/scoop/deeppurge.json:autoupdate.hash.url" $scoop.autoupdate.hash.url $expectedHashUrl

            foreach ($arch in $scoop.architecture.PSObject.Properties) {
                $urls = @($arch.Value.url)
                $hashes = @($arch.Value.hash)
                if ($urls.Count -ne $hashes.Count) {
                    Add-ReleaseValidationFailure "packaging/scoop/deeppurge.json:architecture.$($arch.Name)" "url count ($($urls.Count)) does not match hash count ($($hashes.Count))"
                    continue
                }
                for ($i = 0; $i -lt $urls.Count; $i++) {
                    Assert-ReleaseAsset "packaging/scoop/deeppurge.json:architecture.$($arch.Name).hash[$i]" $urls[$i] $hashes[$i] $checksums $appVersion
                }
            }
        } catch {
            Add-ReleaseValidationFailure "packaging/scoop/deeppurge.json" "could not parse JSON: $_"
        }
    } else {
        Add-ReleaseValidationFailure "packaging/scoop/deeppurge.json" "file is missing"
    }

    if (-not (Invoke-DependencyAuditValidation)) {
        Add-ReleaseValidationFailure "NuGet dependency audit" "project-level dependency audit failed"
    }

    if ($script:ReleaseValidationFailures.Count -gt 0) {
        Write-Host "  [ERROR] Release readiness validation failed:" -ForegroundColor Red
        foreach ($failure in $script:ReleaseValidationFailures) {
            Write-Host "       - $failure" -ForegroundColor Red
        }
        return $false
    }

    Write-Host "  [OK] Release readiness validation passed" -ForegroundColor Green
    return $true
}

function Add-DependencyAuditFailure {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [Parameter(Mandatory=$true)][string]$Message
    )

    if ($null -eq $script:DependencyAuditFailures) {
        $script:DependencyAuditFailures = [System.Collections.Generic.List[string]]::new()
    }
    $script:DependencyAuditFailures.Add("${Key}: $Message") | Out-Null
}

function Invoke-DependencyAuditCommand {
    param(
        [Parameter(Mandatory=$true)][string]$ProjectName,
        [Parameter(Mandatory=$true)][string]$AuditName,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$SuccessText,
        [Parameter(Mandatory=$true)][string]$FailureMessage
    )

    $output = & $script:DotNetExe @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Add-DependencyAuditFailure "$ProjectName $AuditName" "command failed with exit $LASTEXITCODE`n$output"
        return
    }

    if ($output -notmatch [regex]::Escape($SuccessText)) {
        Add-DependencyAuditFailure "$ProjectName $AuditName" "$FailureMessage`n$output"
    }
}

function Invoke-DependencyAuditValidation {
    $script:DependencyAuditFailures = [System.Collections.Generic.List[string]]::new()

    Write-Host ""
    Write-Host "  [*] Auditing NuGet dependencies project-by-project..." -ForegroundColor Yellow

    $projects = @(
        @{ Name = "DeepPurge.Core";  Path = $CoreProject  },
        @{ Name = "DeepPurge.App";   Path = $AppProject   },
        @{ Name = "DeepPurge.Cli";   Path = $CliProject   },
        @{ Name = "DeepPurge.Tests"; Path = $TestsProject }
    )

    foreach ($project in $projects) {
        if (-not (Test-Path $project.Path)) {
            Add-DependencyAuditFailure $project.Name "project file is missing at '$($project.Path)'"
            continue
        }

        Invoke-DependencyAuditCommand `
            -ProjectName $project.Name `
            -AuditName "outdated" `
            -Arguments @("list", $project.Path, "package", "--outdated", "--no-restore") `
            -SuccessText "has no updates" `
            -FailureMessage "outdated packages found"

        Invoke-DependencyAuditCommand `
            -ProjectName $project.Name `
            -AuditName "vulnerable" `
            -Arguments @("list", $project.Path, "package", "--vulnerable", "--include-transitive", "--no-restore") `
            -SuccessText "has no vulnerable packages" `
            -FailureMessage "vulnerable packages found"
    }

    if ($script:DependencyAuditFailures.Count -gt 0) {
        Write-Host "  [ERROR] NuGet dependency audit failed:" -ForegroundColor Red
        foreach ($failure in $script:DependencyAuditFailures) {
            Write-Host "       - $failure" -ForegroundColor Red
        }
        return $false
    }

    Write-Host "  [OK] NuGet dependency audit passed" -ForegroundColor Green
    return $true
}

function Find-DotNet {
    # Check common locations
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    ) | Where-Object { $_ -and (Test-Path $_ -ErrorAction SilentlyContinue) }

    foreach ($path in $candidates) {
        try {
            $output = & $path --version 2>&1 | Out-String
            $output = $output.Trim()
            if ($output -match "^10\.") {
                return $path
            }
        } catch { }
    }
    return $null
}

function Confirm-DotNetSDK {
    $dotnetPath = Find-DotNet
    if ($dotnetPath) {
        try {
            $version = (& $dotnetPath --version 2>&1 | Out-String).Trim()
            Write-Host "  [OK] .NET SDK $version found at: $dotnetPath" -ForegroundColor Green
            $script:DotNetExe = $dotnetPath
            return
        } catch { }
    }

    Write-Host "  [!] .NET 10 SDK not found. Installing..." -ForegroundColor Yellow
    Write-Host ""

    $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
    $installerPath = Join-Path $env:TEMP "dotnet-install.ps1"
    $installDir = Join-Path $env:LOCALAPPDATA "dotnet"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing -ErrorAction Stop

        Write-Host "  [*] Installing .NET 10 SDK to: $installDir" -ForegroundColor Yellow
        & $installerPath -Channel 10.0 -InstallDir $installDir

        # Update PATH for this session
        $env:PATH = "$installDir;$env:PATH"
        $env:DOTNET_ROOT = $installDir

        $dotnetExe = Join-Path $installDir "dotnet.exe"
        if (Test-Path $dotnetExe) {
            $version = (& $dotnetExe --version 2>&1 | Out-String).Trim()
            Write-Host "  [OK] .NET SDK $version installed" -ForegroundColor Green
            $script:DotNetExe = $dotnetExe
        } else {
            throw "dotnet.exe not found after installation"
        }
    }
    catch {
        Write-Host "  [ERROR] Failed to install .NET SDK: $_" -ForegroundColor Red
        Write-Host "  Download manually: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
        Write-Host ""
        Read-Host "  Press Enter to exit"
        exit 1
    }
}

$script:DotNetExe = "dotnet"
Confirm-DotNetSDK

# Ensure DOTNET_ROOT is set for the SDK to find its runtime packs
$dotnetDir = Split-Path $script:DotNetExe -Parent
$env:DOTNET_ROOT = $dotnetDir
$env:PATH = "$dotnetDir;$env:PATH"
Write-Host "  [*] DOTNET_ROOT = $dotnetDir" -ForegroundColor Gray
Write-Host ""

# ── Validate project files exist ──────────────────────────────
if (-not (Test-Path $SolutionFile)) {
    Write-Host "  [ERROR] Solution file not found: $SolutionFile" -ForegroundColor Red
    Write-Host "  Make sure you're running this from the DeepPurge root folder." -ForegroundColor Yellow
    Write-Host ""
    Read-Host "  Press Enter to exit"
    exit 1
}
if (-not (Test-Path $AppProject)) {
    Write-Host "  [ERROR] App project not found: $AppProject" -ForegroundColor Red
    Read-Host "  Press Enter to exit"
    exit 1
}
if (-not (Test-Path $CliProject)) {
    Write-Host "  [ERROR] CLI project not found: $CliProject" -ForegroundColor Red
    Read-Host "  Press Enter to exit"
    exit 1
}

if (-not (Test-Path $CoreProject)) {
    Write-Host "  [ERROR] Core project not found: $CoreProject" -ForegroundColor Red
    Read-Host "  Press Enter to exit"
    exit 1
}

if ($ValidateReleaseOnly) {
    if (-not (Invoke-ReleaseReadinessValidation)) { exit 1 }
    exit 0
}

if ($AuditDependenciesOnly) {
    if (-not (Invoke-DependencyAuditValidation)) { exit 1 }
    exit 0
}

# ── Clean ──────────────────────────────────────────────────────
if (-not $SkipClean) {
    Write-Host "  [*] Cleaning previous build artifacts..." -ForegroundColor Yellow
    if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force -ErrorAction SilentlyContinue }

    # Aggressively clean ALL bin/obj directories under src/
    $srcDir = Join-Path $ProjectRoot "src"
    if (Test-Path $srcDir) {
        Get-ChildItem -Path $srcDir -Recurse -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
            ForEach-Object {
                Write-Host "       Removing $($_.FullName)" -ForegroundColor DarkGray
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
    }

    # Also run dotnet clean to clear MSBuild caches
    try {
        & $script:DotNetExe clean $SolutionFile --nologo 2>&1 | Out-Null
    } catch { }
    Write-Host "  [OK] Clean complete" -ForegroundColor Green
}

New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null

# ── Check NuGet connectivity ───────────────────────────────────
Write-Host "  [*] Checking NuGet feed connectivity..." -ForegroundColor Yellow
try {
    $nugetCheck = Invoke-WebRequest -Uri "https://api.nuget.org/v3/index.json" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    if ($nugetCheck.StatusCode -eq 200) {
        Write-Host "  [OK] NuGet feed reachable" -ForegroundColor Green
    }
} catch {
    Write-Host "  [WARN] Cannot reach NuGet.org: $_" -ForegroundColor Yellow
    Write-Host "         Build may fail if runtime packs aren't cached locally." -ForegroundColor Yellow
}

# ── Verify project files ──────────────────────────────────────
Write-Host "  [*] Verifying project configuration..." -ForegroundColor Yellow
$appCsproj = Join-Path (Join-Path (Join-Path $ProjectRoot "src") "DeepPurge.App") "DeepPurge.App.csproj"
if (Test-Path $appCsproj) {
    $csprojContent = Get-Content $appCsproj -Raw
    if ($csprojContent -match "UseWindowsForms") {
        Write-Host "  [ERROR] App.csproj contains UseWindowsForms - this causes type ambiguity!" -ForegroundColor Red
        Write-Host "         Please re-extract from the latest archive to a CLEAN folder." -ForegroundColor Red
        Read-Host "  Press Enter to exit"
        exit 1
    }
    Write-Host "  [OK] Project files verified" -ForegroundColor Green
}

# ── Restore ────────────────────────────────────────────────────
Write-Host "  [*] Restoring NuGet packages..." -ForegroundColor Yellow
$nugetConfig = Join-Path $ProjectRoot "NuGet.Config"
$restoreArgs = @("restore", $SolutionFile, "--nologo", "--force", "--source", "https://api.nuget.org/v3/index.json")
if (Test-Path $nugetConfig) { $restoreArgs += @("--configfile", $nugetConfig) }
$restoreOutput = & $script:DotNetExe @restoreArgs 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [!] First restore attempt failed. Clearing NuGet caches and retrying..." -ForegroundColor Yellow
    & $script:DotNetExe nuget locals http-cache --clear 2>&1 | Out-Null
    & $script:DotNetExe nuget locals temp --clear 2>&1 | Out-Null
    $restoreOutput = & $script:DotNetExe @restoreArgs 2>&1 | Out-String
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [ERROR] Restore failed:" -ForegroundColor Red
    Write-Host $restoreOutput -ForegroundColor Gray
    Read-Host "  Press Enter to exit"
    exit 1
}
Write-Host "  [OK] Packages restored" -ForegroundColor Green

# -- Tests (optional, required for release) -------------------
if ($Test) {
    Write-Host ""
    Write-Host "  [*] Running test suite..." -ForegroundColor Yellow
    $testProject = Join-Path $ProjectRoot "tests\DeepPurge.Tests\DeepPurge.Tests.csproj"
    if (-not (Test-Path $testProject)) {
        Write-Host "  [!] Test project missing at $testProject - skipping." -ForegroundColor Yellow
    } else {
        & $script:DotNetExe test $testProject -c $Configuration --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  [ERROR] Tests failed - refusing to publish." -ForegroundColor Red
            Read-Host "  Press Enter to exit"
            exit 1
        }
        Write-Host "  [OK] All tests passed" -ForegroundColor Green
    }
}

# ── Build (Single-File Portable) ──────────────────────────────
Write-Host ""
Write-Host "  [*] Building portable single-file executable..." -ForegroundColor Yellow
Write-Host "      Configuration: $Configuration" -ForegroundColor Gray
Write-Host "      Runtime:       win-x64" -ForegroundColor Gray
Write-Host "      Self-contained: Yes" -ForegroundColor Gray
Write-Host "      Single-file:   Yes" -ForegroundColor Gray
Write-Host ""

$publishArgs = @(
    "publish", $AppProject,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "--output", $BuildDir,
    "--nologo",
    "--source", "https://api.nuget.org/v3/index.json"
)

$buildOutput = & $script:DotNetExe @publishArgs 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "  [ERROR] GUI build failed!" -ForegroundColor Red
    Write-Host ""
    Write-Host $buildOutput -ForegroundColor Gray
    Write-Host ""
    Read-Host "  Press Enter to exit"
    exit 1
}

# ── Build CLI companion ────────────────────────────────────────
Write-Host ""
Write-Host "  [*] Building CLI companion (DeepPurgeCli.exe)..." -ForegroundColor Yellow

$cliPublishArgs = @(
    "publish", $CliProject,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "--output", $BuildDir,
    "--nologo",
    "--source", "https://api.nuget.org/v3/index.json"
)

$cliOutput = & $script:DotNetExe @cliPublishArgs 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [ERROR] CLI build failed!" -ForegroundColor Red
    Write-Host $cliOutput -ForegroundColor Gray
    Read-Host "  Press Enter to exit"
    exit 1
}

# ── Build Slim (Framework-Dependent) ─────────────────────────
$SlimDir = Join-Path $BuildDir "slim"
New-Item -ItemType Directory -Path $SlimDir -Force | Out-Null

Write-Host ""
Write-Host "  [*] Building framework-dependent slim executables..." -ForegroundColor Yellow
Write-Host "      Output: build/slim/ (requires .NET 10 runtime on target)" -ForegroundColor Gray

$slimCommon = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--no-self-contained",
    "-p:PublishSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "--nologo",
    "--source", "https://api.nuget.org/v3/index.json"
)
$slimGuiOut = & $script:DotNetExe publish $AppProject @slimCommon --output $SlimDir 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [WARN] Slim GUI build failed (self-contained builds still available)" -ForegroundColor Yellow
} else {
    $slimCliOut = & $script:DotNetExe publish $CliProject @slimCommon --output $SlimDir 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [WARN] Slim CLI build failed" -ForegroundColor Yellow
    } else {
        Get-ChildItem $SlimDir -Exclude "DeepPurge.exe","DeepPurgeCli.exe" |
            Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
        $slimGuiSize = if (Test-Path (Join-Path $SlimDir "DeepPurge.exe")) { [math]::Round((Get-Item (Join-Path $SlimDir "DeepPurge.exe")).Length / 1MB, 1) } else { 0 }
        $slimCliSize = if (Test-Path (Join-Path $SlimDir "DeepPurgeCli.exe")) { [math]::Round((Get-Item (Join-Path $SlimDir "DeepPurgeCli.exe")).Length / 1MB, 1) } else { 0 }
        Write-Host "  [OK] Slim GUI: $slimGuiSize MB  |  Slim CLI: $slimCliSize MB" -ForegroundColor Green
    }
}

# ── Verify Output ──────────────────────────────────────────────
$exePath = Join-Path $BuildDir "DeepPurge.exe"
$cliPath = Join-Path $BuildDir "DeepPurgeCli.exe"
if (Test-Path $exePath) {
    $guiInfo = Get-Item $exePath
    $guiSizeMB = [math]::Round($guiInfo.Length / 1MB, 1)
    $cliSizeMB = 0
    if (Test-Path $cliPath) { $cliSizeMB = [math]::Round((Get-Item $cliPath).Length / 1MB, 1) }

    # Keep only the two final exes; drop side artifacts (pdb leftovers, hostfxr extras).
    Get-ChildItem $BuildDir -Exclude "DeepPurge.exe","DeepPurgeCli.exe" |
        Remove-Item -Force -Recurse -ErrorAction SilentlyContinue

    # ── Authenticode signing (release only, optional) ─────────────
    if ($Sign) {
        Write-Host "  [*] Signing release artifacts..." -ForegroundColor Yellow
        try {
            Invoke-Signing -ExePaths @($exePath, $cliPath)
            Write-Host "  [OK] Authenticode signature applied" -ForegroundColor Green
        } catch {
            Write-Host "  [ERROR] Signing failed: $_" -ForegroundColor Red
            Write-Host "  Continuing with unsigned artifacts. SmartScreen will warn users." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  [i] Skipped signing (-Sign not passed). Release builds should sign." -ForegroundColor DarkGray
    }

    $checksumPath = Write-Sha256Sums -ArtifactPaths @($exePath, $cliPath)
    if ($ValidateRelease) {
        if (-not (Invoke-ReleaseReadinessValidation)) { exit 1 }
    }

    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host "    BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "    GUI:      $exePath ($guiSizeMB MB)" -ForegroundColor White
    Write-Host "    CLI:      $cliPath ($cliSizeMB MB)" -ForegroundColor White
    Write-Host "    SHA256:   $checksumPath" -ForegroundColor White
    Write-Host ""
    Write-Host "    This is a portable executable." -ForegroundColor Gray
    Write-Host "    No installation required - just run it." -ForegroundColor Gray
    Write-Host "    Requires: Windows 10/11 x64" -ForegroundColor Gray
    Write-Host "    Must run as: Administrator" -ForegroundColor Gray
    Write-Host ""

    if ($OpenOutput) {
        Start-Process explorer.exe -ArgumentList "/select,`"$exePath`""
    }
}
else {
    Write-Host "  [ERROR] Output exe not found at: $exePath" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Build output:" -ForegroundColor Gray
    Write-Host $buildOutput -ForegroundColor Gray
    Read-Host "  Press Enter to exit"
    exit 1
}
