# WebRTC-for-UWP build script (Windows 10 Mobile / ARM32 — Lumia 930)
#
# PURPOSE
#   "Test first" harness to BUILD the WebRTC dependency for UWP ARM and the bundled
#   PeerCC sample app, so we can prove a call works on the device BEFORE integrating
#   WebRTC into UniMatrix. Run this on the WINDOWS build machine (VS2017), NOT on macOS.
#
#   WebRTC handles only the media (audio/video/ICE). Signaling for UniMatrix will ride
#   over Matrix m.call.* events later; for this first test we just use the PeerCC sample's
#   own signaling to confirm the device can negotiate and carry a call.
#
# WHY THIS SDK (and not Microsoft WinRTC)
#   - webrtc-uwp-sdk (this script) is the ONLY one that ever targeted ARM32 + Win10 Mobile.
#     It is DEPRECATED (~2018, last branch M71). Expect bitrot — this is an experiment.
#   - microsoft/winrtc is the successor but targets modern Windows / Win32 / ARM64 only,
#     so it cannot produce an ARM32 Win10 Mobile binary for the Lumia 930.
#
# WHAT IT DOES
#   1. -CheckOnly : verifies every prerequisite and prints what's missing (no changes).
#   2. -Clone     : recursively clones webrtc-uwp-sdk close to the drive root (path-length).
#   3. -Build     : invokes VS2017 MSBuild on WebRtc.Universal.sln for ARM/Release.
#   Run with no switches to do Check -> Clone -> Build in sequence.
#
# USAGE (from an elevated "Developer Command Prompt for VS 2017" or PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\build-webrtc-uwp.ps1 -CheckOnly
#   powershell -ExecutionPolicy Bypass -File .\build-webrtc-uwp.ps1 -Clone -Build
#   powershell -ExecutionPolicy Bypass -File .\build-webrtc-uwp.ps1 -Build -Platform ARM -Configuration Release
#
# See README.md in this folder for the full runbook and known failure modes.

[CmdletBinding()]
param(
    # Keep the path SHORT — the WebRTC build hits the Windows MAX_PATH limit otherwise.
    [string]$RepoRoot   = "C:\webrtc-uwp",
    [ValidateSet("ARM","x86","x64")]
    [string]$Platform   = "ARM",
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release",
    [switch]$CheckOnly,
    [switch]$Clone,
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$RepoUrl   = "https://github.com/webrtc-uwp/webrtc-uwp-sdk"
$SlnRel    = "webrtc\windows\solutions\WebRtc.Universal.sln"

# If no action switch is given, do the whole sequence.
if (-not $CheckOnly -and -not $Clone -and -not $Build) {
    $Clone = $true; $Build = $true
}

function Write-Section($text) {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}
function Ok($t)   { Write-Host "  [ OK ] $t"   -ForegroundColor Green }
function Warn($t) { Write-Host "  [WARN] $t"   -ForegroundColor Yellow }
function Bad($t)  { Write-Host "  [FAIL] $t"   -ForegroundColor Red }

$script:Problems = 0
function Require($cond, $okMsg, $badMsg) {
    if ($cond) { Ok $okMsg } else { Bad $badMsg; $script:Problems++ }
}

# --------------------------------------------------------------------------------------
function Find-VsWhere {
    $p = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $p) { return $p }
    return $null
}

function Find-Vs2017Path {
    $vswhere = Find-VsWhere
    if (-not $vswhere) { return $null }
    # VS2017 is the 15.x range. The WebRTC sources are pinned to this toolset.
    $path = & $vswhere -version "[15.0,16.0)" -requires Microsoft.Component.MSBuild `
                       -property installationPath -nologo 2>$null | Select-Object -First 1
    if ($path) { return $path.Trim() }
    return $null
}

function Find-MsBuild {
    $vs = Find-Vs2017Path
    if (-not $vs) { return $null }
    $mb = Join-Path $vs "MSBuild\15.0\Bin\MSBuild.exe"
    if (Test-Path $mb) { return $mb }
    return $null
}

function Test-WindowsSdk($version) {
    $inc = "${env:ProgramFiles(x86)}\Windows Kits\10\Include\$version"
    return (Test-Path $inc)
}

# --------------------------------------------------------------------------------------
function Invoke-PrereqCheck {
    Write-Section "Prerequisite check"

    # OS
    Require ($env:OS -eq "Windows_NT") "Running on Windows" "This build MUST run on Windows (you are not on Windows)."

    # Path length — the single most common reason this build fails.
    Require ($RepoRoot.Length -le 24) `
        "Repo root path is short enough ('$RepoRoot')" `
        "Repo root '$RepoRoot' is long ($($RepoRoot.Length) chars). Use something like C:\webrtc-uwp; the build hits MAX_PATH."

    # git
    $git = Get-Command git -ErrorAction SilentlyContinue
    Require ($git -ne $null) "git found" "git is not on PATH. Install Git for Windows."

    # VS2017 + MSBuild 15
    $vs = Find-Vs2017Path
    Require ($vs -ne $null) "Visual Studio 2017 found ($vs)" `
        "Visual Studio 2017 (15.9.x) not found. The WebRTC sources are pinned to the v141 toolset; VS2019/2022 will NOT work."
    $msbuild = Find-MsBuild
    Require ($msbuild -ne $null) "MSBuild 15.0 found" "MSBuild 15.0 (VS2017) not found."

    # Windows SDKs — BOTH are required by webrtc-uwp-sdk.
    Require (Test-WindowsSdk "10.0.17134.0") "Windows SDK 10.0.17134 present" `
        "Windows SDK 10.0.17134 missing. Hard-coded in the Google sources. Install it (with 'Debugging Tools for Windows') from the SDK archive page."
    Require (Test-WindowsSdk "10.0.17763.0") "Windows SDK 10.0.17763 present" `
        "Windows SDK 10.0.17763 missing. Used by the UWP wrappers. Install it (with 'Debugging Tools for Windows') from the SDK archive page."

    # Python 2.7 — depot_tools/gn era. Python 3 will break the prepare scripts.
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) {
        $ver = (& python --version 2>&1) -join ""
        Require ($ver -match "2\.7") "Python 2.7 active ($ver)" `
            "Active python is '$ver'. webrtc-uwp needs Python 2.7.15 first on PATH (e.g. C:\Python27)."
    } else {
        Bad "python not on PATH. Install Python 2.7.15 and put C:\Python27 first on PATH."; $script:Problems++
    }

    # Strawberry Perl — used by BoringSSL build.
    $perl = Get-Command perl -ErrorAction SilentlyContinue
    Require ($perl -ne $null) "perl found" "Strawberry Perl not on PATH (needed by BoringSSL). Install from strawberryperl.com."

    # Disk space — a full WebRTC tree + build output is large.
    try {
        $drive = (Split-Path -Qualifier $RepoRoot)
        $free  = (Get-PSDrive ($drive.TrimEnd(':'))).Free
        $freeGb = [math]::Round($free / 1GB, 1)
        Require ($free -gt 40GB) "Free space on $drive : $freeGb GB" `
            "Only $freeGb GB free on $drive. Allow ~40 GB+ for the source tree and build outputs."
    } catch { Warn "Could not determine free disk space for $RepoRoot." }

    Write-Host ""
    if ($script:Problems -eq 0) {
        Ok "All prerequisites satisfied."
    } else {
        Bad "$($script:Problems) prerequisite problem(s) found. Fix these before building."
        Write-Host "      (See README.md in this folder for exact download links.)" -ForegroundColor Yellow
    }
    return ($script:Problems -eq 0)
}

