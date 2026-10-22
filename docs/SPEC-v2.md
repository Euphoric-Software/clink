# Clink v2 UI & features spec

Requested 2026-10-20. This is the brief for the "v2" pass; items are grouped by area. Nothing here changes the
capture/encode pipeline itself.

v0.2.5 (requested 2026-10-21) changed the tab layout, the hotkey list, the size targets, the archive controls, the player
and the look of the app. §11 says what the app does since then and wins where it differs from §§1, 3, 6, 9 and 10, which
are kept as written, as the record of the v0.2.0 to v0.2.4 design.

## 1. Window & layout

- The window is **resizable** (remember size + position). Minimum size ≈ 470×700, which must look like the current
  compact layout.
- **No nested scrolling.** At most one scrollable region per pane.
- Wide (≥ ~900 px, "16:9"): **two panes**. Left = settings tabs, right = clips tabs. Narrow: one column with a top
  tab strip: `Capture · Clip · Audio · Alerts · Clips`.
- Settings tabs:
  - **Capture**: monitor map + label, resolution, FPS, bitrate, codec, size estimates, recording mode (see §5).
  - **Clip**: hotkeys list (see §7), replay-length slider (§6), click sound, capture cursor, run at startup,
    Copium token.
  - **Audio**: the per-track rows with source pickers, as they are today (Mix row first, then Game / Mic / Voice chat with
    app-or-device pickers, then the optional Desktop track).
  - **Alerts**: toast settings and the status dot (§8).
  - **Updates** (small group on the Clip tab or Alerts tab): auto-update on/off, current version, "check now", and the
    last install problem if any (e.g. Smart App Control blocked the downloaded exe).
- Clips group (right pane / Clips tab) has three sub-tabs:
  - **Local** – every local file. Each row: thumbnail, title, meta, and small **icons**: cloud icon if it has
    been shared, scissors icon if it is a trim, "local only" icon otherwise. Buttons: Trim, Share/Copy link, folder.
  - **Cloud** – everything that has been shared: title, date, the link (click = copy), open page, the local file if
    still present. Entries stay even if the local file was deleted.
  - **Trimmed** – local files that are trims (also carry the shared icon when uploaded).
- Icons: Segoe Fluent Icons / Segoe MDL2 Assets glyphs (ship with Windows 10/11).

## 2. Replay length slider (§6)

- A slider from **5 s to 60 min** instead of the dropdown. Log scale so 15 s–2 min has room; ticks at 15 s, 30 s,
  1, 2, 5, 10, 30, 60 min. Value label next to it ("1:30").
- The **30–90 s** stretch of the track is drawn in a brighter colour (the "ideal zone"); the rest is the normal
  track colour.
- Show the **RAM** the buffer needs at the current bitrate ("≈ 450 MB of RAM"); turn it red and warn when it is
  more than half of the machine's physical RAM; refuse (clamp) when it cannot be allocated.

## 3. Size estimates & Discord targets

- Under the quality controls show live estimates: **per minute** always, plus **per clip at the selected replay
  length** (e.g. "≈ 190 MB per minute · ≈ 380 MB per 2:00 clip"). When the length is 1 h show the 1 h figure too.
  Estimate = (video bitrate + enabled audio tracks × 192 kbps) × seconds / 8.
- **Size target** option: *None* / *Discord (10 MB)* / *Discord Nitro Basic (50 MB)* / *Discord Nitro (500 MB)*.
  When set, the bitrate is computed so a clip of the selected length fits the limit (minus audio), shown read-only
  with the number it picked, and the user can switch back to manual.

## 4. Codecs

- Detect which hardware encoders exist (H.264, HEVC, AV1) by enumerating MFTs at startup; list unsupported ones
  greyed with "not supported by this GPU".
- Each codec has a one-line description in the UI:
  - H.264 – plays everywhere, incl. Discord inline and every browser; biggest files.
  - HEVC – ~40 % smaller at the same quality; Discord/Chrome may not play it inline, fine for local/archive.
  - AV1 – smallest; needs RTX 40 / RX 7000 / Arc to encode and a recent player. *Experimental.*
- AV1 encode + mux (`av01` sample entry with `av1C` built from the sequence-header OBU) is added but marked
  experimental; it cannot be tested on this machine (RTX 3090 has no AV1 encoder).

## 5. Recording mode & game detection

- **Recording mode**: *Always* or *Only while a game or custom app is running* (default: game-only, as requested).
  In game-only mode the engine (capture, encoder, RAM buffer) is stopped while no game runs and started when one
  appears; the UI says "Waiting for a game…" / "Recording · Overwatch".
