# Roadmap / future ideas

Things that were asked for or came up while building, roughly in the order they'd be worth doing.

## Soon

- [ ] **Frame-accurate trim start** ("smart cut"): re-encode only the partial GOP before the chosen start and copy the
      rest. Today the start snaps to the previous keyframe (1 s grid by default; lower `KeyframeSeconds` for finer).
- [ ] **Copy on save** option: automatically upload every clip and copy the link (off by default, as requested).
- [ ] **Clip length hotkeys**: e.g. F9 = last 30 s, Shift+F9 = last 2 min.
- [ ] **Auto-follow the game's monitor** instead of a fixed monitor choice.
- [ ] **Manual game process picker** for the Game audio track when auto-detection picks the wrong app.
- [ ] Re-check HDR state live (if Windows HDR is toggled while Clink runs, restart capture with the right format).
- [ ] Mouse buttons as hotkeys.

## Distribution

- [x] Auto-update from GitHub Releases (checked on start and every 6 hours, SHA-256 verified, installed on next start).
- [ ] Authenticode code signing for the release exe (SmartScreen + stronger update integrity).

## Stream Deck

- [x] Works today through the Stream Deck "Hotkey" action or `Clink.exe --clip`.
- [ ] Native Stream Deck plugin: Clip / Pause / Share-last-clip actions with a live state icon.

## Audio

- [x] All / Game / Mic / Voice-chat as separate tracks (per-process capture on Windows 10 2004+).
- [ ] Elgato Wave Link / VoiceMeeter virtual outputs as selectable extra tracks.
- [ ] Per-track gain and a mono-mic option.
- [ ] Mix-down presets for sharing (e.g. "game + mic only" when uploading).

## Video

- [ ] **HDR output** (HEVC Main10 + PQ metadata) for people who want true HDR clips; SDR tone-mapped stays the default
      because Discord/Copium embeds expect SDR.
- [ ] AV1 on GPUs that have it.
- [ ] Capture a single window instead of a monitor.
- [ ] Optional on-screen "recording" indicator / overlay.

## Later, maybe

- [ ] Screenshots (ShareX-style) with the same Copium share flow — explicitly low priority; video is the point.
- [ ] Replace-on-share ("keep only the trimmed version").
- [ ] Light theme. (Just kidding.)
