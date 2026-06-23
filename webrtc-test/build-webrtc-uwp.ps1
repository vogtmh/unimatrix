# WebRTC-for-UWP build script (Windows 10 Mobile / ARM32 - Lumia 930)
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
#     It is DEPRECATED (~2018, last branch M71). Expect bitrot - this is an experiment.
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
    # Keep the path SHORT - the WebRTC build hits the Windows MAX_PATH limit otherwise.
    [string]$RepoRoot   = "C:\webrtc-uwp",
    [ValidateSet("ARM","x86","x64")]
    [string]$Platform   = "ARM",
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release",
    [switch]$CheckOnly,
    [switch]$Clone,
    [switch]$Build,
    # Copy the built Org.WebRtc.winmd/.dll + WebRtcScheme.dll into a per-arch app folder.
    [switch]$Harvest,
    [string]$HarvestDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "UniMatrix\UniMatrix\libs\webrtc")
)

$ErrorActionPreference = "Stop"
$RepoUrl   = "https://github.com/webrtc-uwp/webrtc-uwp-sdk"
$SlnRel    = "webrtc\windows\solutions\WebRtc.Universal.sln"

# If no action switch is given, do the whole sequence.
if (-not $CheckOnly -and -not $Clone -and -not $Build -and -not $Harvest) {
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

# Returns the version string (e.g. "2.7.18") of a usable Python, or $null.
# Robust against: ErrorActionPreference=Stop + Python 2 writing its version to stderr,
# and a Python-3-first PATH (falls back to 'py -2' and C:\Python27\python.exe).
$script:PyHow = ""
function Get-PythonVersion {
    $candidates = @(
        @{ How = "python";              Exe = "python";   Args = @("--version") },
        @{ How = "py -2";               Exe = "py";       Args = @("-2","--version") },
        @{ How = "C:\Python27\python";  Exe = "C:\Python27\python.exe"; Args = @("--version") }
    )
    foreach ($c in $candidates) {
        try {
            # Only attempt 'py'/'python' if resolvable; full paths are tested directly.
            if ($c.Exe -notmatch '[\\/]' -and -not (Get-Command $c.Exe -ErrorAction SilentlyContinue)) { continue }
            if ($c.Exe -match '[\\/]' -and -not (Test-Path $c.Exe)) { continue }

            # Capture stdout+stderr WITHOUT letting a stderr write become a terminating error.
            $old = $ErrorActionPreference
            $ErrorActionPreference = "Continue"
            $out = & $c.Exe $c.Args 2>&1 | Out-String
            $ErrorActionPreference = $old

            $m = [regex]::Match($out, '(\d+\.\d+\.\d+)')
            if (-not $m.Success) { $m = [regex]::Match($out, '(\d+\.\d+)') }
            if ($m.Success) {
                $ver = $m.Groups[1].Value
                if ($ver -match '^2\.7') { $script:PyHow = $c.How; return $ver }
                # Remember a non-2.7 hit only if we haven't found anything better yet.
                if (-not $script:PyHow) { $script:PyHow = $c.How; $script:PyFallback = $ver }
            }
        } catch { }
    }
    if ($script:PyFallback) { return $script:PyFallback }
    return $null
}

# --------------------------------------------------------------------------------------
function Invoke-PrereqCheck {
    Write-Section "Prerequisite check"

    # OS
    Require ($env:OS -eq "Windows_NT") "Running on Windows" "This build MUST run on Windows (you are not on Windows)."

    # Path length - the single most common reason this build fails.
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

    # Windows SDKs - BOTH are required by webrtc-uwp-sdk.
    Require (Test-WindowsSdk "10.0.17134.0") "Windows SDK 10.0.17134 present" `
        "Windows SDK 10.0.17134 missing. Hard-coded in the Google sources. Install it (with 'Debugging Tools for Windows') from the SDK archive page."
    Require (Test-WindowsSdk "10.0.17763.0") "Windows SDK 10.0.17763 present" `
        "Windows SDK 10.0.17763 missing. Used by the UWP wrappers. Install it (with 'Debugging Tools for Windows') from the SDK archive page."

    # Debugging Tools for Windows - run.py's prepare step ABORTS with
    # "Windows SDK debug tools are missing!" if this optional SDK feature is absent.
    $dbg = "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers"
    $haveDbg = (Test-Path (Join-Path $dbg "x86\windbg.exe")) -or (Test-Path (Join-Path $dbg "x64\windbg.exe"))
    Require $haveDbg "Debugging Tools for Windows present ($dbg)" `
        "Debugging Tools for Windows MISSING. run.py aborts with 'Windows SDK debug tools are missing!'. Fix: Settings > Apps > 'Windows Software Development Kit' > Modify > check 'Debugging Tools for Windows', for BOTH 10.0.17134 and 10.0.17763."

    # Python 2.7 - depot_tools/gn era. Python 3 will break the prepare scripts.
    # NOTE: Python 2.7 prints its version to STDERR. We must NOT let ErrorActionPreference=Stop
    # turn that stderr write into a terminating error, so query it carefully.
    $pyVer = Get-PythonVersion
    if ($pyVer) {
        Require ($pyVer -match "^2\.7") "Python 2.7 active ($pyVer via '$script:PyHow')" `
            "Active python is '$pyVer' (via '$script:PyHow'). webrtc-uwp needs Python 2.7.x first on PATH (e.g. C:\Python27). If you have Python 3 earlier on PATH, move C:\Python27 ahead of it."
    } else {
        Bad "Could not find a Python 2.7 interpreter (checked 'python', 'py -2', and C:\Python27\python.exe). Install Python 2.7.x and put C:\Python27 first on PATH."
        $script:Problems++
    }

    # Strawberry Perl - used by BoringSSL build.
    $perl = Get-Command perl -ErrorAction SilentlyContinue
    Require ($perl -ne $null) "perl found" "Strawberry Perl not on PATH (needed by BoringSSL). Install from strawberryperl.com."

    # .NET Core SDK - needed by the managed PeerCC projects (Org.WebRtc.Callstats targets
    # netstandard2.0). VS2017's MSBuild alone is NOT enough; without a .NET Core SDK the build
    # fails with "The current .NET SDK does not support targeting .NET Standard 2.0."
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $sdks = (& dotnet --list-sdks 2>$null) | Out-String
        Require ($sdks -match "\d") "dotnet SDK present (for netstandard2.0 / PeerCC sample)" `
            "dotnet is on PATH but no SDKs are installed. Install .NET Core SDK 2.1+ from https://dotnet.microsoft.com/download so the PeerCC managed projects build."
    } else {
        Warn "dotnet SDK not found. The native Org.WebRtc library still builds, but the PeerCC sample (needed for the device call test) will FAIL on netstandard2.0. Install .NET Core SDK 2.1+ from https://dotnet.microsoft.com/download."
    }

    # Disk space - a full WebRTC tree + build output is large.
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
        Ok "Repo already present at $RepoRoot - skipping clone."
        Warn "If you want a clean tree, delete $RepoRoot first and re-run with -Clone."
        return
    }

    $parent = Split-Path -Parent $RepoRoot
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    Write-Host "  Cloning (recursive, this pulls ALL submodules - can take a long time)..." -ForegroundColor Gray
    Write-Host "  git clone --recursive $RepoUrl $RepoRoot" -ForegroundColor Gray
    & git clone --recursive $RepoUrl $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)." }
    Ok "Clone complete."
}

# Finds nuget.exe on PATH, else downloads it next to this script.
function Get-NuGet {
    $onPath = Get-Command nuget.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $local = Join-Path $PSScriptRoot "nuget.exe"
    if (Test-Path $local) { return $local }
    Write-Host "  nuget.exe not found - downloading to $local ..." -ForegroundColor Gray
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $local
        return $local
    } catch {
        Warn "Could not download nuget.exe ($($_.Exception.Message))."
        return $null
    }
}

# Restores the solution's NuGet packages. These projects use packages.config
# (e.g. Microsoft.Windows.CppWinRT), so 'msbuild /t:Restore' is NOT enough - use nuget.exe.
function Restore-Packages($sln) {
    Write-Section "Restore NuGet packages"
    $nuget = Get-NuGet
    if (-not $nuget) {
        Warn "Skipping restore - nuget.exe unavailable. Build may fail on missing CppWinRT package."
        return
    }
    Write-Host "  $nuget restore $sln" -ForegroundColor Gray
    & $nuget restore $sln
    if ($LASTEXITCODE -ne 0) { Warn "nuget restore returned $LASTEXITCODE (continuing; build will report if packages are still missing)." }
    else { Ok "Packages restored." }
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

    Restore-Packages $sln

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
        Write-Host "  WebRtcScheme.dll). For THIS test, deploy the PeerCC sample instead - see README.md." -ForegroundColor Gray
    } else {
        Warn "Build reported success but no Org.WebRtc.winmd/.dll was found for $Platform. Inspect $log."
    }
}

# --------------------------------------------------------------------------------------
# Copies the built ARM/x86/x64 artifacts into the app repo as libs\webrtc\<Platform>\.
function Invoke-Harvest {
    Write-Section "Harvest artifacts -> app repo ($Platform)"

    $buildOut = Join-Path $RepoRoot "webrtc\windows\solutions\Build\Output"
    $wanted = @(
        @{ Name = "Org.WebRtc.winmd"; Sub = "Org.WebRtc\$Configuration\$Platform" },
        @{ Name = "Org.WebRtc.dll";   Sub = "Org.WebRtc\$Configuration\$Platform" },
        @{ Name = "Org.WebRtc.pdb";   Sub = "Org.WebRtc\$Configuration\$Platform" },
        @{ Name = "WebRtcScheme.dll"; Sub = $null }
    )

    $dest = Join-Path $HarvestDir $Platform
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

    $copied = 0
    foreach ($w in $wanted) {
        $src = $null
        if ($w.Sub) {
            $candidate = Join-Path (Join-Path $buildOut $w.Sub) $w.Name
            if (Test-Path $candidate) { $src = $candidate }
        }
        # Fallback: search the whole build tree for the file under this platform.
        if (-not $src) {
            $src = Get-ChildItem -Path $RepoRoot -Recurse -Filter $w.Name -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -match [regex]::Escape("\$Platform\") } |
                   Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ($src) {
            Copy-Item $src $dest -Force
            Ok "$($w.Name) -> $dest"
            $copied++
        } else {
            if ($w.Name -eq "Org.WebRtc.pdb") { Warn "$($w.Name) not found (optional symbols; skipping)." }
            else { Bad "$($w.Name) not found for $Platform. Build it first with -Build."; }
        }
    }
    Write-Host ""
    if ($copied -gt 0) {
        Ok "Harvested $copied file(s) into $dest"
        Write-Host "  Reference Org.WebRtc.winmd from UniMatrix.csproj (conditioned on Platform=$Platform)" -ForegroundColor Gray
        Write-Host "  and register WebRtcScheme as an in-process server in Package.appxmanifest." -ForegroundColor Gray
    } else {
        Warn "Nothing harvested. Did the $Platform build succeed?"
    }
}

# --------------------------------------------------------------------------------------
try {
    Write-Host "webrtc-uwp build harness  (Platform=$Platform Configuration=$Configuration RepoRoot=$RepoRoot)" -ForegroundColor White

    $prereqOk = Invoke-PrereqCheck
    if ($CheckOnly) { return }

    # Harvest only copies already-built files, so it doesn't need the build prerequisites.
    if (-not $prereqOk -and ($Clone -or $Build)) {
        throw "Prerequisites not satisfied; aborting. Re-run with -CheckOnly after fixing them."
    }

    if ($Clone)   { Invoke-Clone }
    if ($Build)   { Invoke-Build }
    if ($Harvest) { Invoke-Harvest }

    Write-Section "Done"
    Ok "Next: build/deploy the PeerCC sample to the Lumia and run a test call (README.md)."
}
catch {
    Write-Host ""
    Bad $_.Exception.Message
    exit 1
}