# --------------------------------------------------------------------------------------
function Invoke-Clone {
    Write-Section "Clone webrtc-uwp-sdk"

    if (Test-Path (Join-Path $RepoRoot ".git")) {
        Ok "Repo already present at $RepoRoot — skipping clone."
        Warn "If you want a clean tree, delete $RepoRoot first and re-run with -Clone."
        return
    }

    $parent = Split-Path -Parent $RepoRoot
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    Write-Host "  Cloning (recursive, this pulls ALL submodules — can take a long time)..." -ForegroundColor Gray
    Write-Host "  git clone --recursive $RepoUrl $RepoRoot" -ForegroundColor Gray
    & git clone --recursive $RepoUrl $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)." }
    Ok "Clone complete."
}

# --------------------------------------------------------------------------------------
function Invoke-Build {
    Write-Section "Build WebRtc.Universal.sln ($Configuration | $Platform)"

    $sln = Join-Path $RepoRoot $SlnRel
    if (-not (Test-Path $sln)) {
        throw "Solution not found at $sln. Did the clone succeed? Run with -Clone first."
    }

    $msbuild = Find-MsBuild
    if (-not $msbuild) { throw "MSBuild 15.0 (VS2017) not found; cannot build." }

    Write-Host "  Solution : $sln" -ForegroundColor Gray
    Write-Host "  MSBuild  : $msbuild" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  NOTE: the FIRST build runs the native WebRTC prepare/compile (gn + ninja via the" -ForegroundColor Yellow
    Write-Host "        bundled depot_tools). This is the long, fragile step and may take 1-2 hours." -ForegroundColor Yellow
    Write-Host "        If it fails, capture the FIRST error and check README.md > Known failure modes." -ForegroundColor Yellow
    Write-Host ""

    $log = Join-Path $PSScriptRoot ("build-{0}-{1}.log" -f $Platform, $Configuration)

    & $msbuild $sln `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /m `
        /v:minimal `
        /flp:"logfile=$log;verbosity=detailed"

    if ($LASTEXITCODE -ne 0) {
        Bad "Build failed (exit $LASTEXITCODE). Full log: $log"
        throw "Build failed."
    }
    Ok "Build succeeded. Full log: $log"

    # Locate the produced dependency artifacts.
    Write-Section "Locating Org.WebRtc artifacts"
    $artifacts = Get-ChildItem -Path $RepoRoot -Recurse -Include "Org.WebRtc.winmd","Org.WebRtc.dll" -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -match [regex]::Escape("\$Platform\") -or $_.FullName -match $Platform }
    if ($artifacts) {
        foreach ($a in $artifacts) { Ok $a.FullName }
        Write-Host ""
        Write-Host "  These are the files UniMatrix would later reference (Org.WebRtc.winmd + .dll +" -ForegroundColor Gray
        Write-Host "  WebRtcScheme.dll). For THIS test, deploy the PeerCC sample instead — see README.md." -ForegroundColor Gray
    } else {
        Warn "Build reported success but no Org.WebRtc.winmd/.dll was found for $Platform. Inspect $log."
    }
}

# --------------------------------------------------------------------------------------
try {
    Write-Host "webrtc-uwp build harness  (Platform=$Platform Configuration=$Configuration RepoRoot=$RepoRoot)" -ForegroundColor White

    $prereqOk = Invoke-PrereqCheck
    if ($CheckOnly) { return }

    if (-not $prereqOk) {
        throw "Prerequisites not satisfied; aborting. Re-run with -CheckOnly after fixing them."
    }

    if ($Clone) { Invoke-Clone }
    if ($Build) { Invoke-Build }

    Write-Section "Done"
    Ok "Next: build/deploy the PeerCC sample to the Lumia and run a test call (README.md)."
}
catch {
    Write-Host ""
    Bad $_.Exception.Message
    exit 1
}
