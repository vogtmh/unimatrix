# WebRTC calls — feasibility test (Lumia 930 / Windows 10 Mobile, ARM32)

This folder is a **standalone experiment**, separate from the UniMatrix app. The goal is to
answer one question before we write any call code:

> Can we build WebRTC for ARM32 Windows 10 Mobile and make a real call on the Lumia 930?

If the bundled **PeerCC** sample can place a call on the device, integration into UniMatrix is
worth doing. If the SDK can't be built/deployed, we know now and avoid wasted work.

> Run everything here on the **Windows build machine (VS2017)**. None of this builds on macOS.

---

## Background: what actually needs to happen

WebRTC is two separate concerns:

1. **Media** — capturing mic/camera, encoding, ICE/STUN/TURN, SRTP transport. This is the hard
   native part we need a prebuilt/compiled library for.
2. **Signaling** — exchanging the offer/answer SDP and ICE candidates. This is *transport-agnostic*.

For UniMatrix, signaling will ride over **Matrix** using the standard VoIP events
(`m.call.invite`, `m.call.candidates`, `m.call.answer`, `m.call.hangup`). The app already parses
`m.call.*` events into timeline markers, so we have a starting point. **This test does not touch
Matrix** — PeerCC uses its own simple signaling server, which is enough to prove the media stack
works on the device.

## Which SDK and why

| Option | Targets | Verdict |
|---|---|---|
| **webrtc-uwp-sdk** (M71) | UWP incl. **ARM32 + Win10 Mobile** | The one we must use. Deprecated (~2018), expect bitrot. |
| microsoft/winrtc | Modern Windows, Win32, UWP **ARM64** | Can't produce ARM32/Win10 Mobile binaries. Not usable here. |

We deliberately accept the deprecated SDK because the **device itself is from that era** — the M71
toolchain matches Windows 10 Mobile far better than anything current.

---

## Prerequisites (install these first)

- **Visual Studio 2017** (15.9.x), Community is fine. **Not** VS2019/2022 — the sources are pinned
  to the v141 toolset.
  - Workloads: *Universal Windows Platform development* and *Desktop development with C++*.
- **Windows SDK 10.0.17134** *and* **10.0.17763** — both, from the
  [Windows SDK archive](https://developer.microsoft.com/en-us/windows/downloads/sdk-archive).
  - When installing each, tick **Debugging Tools for Windows** (the prepare scripts need it; the
    SDK that ships inside VS does **not** include it).
- **C++/WinRT** VS2017 extension (from the VS Marketplace).
- **Python 2.7.15**, with `C:\Python27` **first** on `PATH`, then `pip install pywin32`.
- **Strawberry Perl** (from strawberryperl.com) — needed by the BoringSSL build.
- **Git for Windows**.
- ~**40 GB** free disk, and clone **close to the drive root** (path length / MAX_PATH).

Run the checker to confirm your machine is ready (makes no changes):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-webrtc-uwp.ps1 -CheckOnly
```

---

## Step 1 — clone + build the SDK (ARM/Release)

```powershell
# Clone close to root (short path!) and build Org.WebRtc + samples for ARM.
powershell -ExecutionPolicy Bypass -File .\build-webrtc-uwp.ps1 -Clone -Build -Platform ARM -Configuration Release
```

What to expect:
- The **first** build runs the native WebRTC prepare/compile (gn + ninja via the SDK's bundled
  depot_tools). This is the long part — **1–2 hours** is normal — and the most likely thing to
  fail. A full MSBuild log is written next to the script (`build-ARM-Release.log`).
- On success the script prints the produced `Org.WebRtc.winmd` / `Org.WebRtc.dll` paths.

If you prefer the GUI for the first run (often easier to read errors):
1. Open `C:\webrtc-uwp\webrtc\windows\solutions\WebRtc.Universal.sln` in VS2017.
2. *Tools → Options → Projects and Solutions → uncheck "Allow parallel project initialization"*
   (only needed on VS older than 15.9.7).
3. Set **PeerConnectionClient.WebRtc** as the startup project.
4. Set configuration to **Release / ARM**.
5. Build.

## Step 2 — deploy PeerCC to the Lumia and make a test call

1. In `WebRtc.Universal.sln`, set **PeerConnectionClient.WebRtc** as startup, **Release / ARM**.
2. Connect the Lumia 930 (Device target) and **Deploy/F5**.
3. Run the matching PeerCC peer on a second machine (or the x64 build on the PC), point both at
   the same signaling server (the sample defaults are in its settings UI), and connect.
4. **Success criteria:** the two peers connect and you get live audio (and video if the camera
   negotiates). That's the green light.

> Tip: test **audio-only first**. The Lumia 930 camera capture module is the flakiest part of this
> old stack; a working audio call already proves the media pipeline.

---

## Known failure modes (and what they mean)

- **`Windows SDK debug tools are missing!`** (run.py aborts in ~2 s, MSB3073 / exit code 4) → the
  **Debugging Tools for Windows** SDK feature isn't installed. Fix: *Settings → Apps → "Windows
  Software Development Kit" → Modify → check "Debugging Tools for Windows"* — for **both** the
  10.0.17134 and 10.0.17763 SDKs. This is separate from the base SDK and is the most common first
  failure.
- **`The current .NET SDK does not support targeting .NET Standard 2.0`**
  (`Org.WebRtc.Callstats.csproj`) → that project is optional telemetry, not needed for a call test.
  Either install a modern .NET SDK, or unload/skip `Org.WebRtc.Callstats` and build only the native
  `Org.WebRtc` + `PeerConnectionClient.WebRtc` projects.
- **`MAX_PATH` / "file name too long"** → repo path too deep. Re-clone to `C:\webrtc-uwp`.
- **gn/ninja or gclient errors during the first build** → almost always Python (must be 2.7.x first
  on PATH) or a missing Windows SDK 17134/17763.
- **`v141` toolset / SDK not found** → VS2017 or one of the two Windows SDKs isn't installed.
- **BoringSSL build errors** → Strawberry Perl missing from PATH.
- **googlesource/depot_tools download failures** → network/proxy, or the deprecated pinned URLs
  have rotted. Retry; if persistent, this is the risk we're testing for.

## If it works — integration sketch (next phase, not now)

1. Add `Org.WebRtc` (ARM winmd/dll) + `WebRtcScheme.dll` as references in `UniMatrix.csproj`, and
   register `WebRtcScheme` in `Package.appxmanifest` (in-process server extension).
2. Add `microphone` (and `webcam`) capabilities to the manifest.
3. Build a `CallService` that:
   - creates an `RTCPeerConnection`,
   - sends/receives SDP + ICE as Matrix `m.call.*` events (we already parse these),
   - drives a minimal in-call UI.
4. Start with **1:1 audio**; add video, then group calls later.

Nothing in this folder is wired into the app yet — it's purely the build/feasibility probe.
