# Tasks

- [x] Step 1: Mp4Muxer progressive mode + journal + Recover + tests
- [x] Step 2 (b9591c5): Engine/ClipWriter.cs -> incremental writer (AddVideo/AddAudio/Finish), Write stays a wrapper; existing tests green
- [x] Step 3 (73abefc): Engine/RecordingSink.cs (queue + "Clink.Recorder" writer thread, byte budget max(64 MB, 15 s), drop-to-keyframe backpressure, limits, stop with audio catch-up <=800 ms), Engine API (StartRecording/StopRecordingAsync/IsRecording/Recording status/RecordingStopped event/RecoverRecordings), _tapGate fan-out at Engine.cs:172, AudioTrack.FrameTap, detach taps before TearDown, Dispose waits <=15 s; SelfTest --record; tests with fake producers
- [x] Step 4 (93af45d): "record" hotkey action, tray item, --record command; AppController.Recording.cs + RecordingPolicy (pure restart rules, tests); RecordingStopped marshalled with BeginInvoke; red status dot + "Recording mm:ss"; settings restarts postponed; continuation parts; queued start; startup recovery with single toast; bounded stop on session end/exit
- [ ] Step 5: recordings folder + max part length settings (+ SettingsImport categories); separate toast switch for recordings; scan `Recordings\*.mp4` in ClipLibrary.Load (also fixes recordings lost during an import restart) + IsRecording badge in ClipsPane; "Recording saved" toast totals across parts + Open folder; recovered entries: take time from file name; Trimmer seek-and-stream or selection cap before trim is offered on recordings; archive opt-in decision; engine-owned run id instead of controller `_engineRunGen` (slow starts >15 s); count pending technical restarts instead of clearing in StartEngine (back-to-back display events can create a short extra part); `StartEngine` returns whether it issued a start; `--record` at cold start in game-only mode should stay queued until the first game poll; wording after 3 empty automatic parts when earlier parts were saved; controller sequencing tests; release note: older Clink treats a "record" hotkey as a clip
- [ ] Manual soak: 2 h recording, clip during recording, kill process -> recovered on restart, display change -> part 2

## Blocked / needs user
- Live app test not run because the user's installed Clink was running; run the manual test script in STATE.md.
- Product decision pending: continuing a recording after an arbitrarily long sleep.

## Dev Build channel follow-ups
- [x] Merged feature/dev-channel to main (c72fb2d), `dev` created and pushed; first dev build v0.3.1-dev.1 published and verified (run 40130530774)
- [ ] User: install v0.3.1-dev.1 by hand once (replace running Clink.exe), then tick Dev Build
- [ ] GUI-check the footer popup with a build using `-p:UpdateRepo=owner/repo`
- [ ] Optional hardening: split release-dev.yml into a read-only build/test job and a separate publish job

## Screenshots (design: docs/screenshots-design.md)
- [x] S1 capture to PNG (2cb39b0)
- [x] S2 pixel library (merged 0d1dc6f; worktree branch kept)
- [x] S3 editor window + vector tools, edit-after, edit-last (f7aeb3f)
- [x] S4 raster tools, adjustments, insert image (77530e1)
- [x] S5 edit-before in the overlay (5ea3308)
- [x] S6 upload, toast actions, Copium nudge, settings completion, AI seam, export off UI thread (56bb20d)
- [x] S7 consent and sharing controls (4df4efa) (supersedes the old "WGC HDR source" slot, which stays pending the user HDR check):
  - one-time Copium opt-in pop-up with public-link warning; cloud sharing default OFF (clips and screenshots); automatic upload opt-in
  - global "Allow cloud sharing" kill switch with two-step re-enable
  - feature-visibility toggles (Clips / Screenshots); hiding Clips stops the replay engine (assumption, confirm)
  - S6 leftovers: per-install keys into `IsOwnKey`; accept any https for returned `link`/`url`, pin only the token destination; 403 pauses only when text mentions token/auth/key; type-refusal regex with word boundaries; deferred-close vs quit double prompt; `AllowAutoRedirect=false`; DIB header size allow-list; `WrittenUtc` 2 s tolerance; link copy with image-copy off via sequence gate; mapped network drives not detected as network
