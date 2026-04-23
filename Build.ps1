<#
.SYNOPSIS
    DeepPurge Build Script v0.9.0
    Compiles the project into single portable .exe files (GUI + CLI)

.DESCRIPTION
    Automatically installs .NET 8 SDK if needed, then builds self-contained
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
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Continue"
$ProjectRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($ProjectRoot)) { $ProjectRoot = Get-Location }

$BuildDir = Join-Path $ProjectRoot "build"
$SolutionFile = Join-Path $ProjectRoot "DeepPurge.sln"
$AppProject = Join-Path $ProjectRoot "src\DeepPurge.App\DeepPurge.App.csproj"
$CliProject = Join-Path $ProjectRoot "src\DeepPurge.Cli\DeepPurge.Cli.csproj"

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

# ── Locate or Install .NET 8 SDK ──────────────────────────────
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
            if ($output -match "^8\.") {
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

    Write-Host "  [!] .NET 8 SDK not found. Installing..." -ForegroundColor Yellow
    Write-Host ""

    $installerUrl = "https://dot.net/v1/dotnet-install.ps1"
    $installerPath = Join-Path $env:TEMP "dotnet-install.ps1"
    $installDir = Join-Path $env:LOCALAPPDATA "dotnet"

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing -ErrorAction Stop

        Write-Host "  [*] Installing .NET 8 SDK to: $installDir" -ForegroundColor Yellow
        & $installerPath -Channel 8.0 -InstallDir $installDir

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
        Write-Host "  Download manually: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
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

    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host "    BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "    GUI:      $exePath ($guiSizeMB MB)" -ForegroundColor White
    Write-Host "    CLI:      $cliPath ($cliSizeMB MB)" -ForegroundColor White
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