- **Game detection** must be good:
  1. a visible, non-cloaked window that covers the captured monitor (fullscreen/borderless) and is not on the
     ignore list (browsers, launchers, voice apps, shell, Clink itself);
  2. the process image path is inside a known game library folder (`steamapps\common`, `Epic Games`, `Riot Games`,
     `Battle.net`/Blizzard, `Ubisoft Game Launcher\games`, `EA Games`/`Origin Games`, `GOG Galaxy\Games`, `XboxGames`,
     `Rockstar Games`) and it has a visible window;
  3. the process is in the user's **custom apps** list (exe name; UI has "Add running app…" picker and a free-text
     entry) — these always count, windowed or not.
  Poll every second; a game is "gone" when its process exits (not when it loses focus). The Game audio track uses
  the same detection.
- Also an **ignore list** editor is not required; keep the built-in list.

## 6. Hotkeys

- A **list** of hotkeys instead of one. Each entry: key combo + replay length (seconds, uses the same 5 s–1 h scale;
  default = the slider value) + action: *Clip* (default), *Pause/resume recording*, *Share last clip*.
  Default list: F9 → clip (slider length). Add / remove / change keys inline.
- The low-level hook must match any of the bindings.

## 7. Toasts & status dot (§8)

- Toast types: **Clip saved**, **Shared** (link copied), **Recording started** (game detected) — each with its own
  enable switch and duration (1–30 s, default 7 / 4 / 4).
- Toast **position**: top-left, top, top-right, left, right, bottom-left, bottom, bottom-right (default bottom-right)
  on the **captured monitor**'s work area, with a 16 px margin. Toasts stack if several are visible.
- Optional **always-on status dot**: a tiny click-through topmost window (≈ 12 px) in a chosen corner of the captured
  monitor: green = recording, amber = waiting for a game, red = error. Off by default.

## 8. Library data

- `ClipEntry` gets `IsTrim` (set by the trim editor), `SourcePath` (for trims), and keeps Copium link/id/page. Entries
  whose file was deleted stay only if shared (for the Cloud tab).

## 9. Archive storage (advanced): NAS / share / second drive

- Clips are always written to the **local clips folder** first (fast, on the system drive). An optional **archive
  location** moves them later in the background. Must work with UNC shares (`\\nas\clips`), mapped drives and any
  local folder; the user authenticates to the share in Explorer, Clink never handles credentials (on access denied
  the status says "open the share in Explorer once to sign in").
- Settings (an **Advanced / Storage** group on the Clip tab or its own "Storage" tab): local clips folder (picker +
  open), `ArchiveEnabled`, `ArchivePath`, `ArchiveAfterDays` (0 = off), `ArchiveWhenLocalExceedsGB` (0 = off):
  **whichever comes first**. Buttons: *Browse…*, *Test* (reachable? writable? free space), status line
  ("12 clips archived · last move 14:02 · NAS reachable" / "archive unreachable, will retry").
- `ArchiveService` (background): runs 1 min after a clip is saved and every 5 min. Picks eligible clips oldest-first:
  older than N days, or while the local folder is above N GB. Copies with a small buffer on a lowest-priority thread
  with short sleeps between chunks ("performance nice", never competes with the game), verifies the length, then
  deletes the local file and updates the `ClipEntry.Path` (and `SourcePath` for trims) in the library. Never moves a
  file that is open (uploading / trimming / playing): try opening with `FileShare.None` first. Unreachable archive =
  skip quietly, retry next run; never delete a local file whose copy was not verified. Logs every move.
- Library rows get an **archived** icon (NAS/drive glyph); Trim, Share and thumbnails work from the archive path.
  If the archive is offline the row says so instead of failing.

## 10. Tab layout, second pass (requested after v0.2.1)

- **Capture tab**, top to bottom: *Capturing* (monitor map, resolution, FPS, bitrate, codec with description, size
  target incl. "Copium (200 MB, 10 min)", capture cursor) · *Audio* (the track rows, with the set-up warning until
  hovered/changed) · *Recording* (mode + custom apps) · *Replay length* (the slider) · *Hotkeys* (the list editor,
  "Clip now", click sound).
