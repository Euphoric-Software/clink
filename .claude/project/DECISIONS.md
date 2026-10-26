# Decisions

## Dev Build update channel
- Dev builds are immutable `vX.Y.Z-dev.N` prereleases (not a rolling tag), pruned to the newest 10.
- Version derivation: X.Y.Z = csproj version if above latest stable, else latest stable patch+1; N = max(run_number, highest existing dev.N + 1).
- The dev channel considers stable releases too; install rule is target > current (dev.N ordered numerically). A stable-channel install never installs a dev build.
- Leaving dev for an older stable release happens only on a manual check.
- `UseDevBuilds` and `PendingUpdateFrom` are per-installation settings and excluded from settings import.
- Clicking the footer version opens a popup ('Dev Build', 'Check for updates') instead of checking immediately.

## Screenshots
- GDI BitBlt capture first, behind `IScreenshotSource`; WGC/HDR source later (S7).
- Per-monitor opaque overlay windows sized in physical pixels (mixed DPI safe).
- Separate subsystem (`Clink.Screenshots`, own history); no ClipEntry reuse.
- Hotkey seeding of the `screenshot` action instead of a settings schema bump.
- Bare PrintScreen is the default screenshot hotkey (user decision).
- Trigger-while-open cancels the overlay (no BringToFront).
- Raster effects sit below vector annotations in v1.
- `BgraImage` has no bitmap cache; `ToBitmapSource()` is always fresh.
- Editor document is an immutable `DocumentState` with snapshot undo/redo (200); export is a separate exact-pixel `Exporter` (RTB at 96 dpi, tiled).
- `RasterComposer` with below-cache and `IRasterOp`/`Adjuster` seams (S3) so S4 plugs in raster tools without touching the editor.
- Pixel ops are `Op(src, dst, clip, ...)`; local ops (blur, highlight, adjustments, resample) recompose dirty rects, non-local (pixelate, smart erase, magnifier, spotlight) invalidate the whole footprint; in-place is single-shot.
- Edit-after never writes until Done/Save; edit-last always saves a new file; one editor at a time.
- Sharpness 0..200 %, default 0, unsharp amount = Sharpness/100.
- S2 was developed in a worktree and merged with `--no-ff`; the worktree branch is kept.
- Adjustment order stays sharpen then LUT (design).
- Freehand highlight is a toolbar toggle (Shift = square).
- Inserted images are stored at <= picture size.
- Interactive spotlight level-rebuild throttle is 120 ms.
- S5 overlay editing: Esc ladder (gesture -> deselect -> tool -> cancel); Esc cancels without a confirm prompt.
- Idle watchdog is 2 min while selecting, 20 min while editing (with a notice).
- `LastEdit` is rebased to the crop after an overlay edit.
- Overlay toolbar is placed in monitor pixels over the full monitor (ToolbarPlacement).
- S6 upload: image endpoint override is pinned to copium.pics and never imported from settings files; returned links must be https; uploads are serialized; the clipboard link replaces the image only if the clipboard is unchanged since the image copy.
- S6 export: compose/encode runs off the UI thread; AI background remover is a seam only (reports unavailable).
- 2026-10-25 product decision: all cloud sharing defaults OFF with a one-time opt-in pop-up warning that uploads are immediately publicly accessible by link; global "Allow cloud sharing" kill switch (two-step re-enable) for clips and screenshots; feature-visibility toggles hide and disable a feature; library pane becomes Clips | Screenshots with Local | Shared beneath and Trimmed as a filter (S7/S8).
- S7 sharing is opt-in: nothing uploads until the user agrees in the Copium consent dialog (public-link warning; Not now is default/Esc; Don't ask again; max two proactive prompts a day apart; never over a game; none without a token). Consent keys are per-install and never imported.
- S7 kill switch: global `Allow cloud sharing` off = no uploads (clips and screenshots), sharing UI hidden, token kept, running uploads cancelled; re-enable needs a confirmation repeating the warning; an imported `off` is honoured, an imported `on` never.
- S8 Delete semantics: screenshot Delete sends the file to the Recycle Bin via SHFileOperation, fixed local drives only; if it cannot be recycled Windows asks explicitly; refused while the editor holds the file.
- S8 shared entries keep their link: deleting a shared screenshot's file leaves the entry and link; "Remove from list" drops the entry only.
- S8 list path rule: only paths passing the screenshots path rule are listed, probed or acted on.
- S8 library view persistence: last feature (Clips/Screenshots) and level (Local/Shared) are remembered; Trimmed is a filter chip, not a tab.
- S7 features: Clips and Screenshots toggles (last one stays on). Clips off stops the recorder, unbinds clip hotkeys and hides clip UI. Copium token card lives on App -> Cloud sharing.
