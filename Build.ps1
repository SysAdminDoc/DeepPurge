<#
.SYNOPSIS
    DeepPurge Build Script v0.8.0
    Compiles the project into a single portable .exe

.DESCRIPTION
    Automatically installs .NET 8 SDK if needed, then builds a self-contained
    single-file portable executable. No Visual Studio required.

.NOTES
    Run from the DeepPurge project root directory.
    Output: build\DeepPurge.exe
#>

param(
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipClean,
    [switch]$OpenOutput
)

$ErrorActionPreference = "Continue"
$ProjectRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($ProjectRoot)) { $ProjectRoot = Get-Location }

$BuildDir = Join-Path $ProjectRoot "build"
$SolutionFile = Join-Path $ProjectRoot "DeepPurge.sln"
$AppProject = Join-Path $ProjectRoot "src\DeepPurge.App\DeepPurge.App.csproj"

Write-Host ""
Write-Host "  ============================================" -ForegroundColor Cyan
Write-Host "    DeepPurge Build Script v0.8.0" -ForegroundColor Cyan
Write-Host "  ============================================" -ForegroundColor Cyan
Write-Host ""

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
    Write-Host "  [ERROR] Build failed!" -ForegroundColor Red
    Write-Host ""
    Write-Host $buildOutput -ForegroundColor Gray
    Write-Host ""
    Read-Host "  Press Enter to exit"
    exit 1
}

# ── Verify Output ──────────────────────────────────────────────
$exePath = Join-Path $BuildDir "DeepPurge.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    $sizeMB = [math]::Round($fileInfo.Length / 1MB, 1)

    # Clean up extra files that dotnet publish creates alongside the exe
    Get-ChildItem $BuildDir -Exclude "DeepPurge.exe" | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host "    BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "  ============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "    Output:   $exePath" -ForegroundColor White
    Write-Host "    Size:     $sizeMB MB" -ForegroundColor White
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