- Directly under the replay-length slider, **one combined readout** that changes with resolution, FPS, bitrate,
  codec, audio tracks and length: file size per minute, file size for a clip of the chosen length (and the hour
  figure when the length is 1 h), and the RAM the buffer takes ("≈ 126 MB per minute · ≈ 53 MB per 25 s clip ·
  ≈ 62 MB of RAM"), plus the size-target note. Nothing about size/RAM is shown anywhere else.
- **Clip tab**: only *Copium* (token, "I have Copium+") and *Storage* (clips folder, archive).
- **Alerts tab**: toasts, status dot, updates, and "run at startup" next to updates.

## 11. v0.2.5

What the app does since v0.2.5. Where this section and §§1, 3, 6, 9 and 10 differ, this section wins.

### 11.1 Defaults and the settings file (schema 3)

- A fresh install starts with: resolution **1080p**, **60** fps, **20** Mbps, codec **H.264**, size target **None**,
  capture cursor **on**, recording mode **game** (only while a game or custom app runs), replay length **45 s**, theme
  **Dark** with the **Cyan** accent, and main hotkey **Alt+C** (§11.2). They live in one place (`SettingsDefaults`): the
  `Settings` initializers, `Normalize()` and the UI's fallbacks all read it. An existing `settings.json` keeps every value
  it has: a default only fills in a key the file does not have, so changing one never changes what someone chose. (A v0.1
  file never had `SizeTarget`, so it now gets `none` where it used to get `copium200`.)
- `SchemaVersion` is 3. A file without it counts as 2 when it has `Hotkeys`, else 1. A file of an older schema, and a first
  start, is saved once at start in the new layout; that first save keeps the old file as `settings.pre-v3.json` (once: an
  existing copy is never overwritten) next to the usual `settings.json.bak`. Saving always writes 3, even over a higher
  number from a newer Clink, because once this version has rewritten the file the number must describe what is in it.
- New keys: `SchemaVersion`, `Theme` (`dark` | `light`), `Accent` (`cyan` | `blue` | `violet` | `pink` | `orange` | `green`)
  and `ImportDeclined` (§11.9).
- Loading is tolerant. The keys are bound one by one, so a value of the wrong kind (`"Fps": "sixty"`,
  `"BufferSeconds": [1]`, a null for a number) keeps its default and costs nothing else. It is logged and said once at
  start (*settings.json: kept the defaults for Fps, BufferSeconds (unreadable values)*), and the original file is kept as
  `settings.json.unreadable` before anything is saved. A file that is not JSON at all still falls back to the `.bak` (and
  is kept as `.unreadable`).
- Keys this version does not know (written by a newer Clink) are kept and written back, inside hotkeys, hotkey keys and
  toast settings too, and so are top-level keys named like a member that is not saved (`MainHotkey`, `Migrated`,
  `CopiumToken`, …): a downgrade and upgrade round trip loses nothing.
- Saving: a NaN or infinity in a number setting (a window position that was never set) is put back to its default (null
  for the window's position and size) before every save, so a save never throws on it. A save asked for from another
  thread (the updater's) runs on the UI thread. `Settings.FreezeSaves()` stops every save while an import replaces the
  file (§11.9).

### 11.2 Hotkeys (replaces §6's list)

- Storage is unchanged: `Hotkeys`, a list of `{ Keys, Action, Seconds }`, so v0.2.4 still reads it. After a load,
  **`Hotkeys[0]` is the main hotkey**: a `clip` with `Seconds` 0 (always the full replay) whose keys may be unset (`Vk` 0).
  `Hotkeys[1..]` are the additional hotkeys: a `clip` with a fixed `Seconds` above 0, `pause` or `share-last`.
  `Settings.Hotkey` (the v1 field) mirrors the main hotkey's keys while it has any.
- On load (`Settings.EnsureHotkeyModel`, idempotent; nothing that is bound changes what it does):
  1. No `Hotkeys` key and an empty list (a v0.1 file, or a first start): the main is `Hotkey` (the file's own key for
     v0.1, Alt+C on a first start).
  2. A `Hotkeys` key with an empty list ("no hotkeys, by choice" in v0.2.4, or a list that could not be read): an unset
     main and the notice below. Alt+C is not bound, and so not swallowed system-wide, for someone who chose to have no hotkeys.
  3. In a file of schema 3 or higher, a full-replay `clip` row that already leads the list is the main hotkey, set or
     cleared, and nothing is promoted over it. (v0.2.4's Add hotkey made rows of `Seconds` 0, so a cleared main with
     another full-replay row below it is common.)
  4. Otherwise the main is the first full-replay `clip` row that has keys and that no earlier row with the same keys
     shadows (the first match fires); it moves to index 0 and keeps its keys (an F9 file stays on F9).
  5. No such row (every clip hotkey has a fixed length, or the only full-replay row is the keyless "Default" row that
     v0.2.4's Add hotkey makes): a main is added at index 0 (that row, or a new one) on **Alt+C**, unset when another row
     already uses Alt+C.
  6. The main is forced to `clip` with `Seconds` 0 and `Hotkey` follows its keys. Anything that changed is saved once at start.

  A start that gave the main Alt+C says once, in the footer and a tray balloon, *Alt+C now saves the full replay. Change it
  under Capture → Hotkeys*; one that left it unset says *Set your main hotkey under Capture → Hotkeys: it saves the full
  replay*.
- Capture tab, **Hotkeys** card. *Main hotkey* is one row: the keys button (click, then press the combo; Esc cancels), the
  hint *saves the full replay (0:45)*, which follows the replay length, and a *Clear* button. The main hotkey cannot be
  removed, only cleared (the button then reads *Not set*). *More hotkeys* lists the others, each with its keys, an action
  (*Clip*, *Pause / resume*, *Share last clip*) and, for a clip, a fixed length: the steps of the length scale (5, 10, 15, 20,
  30, 45, 60, 90, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600 s) that are **shorter** than the replay length; the
  "Default" entry is gone. A saved length at or above the replay length reads *X (capped to replay)* (the value is kept and
  counts again once the replay is longer); a hotkey from before v0.2.5 that saves the full replay and sits below the main
  reads *Full replay (m:ss)*; with the shortest replay (5 s) that is the only entry. *Add hotkey* adds a clip row on the
  longest of 30, 15, 10 and 5 s that is below the replay length (5 s when none is) and waits for its keys; a row switched
  from Pause or Share last to Clip starts on such a length too.
- Keys already used by another row, the main one included, are refused with a hint under the row being edited (it keeps
  listening; Esc cancels). A bare letter, digit or editing key is refused as before (*Letters, digits and editing keys
  need Ctrl or Alt; F-keys, ScrollLock and Pause work alone*).
- **No menu bar on Alt chords.** A hotkey (or a key capture in the editor) that swallows a key while Alt is held and Ctrl
  is not (Alt+C, any Alt+Shift+… combo) would leave the program in front with a lone Alt press and release, which opens its
  menu bar. The hook sends a dummy key, virtual key `0xE8` (AutoHotkey's MenuMaskKey), between them, tagged in `dwExtraInfo`
  so the hook passes its own key straight through; Ctrl+Alt combos need none. Nothing synthetic is sent while the detected
  game (a custom app included) is in front: the hotkey still fires and its key is still eaten, and the game sees the lone
  Alt press and release, which it handles itself. Desktop programs, and the key capture in the settings window, keep the
  mask. The game is found by a poll once a second, so in the first moment after its window appears an Alt chord is still
  masked. Key code `0xE8` is reserved: never a hotkey (a hand-edited binding on it is ignored, with a warning in the log),
  never taken by a capture, never eaten. What the hook does with a key is one pure function (`Hotkeys.Decide`), tested as it is.

### 11.3 Capture tab and size targets (changes §2, §3, §10)

- The Replay length slider and its readout (size and RAM per clip, the size-target note) are in the **Capturing** card,
  under Size target; the separate Replay length card is gone. The card reads: monitor, resolution / FPS / bitrate / codec,
  the codec's description, size target, replay length, readout, capture cursor. The readout follows the slider while it is
  dragged.
- Size targets, in the order of the combo (`Capabilities.SizeTargets`; the ids are what `SizeTarget` stores):

  | id | name | size limit | length limit |
  |----|------|------------|--------------|
  | `none` | None | | |
  | `discord10` | Discord (10 MB) | 10 MB | |
  | `discord50` | Discord Nitro Basic (50 MB) | 50 MB | |
  | `copium200` | Copium (200 MB, 10 min) | 200 MB | 10 min |
  | `discord500` | Discord Nitro (500 MB) | 500 MB | |
  | `copiumplus` | Copium Plus (2 GB, 60 min) | 2 GB | 60 min |

  Sizes are decimal (10 MB is 10 000 000 bytes). As in §3 the bitrate is lowered, never raised, so that a clip fits with 5 %
  headroom; the clip that has to fit is `min(replay length, length limit)`. The note under the readout is shown in the Warn
  colour when no bitrate can make the clip fit, and when the replay length reaches the target's length limit (a longer clip
  has to be trimmed before Share anyway).
- Picking *Copium Plus* also ticks *I have Copium+* (the footer says so): Share goes by that account setting, not by the
  target, so unticking it later is the user's call. It ticks only for a deliberate pick: from the open list (mouse, or F4 /
  Alt+Down, arrows and Enter), or when the user stops on it with the arrow keys on the closed box and the box loses the
  keyboard focus. Stepping through it on the way to another target changes nothing, and focusing the box later never ticks
  a box the user unticked.

### 11.4 Tabs (changes §1, §10)

- Settings tabs: **Capture · Clips · Alerts · App** (the Clip tab is now Clips). When the window is narrower than 900 px the
  clips pane is the last tab, **Library** (it was *Clips*, which would now be two tabs of one name). Its lists are
  **Local · Shared · Trimmed** (Shared was Cloud: it lists what was shared to Copium, *Clips shared to Copium are listed
  here*, even after the local file is gone).
- **Clips**: *Copium* (the token, *I have Copium+*, a hint that names both limits and says what Share does without a
  token) and *Storage* (§11.5). **Alerts**: *Toasts* and *Status dot*. **App**: *Appearance* (§11.8); *Updates* (the card
  moved here from Alerts, controls unchanged: automatic updates, *Run at startup*, the version, *Check now*, *Update*, the
  problem line); and *Settings* (*Saved in* the settings folder, an *Open folder* link, and the banners of §11.9).

### 11.5 Storage and "Archive now…" (changes §9)

- The Storage card has two parts. The **archive location** (path, *Browse…*, *Test*, *Archive now…*) is always enabled.
  **Move clips there automatically** (was *Archive older clips elsewhere…*) switches the background moves, and its rules
  (*older than N days*, *when the local folder exceeds N GB*) are enabled only while it is ticked. *Archive now…* is
  enabled whenever a location is set (tooltip *Set an archive location first* otherwise). A path typed into the box is
  accepted, or refused with its reason, before the dialog opens. The status line under the location reads *Automatic
  moves off · Archive now… moves clips by hand* with the switch off and a location set, and *Automatic moves are on, but
  no rule is set (days / GB)* or *Automatic moves are on, but no archive location is set* with it on.
- *Archive now…* opens the modal **Archive clips** dialog. It needs only a location: not the automatic switch and not a
  rule. It shows where clips go and whether that works (the answer of *Test*: reachable, writable, free space), then two
  choices with counts and sizes, counted off the UI thread: *Everything due by your rules (N clips · X GB)*, with the rule in
  words (*older than 1 day / while the folder is over 100 GB*; *no rule set*, and disabled, when there is none), and *All
  local clips (N clips · X GB)*: every clip still in the clips folder, oldest first, whatever the rules say. *All* is
  preselected when nothing is due. (Why *Archive now* used to seem to do nothing: it only ran the background job early, and
  that moves a clip only when it is older than the days rule or the folder is over the GB rule.)
- *Archive* moves the clips the way the background job does: copy to a `.clinkpart`, verify the length, rename, delete the
  local file, point the library entry at the archive. A progress bar and *Moving 3 of 12 · Copying clip.mp4 (40 %)* show
  where it is. *Cancel*, or closing the window, stops at the next 512 KB chunk, deletes the partial copy and leaves the
  local clip (clips already moved stay moved). A line at the end says what happened (*Moved 12 clips (3.4 GB) to T:\clips ·
  1 in use, try again later*, or *Archive unreachable*, *Moved 2 clips (…) · 3 failed: archive is full (1 MB free)*, …). A
  clip that is open, changed while it was copied, or was saved under 30 s ago is left alone and counted; a later run takes
  it. The copy goes at full speed when no game is detected; while one runs it keeps the gentle pace of the automatic moves
  (a pause of 8 ms per 512 KB, about 60 MB/s), chosen when Archive is pressed. A location that is not a full path, is the
  clips folder, or cannot be reached is refused with the reason.
- `ArchiveService`: `RunManualAsync(scope, throttle, progress, ct)` queues the run on the archive thread, which does one
  thing at a time; the automatic pass gives way to it at its next chunk and runs again about a minute after it. The pure
  `SelectCandidates` picks the clips for the background pass and for the dialog, oldest first: *Due* applies the age and
  size rules (each pick counts as gone from the folder for the next one), *All* takes every non-archived clip in the clips
  folder. A run ends when the location is changed; an automatic one also when the switch is turned off.

### 11.6 Sharing without a token; Copium limits

- **No token.** Share does not end in an error. It opens the **Share window**, a small borderless window, *Share clip*,
  with the clip's file as a card: its thumbnail, name, *size · length* (*190 MB · 1:30*) and *Drag into Discord, a browser
  upload box or a folder*. Dragging the card is an ordinary file drag that allows **Copy only** (the data carries
  `Preferred DropEffect` = Copy), so Explorer copies the clip into the folder it is dropped on and the original stays in the
  library. *Copy file* puts the same file on the clipboard (Ctrl+V in Discord attaches it, in a folder copies it; Ctrl+C in
  the window does the same), *Open folder* shows it in Explorer, *Set up Copium…* brings the main window forward (*Add your
  token under Clips → Copium*) and closes the window; Close, ✕ and Esc close it. One window per clip.
- It opens from Share on a clip row, on the *Clip saved* toast, *Save & share* in the trim editor and the tray's *Share last
  clip*; the footer says *Drag the clip where you want to share it*, the row shows no error line, the toast fades away and
  the trim editor says *Saved <name>: drag it from the share window* and closes. The share-last hotkey, or `--share-last`,
  while the detected game (or any full-screen application the shell reports: busy, Direct3D full screen, presentation
  mode) is in front opens no window over it: the footer and a Clink toast say *No Copium token: open Clink to share the
  file*. A share with a token, and a clip that already has a link, behave as before.
- The window floats above other windows (topmost) so it stays in reach while the drop target is brought forward, has no
  owner (minimizing or hiding the main window does not take it along), opens centred on the monitor under the pointer, and is
  kept out of screen captures like the toasts, its tooltips included, so it never ends up in a clip. It shows no file
  path anywhere. While the detected game has the focus it goes behind the game's window (topmost again once it is
  activated or the game loses the focus), and opened over a game (the game has the focus, or a toast over it was just
  clicked) it opens without taking the focus, at the toasts' position on the captured monitor (*Toast position*, 16 px
  margin). Every colour in it is a theme resource.
- An archived clip is looked up on a worker first, again whenever the window comes forward, and before *Open folder*: the
  card says *Looking in the archive…*, then *archive offline* or *deleted from the archive* with the card, Copy file and
  Open folder disabled when the file cannot be reached, so an offline NAS never freezes the window.
- **Limits.** The share pre-check goes by the account tier (*I have Copium+*), not by the size target: free **200 MB / 10
  min**, Copium Plus **2 GB / 60 min** (Plus used to mean no check at all). A clip over a limit is not uploaded: the row, the
  toast and the trim editor say *Over Copium's free limit (200 MB / 10 min)* or *Over Copium Plus's limit (2 GB / 60 min)*;
  the footer says what the account accepts, how big the clip is and what to do (*trim it, or tick 'I have Copium+' if you
  do*, the second half only without Plus); the toast's button reads *Over free limit* / *Over Plus limit*. A clip exactly at a
  limit goes. (`AppController.ShareRefusalFor`, `Capabilities.CopiumLimits`.)
- **The toast's share button** reads *Share* without a token, *Share to Copium* with one and *Copy link* when the clip is
  already shared; after a failure *Upload failed*, or *Share failed* in the rare case that the share window itself could
  not be opened (*Could not open the share window*).

### 11.7 Player, trim editor and All audio (changes §1)

- A click on a clip (its row or its thumbnail, in Local, Shared and Trimmed) opens Clink's own **player**, the clip window
  in player mode: the preview, play / pause, the position, a plain seek bar (the part already played is filled; a click or
  drag anywhere scrubs; no range, handles or keyframe ticks) and the audio rows with their Listen pills and volume
  sliders; no *Set in / Set out*, include boxes or save row. Playing runs to the end of the clip and goes back to the start;
  Space, ←/→ and Home / End work, I and O do not. The title is the file name, and *Open in default player* in the header
  hands the file to the system's player.
- The **✂ Trim** switch right after Play turns the same window into the **trim editor** (brackets, *Set in / Set out*,
  include boxes, keyframe ticks, the save row; title *Trim · <file>*) and back. Nothing is lost by switching: the selection,
  the volumes and the ticks stay. The *Trim* button on a row, the toast's *Trim* and `--trim-last` open the editor directly.
  The keyframes of the clip are read only when the editor is shown, so playing a clip does not read the whole file. A second
  click on a clip that is already open brings its window forward, in the mode asked for, instead of stacking another;
  clicks in the first 350 ms of a new window are ignored, so a double click on a row does not land its second click on the
  video or a button; a click that began on another row does not play this one.
- **All audio follows the sliders.** Track 1 (the Mix) is baked at record time, so the Game, Mic and Voice sliders used to
  change only their own tracks, never what the Mix contains. A plan (`MixPlan`, pure; the roles come from the track names
  in the file, trusted only for a file Clink wrote, whose video trak is named `Video`) now decides what the preview plays
  and what the export renders:
  - *Rebuild* (the file has Mix, Game, Mic and Voice): All audio = master × Σ (gain of source × source). The Mix is rendered
    again from the three tracks (`Trimmer.RenderTrack` over `PcmTrackReader` blocks: 1024-frame blocks summed at their
    gains, the master gain, the soft clip, AAC; only the current block is held in memory).
  - *Delta* (the Mix and at least one, but not all, of the sources; Voice is not saved as its own track by default):
    All audio = master × (mix + Σ (gain − 1) × source). A turned-down source is taken out of the baked mix again by adding it
    at a negative volume. The mix and the source were AAC-coded separately, so pulling a source to 0 % can leave a faint
    trace of it (a synthetic clip left 0.1 % of the game tone, −57 dB). The editor says which source is only inside All audio
    and cannot be changed, and that taking another one out can leave that trace.
  - With every source at 100 % the baked Mix is used untouched, in the preview and in the file. A source's include box
    decides only whether its own track is saved; its slider always shapes All audio. Desktop and any other track stand
    alone: Desktop is never part of All audio. A clip whose names are not Clink's (a v0.1.0 clip's *All audio*, other
    tools' files) behaves as before. The Mix row is labelled *All audio (mix)* (the file keeps the name `Mix`).
  - The tracks of a Clink file start at different instants (an edit list per track), which Media Foundation ignores, so
    `Trimmer.ReadAudioTracks` reads each track's start offset and the player and the renderer apply it. Plain cuts are
    byte for byte the same file as before.
- Command line (hidden, for checking without a window): `Clink.exe --trim <in> <out> <start> <end> [--gain Name=value]...
  [--drop Name]...` builds the plan the editor would (a value is 0.5 or 50%); `Clink.exe --probe <file> --levels` prints the RMS
  and peak of each audio track.

### 11.8 Themes and the App tab

- Two settings, `Theme` (Dark | Light) and `Accent` (Cyan, Blue, Violet, Pink, Orange, Green): twelve looks. The App
  tab's *Appearance* card has the theme pills and six accent swatches. A choice is saved and applied **at once to every
  window that is open**: the main window, the player and trim editor, the share window, the archive dialog, the toasts, the
  status dot and the owner-drawn controls (monitor map, replay-length slider, timeline). The saved choice is applied at
  start, before any window exists; an unknown id means Dark or Cyan. *System* (follow Windows) is not offered yet (ROADMAP).
- `ThemeManager` builds a dictionary of frozen brushes, one per token plus `BgColor`, and puts it in place of the first merged
  dictionary of the application's resources (`UI/Palette.xaml` holds the default, Dark / Cyan). Every colour reference in
  XAML is a `DynamicResource` (a guard test fails on a `StaticResource` of a palette token) and code that sets a brush sets a
  resource reference, so open windows repaint. Dark is the brand palette (Midnight, Slate, Electric Cyan, Blue Cyan, Cool White).
- Fonts: the interface is *Inter, Segoe UI Variable Text, Segoe UI*; headings and window titles are *Sora, Segoe UI Variable
  Display, Segoe UI*. Nothing is bundled or downloaded, so a machine without Inter and Sora falls back to Segoe UI Variable /
  Segoe UI (ROADMAP). Primary buttons are the accent with `OnAccent` text; secondary buttons are `Panel2` with a `Border`
  outline that turns `Accent2` on hover; toggles are the accent when on and `Panel3` when off; the selected tab (the clips pane's
  too) is filled with the accent; links and the keyboard focus ring use `Accent2`. Contrast is tested: text at least
  4.5:1 on its fill or panel, the accent and `Accent2` on `Panel` at least 3:1.
- Tokens (the same for every accent):

  | token | use | Dark | Light |
  |-------|-----|------|-------|
  | `Bg` (and `BgColor`) | window | `#0A0E14` | `#F4F6FA` |
  | `Panel` | cards, tab strip, toasts | `#1A1F2B` | `#FFFFFF` |
  | `Panel2` | inputs, buttons, combos | `#232A37` | `#EEF0F3` |
  | `Panel3` | hover, slider track, chips | `#2E3645` | `#E2E5EA` |
  | `Border` | outlines | `#2A3240` | `#D3D7DE` |
  | `Fg` | text | `#EAF2FF` | `#0A0E14` |
  | `Muted` | secondary text, ticks | `#8A94A6` | `#5E6571` |
  | `Accent2` | links, focus rings, the played part of the seek bar, a secondary button's hover | `#3B82F6` | `#2563EB` |
  | `Good` | recording, ok | `#5EDB8A` | `#15803D` |
  | `Warn` | warnings; `WarnTint` is Warn at 12 % (banners) | `#F2C94C` | `#A16207` |
  | `Danger` | errors, RAM over half; `DangerTint` is Danger at 44 % (the RAM limit on the slider) | `#F26B6B` | `#C73A3A` |

  Accent sets (`Accent` / `AccentDim` / `OnAccent`):

  | accent | Dark | Light |
  |--------|------|-------|
  | cyan | `#22D3EE` `#1B6F84` `#0A0E14` | `#0891B2` `#BDE6F4` `#0A0E14` |
  | blue | `#3B82F6` `#2F5D9E` `#0A0E14` | `#2563EB` `#C9DAFB` `#FFFFFF` |
  | violet | `#B49CFF` `#5E4B9E` `#0A0E14` | `#7C3AED` `#DDD1FB` `#FFFFFF` |
  | pink | `#F78FC6` `#9E4A78` `#0A0E14` | `#DB2777` `#F8CFE3` `#FFFFFF` |
  | orange | `#FFB15E` `#9E6A2F` `#0A0E14` | `#D97706` `#F8DDBA` `#0A0E14` |
  | green | `#6EE7A0` `#2F8F5B` `#0A0E14` | `#15803D` `#C6EBD3` `#FFFFFF` |

  On Light, `OnAccent` is Midnight for Cyan and Orange because white reads only 3.7:1 and 3.2:1 on them.

### 11.9 Settings kept in a packaged app's private AppData

- Windows keeps a private copy of `%APPDATA%` and `%LOCALAPPDATA%` for an app that runs from a package (the Claude desktop
  app's agents are one). A Clink such an app started kept its settings in
  `%LOCALAPPDATA%\Packages\<package>\LocalCache\Roaming\Clink`, where a Clink started from Explorer or the Start menu never
  looks. (This, not a schema change, is how v0.2.4's settings seemed to be lost.)
- `AppDataRedirect.Detect()` creates a file in the settings folder and asks `GetFinalPathNameByHandle` where Windows really
  put it: opening the folder tells nothing, it exists in the real AppData too and opens as the real one. A redirected Clink
  says once per start *Clink was started from inside Claude: Windows keeps a private copy of its settings for that app, so
  changes made now are not seen by Clink started from Explorer or the Start menu*, and shows it as a banner under *App →
  Settings*. It is not run in a `--dev` instance.
- A **first start** of a real Clink (no `settings.json` or `.bak` yet) that is not itself inside a packaged app takes over
  the newest copy another Clink saved in a package's private AppData, before anything reads the settings: its `settings.json`
  as it is, and its clip index merged into ours (every clip once, ours first; the other copy only fills what ours leaves
  empty: Copium link, page and id, archive time, audio tracks, length, trim source). It says *Imported the settings Clink
  saved while started from inside Claude (2026-10-21 14:32)*.
- A Clink that already has settings is **never changed unasked**. After start it looks, on a worker, for a copy that differs
  (ignoring where the window sits, `LastUpdateCheck`, the updater's state, `AudioSetupSeen`, `SchemaVersion` and which copy
  was turned down; the Copium token is compared decrypted, and nothing is logged but the names of what differs) and offers
  it: one tray balloon (*Settings from another copy of Clink found: right-click the tray icon to import*), the tray item
  *Import settings saved 2026-10-21 14:32 (Claude copy)…* and a banner under *App → Settings* (*Clink also has settings
  saved … by a copy started from inside Claude (video quality, hotkeys, …)* with *Use those settings* and *Dismiss*). The
  tray item asks, listing what differs: *Yes* imports and restarts Clink (`--show`), *No* keeps the current settings and
  stops offering that copy, *Cancel* decides later. Importing freezes settings saves first, so no save that is still pending
  puts the old ones back, and keeps what it replaces as `settings.pre-import.json`. *Dismiss* and *No* write
  `ImportDeclined` (`<package>|<ticks of the time that copy was saved>`, one per package): that copy is offered again only
  once it has been saved again.

### 11.10 Test aids

- `Clink.exe --unittest [filter]`: the in-exe tests (`Clink/Tests`: `[UnitTest]` static methods found by reflection,
  `Check.*`, `TempDir`), run one after the other on the UI thread; the filter is a case-insensitive substring of
  `Class.Method`; exit code 0 means all passed. It never touches `%APPDATA%\Clink`, the single-instance mutex or the pipe
  (`Settings.DirOverride`, `ClipLibrary.IndexPathOverride` and the updates folder point at a scratch folder for the whole run
  and are put back after every test).
- Hidden `Clink.exe --dev [folder] [--dev-hotkeys] [--dev-record] [--dev-token]` (also `--dev=<folder>`) starts a second
  Clink next to the real one: its own data folder (default `%TEMP%\clink-dev`), its own single-instance mutex and control
  pipe (named from a hash of the folder, `.dev.` and eight hex digits, so `--clip` and `--show` only ever reach that
  instance), no updater, no Run-key writes, "Clink (dev)" in the title, tray and toasts, paused unless `--dev-record`, no
  hotkeys bound unless `--dev-hotkeys` (the hook is installed either way, so key capture in the editor works), and no Copium
  token unless `--dev-token`. A fresh dev folder keeps its clips folder inside it, with automatic updates and run at startup
  off; whatever its settings file says, every start and every import keeps the instance away from the real clips folder,
  archive, updates and account (in memory; the log says what changed). A mistake (an unknown `--dev*` switch, `--dev` twice,
  `--dev=` with nothing, a dev flag without `--dev`, a folder that is the real settings folder) is an error, exit code 2,
  before anything starts: never a normal start, which would take the real Clink's mutex or forward to it. Every restart (an
  import, an update) carries `--dev <folder>` and its flags.
- Test only: `--packages-root <fake Packages folder>` (the only place a dev instance looks for another copy's settings,
  never the real one) and `--dev-accept-import` (import what is offered, once). `--selftest` prints where the settings are
  read from, and says so when this process sees a packaged app's private copy.

Not in v0.2.5, on the ROADMAP: a *System* theme that follows Windows, the Inter and Sora fonts bundled with the exe, and a
smaller copy for Discord's 10 MB made in the Share window.

## Settings already defined (do not rename)

`Settings.cs` already carries the v2 keys: `RecordingMode`, `CustomApps`, `Hotkeys` (list of `HotkeyAction { Keys, Action, Seconds }`),
`ToastPosition`, `ToastClip` / `ToastShared` / `ToastRecording` (`ToastSetting { Enabled, Seconds }`), `StatusDot`,
`StatusDotPosition`, `SizeTarget`, `ArchiveEnabled`, `ArchivePath`, `ArchiveAfterDays`, `ArchiveWhenLocalExceedsGB`,
`WindowWidth` / `WindowHeight`. `ClipEntry` has `IsTrim`, `SourcePath`, `ArchivedAt`, `IsShared`, `IsArchived`.
v0.2.5 adds `SchemaVersion`, `Theme`, `Accent` and `ImportDeclined` (§11.1); `Hotkeys[0]` is the main hotkey (§11.2) and
`SizeTarget` also takes `copiumplus` (§11.3).

## Non-goals for this pass

- Screenshot capture, Stream Deck plugin, code signing (see ROADMAP).
