# Clink

A tiny Windows replay-buffer clipper for games. It keeps the last couple of minutes of your screen and audio in
memory; press a key, hear a click, and the clip is on disk. Share it to [Copium](https://copium.pics) with one
click and the link lands in your clipboard.

* Single `Clink.exe`, no installer, no background services. Needs the .NET 10 Desktop Runtime (Windows 10 2004+ / Windows 11; HDR and per-process audio need Windows 11).
* Captures with Windows Graphics Capture, so borderless **and** exclusive-fullscreen games work.
* **HDR done right**: on an HDR display it captures the FP16 scRGB frame and tone-maps it on the GPU using the
  display's SDR white level, so clips look like the game instead of grey and washed out.
* GPU-only pipeline: capture → compute-shader NV12 convert → NVENC/AMF/QuickSync H.264 (or HEVC). Frames never
  touch the CPU.
* Multi-track audio in every clip. Track 1 is always a **mix of game + mic + voice chat**, so Discord, browsers and
  every player that only plays the first track hear everything. The parts are also saved as their own tracks, and the
  source of each is picked in the window:
  1. **Mix** – game + mic + voice, always first
  2. **Game** – only the game's own audio (Windows per-process capture). Auto-detects the fullscreen app on the captured
     monitor, or pick a running app, or pick an audio device instead (a virtual input such as Wave Link's "Game", or the
     loopback of any output); falls back to "everything except your voice app" until a game is detected
  3. **Mic** – the default microphone or any other capture device
  4. **Voice chat** – Discord / TeamSpeak / Slack / Mumble / Zoom / Skype found automatically, any running app, or an audio
     device (off as a separate track by default; always part of the mix)
  5. **Desktop** (optional) – everything you hear from an output device, as an extra track
* Trim (start/end) without re-encoding, then save or save-and-share.
* Shows which monitor it is capturing; click another one on the little map to switch.
* Borderless dark UI, lives in the tray while you play.

## Installing & updating

1. Download `Clink.exe` from the [Releases](../../releases/latest) page. Put it anywhere (Desktop, `C:\Tools`, a
   USB stick) and run it. There is no installer; settings live in `%APPDATA%\Clink`, downloaded updates in
   `%LOCALAPPDATA%\Clink`.
2. It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). If it is missing,
   Windows shows a dialog with the download link on first run.
3. The exe is not code-signed (yet, see [ROADMAP.md](ROADMAP.md)), so SmartScreen may show "Windows protected your
   PC" the first time: *More info* → *Run anyway*. After that it does not ask again for that file.

**Updates are automatic.** Clink checks [GitHub Releases](../../releases) when it starts and every 6 hours after
that. When a newer version exists it downloads the new `Clink.exe` to `%LOCALAPPDATA%\Clink\updates`, checks it
against the `Clink.exe.sha256` published with the release (this catches a corrupted download; it does not prove who
built the file, which is what code signing is for, see [ROADMAP.md](ROADMAP.md)), and installs it the next time
Clink starts, so a recording session is never interrupted. The footer and the tray menu show **Update to vX.Y.Z** to
install it right away instead. Before installing, the file is hashed again against what was verified, and the exe is
swapped in by renaming with the previous `Clink.exe` kept as `Clink.exe.bak` until the new one has started, so a
failed install never leaves you without a working Clink. If an update cannot be installed (for example `Clink.exe`
lives in a folder you cannot write to, or Clink runs as administrator) Clink says so in the footer and a tray message
and leaves the download in `%LOCALAPPDATA%\Clink\updates` to install by hand. To turn auto-update off, set
`"AutoUpdate": false` in `settings.json` (see [Settings](#settings)); Clink then never contacts GitHub on its own, and
clicking the version in the footer checks by hand and opens the release page if there is something newer.

## Using it

1. Run `Clink.exe`. It starts recording the primary monitor right away.
2. Press **F9** (change it in the app) while gaming. You hear a short click; the clip is saved to
   `Videos\Clink\<game> <date>.mp4` and a small "Clip saved" toast appears.
3. Click **Share** (in the toast or the clip list). The clip is uploaded to Copium and the link is copied.
   By default the copied link is the raw video file, so pasting it in Discord plays the video inline with no
   embed card. If you turn on custom embeds on copium.pics, Copium's `link` becomes the share page instead; Clink
   just uses whatever Copium says to copy.
4. **Trim** opens an editor: the clip plays in a preview with a timeline underneath; drag the in/out handles (or
   press I / O at the playhead, Space to play, ←/→ to step a frame). Every audio track has its own include toggle
   and volume, which you hear in the preview and which are baked into the saved file; the Listen pill picks the
   track you audition. The in point snaps to a keyframe (every second by default) so the cut is exact in every
   player, and the export copies the video without re-encoding. Save, or Save & share.

Copium: make an upload token under *ShareX & API* on copium.pics (it starts with `eup_`) and paste it in the
Copium box. It is stored encrypted for your Windows user (DPAPI). Nothing is uploaded unless you press Share.

### Stream Deck

Two ways, no plugin needed:

* Add a **Hotkey** action that sends Clink's hotkey (F9 by default). Clink uses a low-level keyboard hook, so
  it sees injected keys even when a game has focus.
* Or use a **System → Open** action with `Clink.exe --clip`. If Clink is already running this just tells it to
  clip and exits. `Clink.exe --trim-last` opens the trim editor on the newest clip the same way.

Other commands: `--show`, `--minimized` (start in the tray), `--no-update` (start without installing a downloaded
update, handy for a debug build), `--selftest [seconds]` (records headless and writes a clip, prints what it did),
`--probe <file.mp4> [frame.png] [seconds]` (stream timing report, optional decoded frame),
`--trim <in> <out> <start> <end>`.

### Settings

`%APPDATA%\Clink\settings.json` (the UI writes it). The Audio tracks pickers store `AllDeviceId` / `MicDeviceId` (endpoint
ids; `null` = the default device, and a device that is unplugged stays selected until you pick another) and
`GameProcess` / `VoiceApp` (a process name, `.exe` optional, any case; `auto` = detect: the fullscreen app on the captured
monitor, or the first of Discord, TeamSpeak, Slack, Mumble, Zoom, Skype found running). Things not in the UI:

| key | default | what |
|-----|---------|------|
| `KeyframeSeconds` | 1 | keyframe interval; smaller = finer trim snapping, slightly bigger files |
| `HdrKnee` / `HdrPeak` | 0.75 / 4 | tone-mapper shoulder: everything below `knee` (relative to SDR white) is untouched, highlights roll off softly above it |
| `ClickVolume` | 0.5 | 0..1 |
| `ShowToast` | true | the "Clip saved" popup |
| `StartMinimized` | false | |
| `AutoUpdate` | true | check GitHub Releases on start and every 6 hours and download a newer `Clink.exe`, installed on the next start. `false` = never contact GitHub unprompted (the version in the footer still checks on click) |
| `UpdateRepo` | *(built in)* | GitHub `owner/repo` whose releases to install, overriding the repository baked into the exe. Only for your own fork: it is logged at start and shown in the version tooltip |

Logs: `%APPDATA%\Clink\clink.log`.

## Building

* .NET 10 SDK (`build.ps1` also finds a user-local SDK in `%USERPROFILE%\.dotnet`).
* `.\build.ps1` → debug build. `.\build.ps1 -Publish` → `dist\Clink.exe` (single file, needs the .NET 10 Desktop
  Runtime on the target machine; add `-p:SelfContained=true` to the publish for a runtime-free but ~70 MB exe).
* No native dependencies. Win32/COM is reached through CsWin32-generated bindings (`NativeMethods.txt`), Windows
  Graphics Capture through the built-in WinRT projection.

### Releasing

Releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)), not on a
dev machine, so the published exe always corresponds to a tag. Every push to `main` is also compiled by
[`build.yml`](.github/workflows/build.yml).

