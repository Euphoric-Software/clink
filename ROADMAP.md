# Roadmap / future ideas

Things that were asked for or came up while building, roughly in the order they'd be worth doing.

## Soon

- [ ] **Frame-accurate trim start** ("smart cut"): re-encode only the partial GOP before the chosen start and copy the
      rest. Today the start snaps to the previous keyframe (1 s grid by default; lower `KeyframeSeconds` for finer).
- [ ] **Copy on save** option: automatically upload every clip and copy the link (off by default, as requested).
- [x] **Clip length hotkeys**: the hotkey list (Capture → Hotkeys) gives every binding its own length and action. Since
      v0.2.5 the first one is the main hotkey (Alt+C by default), which always saves the full replay; the others are
      shorter clips, pause/resume and share-last.
- [ ] **Auto-follow the game's monitor** instead of a fixed monitor choice.
- [x] **Manual game process picker** for the Game audio track, plus the custom-apps list for game detection.
- [ ] Re-check HDR state live (if Windows HDR is toggled while Clink runs, restart capture with the right format).
- [ ] Mouse buttons as hotkeys.
- [x] Apply the size target's bitrate and the AV1 choice in `AppController.BuildConfig` (the engine records at the
      bitrate the Capture tab shows, and a codec the GPU lacks falls back to one it has, as the tab says).
- [x] **No silent misses** (v0.2.4): a clip or share that does not happen says why over the game (Clink toast) or in a
      tray balloon; a recorder that fails to start is retried with a backoff and reported once.
- [x] **GPU reset recovery** (v0.2.4): a driver crash / TDR is detected and capture restarts on a fresh device.
- [x] **Settings that survive** (v0.2.5): a Clink started from inside another app (a Store/MSIX desktop app, say) keeps its
      settings in that app's private AppData, which a Clink started from Explorer never sees; v0.2.5 detects that, takes
      the copy over on a first start or offers it (tray, App tab), and a settings file with a bad value, or keys from a
      newer Clink, no longer costs anything.
- [x] **Better defaults for a fresh install** (v0.2.5): 1080p, 60 fps, 20 Mbps, H.264, no size target, cursor on,
      record only while a game runs, a 45 s replay and Alt+C as the main hotkey.

## Distribution

- [x] Auto-update from GitHub Releases (checked on start and every 6 hours, SHA-256 verified, installed on next start).
- [ ] Authenticode code signing for the release exe (SmartScreen + stronger update integrity).

## Stream Deck

- [x] Works today through the Stream Deck "Hotkey" action or `Clink.exe --clip`.
- [x] `--clip` / `--share-last` / `--trim-last` with Clink not running start it in the tray and run the command, and a
      "clip, then share" multi-action shares the clip it just made (v0.2.4).
- [ ] Native Stream Deck plugin: Clip / Pause / Share-last-clip actions with a live state icon.

## Audio

- [x] All / Game / Mic / Voice-chat as separate tracks (per-process capture on Windows 10 2004+).
- [ ] Elgato Wave Link / VoiceMeeter virtual outputs as selectable extra tracks.
- [x] **Desktop track from a normal audio input as well**: the Desktop picker takes a capture device (a Wave Link
      mix, a virtual cable) or any output's loopback, like the Game and Voice pickers (`in:` / `out:` ids; a bare
      output id from an older settings file still works).
- [x] **All audio follows the Game / Mic / Voice sliders** (v0.2.5): in the player and the trim editor, live and in the
      saved file (the mix is rebuilt from those tracks; Desktop never feeds it).
- [ ] Per-track gain and a mono-mic option.
- [ ] Mix-down presets for sharing (e.g. "game + mic only" when uploading).

## Video

- [ ] **HDR output** (HEVC Main10 + PQ metadata) for people who want true HDR clips; SDR tone-mapped stays the default
      because Discord/Copium embeds expect SDR.
- [x] AV1 on GPUs that have it (experimental: encode + `av01` mux are in, untested here because the RTX 3090 has no
      AV1 encoder).
- [ ] Capture a single window instead of a monitor.
- [x] Optional on-screen "recording" indicator: the status dot (Alerts tab).
- [x] Toasts and the status dot are excluded from capture, so they never end up in a clip (v0.2.4; the Share window too
      since v0.2.5).
