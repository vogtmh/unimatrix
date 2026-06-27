# External dependencies (build these first)

This folder holds the **native dependencies that must be compiled on the Windows build
machine before (or alongside) the UniMatrix app**. They are *not* part of the app's normal
C#/XAML build — they produce native binaries that the app then consumes app-local.

Everything here targets **Windows 10 Mobile / UWP ARM32** (the Lumia 930). None of it builds
on macOS; the agent can edit these scripts but you run them on the **VS2017 Windows box**.

| Folder | Produces | Consumed by the app as | Required? |
|---|---|---|---|
| [`olm-build/`](olm-build/) | `olm.dll` (libolm, Matrix E2EE) | `<Content>` + P/Invoke (`Services/OlmNative.cs`) | Yes, for end-to-end encryption (`CRYPTO` define) |
| [`webrtc-test/`](webrtc-test/) | `Org.WebRtc.winmd` + `.dll`, `WebRtcScheme.dll` | WinRT reference (`WEBRTC` define) | Only for voice/video calls |

Each subfolder has its own README/script header with the full runbook, prerequisites and
known failure modes. This page is just the map.

## How it fits together

1. **Build** the native library on the Windows machine using the folder's PowerShell script.
2. **Harvest** the output into the app project under `UniMatrix\UniMatrix\libs\<dep>\<arch>\`.
   Each script does this for you with its `-Harvest` switch (destination resolved relative to
   the script — no need to pass paths for the default ARM layout).
3. **Build the app** Release|ARM. The `.csproj` references the harvested binaries per
   architecture, so they get packaged app-local automatically.

## olm-build — libolm (end-to-end encryption)

libolm is pure C/C++11 with all crypto vendored, so it cross-compiles cleanly for the UWP
appcontainer. It is a **plain native C DLL** (not a WinRT component), bound via `DllImport`.

```powershell
cd olm-build
# One-shot: clone the libolm source, build ARM/Release, harvest olm.dll into the app.
.\build-olm-uwp.ps1 -Clone -Build -Harvest
```

Output lands in `UniMatrix\UniMatrix\libs\olm\ARM\olm.dll`. The app's `CRYPTO` build define
expects it there, and the csproj packages it at the **package root** (`<Link>olm.dll</Link>`) so
the appcontainer loader can resolve the `DllImport("olm.dll")` — a DLL left in a subfolder fails
with `ERROR_MOD_NOT_FOUND` (126).

The UWP toolchain forbids static-CRT DLLs (MSB8024), so `olm.dll` links the appcontainer VC++
runtime (`MSVCP140_APP.dll` / `VCRUNTIME140_APP.dll`) dynamically. These come from the
`Microsoft.VCLibs.140.00` framework package, declared as a `<PackageDependency>` in
`Package.appxmanifest`. On most devices (and via Device Portal install) VCLibs is already present,
so nothing extra is needed. If you must deploy to a device that lacks it, `-HarvestCrt` (optionally
`-CrtSource <folder-or-appx>`) copies those two DLLs app-local — but that is rarely necessary.

## webrtc-test — WebRTC (calls, experimental)

A standalone feasibility harness for the deprecated **webrtc-uwp-sdk (M71)** — the only WebRTC
SDK that ever targeted ARM32 / Windows 10 Mobile. It first proves a call works on the device
via the bundled PeerCC sample, then harvests the media libraries for the app's `WEBRTC` define.

```powershell
cd webrtc-test
.\build-webrtc-uwp.ps1 -CheckOnly      # verify prerequisites first
.\build-webrtc-uwp.ps1 -Clone -Build   # clone + build (long; expect bitrot)
.\build-webrtc-uwp.ps1 -Harvest -Platform ARM
```

Output lands in `UniMatrix\UniMatrix\libs\webrtc\ARM\`. See
[`webrtc-test/README.md`](webrtc-test/README.md) for the detailed prerequisites (VS2017,
specific Windows SDKs, Python 2.7, Strawberry Perl, .NET Core SDK) and failure modes.

## Notes

- Run scripts from **inside** their own folder (the examples above `cd` first) — harvest paths
  are resolved relative to each script.
- Only **ARM** is harvested so far. To ship x86/x64 too, build those configs and add matching
  `<Content>` / reference groups in `UniMatrix.csproj`.
- These builds are slow and toolchain-sensitive; once the binaries are harvested you rarely
  need to rebuild them — only when updating the dependency version.