* **First time**: `.\tools\publish-repo.ps1` creates the GitHub repository (public by default; the updater reads
  releases anonymously, so it needs to be public), pushes `main` and prints what to do next. It needs Git and the
  GitHub CLI: `winget install --id Git.Git -e`, `winget install --id GitHub.cli -e`, then `gh auth login`.
* **Each release**: `.\tools\release.ps1 -Version 0.2.0 [-Message "what changed"]`. The script checks that the
  working tree is clean and the tag is new, sets `<Version>` in `Clink\Clink.csproj`, commits `Release v0.2.0`,
  creates the annotated tag `v0.2.0` and pushes both (`git push origin HEAD --follow-tags`). The workflow then runs
  `dotnet publish -p:Version=0.2.0 -p:UpdateRepo=<owner>/<repo>`, writes `Clink.exe.sha256` and creates the release
  "Clink v0.2.0" with generated notes and both files attached. Running copies of Clink see it within 6 hours.
* The asset names matter: the updater downloads the asset called exactly `Clink.exe` and checks it against the first
  64-hex token of `Clink.exe.sha256`; a release without the `.sha256` file is refused. The exe must also report the
  version of its tag (the workflow guarantees this with `-p:Version`), or the updater throws it away. `UpdateRepo`
  tells the exe which repository's releases to watch, so only exes built by the workflow (or a local publish with
  the same `-p:UpdateRepo=...`) download or install updates; a plain debug build ignores the shared updates folder.
* A version with a `-` suffix (`0.2.0-beta.1`) is published as a pre-release, which the updater ignores; a build of
  that pre-release does pick up the final `0.2.0` when it ships. To redo a failed release, delete the tag
  (`git tag -d v0.2.0; git push origin :refs/tags/v0.2.0`) and run the script again. Running the workflow by hand
  refuses a version that is already tagged.
* The workflow's actions are pinned to commit SHAs and `.github/dependabot.yml` opens PRs to bump them: whatever code
  runs in that job can publish a release, so a floating tag would be a way in.

### How it works (short)

```
monitor ──WGC (BGRA8 or FP16 scRGB)──► latest frame texture
                                            │ 60 Hz pacer
                                   compute shader: scale, tone-map, BT.709 → NV12
                                            │
                                   hardware encoder MFT (async) ──► ring buffer of compressed frames (RAM)
WASAPI loopback / mic / process loopback ──► per-track timeline (silence-filled) ──► AAC ──► ring of frames
                                            │ hotkey
                                   snapshot both rings from the last keyframe ──► own MP4 muxer (fast-start)
```

Timestamps everywhere are QPC-based 100 ns units, which is what WGC and WASAPI both report, so video and the
audio tracks line up without guesswork. Clips are muxed by Clink's own small ISO-BMFF writer (one video track,
N AAC tracks with names, edit lists for sub-frame alignment).

See [ROADMAP.md](ROADMAP.md) for what's next.