- [x] Outputs wider than 4096 px (super-ultrawide at Native) record HEVC instead of failing in H.264 (v0.2.4).
- [x] Portrait monitors: the 1440p / 1080p / 720p presets name the short side (v0.2.4).
- [ ] Trim-editor preview and `--probe` frames of HEVC clips whose height is not a multiple of 16 (1080p) include the
      encoder's 8 padding rows at the bottom: crop to `MF_MT_MINIMUM_DISPLAY_APERTURE`. The saved and trimmed files are
      fine (players crop them); deferred from v0.2.4.

## Interface

- [x] **Replay length beside the size target, one main hotkey, a Copium Plus size target** (v0.2.5): the slider and its
      readout sit in the Capturing card; Capture → Hotkeys has a main hotkey and "More hotkeys"; the size targets include
      Copium (200 MB, 10 min) and Copium Plus (2 GB, 60 min).
- [x] **Clink's own player** (v0.2.5): a click on a clip plays it; the ✂ Trim switch turns it into the trim editor.
- [x] **Archive now…** (v0.2.5): a dialog that moves what is due by your rules, or every local clip, with progress and a
      result; the Storage card has a location that is always usable and "Move clips there automatically".
- [x] **Share without a Copium account** (v0.2.5): with no token Share opens a window with the clip's file to drag into
      Discord, a browser or a folder (copy only), or to copy and paste. Copium Plus's limits (2 GB, 60 min) are in the
      share check.
- [ ] **Share window: make a smaller copy for Discord's 10 MB**: re-encode the clip to a size from the Share window (today
      it shares the file as it is).
- [x] **Dark or Light, six accents** (v0.2.5): the new App tab (with Updates and where the settings are kept); every
      window follows a change at once.
- [ ] **System theme** (follow Windows): a third choice next to Dark and Light.
- [ ] **Bundle the Inter and Sora fonts**: the interface asks for them by name, but they are used only where they are
      installed; otherwise Windows falls back to Segoe UI Variable / Segoe UI.

## Screenshots

- [x] **Screenshot tools, iteration 1**: region capture over a frozen desktop (mixed DPI, multi monitor), PNG to its own
      folder and history, clipboard, notification with Edit / Upload / Copy link / Folder; the editor (vector and raster
      tools, adjustments, insert image, undo) as a window or right on the screen; edit-last; Copium upload with the link on
      the clipboard, the "link your account" hint, the settings and the AI background-remover seam (no model yet).
- [x] **Opt-in sharing, kill switch and Features (S7)**: nothing is uploaded until the user agrees once in the *Share with
      Copium?* pop-up (with the public-links warning; Enter never agrees); *App → Cloud sharing* turns every upload off at
      once (running ones too) and back on only after a confirmation; *App → Features* switches Clips and Screenshots on or
      off live; the Copium token moved to the App tab. Replaces the S6 "a token means automatic upload", its one-time
      notice and once-a-day hint.
- [ ] **GIF / video from the screenshot tool**: drag a region and record it (the region picker already returns a result a
      recorder could use).
- [ ] **Scrolling capture** (long web pages, chats).
- [ ] **Delayed capture** (a countdown, for menus and tooltips).
- [ ] **Auto / repeat capture** (every N seconds, or on a change).
- [ ] **Windows Graphics Capture source for HDR** (S7): GDI clips HDR highlights; the seam (`IScreenshotSource`) is there,
      and the same source would be the fallback for an all-black exclusive-fullscreen frame.
- [ ] **AI background remover**: `IBackgroundRemover` is in place and unavailable. Needs an on-demand model download (https,
      pinned SHA-256, consent showing size and licence) and a runtime decision; RMBG-1.4 is non-commercial (never the
      default), u2net / ISNet / MODNet (Apache-2.0) and BiRefNet (MIT) are permissive.
- [ ] Edits made on screen are lost on a quit / Windows sign-out (the editor window saves a copy; the overlay does not yet).
- [ ] A screenshot history window (today: the last 24 in the insert-image picker, and Edit last).

## Later, maybe

- [ ] Replace-on-share ("keep only the trimmed version").
