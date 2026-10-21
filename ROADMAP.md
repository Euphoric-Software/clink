# Roadmap / future ideas

Things that were asked for or came up while building, roughly in the order they'd be worth doing.

## Soon

- [ ] **Frame-accurate trim start** ("smart cut"): re-encode only the partial GOP before the chosen start and copy the
      rest. Today the start snaps to the previous keyframe (1 s grid by default; lower `KeyframeSeconds` for finer).
- [ ] **Copy on save** option: automatically upload every clip and copy the link (off by default, as requested).
- [x] **Clip length hotkeys**: the hotkey list (Clip → Hotkeys) gives every binding its own length and action.
- [ ] **Auto-follow the game's monitor** instead of a fixed monitor choice.
- [x] **Manual game process picker** for the Game audio track, plus the custom-apps list for game detection.
- [ ] Re-check HDR state live (if Windows HDR is toggled while Clink runs, restart capture with the right format).
- [ ] Mouse buttons as hotkeys.
- [x] Apply the size target's bitrate and the AV1 choice in `AppController.BuildConfig` (the engine records at the
      bitrate the Capture tab shows, and a codec the GPU lacks falls back to one it has, as the tab says).

## Distribution

- [x] Auto-update from GitHub Releases (checked on start and every 6 hours, SHA-256 verified, installed on next start).
- [ ] Authenticode code signing for the release exe (SmartScreen + stronger update integrity).

## Stream Deck

- [x] Works today through the Stream Deck "Hotkey" action or `Clink.exe --clip`.
- [ ] Native Stream Deck plugin: Clip / Pause / Share-last-clip actions with a live state icon.

## Audio

- [x] All / Game / Mic / Voice-chat as separate tracks (per-process capture on Windows 10 2004+).
- [ ] Elgato Wave Link / VoiceMeeter virtual outputs as selectable extra tracks.
- [x] **Desktop track from a normal audio input as well**: the Desktop picker takes a capture device (a Wave Link
      mix, a virtual cable) or any output's loopback, like the Game and Voice pickers (`in:` / `out:` ids; a bare
      output id from an older settings file still works).
- [ ] Per-track gain and a mono-mic option.
- [ ] Mix-down presets for sharing (e.g. "game + mic only" when uploading).

## Video

- [ ] **HDR output** (HEVC Main10 + PQ metadata) for people who want true HDR clips; SDR tone-mapped stays the default
      because Discord/Copium embeds expect SDR.
- [x] AV1 on GPUs that have it (experimental: encode + `av01` mux are in, untested here because the RTX 3090 has no
      AV1 encoder).
- [ ] Capture a single window instead of a monitor.
- [x] Optional on-screen "recording" indicator: the status dot (Alerts tab).

## Later, maybe

- [ ] Screenshots (ShareX-style) with the same Copium share flow — explicitly low priority; video is the point.
- [ ] Replace-on-share ("keep only the trimmed version").
- [ ] Light theme. (Just kidding.)
