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
