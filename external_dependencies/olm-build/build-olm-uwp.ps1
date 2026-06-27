<#
.SYNOPSIS
    Builds libolm as a native shared library (olm.dll) for UWP / Windows 10 Mobile (ARM32),
    then harvests it into the UniMatrix project so it can be packaged app-local and called
    via P/Invoke (Services/OlmNative.cs).

.DESCRIPTION
    libolm is pure C/C++11 with all dependencies vendored (curve25519-donna, the
    crypto-algorithms AES/SHA implementations). It performs no OS calls, does no heap
    allocation (the caller supplies every buffer) and takes randomness from the caller, so
    it ports cleanly to the UWP appcontainer. We build it with CMake targeting the
    WindowsStore (UWP) toolchain for the ARM (ARMv7) architecture as a SHARED library, which
    yields olm.dll. Unlike the WebRTC harvest this is a plain native C DLL (NOT a WinRT
    component / WinMD), so in the csproj it is referenced as a <Content> copy and bound with
    DllImport — there is no winmd and no <Implementation> element.

    Run this on the Windows VS2017 build box (the same machine used for the Release|ARM app
    build). The agent cannot build or harvest the DLL on macOS.

    Prerequisites (same toolchain as the WebRTC build):
      - Visual Studio 2017 (15.9.x) with the "Universal Windows Platform development" and
        "Desktop development with C++" workloads (v141 toolset, ARM build tools).
      - CMake 3.15+ on PATH (the VS2017-bundled CMake under
        "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin" works).
      - git on PATH.

.PARAMETER OlmTag
    The libolm git tag to build. Defaults to 3.2.16 (the final libolm release).

.PARAMETER Platform
    Target architecture passed to CMake's -A generator flag: ARM (default), Win32, x64.
    ARM is the Lumia 930 target; Win32/x64 are only needed if those app archs are shipped.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Clone
    Clone (or update) the libolm source under .\src before building.

.PARAMETER Build
    Configure + build olm.dll.

.PARAMETER Harvest
    Copy the built olm.dll into the UniMatrix project's libs\olm\<Platform>\ folder.

.PARAMETER SharedCrt
    Link the VC++ runtime dynamically (/MD), which makes olm.dll depend on the
    Microsoft.VCLibs.140.00 framework package (MSVCP140_APP.dll / VCRUNTIME140_APP.dll) being
    deployed on the device. By default this is OFF and the CRT is linked STATICALLY (/MT) so
    olm.dll is self-contained and loads in the appcontainer without any framework package. Use
    this only if you specifically want the dynamic-CRT build.

.PARAMETER HarvestDir
    Override the harvest destination root. Defaults to the project libs\olm folder resolved
    relative to this script (..\..\UniMatrix\UniMatrix\libs\olm).

.EXAMPLE
    # One-shot: clone, build ARM/Release, harvest into the project.
    .\build-olm-uwp.ps1 -Clone -Build -Harvest

.EXAMPLE
    # Rebuild after a source update and re-harvest.
    .\build-olm-uwp.ps1 -Build -Harvest -Platform ARM -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$OlmTag = "3.2.16",
    [ValidateSet("ARM", "Win32", "x64")]
    [string]$Platform = "ARM",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$Clone,
    [switch]$Build,
    [switch]$Harvest,
    [switch]$SharedCrt,
    [string]$HarvestDir
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$srcDir = Join-Path $root "src"
$buildDir = Join-Path $root "build\$Platform-$Configuration"
$olmUrl = "https://gitlab.matrix.org/matrix-org/olm.git"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not ($Clone -or $Build -or $Harvest)) {
    Write-Host "Nothing to do. Pass -Clone and/or -Build and/or -Harvest." -ForegroundColor Yellow
    Write-Host "Typical first run:  .\build-olm-uwp.ps1 -Clone -Build -Harvest"
    return
}

if ($Clone) {
    Write-Step "Cloning libolm $OlmTag"
    if (Test-Path $srcDir) {
        Write-Host "Source already present at $srcDir; fetching tags."
        Push-Location $srcDir
        git fetch --tags --depth 1 origin $OlmTag
        git checkout $OlmTag
        Pop-Location
    }
    else {
        git clone --depth 1 --branch $OlmTag $olmUrl $srcDir
    }
}