- [x] S8 library pane rework (6795381): Clips | Screenshots switch, Local | Shared subordinate, Trimmed as a filter. Screenshot tools iteration 1 complete.
- [ ] S8 follow-ups: Tab walk GUI check; shell second confirmation; EditorOpen/ListNotUpdated tests; 500-row thumbnail memory; tampered roots; junctions inside folder
- [ ] Deferred: GIF/video from the tool, scrolling, delayed, auto/repeat capture, AI background remover, WGC HDR
- [ ] User checks: HDR look, PrintScreen over a game, first real Copium upload, consent dialog, kill switch, Features toggles, library pane in both themes/widths
- [ ] S7/S8 follow-ups: elevated-run control-pipe `CurrentUserOnly` client failure; real-upload verification against Copium; Win+V / clipboard managers may prevent link replacement
- [x] Follow-up: Alerts tab "Screenshot saved" toast row + upload toggles UI (S6)
- [x] Follow-up: README/ROADMAP docs for screenshots (S6)
- [ ] User manual test list: STATE.md Dev manual checklist (S1, S3, S4, S5)

## Screenshots follow-ups
- [ ] Quit flow: synchronous emergency save for non-user exits; message when a long-running save blocks a user quit
- [ ] Overlay `Session` window-factory test seam
- [ ] `LastEdit` retention cap/expiry
- [ ] Toast Folder/Open after the folder changes (IsOpenable)
- [ ] Design doc A3: name `AdjustmentValues` (Sharpness 0..200, default 0, amount = Sharpness/100)
- [ ] CI: desktop-dependent tests (GDI monitor capture, busy clipboard) first run on a GitHub runner with the first dev push

## S4 requirements (raster tools into the editor) - all done in 77530e1
- [x] `IRasterOp.AffectedBy(changedInput)` for non-local ops (magnify Source->Dest, smart erase ring->whole rect, pixelate block-grid snap, spotlight global)
- `Apply` must not depend on `area`; the composer computes the dirty area from old+new `Affects`
- [x] Bounded memory: no full-size buffer per op (below-cache for the active object + final composite, or per-op patches)
- [x] One compose in flight; double-buffer before WritePixels/Export; version tag; cancel only between composes
- [x] Two-stage Adjuster: LUT synchronous, unsharp debounced 60 ms on a background thread
- [x] Spotlight: cache `SpotlightLevels` keyed by below-version+radius+gradient (set `Version`); recompose dirty rects into a separate dst (in-place is single-shot); debounce `Spotlight.Build` on radius drag
- `Resample.Over` needs the below pixels restored first
- `PlanePool.Clear()` on editor close, after cancelling background work
- [x] Blur/unsharp dirty-rect output may differ by +-1 from a full run

## S4 follow-ups
- [ ] `Export()` runs on the UI thread (4K save/copy may freeze 0.5-2 s): move to a worker, likely in S6 with upload
- [ ] With > 2 spotlights all level sets are alive during a compose
- [ ] Ctrl+V during an insert is silently ignored
- [ ] Picker `Panel2` via `FindResource` does not follow a theme change
- [ ] Picker thumbnails soft above ~110% DPI
- [ ] `Recovered` clears any status text
- [ ] User manual test list: S4 list in STATE.md

## S5 follow-ups
- [ ] Overlay edits are lost on quit/session end
- [ ] 4K export runs on the UI thread (move off in S6)
- [ ] Native file dialog cannot be capture-excluded (may appear in a replay clip)
- [ ] Two editor sessions at once (overlay + window) raise the memory peak
- [ ] `Rebase` re-export may differ for edge effects (blur/magnify near the crop edge)

## S7 follow-ups
- [ ] Recording stop ordering (queued notice / async void) when Clips is turned off while recording
- [ ] Bare 403 vs plan limits
- [ ] Share window not updated live after a kill-switch flip
- [ ] Tab-to-Enable + Enter agrees in consent dialog
- [ ] Capture-excluded consent dialog invisible in screen shares
- [ ] Real-upload verification with a valid token; Copium image size limit unknown
- [ ] Dev build #3 (slices 6-7): pending this push
- Next: merge S8 to dev; user manual checks
