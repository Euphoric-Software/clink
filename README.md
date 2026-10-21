# Clink

A tiny Windows replay-buffer clipper for games. It keeps the last couple of minutes of your screen and audio in
memory; press a key, hear a click, and the clip is on disk. Share it to [Copium](https://copium.pics) with one
click and the link lands in your clipboard.

![Clink's window: the Capture tab with the monitor map, codec, size target and audio tracks on the left, the clip list with Trim / Share buttons on the right](docs/screenshots/main-window.png)
<sub>Recording a game: capture settings and audio tracks on the left, your clips on the right. More under
[Screenshots](#screenshots).</sub>

* Single `Clink.exe`, no installer, no background services. Needs the .NET 10 Desktop Runtime (Windows 10 2004+ / Windows 11; HDR and per-process audio need Windows 11).
* Captures with Windows Graphics Capture, so borderless **and** exclusive-fullscreen games work.
* **HDR done right**: on an HDR display it captures the FP16 scRGB frame and tone-maps it on the GPU using the
  display's SDR white level, so clips look like the game instead of grey and washed out.
* GPU-only pipeline: capture → compute-shader NV12 convert → NVENC/AMF/QuickSync H.264 (or HEVC, or AV1 where the
  GPU can). Frames never touch the CPU.
* Multi-track audio in every clip. Track 1 is always a **mix of game + mic + voice chat**, so Discord, browsers and
  every player that only plays the first track hear everything (it is also the only track marked as enabled, so
  QuickTime and iPhones do not play all of them at once). The parts are also saved as their own tracks, and the
  source of each is picked in the window:
  1. **Mix** – game + mic + voice, always first
  2. **Game** – only the game's own audio (Windows per-process capture). Auto-detects the fullscreen app on the captured
     monitor (or the game-library / custom app Clink detected, windowed or not), or pick a running app, or pick an audio device instead (a virtual input such as Wave Link's "Game", or the
     loopback of any output); falls back to "everything except your voice app" until a game is detected
  3. **Mic** – the default microphone (it follows Windows when you change the default device) or any other capture
     device
  4. **Voice chat** – Discord / TeamSpeak / Slack / Mumble / Zoom / Skype found automatically, any running app, or an audio
     device (off as a separate track by default; always part of the mix)
  5. **Desktop** (optional) – everything you hear from an output device (the default one follows Windows too), or an
     audio input (a Wave Link / VoiceMeeter mix, a virtual cable), as an extra track
* Trim (start/end) without re-encoding, then save or save-and-share.
* **Records only while a game runs** (default) or always. Games are detected by a fullscreen/borderless window on the
  captured monitor, by living in a known game-library folder (Steam, Epic, Riot, Battle.net, Ubisoft, EA, GOG, Xbox,
  Rockstar), or by being on your custom-apps list; while nothing runs the capture, encoder and RAM buffer are stopped.
  Maximized apps and overlays (Game Bar, the NVIDIA overlay, PowerToys, widgets) do not count as games; borderless
  games that hide their frame (SDL3 games such as osu!lazer) do.
* **A list of hotkeys**: each one is a key combo plus what it does (clip, pause/resume, share last clip) and, for clips,
  how much to save (e.g. F9 = the replay length, Shift+F9 = the last 30 s). While the game is in front a hotkey also
  fires with extra Ctrl / Alt / Shift held (crouch + F9 still clips); on the desktop only the exact combo does, so
  Ctrl+F9 in an IDE still reaches the IDE.
* **Never a silent miss**: if a clip cannot be saved (recording paused, still waiting for a game, the recorder failed,
  disk full) or a share cannot be uploaded, Clink says why where you are looking: a toast over the game, a tray balloon
  otherwise. With nothing recording you hear the Windows error sound instead of the click.
* **Recovers by itself**: a recorder that fails to start (encoder busy, monitor still waking up) is retried in the
  background, and after a graphics driver crash (TDR) Clink notices, says *The GPU was reset* and restarts capture on a
  fresh device. You are told once, not on every retry.
* **Replay length slider** from 5 s to 60 min with one readout under it: the **size** per minute and per clip of that
  length and the **RAM** the buffer costs, live; and **size targets** (Copium 200 MB / 10 min, the default; Discord
  10 / 50 / 500 MB) that lower the bitrate, only when needed, so a clip of that length fits.
* H.264 or HEVC, and AV1 (experimental) when the GPU has an AV1 encoder; codecs the GPU lacks are listed greyed out.
  H.264 stops at 4096 px, so a wider recording (a 5120×1440 super-ultrawide at *Native*) is made in HEVC, and the
  Capture tab says so.
* Toasts for *clip saved*, *shared* and *recording started*, each with its own switch, duration and a preview; eight
  positions on the captured monitor. Away from the window you also get short notices for *Uploading to Copium…*
  (share hotkey), *Recording paused / resumed* (pause hotkey) and anything that went wrong. Optional always-on **status dot** in a corner (green recording, amber waiting, red
  error, grey paused). Toasts and the dot are hidden from screen capture, so they never end up in a clip.
* Optional **archive**: clips land in the local folder first and move to a NAS share, mapped drive or any other folder
  in the background (slowly, oldest first, after N days and/or when the local folder is over N GB).
* Shows which monitor it is capturing; click another one on the little map to switch.
* Resizable borderless dark UI: settings tabs (Capture · Clip · Alerts) with the clips pane beside them when the
  window is wide, as a fourth tab when it is narrow. Lives in the tray while you play.

## Screenshots

<table>
<tr>
<td width="50%"><a href="docs/screenshots/capture-audio.png"><img src="docs/screenshots/capture-audio.png" width="400" alt="Capture tab scrolled to the audio tracks and the recording mode"></a><br><sub>Audio tracks: a source per track, and what each one is capturing right now. Below, the recording mode and custom apps.</sub></td>
<td width="50%"><a href="docs/screenshots/replay-length.png"><img src="docs/screenshots/replay-length.png" width="400" alt="Replay length slider with the size and RAM readout, and the hotkey list"></a><br><sub>Replay length with the size / RAM readout and what the size target did, and the hotkey list.</sub></td>
</tr>
<tr>
<td><a href="docs/screenshots/clip-tab.png"><img src="docs/screenshots/clip-tab.png" width="400" alt="Clip tab with the Copium token and the archive settings, and the Cloud list of shared clips"></a><br><sub>Clip tab: Copium token and the NAS archive. Right: everything you shared, with its link.</sub></td>
<td><a href="docs/screenshots/alerts-tab.png"><img src="docs/screenshots/alerts-tab.png" width="400" alt="Alerts tab with toast switches, the status dot and updates"></a><br><sub>Alerts tab: toasts, the status dot, updates and run at startup.</sub></td>
</tr>
<tr>
<td><a href="docs/screenshots/trim-editor.png"><img src="docs/screenshots/trim-editor.png" width="400" alt="Trim editor with preview, timeline and per-track volume"></a><br><sub>Trim editor: in / out on the timeline, per-track include and volume, Save or Save &amp; share.</sub></td>
<td><a href="docs/screenshots/toast.png"><img src="docs/screenshots/toast.png" width="400" alt="Clip saved and Shared toasts"></a><br><sub>Toasts: <i>Clip saved</i> with Share / Trim / Folder, and <i>Shared · link copied</i>.</sub></td>
</tr>
<tr>
<td><a href="docs/screenshots/narrow.png"><img src="docs/screenshots/narrow.png" width="240" alt="The window at its narrow size, with Clips as a fourth tab"></a><br><sub>Narrow window: the clip list becomes a fourth tab.</sub></td>
<td></td>
</tr>
</table>

All screenshots are of Clink itself, rendered from synthetic clips (no real games or accounts).

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
failed install never leaves you without a working Clink: a new exe that does not start is swapped back, and that
download is not tried again on every start. If an update cannot be installed (for example `Clink.exe`
lives in a folder you cannot write to, or Clink runs as administrator) Clink says so in the footer, in a tray message
and under *Alerts → Updates*, and leaves the download in `%LOCALAPPDATA%\Clink\updates` to install by hand. *Alerts →
Updates* also shows *Last check failed: …* when GitHub could not be reached (it gives up after 30 s) and *Update
problem: …* when a download failed. To turn auto-update off, untick *Install updates automatically* under *Alerts →
Updates*; this takes effect at once (no restart), Clink then never contacts GitHub on its own, and *Check now* (or
clicking the version in the footer) checks by hand and opens the release page if there is something newer.

## Using it

1. Run `Clink.exe`. The header says *Waiting for a game…* until a game or custom app starts, then *Recording · Overwatch*
   (switch to *Always* under *Capture → Recording* to record whatever is on the monitor, as before).
2. Press **F9** (or any hotkey from your list under *Capture → Hotkeys*) while gaming. You hear a short click; the clip is
   saved to `Videos\Clink\<game> <date>.mp4` and a small "Clip saved" toast appears. If nothing was recording you hear
   the Windows error sound instead and a *Clip not saved* notice says why (paused, waiting for a game: add it under
   *Custom apps*, or the recorder's error); a save that fails (disk full) says so the same way. The file is written
   under a temporary name and renamed when complete, so a failed save never leaves a broken clip behind.
3. Click **Share** (in the toast or the clip list). The clip is uploaded to Copium and the link is copied.
   By default the copied link is the raw video file, so pasting it in Discord plays the video inline with no
   embed card. If you turn on custom embeds on copium.pics, Copium's `link` becomes the share page instead; Clink
   just uses whatever Copium says to copy. A clip is uploaded once: pressing Share again (or the share hotkey) while
   it uploads waits for that upload. When an upload fails the toast and the clip row say why (*Add your Copium token
   first*, *Copium is having trouble (HTTP 502), try again later*, *Can't reach Copium - check your connection*, …).
4. **Trim** opens an editor: the clip plays in a preview with a timeline underneath; drag the in/out handles (or
   press I / O at the playhead, Space to play, ←/→ to step a frame). Every audio track has its own include toggle
   and volume, which you hear in the preview and which are baked into the saved file; the Listen pill picks the
   track you audition. The in point snaps to a keyframe (every second by default) so the cut is exact in every
   player, and the export copies the video without re-encoding. *Save trimmed* writes `<clip> (trim).mp4` next to the
   original (tick *Replace original* to replace the clip with the cut instead), or *Save & share*.

Copium: make an upload token under *ShareX & API* on copium.pics (it starts with `eup_`) and paste it in the
Copium box. It is stored encrypted for your Windows user (DPAPI). Nothing is uploaded unless you press Share. Without
Copium+ an upload may be at most **200 MB and 10 minutes** long: Share says so instead of uploading a clip that is over
either limit (trim it, or tick *I have Copium+* under *Clip → Copium* if you have it), and the *Copium (200 MB, 10 min)*
size target, the default, keeps clips of the replay length (or of 10 minutes, when the replay length is longer) under the
size limit. Sizes are shown in decimal MB everywhere, the unit the limits use.

### The window

* **Capture**, top to bottom: **Capturing** - the monitor map, resolution / FPS / bitrate / codec with a one-line
  description of each codec (the ones your GPU cannot encode are greyed out; the 1440p / 1080p / 720p presets name the
  short side, so a portrait 1080×1920 monitor at 1080p records 1080×1920), the **size target** (*None*, *Discord 10 MB*,
  *Nitro Basic 50 MB*, *Copium 200 MB, 10 min* (the default), *Nitro 500 MB*: when a clip of the replay length would not
  fit, the encode bitrate is lowered just enough, audio tracks included; it is never raised above the bitrate you chose)
  and *Capture cursor*; **Audio tracks** - the per-track rows with their source pickers (a *Set up your audio* banner sits
  above them until you rest the pointer on the group for a moment or change an option; it goes when the pointer leaves
  the group, so nothing jumps under it, and does not come back) and under them what each track is capturing right now or
  why it is not (*● Mic (USB Microphone)*, *○ Voice (waiting for app)*); **Recording** - the recording
  mode and the custom-apps list (type an exe name or pick one of the apps running right now); **Replay length** - the slider
  (the 30–90 s stretch is the sweet spot) with the one size/RAM readout right under it, recomputed on every resolution, FPS,
  bitrate, codec, size-target, audio-track or length change; the figures are bitrate × audio tracks × length (resolution and
  codec decide how good a clip looks at that bitrate, not how big it is): *≈ 124 MB per minute · ≈ 52 MB per 25 s clip*
  (16 Mbps, three tracks; at 1 h the clip figure becomes the per-hour figure), *≈ 62 MB of RAM for 25 s at 16 Mbps* (red
  above half of your RAM; the slider refuses lengths that could not be allocated at all) and what the size target did, in
  yellow when the replay length is Copium's 10 minutes or more - the Copium target then fits a 10-minute clip, not the whole
  replay, since a longer clip has to be trimmed before Share anyway; **Hotkeys** - the list (click the keys box, press the
  combo; Esc cancels) with *Clip now* in its header and the click sound under it. A combo another row already has, or
  a bare letter, digit or editing key (it would be eaten in every program), is refused with a hint and the box keeps
  listening; F-keys, ScrollLock and Pause work alone.
* **Clip**: **Copium** - the upload token and the *I have Copium+* switch - and **Storage**: the local clips folder and the
  archive (below).
* **Alerts**: **Toasts** - the position and the three toast switches with their durations and a *Preview* button each;
  **Status dot** - the dot and its corner; **Updates** - auto-update, *Check now* and *Run at startup*.
* **Clips** (right pane when the window is at least 900 px wide, otherwise the last tab): **Local** lists every file
  with small icons (cloud = shared, scissors = trimmed, PC = local only, drive = archived) and Trim / Share / folder
  buttons; **Cloud** lists everything you shared with the link (click = copy), *Open page* and the local file if it
  still exists, and keeps entries whose local file is gone; **Trimmed** lists the cuts made by the trim editor.
* The footer shows the game, encoder, buffer and dropped frames, or the latest notice (hover it for the full text). The
  mouse wheel scrolls the page even over a closed dropdown, so scrolling never changes a setting by accident.

### Archive (NAS, second drive)

Clips are always written to the local clips folder first. Under *Clip → Storage* you can turn on an archive location:
a UNC share (`\\nas\clips`), a mapped drive or any folder. Sign in to a share once in Explorer; Clink never asks for
credentials, and on *access denied* the status line tells you to do exactly that. *Test* checks that it is reachable and
writable and shows the free space. The rules are *older than N days* and/or *while the local folder is over N GB*,
whichever comes first (0 = off). A background thread at the lowest priority then copies eligible clips oldest-first in
small chunks with pauses in between (it never competes with the game), verifies the copy, deletes the local file and
points the library entry at the archive, so Trim, Share and thumbnails keep working from there; a clip that is open
(uploading, trimming, playing) is skipped, an unreachable archive is retried on the next run (1 min after a clip is saved
and every 5 min), and a local file is never deleted before its copy was verified. Every move is logged.

The path must be a full one (`D:\Clips`, `\\nas\clips`); a relative path (`\nas\clips`, a lost backslash) is refused
with a hint, and *Test* then says why instead of testing the previous path. Before each copy Clink checks the free space:
a clip that does not fit is skipped without writing anything, and an archive that is full, read-only or denies access
ends the run with a status line that says which (*archive is full (12 GB free)*, *archive is read-only*, *access denied:
open the share in Explorer once to sign in*) instead of trying every clip again. Turning the archive off or pointing it
elsewhere stops a run in progress (the partial copy is deleted). *Archive now* moves what is due right away, or says why
nothing is (*no clip is older than 14 days and the clips folder is under 200 GB*). In the clip list an archived clip
whose share is offline says *archive offline* at once (Trim, play and folder do not wait out the network timeout) and
clears itself when the share is back; a clip that was deleted from the archive drops out of the list, or, if you shared
it, stays under **Cloud** marked *deleted from the archive*.

### Stream Deck

Two ways, no plugin needed:

* Add a **Hotkey** action that sends one of Clink's hotkeys (F9 by default; a second binding with a shorter length
  makes a good second button). Clink uses a low-level keyboard hook, so it sees injected keys even when a game has focus.
* Or use a **System → Open** action with `Clink.exe --clip`. If Clink is already running this just tells it to
  clip and exits. `Clink.exe --trim-last` opens the trim editor on the newest clip and `Clink.exe --share-last`
  uploads it (or copies its link) the same way. If Clink is not running, these start it in the tray and then run the
  command; a `--clip` then only says there was nothing to save yet, since nothing was recording.
* A multi-action "clip, then share" (`--clip`, then `--share-last`) shares the clip just made: the share waits for the
  save, however long it takes, and if that clip could not be saved nothing is shared (never the clip before it).

Other commands: `--show`, `--minimized` (start in the tray), `--no-update` (start without installing a downloaded
update, handy for a debug build), `--selftest [seconds] [folder] [--codec h264|hevc|av1] [--quiet]` (records headless
and writes a clip to that folder, the clips folder by default, and prints what it did),
`--probe <file.mp4> [frame.png] [seconds]` (stream timing report, optional decoded frame),
`--trim <in> <out> <start> <end>`. The last three can run next to a running Clink.

### Settings

`%APPDATA%\Clink\settings.json` (the UI writes it; it and the clip index `clips.json` are replaced in one rename on every
save and keep the previous version as `.bak`, which is read when the file is damaged; hand edits may use `//` comments,
trailing commas and numbers in quotes, and a hand edit that still does not parse is kept as `settings.json.unreadable`
and Clink says so at start). The audio track pickers (*Capture → Audio tracks*) store
`AllDeviceId` (the Desktop track: `out:<id>` or a bare id = that output's loopback, `in:<id>` = a capture device) /
`MicDeviceId` (endpoint ids; `null` = the default device, which follows Windows when you change the default; a device
that is unplugged stays selected until you pick another, and the status line says why it is not recording) and
`GameProcess` / `VoiceApp` (a process name, `.exe` optional, any case; `auto` = detect: the fullscreen app on the captured
monitor, or the first of Discord, TeamSpeak, Slack, Mumble, Zoom, Skype found running). The v2 keys are `RecordingMode`
(`game` | `always`), `CustomApps`, `Hotkeys` (a list of `{ Keys, Action, Seconds }`; `Seconds` 0 = the replay length),
`SizeTarget` (`none` | `copium200` | `discord10` | `discord50` | `discord500`, default `copium200`), `CopiumPlus`, `AudioSetupSeen`, `ToastPosition`, `ToastClip` / `ToastShared` / `ToastRecording` (`{ Enabled, Seconds }`; an empty `Hotkeys` list means no hotkeys, only a file without the key gets the old single `Hotkey` migrated), `StatusDot` /
`StatusDotPosition`, `ArchiveEnabled` / `ArchivePath` / `ArchiveAfterDays` / `ArchiveWhenLocalExceedsGB` and the window
position and size. Things not in the UI:

| key | default | what |
|-----|---------|------|
| `KeyframeSeconds` | 1 | keyframe interval; smaller = finer trim snapping, slightly bigger files |
| `HdrKnee` / `HdrPeak` | 0.75 / 4 | tone-mapper shoulder: everything below `knee` (relative to SDR white) is untouched, highlights roll off softly above it |
| `ClickVolume` | 0.5 | 0..1 |
| `StartMinimized` | false | |
| `UpdateRepo` | *(built in)* | GitHub `owner/repo` whose releases to install, overriding the repository baked into the exe. Only for your own fork: it is logged at start and shown in the version tooltip |

Logs: `%APPDATA%\Clink\clink.log`, written only by the running Clink (a Stream Deck `--clip` that just forwards its
command does not touch it); `--selftest` / `--probe` / `--trim` write `clink-cli.log`. Each starts with a header line
(date, process id, arguments) and marks every new day; once a log passes a few MB it moves to `clink.prev.log`
(`clink-cli.prev.log`), so the two files together stay under about 8 MB.

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