if ($Build) {
    if (-not (Test-Path $srcDir)) {
        throw "Source not found at $srcDir. Run with -Clone first."
    }
    Write-Step "Configuring CMake ($Platform / $Configuration, UWP shared)"
    # -DCMAKE_SYSTEM_NAME=WindowsStore + version 10.0 selects the UWP (appcontainer) toolchain.
    # -A <arch> picks the target architecture for the VS generator.
    # BUILD_SHARED_LIBS=ON yields olm.dll (+ olm.lib import lib). Tests/fuzzers are off — they
    # are desktop console programs that won't link under the WindowsStore toolchain.
    # CMAKE_POLICY_VERSION_MINIMUM=3.5: libolm's CMakeLists declares cmake_minimum_required at a
    # version below 3.5, and CMake 3.31+ removed that compatibility. This flag lets the old
    # project configure under a modern CMake without editing the vendored source.
    # /sdl-: the WindowsStore (UWP) toolchain turns on /sdl by default, which PROMOTES warning
    # C4146 ("unary minus on unsigned type") to a hard error. libolm's vendored ed25519 code
    # (lib/ed25519/src/fe.c) does this deliberately, so we disable /sdl for the build. /wd4146
    # additionally silences the (harmless) warning. These go in *_FLAGS so they land in the
    # vcxproj AdditionalOptions after the toolchain's /sdl and therefore override it.
    $relaxFlags = "/sdl- /wd4146"
    $cmakeArgs = @(
        "-S", $srcDir,
        "-B", $buildDir,
        "-G", "Visual Studio 15 2017",
        "-A", $Platform,
        "-DCMAKE_SYSTEM_NAME=WindowsStore",
        "-DCMAKE_SYSTEM_VERSION=10.0",
        "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
        "-DCMAKE_C_FLAGS=$relaxFlags",
        "-DCMAKE_CXX_FLAGS=$relaxFlags",
        "-DBUILD_SHARED_LIBS=ON",
        "-DOLM_TESTS=OFF"
    )

    # CRT linkage. By default link the CRT STATICALLY (/MT) so olm.dll embeds the C/C++
    # runtime and carries NO dependency on the Microsoft.VCLibs.140.00 framework package
    # (MSVCP140_APP.dll / VCRUNTIME140_APP.dll). On Windows 10 Mobile that framework package is
    # awkward to deploy, and a missing one makes olm.dll fail to load in the appcontainer with
    # ERROR_MOD_NOT_FOUND (126) -> surfaced as "Unresolved P/Invoke". libolm is pure
    # computation (no OS calls), so the static-CRT functions it pulls in are appcontainer-safe.
    # CMAKE_POLICY_DEFAULT_CMP0091=NEW activates the CMAKE_MSVC_RUNTIME_LIBRARY abstraction.
    if ($SharedCrt) {
        Write-Host "CRT: dynamic (/MD) -> requires Microsoft.VCLibs.140.00 on the device." -ForegroundColor Yellow
        $cmakeArgs += "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW"
        $cmakeArgs += "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL"
    }
    else {
        Write-Host "CRT: static (/MT) -> self-contained olm.dll, no VCLibs dependency." -ForegroundColor Green
        $cmakeArgs += "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW"
        $cmakeArgs += "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded"
    }

    cmake @cmakeArgs

    Write-Step "Building"
    cmake --build $buildDir --config $Configuration

    $dll = Join-Path $buildDir "$Configuration\olm.dll"
    if (-not (Test-Path $dll)) {
        # Some generators drop the artifact directly in the build dir.
        $dll = Join-Path $buildDir "olm.dll"
    }
    if (Test-Path $dll) {
        Write-Host "Built: $dll" -ForegroundColor Green
        Write-Host "       (ensure this is the $Platform build before harvesting)" -ForegroundColor DarkGray
    }
    else {
        throw "Build finished but olm.dll was not found under $buildDir."
    }
}

if ($Harvest) {
    if (-not $HarvestDir) {
        $HarvestDir = Join-Path $root "..\..\UniMatrix\UniMatrix\libs\olm"
    }
    $destDir = Join-Path $HarvestDir $Platform
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    $dll = Join-Path $buildDir "$Configuration\olm.dll"
    if (-not (Test-Path $dll)) { $dll = Join-Path $buildDir "olm.dll" }
    if (-not (Test-Path $dll)) {
        throw "olm.dll not found under $buildDir. Run with -Build first."
    }
    Copy-Item $dll (Join-Path $destDir "olm.dll") -Force
    Write-Step "Harvested olm.dll -> $destDir"
    Write-Host "Now ensure UniMatrix.csproj has the matching <Content> item for libs\olm\$Platform\olm.dll." -ForegroundColor Yellow
}

Write-Host "Done." -ForegroundColor Green
