# Clink v2 UI & features spec

Requested 2026-10-20. This is the brief for the "v2" pass; items are grouped by area. Nothing here changes the
capture/encode pipeline itself.

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
  - **Audio**: the per-track rows with source pickers (already on branch `feature/audio-sources`).
  - **Alerts**: toast settings and the status dot (§8).
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

## Non-goals for this pass

- Screenshot capture, Stream Deck plugin, code signing (see ROADMAP).
