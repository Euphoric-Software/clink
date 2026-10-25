# Clink screenshots: capture + instant editor (design, iteration 1)

Status: design only. Base: `feature/screenshots` = `main` 3f66291 (v0.3.1). Dev channel first (merge to `dev` -> `release-dev.yml`).
Constraints honoured: .NET 10 WPF code-behind, CsWin32 is the only package (all pixel code hand-written), in-exe `[UnitTest]` tests,
PerMonitorV2 (app.manifest:19), PublishSingleFile (Clink.csproj:26), x64 only.

Repo evidence this design leans on (beyond the coordinator's fact sheet):
- `Settings.cs:514` `older = ... _fileSchema < CurrentSchema` drives EnsureHotkeyModel's v0.2 rules; `Settings.cs:419` keeps
  `settings.pre-v3.json` for any file with `_fileSchema < CurrentSchema`; `Settings.cs:336-337` marks Migrated and always writes
  CurrentSchema (an older Clink rewrites a newer number down). => Bumping CurrentSchema to 4 would (a) treat every schema-3 file as
  "older" and re-run the v0.2 main-hotkey promotion rules (can move a user's cleared main), (b) write a misleading
  `settings.pre-v3.json`, (c) re-run any 3->4 step after every downgrade/upgrade round trip. **Do not bump the schema.**
- `Hotkeys.cs:57` onPressed runs on the hook thread; `AppController.cs:349` marshals with `_ui.BeginInvoke(() => OnHotkey(a))`.
- `Hotkeys.cs:199-235` already has `keybd_event` + `MaskTag` dummy-key injection (`MaskAlt`), never while the game is in front.
- `Hotkeys.cs:417` `KeyName(0x2C) = "PrintScreen"`; `IsReservedForCapture` (242-252) does not refuse PrintScreen, so the hotkey
  editor can already capture Ctrl+PrintScreen.
- `AppController.cs:1004` `ActiveHotkeys` binds nothing in a `--dev` instance unless `--dev-hotkeys`: new actions inherit this.
- `DevOptions.cs:317-327` `Isolate` re-points ClipsFolder/ArchivePath into the dev folder: a new folder setting must join it.
- `SettingsImport.cs:289-307` label table; unlisted keys become "other settings"; `IsVolatile` (331-335) for flags like AudioSetupSeen.
- `ClipLibrary.cs:90` / `ArchiveService.cs:1103` scan only top-level `*.mp4`: a `Screenshots` subfolder of ClipsFolder is invisible
  to the library and the archiver (safe default location).
- `ToastWindow.xaml.cs:232` toasts exclude themselves from capture; `CaptureExclusion.cs` handles their tooltips.
- `MonitorCapture.cs:109-126` borderless request (needs IsBorderRequired, i.e. Win11 / Server 2022+; csproj min is 19041 => Win10
  users would get a yellow border flash from any WGC one-shot).
- `Monitors.cs:11-24` MonitorInfo is physical pixels incl. IsHdr/SdrWhiteNits; the sample label "AW2726DM" suggests the user owns an
  HDR QD-OLED, so HDR behaviour is a first-class concern, not an edge case.
- `TestRunner.cs:10-19` tests run on the UI thread (STA), so WPF `RenderTargetBitmap` / `BitmapSource` tests are possible in-exe.
- `CopiumClient.cs:19` error text says tokens come from "ShareX & API" on copium.pics: suggestive (not proof) that images are accepted
  somewhere; the upload host `v.copium.pics` may be video-only.

---------------------------------------------------------------------------------------------------------------------------------

## A1. Screenshot source: GDI BitBlt now, WGC one-shot later behind `IScreenshotSource`

| | GDI BitBlt (screen DC -> DIB section) | WGC one-shot (CreateForMonitor, 1 frame) |
|---|---|---|
| Latency | synchronous, ~5-25 ms per 4K monitor | item+pool+session + first frame: 40-200 ms per monitor, plus staging readback |
| HDR monitor | DWM hands GDI an 8-bit SDR conversion: SDR desktop content roughly right (SDR brightness slider may not match what you see), HDR game/video content above SDR white is hard-clipped (blown, flat highlights) | FP16 scRGB; tone-map with the same knee/peak as clips (HdrKnee/HdrPeak) -> matches recorded clips |
| Exclusive-fullscreen (non-FSO) game | black or stale image | works (MonitorCapture header) |
| Borderless/FSO game | works | works |
| Cursor | not included (would need DrawIconEx) | IsCursorCaptureEnabled |
| Yellow border | none | flash on Win10 (no IsBorderRequired) |
| WDA_EXCLUDEFROMCAPTURE windows | excluded | excluded |
| New code | ~120 lines CsWin32 GDI | new tone-map-to-BGRA compute shader + staging/Map (no CPU readback exists today) + per-adapter devices |

Decision:
- Slice 1 ships `GdiScreenshotSource` for every monitor. No cursor in v1 (ShareX default is also off; add later via the seam).
- Keep a seam: `IScreenshotSource` chosen per monitor. A later `WgcScreenshotSource` is used only for monitors with `IsHdr`
  (and optionally as a fallback when GDI returns an all-black monitor). One interface; worth it because the user's monitor is very
  likely HDR.
- HDR consequence (state in dev release notes): "On an HDR monitor, bright HDR game highlights are clipped in screenshots for now."
  The S1 manual test explicitly checks this on the user's HDR monitor; if unacceptable, WGC-HDR (S7) is pulled into this iteration.
  (Exact GDI-on-HDR appearance varies by Windows build: assumption to verify, not a known fact.)
- All-black detection: `FrozenMonitor.LooksBlack(rect)` (sampled 64x64 grid, all RGB <= 2). If the selected crop is all black, toast:
  "Screenshot is black - exclusive fullscreen? Use borderless". Pure, tested.
- GDI details: `GetDC(HWND.Null)`, `CreateCompatibleDC`, `CreateDIBSection` (32 bpp, top-down: negative biHeight), `SelectObject`,
  `BitBlt(mem, 0,0,w,h, screen, mon.Left, mon.Top, SRCCOPY | CAPTUREBLT)`, copy bits, **force alpha = 255** (GDI leaves it 0 -> a
  transparent PNG otherwise), release everything in `finally`. One capture per monitor (no holes for non-rectangular layouts).
  PMv2 process => screen-DC coordinates are physical; MonitorInfo is physical: no conversion. CAPTUREBLT can flicker the cursor on
  some drivers (verify in S1; drop it if so - DWM composition already includes layered windows).
- Capture all monitors, always (the overlay shows a frozen image on every monitor). Re-enumerate `Monitors.Enumerate()` at capture time
  (the cached `AppController.Monitors` can be stale after a display change; enumeration is a few ms).

NativeMethods.txt additions (S1): `GetDC, ReleaseDC, CreateCompatibleDC, DeleteDC, CreateDIBSection, SelectObject, BitBlt, BITMAPINFO,
BITMAPINFOHEADER, DIB_USAGE, ROP_CODE, GetCursorPos, SetCursorPos, GetDpiForMonitor, MONITOR_DPI_TYPE` (DeleteObject, SetWindowPos,
GetDpiForWindow, MonitorFromPoint already present). CsWin32 maps POINT to `System.Drawing.Point` (ToastWindow.xaml.cs:368): fully
qualify; `System.Drawing` / `System.Windows.Forms` are not global usings (csproj:51-52).

## A2. Region overlay

Decision: **one topmost borderless opaque WPF window per monitor** (`RegionOverlayWindow`), sharing one `RegionSelection` model.
Why not one virtual-screen window: under PMv2 a window has one DPI, so toolbar/labels are mis-sized on other monitors; the window gets
WM_DPICHANGED as it straddles monitors; a 3x4K span nears WPF's 16k surface limit; irregular layouts leave dead areas.

Window recipe (each item verified in the S1 manual test):
- `WindowStyle=None, ResizeMode=NoResize, ShowInTaskbar=False, Topmost=True, AllowsTransparency=False` (opaque: no layered-window
  software path), `Background=Black`, `UseLayoutRounding=True`.
- Placement: in `SourceInitialized`, `SetWindowPos(HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, SWP_NOACTIVATE)`; if the
  move changed DPI (WPF rescales on WM_DPICHANGED) set the exact rect again (two-pass like `ScreenPlacement.Place`,
  ToastWindow.xaml.cs:431). Assert `GetWindowRect == monitor rect`; log a mismatch.
- Frozen image: `BitmapSource.Create(w, h, 96*scale, 96*scale, Bgra32)` frozen, in an `Image Stretch=Fill` sized to the window,
  `RenderOptions.BitmapScalingMode=NearestNeighbor` => exactly one image pixel per physical pixel.
- `ICaptureExcludedWindow` + `ExcludeFromCapture` (a replay being recorded shows the live game, not the frozen overlay).

Coordinates: **physical virtual-screen pixels end to end** (`RectI` in the model and the PNG). Convert only at the window edge:
`dipX = (px - mon.Left) / scale`. Pointer position is read with `GetCursorPos` (physical) on every move instead of WPF's
`e.GetPosition` (no DIP round-trips once the pointer leaves the capturing window for a monitor with another DPI).

Cross-monitor selection: supported for modes none/after. A drag starts on any overlay window (which takes `Mouse.Capture`); the model
holds the global rect; every overlay draws `selection intersect monitor`. Export composes the crop from each monitor's frozen pixels
(`FrozenDesktop.Compose(rect)`; gaps outside all monitors = transparent).

Input map:
- Left drag: rubber band (min 3x3 px, else a click = clear selection). Shift = square (cheap).
- Release (modes none/after): commit at once. (Mode before: enters editing, see A4.)
- Enter / Space with no selection: whole monitor under the cursor. Enter with a selection: commit.
- Esc: cancel everything. Right-click: clear the selection being made, else cancel (ShareX behaviour).
- Arrow keys: move the pointer 1 px (Shift 10 px) via SetCursorPos for precise starts (keyboard accessibility).
- Visuals: dim layer (60% black) = `Path` with EvenOdd geometry (monitor rect minus selection), 1 px accent border, size label
  "1280 x 720" (physical px) above-left of the selection (inside if no room), optional crosshair lines.
- Loupe (`ScreenshotLoupe`, default on): 128x128 DIP box near the cursor, 8x NearestNeighbor zoom via `ImageBrush.Viewbox` over the
  frozen bitmap (no copies), centre-pixel outline, caption "x, y  #RRGGBB"; flips side at monitor edges.

Guaranteeing a clean frozen frame:
1. `OnHotkey` (UI thread, via BeginInvoke from the hook) -> `ScreenshotService.CaptureRegion(trigger)`.
2. Re-entrancy guard: an overlay or capture already in progress -> bring the overlay forward, return.
3. From the tray menu: `await Task.Delay(250)` first (the ContextMenuStrip fades out and is not capture-excluded).
4. `await Task.Run(() => FrozenDesktop.CaptureAll(...))` - BitBlt is thread-agnostic; nothing of ours is created before it returns.
   Toasts/share window/tooltips are WDA-excluded anyway; a visible main window is captured on purpose.
5. Only then construct and show the overlay windows.
Focus: overlays need the keyboard (Esc/Enter). `ShowActivated=True` + `Activate()`; if `GetForegroundWindow()` is not ours and the
detected game is not in front, inject the tagged dummy key (new `Hotkeys.GrantForeground()` reusing MaskAlt's keybd_event + MaskTag),
then `SetForegroundWindow`. Over a game: no injection (anti-cheat rule, Hotkeys.cs:289); the first click activates the overlay and
right-click cancel always works. Exclusive-fullscreen games drop out of exclusive mode when the topmost overlay appears (documented).

## A3. Editor document model and rendering

Coordinates: document pixels = base-image pixels (doubles for vectors, `RectI` for raster objects). Widths/sizes are in document
pixels (WYSIWYG at 100%); handle sizes and hit tolerance are in screen DIPs (zoom-independent).

Model (immutable records; `System.Collections.Immutable` is in the shared framework):
```
record DocumentState(BgraImage Base, ImmutableList<EditorObject> Raster, ImmutableList<EditorObject> Vector,
                     RectI Crop, Adjustments Adjust);      // Crop = full image in edit-after; = selection in edit-before
record Adjustments(int Brightness, int Contrast, int Sharpness);   // -100..100, -100..100, 0..200 (%)
abstract record EditorObject(int Id);
// vector band (drawn by WPF, always above the raster band)
record PenStroke(int Id, ImmutableArray<Point> Pts, Color Color, double Width)
record LineShape(int Id, Point A, Point B, Color Color, double Width, bool ArrowAtEnd)   // straight line / straight arrow
record FreehandArrow(int Id, ImmutableArray<Point> Pts, Color Color, double Width)
record RectOutline(int Id, Rect R, Color Color, double Width)
record TextNote(int Id, Point Origin, string Text, double Size, Color Fill, bool Bold, TextBackdrop Backdrop, Color BackdropColor)
// raster band (CPU BGRA; each op samples the composite of everything below it in the raster band)
record BlurRegion(int Id, RectI R, int Radius, bool Pixelate, int Block)
record HighlightRect(int Id, RectI R, Color Color)
record HighlightStroke(int Id, ImmutableArray<Point> Pts, double Width, Color Color)
record SpotlightBox(int Id, RectI Box, double Dim, int Radius, bool Gradient, int FalloffPx)
record MagnifyBox(int Id, RectI Source, RectI Dest, Color Outline, double OutlineWidth, bool Smooth)
record SmartEraseRect(int Id, RectI R)
record InsertedImage(int Id, BgraImage Image, RectI Dest, double Opacity)
record BackgroundMask(int Id, AlphaMask Mask)               // AI seam (A9), unused in v1
```
Z-order (decision for v1): **two bands**. Raster-band objects (images, blur, highlight, spotlight, magnify, smart erase) compose in
list order and each samples everything below it *in the raster band*. Vector-band objects always draw above the whole raster band.
Accepted consequences (tooltip + docs): blur/pixelate/spotlight/magnify never affect pen/arrows/text; an inserted image can be
blurred; bring forward/backward works within a band. Rationale: no vector rasterization per effect, simple caching, deterministic
tests. Alternative B (unified z-order: rasterize vectors under each effect with RenderTargetBitmap) remains possible later; the
ordered-list model does not block it.

Rendering:
- `RasterComposer` (pure, CPU, `Parallel.For` over rows): `Base -> Adjusted (cached per Adjustments) -> ops in order -> Composite`.
  Displayed through one `WriteableBitmap` (Bgra32) updated with `WritePixels(dirtyRect)`.
- Vector band: one `DrawingVisual` per object hosted in a `FrameworkElement` with a `VisualCollection` (only changed objects
  re-render; WPF geometry hit testing available). Selection handles in a separate adorner visual on top.
- Export (`Exporter`): (1) `Composite` cropped to `Crop` (exact CPU pixels); (2) if any vector objects: render the vector band into a
  crop-sized `RenderTargetBitmap(w, h, 96, 96, Pbgra32)` with `TranslateTransform(-crop.X, -crop.Y)`, `TextRenderingMode=Grayscale`
  (ClearType on a transparent target fringes), `TextFormattingMode=Ideal`; (3) CPU premultiplied-over blend of (2) onto (1).
  The resulting `BgraImage` is cached for copy/save/upload (export once per commit). Crops over 8192 px on a side are rendered in
  4096-px strips (multi-monitor spans; keeps RTB allocations bounded).
- Editor zoom: `Zoom` = physical pixels per image pixel; content transform scale = `Zoom / dpiScale`, so 100% is truly 1:1 on
  high-DPI. `BitmapScalingMode`: HighQuality when Zoom < 1, NearestNeighbor when >= 2, Linear between.

Undo/redo: `UndoStack<DocumentState>` (snapshot/memento; immutable states share structure; base image shared by reference). Push on
gesture end (mouse up, text commit, slider release); drags and slider moves only change a transient preview state. Limit 200.
Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z. Crop and adjustments are state fields, so they undo like everything else.

Selection / move / resize / delete:
- Select (V) is the default tool; a newly drawn object is auto-selected (ShareX-like); Esc deselects.
- Hit testing, topmost first: crop handles (edit-before) > selection handles > vector band reversed > raster band reversed. Vectors:
  point-to-polyline distance <= width/2 + 6 DIP / zoom (pure `HitTest`); text/rect: bounds; raster: bounds (magnify: Dest or Source).
- Handles: 8 for rect-like objects (blur, highlight rect, spotlight, magnify dest/source, smart erase, image, rect outline); 2 endpoints
  for line/arrow; freehand pen/arrow/highlight and text: move only. Shift keeps aspect (images keep aspect by default).
- Delete/Backspace deletes; arrows nudge 1 px (Shift 10); Ctrl+D duplicates; PageUp/PageDown reorder within the band.
- Tool keys: V select, P pen, A arrow (freehand), Shift+A straight arrow, L line, R rectangle, T text, B blur, X pixelate, H highlight,
  S spotlight, M magnify, E smart erase, I insert image, C crop. Disabled while the text box has focus.

Highlight (multiply): raster op. Mask: rect = 1 inside; freehand = own CPU capsule rasterizer `StrokeMask.Rasterize(points, width,
bounds)` -> 8-bit coverage, combined with **max** (self-overlaps don't double-darken), 1-px AA ramp. Per channel:
`out = src * lerp(255, hl, m) / 255` (m = coverage 0..1). Black text stays black; white paper becomes the highlight colour. Default
#FFEB3B, freehand width 18 px. CPU rasterizer chosen over RenderTargetBitmap: thread-safe, deterministic, unit-testable.

Blur / pixelate: Gaussian approximated by 3 box passes (radii from sigma, Kovesi), separable running sums (O(n), radius-independent),
edge clamp. Pixelate = block means (default block 12 px). Reads `R inflated by 3*radius` from the below-composite.

Spotlight: inside the box untouched; outside blurred + darkened by `Dim` (default 0.55) with a 2-px feather on the box edge.
- Fixed blur: one blurred level L(Radius) of the below-composite.
- Gradient blur: K = 5 levels, L0 = original, L1..L4 at radius Radius*k/4, built incrementally (each from the previous with
  sigma_inc = sqrt(s_k^2 - s_(k-1)^2)); levels with sigma >= 6 computed at half resolution and upsampled bilinearly. Per pixel:
  `d` = Euclidean distance to the box (dx = max(x0-x, 0, x-x1), dy likewise), `t = clamp(d / FalloffPx, 0, 1)`, `f = t * 4`, lerp
  between levels floor(f) and ceil(f). FalloffPx default = 35% of the shorter image side.
- Cost estimate (4K, 8 cores): levels ~120-200 ms once per source version; per-pixel blend ~15-25 ms. **Levels do not depend on the
  box**, so moving/resizing a spotlight only re-blends: interactive at full resolution. Each op logs its time; budget < 300 ms.

Magnify: raster op. Sample the below-composite `Source`, scale into `Dest` (zoom = Dest/Source, default 2x, aspect locked),
NearestNeighbor by default (crisp pixels), bilinear when `Smooth`; outline = CPU-filled border ring (default 3 px white + 1 px dark
outer ring). A dashed source frame shows only while selected (not exported). Vector annotations are not magnified (v1).

Smart erase - precise spec, `SmartErase.Fill(BgraImage img, RectI r, int ring = 3, int tolerance = 12)`:
1. Clip `r` to the image; empty -> no-op. Ring = `r` inflated by `ring`, minus `r`, clipped to the image. A side is present when at
   least one ring row/column of that side is inside the image.
2. Uniformity: per channel compute P10 and P90 over all ring pixels; if `max_c(P90 - P10) <= tolerance` fill `r` with the per-channel
   median, alpha 255. Done.
3. Else build 1-D boundary profiles `T(x)`, `B(x)` (x in r: mean over that side's ring rows at column x) and `L(y)`, `R(y)`; filter each
   with a 1-D median of width 5 then a box of width 3 (text strokes / noise crossing the ring do not streak into the fill).
4. Interpolate by available sides: 4 -> bilinearly blended Coons patch
   `P(u,v) = (1-v)T(u) + vB(u) + (1-u)L(v) + uR(v) - [(1-u)(1-v)C00 + u(1-v)C10 + (1-u)vC01 + uvC11]`,
   u = (x - x0 + 0.5)/w, v = (y - y0 + 0.5)/h, corners = mean of the two profile endpoints meeting there;
   3 -> linear between the complete opposite pair; 2 opposite -> linear between them; 2 adjacent -> inverse-distance weighting
   (w = 1/d^2); 1 -> extrude that side; 0 (rect = whole image) -> median of the rect's own 1-px border. Clamp 0..255, alpha 255.
Testable properties: uniform ring -> exact colour; any linear gradient a+bx+cy with 4 sides -> max error <= 1; rect touching the top
edge with a horizontal gradient -> <= 1; a 1-px dark line crossing the ring -> deviation <= 6; deterministic; never writes outside `r`.
(ShareX's start-pixel fill fails all but the first property.)

S2 implementation notes (pixel library, `Clink/Screenshots/Imaging`):
- Smart erase profiles are MEDIANS across the ring depth (not means), run over the ring's corner pixels past the rectangle (median 5,
  then box 3) and are then cut back to the rectangle; corner values are medians of the ring's corner blocks. A fully transparent
  ring (or interpolated alpha ~0) yields (0, 0, 0, 0), not opaque black.
- Spotlight levels: from 4 MP up, ALL blurred levels are kept at half resolution; below that only levels with sigma >= 6.
- Final signatures: every raster op is `Op(src, dst, clip, ...params)` (reads src, writes only clip inside its footprint).
  `Spotlight.Blend(src, dst, clip, levels, box, dim, falloffPx)`; `SpotlightLevels` holds the blurred levels only (level 0 /
  radius 0 is `null` = the caller's src) and exposes them read-only, with an optional caller-set `Version` stamp echoed in Blend's
  argument errors.
- In place (`dst == src`) is single-shot only: `BoxBlur.Blur`, `Adjustments.Unsharp` and `SmartErase.Fill` of a whole-image rectangle
  throw `ArgumentException` unless `clip` covers the whole region. Dirty-rect recomposition must use a separate dst.
- LOCAL ops (a dirty sub-rectangle can be recomposed alone): BoxBlur (margin = sum of the radii), Highlight, Adjustments, Resample.
  NON-LOCAL ops (invalidate the whole footprint): Pixelate, SmartErase, Magnifier, Spotlight.
- `Resample.Over` composites onto dst: dst must already hold the pixels below inside the clip; it is not idempotent.
- `PlanePool` (blur float planes) keeps at most 6 planes and 160 MB; `PlanePool.Clear()` on editor close also drops planes still
  rented from before it.
- Sharpness mapping (pinned): Sharpness 0..200 (%), default 0 = no change, unsharp amount = Sharpness / 100 (sigma 1.5);
  Brightness and Contrast -100..100 straight into the lookup table.

Inserted image: WPF `BitmapDecoder` + `FormatConvertedBitmap(Bgra32)` (file), clipboard `GetImage` / "PNG" stream (paste), or a history
PNG; refused above 16384 px per side or 100 MP (toast). Resampled into `Dest` (bilinear; box prefilter when shrinking > 2x),
straight-alpha over, optional opacity.

Adjustments (base only): brightness/contrast as one 256-entry LUT per channel (contrast factor `f = 259(c+255) / (255(259-c))`, c scaled
to -255..255; brightness offset b*2.55), sharpness = unsharp mask `out = src + a*(src - blur_1.5px(src))`, clamped. Order: sharpen, then
LUT. Live preview: LUT synchronously while dragging (~10 ms at 4K), unsharp debounced 60 ms on a background task; result applied only
if its version is still current.

Freehand arrow head: resample the stroke by arc length (2 px); window = last `D = clamp(3 * headLength, 12 px, 40% of stroke length)`;
direction = principal axis (largest eigenvector of the 2x2 covariance) of the window's points, oriented from the window's first point to
its last. Stroke shorter than headLength -> first-to-last direction; total length < 2 px -> no head (a dot). Displayed shaft = Chaikin
smoothed (2 iterations) polyline cut at `L - 0.8 * headLength` so it doesn't poke through the head; head = filled triangle, length
max(10, 4.5 * width), half-angle 25 degrees. Straight arrows reuse the same head function.

Text UX: click with Text -> inline `TextBox` (transparent, Segoe UI, size/colour from the toolbar, LayoutTransform = view scale) at the
click; type; Enter commits (Shift+Enter newline); clicking elsewhere or switching tool commits; Esc cancels a new empty note or reverts
an edit. Double-click a note with Select to edit it. Backdrop: none / box (rounded, colour) / outline (2 px dark stroke from
`FormattedText.BuildGeometry`).

Performance plan: `Parallel.For` per row in every op. Incremental composition with a **below-cache**: while object k is selected or
being drawn, cache `Composite(0..k-1)`; each change recomputes only `dirty = (old bounds + new bounds) inflated by the op's read margin`
for op k and the ops above it that intersect `dirty`; global ops (spotlight) re-blend fully but reuse cached levels. Heavy work runs in
`Task.Run` tagged with an increasing version; stale results are dropped; at most one compute in flight, coalesced on
`CompositionTarget.Rendering`. Memory (4K): base + adjusted + composite + below-cache + WriteableBitmap ~ 165 MB, + half-res spotlight
levels ~ 35 MB; dual-4K spans double it. On editor close drop every reference and run one
`GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect()` (Clink is a long-running tray app).
Correctness guard: random edit sequences -> incremental composite byte-identical to a from-scratch compose (unit test).

## A4. One editor surface, two hosts

Shared component boundary (`Clink.UI.Screenshots`):
- `EditorSession` (no visuals): owns `DocumentState` (+ transient preview state), `UndoStack`, `CurrentTool`, `ToolOptions` (colour,
  width, font size, blur radius, pixelate block, highlight colour, spotlight dim/gradient, magnify zoom; persisted to
  `Settings.ScreenshotEditor` on change), `RasterComposer`, `Exporter`; commands Undo/Redo/Delete/Duplicate/SetTool/Commit/Cancel/
  CopyImage/Save/Upload; events `StateChanged`, `CommitRequested`, `CancelRequested`.
- `EditorCanvas : FrameworkElement`: renders a session (WriteableBitmap + vector visual host + adorners + inline TextBox), handles mouse
  and keyboard per tool, exposes `ViewTransform` (doc px -> element DIP, set by the host) and `ShowCropHandles`.
- `EditorToolbar : UserControl` (XAML, Theme/Palette DynamicResources, Segoe Fluent Icons; `AutomationProperties.Name` and a tooltip
  with the shortcut on every button; keyboard focusable): tool radio buttons, an options flyout for the current tool, undo/redo,
  actions (Copy, Save, Upload, Done); a `Compact` look for the overlay.
Hosts:
- (a) Edit-before (`RegionOverlayWindow`, mode "before"): on mouse-up of the rubber band, the overlay of the monitor holding the
  selection creates an `EditorSession` with `Base = that monitor's frozen image` and `Crop = selection` (monitor-local px), adds an
  `EditorCanvas` covering the window (ViewTransform = 1/scale, `ShowCropHandles = true`, crop handles have hit priority) and a floating
  `EditorToolbar` placed by pure `ToolbarPlacement.Place(selection, toolbarSize, workArea)`: below the selection, else above, else
  inside at the bottom; left-aligned, clamped to the work area. Annotations may extend outside the crop (clipped on export). Enter or
  Done commits; Esc cancels the capture. Other monitors' overlays stay dimmed and inert.
  **Selection spanning monitors in mode "before"**: no in-overlay editing; close the overlays and open the standalone editor with the
  composed crop (edit-after behaviour), with a one-line hint in the editor header.
- (b) `ScreenshotEditorWindow` (edit-after, edit-last, toast Edit): TrimWindow-style chrome (borderless, header + IconButton close,
  bottom bar with AccentButton "Done"), toolbar on top, zoom container (fit by default, 100% if it fits; Ctrl+wheel zooms at the
  cursor, Space/middle-drag pans, Ctrl+0 fit, Ctrl+1 100%). Opens centred on the monitor of the capture. Closing with unsaved edits asks
  "Save edits?" (Save / Discard / Keep editing). `Base = composed crop`, `Crop = full`; the Crop tool edits `Crop` (non-destructive,
  undoable). Not capture-excluded (it is a normal app window).

## A5. Output pipeline and history

Namespace layout (no `Capture` sub-namespace: inside `Clink.Screenshots.*` it would shadow `Clink.Capture`):
```
Clink\Screenshots\Sources\   IScreenshotSource.cs, GdiScreenshotSource.cs, FrozenDesktop.cs (FrozenMonitor), [S7] WgcScreenshotSource.cs
Clink\Screenshots\Imaging\   BgraImage.cs, RectI.cs, BoxBlur.cs, Pixelate.cs, Highlight.cs, StrokeMask.cs, SmartErase.cs, Spotlight.cs,
                             Magnifier.cs, Adjustments.cs, Resample.cs, Blend.cs
Clink\Screenshots\Geometry\  SelectionMath.cs, ToolbarPlacement.cs, ArrowGeometry.cs, StrokeSmoothing.cs, HitTest.cs
Clink\Screenshots\Editing\   DocumentState.cs (+ object records), UndoStack.cs, RasterComposer.cs, Exporter.cs
Clink\Screenshots\           ScreenshotService.cs, ScreenshotNaming.cs, ScreenshotHistory.cs, ScreenshotClipboard.cs, CopiumNudge.cs, PngFile.cs
Clink\Screenshots\Ai\        IBackgroundRemover.cs (seam only)
Clink\UI\Screenshots\        RegionOverlayWindow.xaml(.cs), EditorSession.cs, EditorCanvas.cs, EditorToolbar.xaml(.cs),
                             ScreenshotEditorWindow.xaml(.cs), InsertImagePicker.xaml(.cs)
Clink\Services\AppController.Screenshots.cs  (partial, like AppController.Recording.cs: hotkey/tray/CLI glue, owns ScreenshotService)
Clink\Tests\                 ScreenshotImagingTests.cs, ScreenshotGeometryTests.cs, ScreenshotEditorTests.cs,
                             ScreenshotServiceTests.cs, ScreenshotSettingsTests.cs
```
SDK globbing picks up new files: Clink.csproj needs no change.

Key interfaces:
```
interface IScreenshotSource { string Name { get; } bool Handles(MonitorInfo m); FrozenMonitor Capture(MonitorInfo m); }
sealed record FrozenMonitor(MonitorInfo Monitor, BgraImage Image, string SourceName) { bool LooksBlack(RectI localRect); }
sealed class FrozenDesktop {
  IReadOnlyList<FrozenMonitor> Monitors; DateTime CapturedAt; RectI VirtualBounds;
  static FrozenDesktop CaptureAll(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<IScreenshotSource> sources);  // first that Handles()
  BgraImage Compose(RectI virtualRect); FrozenMonitor? MonitorAt(int x, int y);
}
sealed class BgraImage { int Width, Height; byte[] Pixels; int Stride => Width * 4;   // straight BGRA, row-major, no padding
  BgraImage Clone(); BgraImage Crop(RectI r); BitmapSource ToBitmapSource(double dpi = 96); static BgraImage From(BitmapSource s); }
enum ScreenshotMode { None, Before, After }      // settings strings "none" | "before" | "after"
enum ScreenshotTrigger { Hotkey, Tray, Cli }
sealed class ScreenshotService {                 // UI thread
  void CaptureRegion(ScreenshotTrigger trigger);
  void EditLast();
  Task<ScreenshotEntry?> CommitAsync(BgraImage final, ScreenshotOrigin origin, DocumentState? doc);
  Task<bool> UploadAsync(ScreenshotEntry e);      // S6
  bool IsBusy { get; }
}
```
Commit pipeline (`CommitAsync`): overlays closed first (instant feedback) -> `Task.Run` PNG encode (`PngBitmapEncoder`, frozen
BitmapSource) to `<name>.png.partial` -> `File.Move` to the final name (no half files) -> `ScreenshotHistory.Add` -> UI thread:
clipboard image (if `ScreenshotCopyImage`) -> toast -> (S6) upload if `ScreenshotUpload` and a token -> on success copy the link (if
`ScreenshotCopyLink`; replaces the image on the clipboard, ShareX behaviour) and update the toast in place. `ClickSound.Play` when
`Settings.ClickSound`. Every step logs its duration (`screenshot: capture 18 ms, encode 240 ms, ...`).

Folder: `ScreenshotsFolder` (string?, null = `<ClipsFolder>\Screenshots`, resolved at use, created on demand). Safe from the library
and the archiver (top-level `*.mp4` only).

Naming (`ScreenshotNaming`, pure): `"{Game or 'Screenshot'} yyyy-MM-dd HH-mm-ss.png"`, the detected game only when the crop overlaps the
recorded monitor and `CurrentGame` is set; invalid filename chars -> '_', trimmed, max 60 chars, reserved device names (CON, NUL,
COM1...) prefixed with '_', no leading/trailing dots; uniqueness `" (n)"` as in Engine.cs:557-579. Edit-last saves a **new** file
(original untouched; history entry gets `EditedFrom`).

History (`ScreenshotHistory`): `<Settings.Dir>\screenshots.json` (so the test runner's scratch DirOverride and `--dev` folders isolate it
automatically), written with `AtomicFile`; entries `{ Path, Created, Width, Height, Size, Game?, CopiumLink?, CopiumPageUrl?,
CopiumId?, EditedFrom? }` + `[JsonExtensionData]`; newest first, cap 500, missing files pruned on load; unreadable or missing json ->
rebuilt from a folder scan of `*.png` by mtime. In-memory `LastEdit` (document state + path + file mtime): edit-last reopens the live
document (objects still editable) when it is the newest entry and the file is unchanged; otherwise it opens the flattened PNG. No
history UI in this iteration beyond "recent screenshots" in the insert-image picker (last 24 thumbnails, decoded at 160 px with
`DecodePixelWidth`).

ScreenshotClipboard: UI thread only. `DataObject` with "PNG" (MemoryStream of the encoded PNG) + `DataFormats.Bitmap`
(BitmapSource); `Clipboard.SetDataObject(data, copy: true)` (flushed: survives exit), up to 5 retries x 60 ms on `COMException`
(CLIPBRD_E_CANT_OPEN); failure -> toast subtitle "Saved (clipboard busy, not copied)". Never from the hook thread (Hotkeys.cs:16-18).
The existing `CopyToClipboard(string)` (AppController.cs:1329) is reused for links. FileDrop not included by default (open question).

Toast (`Toasts.ShowScreenshot(ScreenshotEntry e, ScreenshotToastState s)`, kind "screenshot", own switch `ToastScreenshot`
{Enabled, 6 s}): title "Screenshot saved" (later "Screenshot saved · link copied"), subtitle "1280 x 720 · copied to clipboard",
small thumbnail (click opens the PNG). Buttons: **Edit** (S3), **Copy link** / **Upload** / "Uploading 42%" (S6), **Folder**
(explorer /select, `OpenFolder(path)`). Upload failure: reason in the subtitle (wrapping, as Share_Click does), button "Retry".
Problems ("Screenshot not saved: disk full") go through `ShowProblem`. The toast stays capture-excluded like all toasts.

Copium nudge (pure `CopiumNudge.ShouldShow(hasToken, uploadEnabled, dismissed, shownCount)`): only without a token, with upload
enabled, not dismissed, shown fewer than 3 times. Rendered as one line in the screenshot toast: LinkButton "Link Copium for share
links" (-> `ShowCopiumSetup()`, AppController.cs:1314) and a small "Don't show again" (sets `CopiumNudgeDismissed`). Never a dialog,
never blocks; the Screenshots tab carries a static hint instead.

## A6. Settings, hotkeys, CLI, tray

New settings (Settings.cs "v0.4 screenshots" block; **no CurrentSchema change**):
| Key | Type / default | Notes |
|---|---|---|
| ScreenshotsFolder | string? null | null = `<ClipsFolder>\Screenshots`; DevOptions.Isolate: outside the dev folder -> null |
| ScreenshotMode | "none" (recommended; open question) | Normalize: unknown/blank -> "none" |
| ScreenshotCopyImage | bool true | |
| ScreenshotUpload | bool true | acts only with a token |
| ScreenshotCopyLink | bool true | link replaces the image after upload |
| ScreenshotLoupe | bool true | |
| ToastScreenshot | ToastSetting {true, 6} | Normalize: null -> default |
| ScreenshotEditor | ScreenshotEditorOptions + JsonExtensionData | last-used tool options; Normalize clamps |
| SeededHotkeyDefaults | List<string> empty | actions whose default binding was offered once |
| CopiumNudgeDismissed / CopiumNudgeShown | bool false / int 0 | IsVolatile |
| CopiumImageUploadUrl | string? null | S6 hidden override: https on copium.pics or a subdomain only (the token goes with the request); this machine's own key, never imported |
| CopiumAutoUploadNoticeShown | bool false | the one-time "uploaded automatically" notice was shown; IsVolatile |
| ScreenshotEditorExcludeFromCapture | bool true | hidden, no UI: the editor window and its picker are kept out of capture; IsVolatile |

Hotkey defaults: **Ctrl+PrintScreen = `screenshot`, Ctrl+Shift+PrintScreen = `screenshot-edit-last`.** Bare PrintScreen stays with
Windows (Snipping Tool); Win+PrtSc / Alt+PrtSc are Windows'; Ctrl and Ctrl+Shift combos are believed free (verify on Win11 26200).
The LL hook swallows the press before RegisterHotKey-style handlers see it (assumption; verified in S1). Also verify the LL hook
receives a key-down for VK_SNAPSHOT (0x2C) - expected, since KeyName handles it, but not proven in this repo.
Seeding instead of a schema migration: `Settings.SeedHotkeyDefaults()` called from `Load` after `Normalize` (not inside Normalize,
which `FromJson` also uses for import comparisons): for each default `(action, keys)` not in `SeededHotkeyDefaults`: append a row iff no
row has that action and no row uses those keys; record the action as seeded either way; raise `Migrated` if anything changed. A user who
deletes the row never gets it back; a downgrade/upgrade round trip cannot re-add it (older versions keep the list via `Unknown`).
`screenshot` is seeded in S1, `screenshot-edit-last` in S3 (the per-action list allows that). `--dev` instances bind nothing anyway.

OnHotkey fix (S1): pure `internal static HotkeyCommand CommandFor(string? action)` -> Clip (null / "" / "clip"), Pause, ShareLast
("share-last" / "share"), Record, Screenshot, ScreenshotEditLast, Unknown. `OnHotkey` switches on it; **Unknown logs
`hotkey: unknown action 'x' on Ctrl+Y: ignored` and does nothing** (today, AppController.cs:1039, it saves a clip). Behaviour change for
a hand-edited typo action: documented in the commit. `Hotkeys.IsClip` is unchanged (it defines clip rows for the settings model).
`HotkeyListEditor.s_actions` (HotkeyListEditor.xaml.cs:18-22): add ("Take screenshot", "screenshot") in S1 and ("Edit last screenshot",
"screenshot-edit-last") in S3; the length combo stays clip-only.

CLI/pipe: `--screenshot` -> "screenshot", `--screenshot-edit-last` -> "screenshot-edit-last" in `CommandFromArgs` (AppController.cs:198)
and `RunCommand` (299); `RunStartupCommand` (319) runs them at ApplicationIdle (no engine needed). Also update `--help` text if it
lists commands.
Tray (`CreateTray`, 1381-1415): "Take screenshot (Ctrl+PrintScreen)" after "Clip now" (label follows the binding like `ClipTrayText`;
rebuilt in `UpdateTrayHotkey`), "Edit last screenshot" (S3). Tray-triggered captures wait 250 ms (A2).

Screenshots tab (new TabItem between Clips and Alerts in MainWindow.xaml; MainWindow.xaml.cs only uses `ClipsTab` and index 0, so the
insert is safe): Folder (picker + Open, like ClipsFolder at MainWindow.xaml.cs:1216-1273) [S1]; Copy image to clipboard, Loupe [S1];
read-only "Hotkeys: Ctrl+PrintScreen, Ctrl+Shift+PrintScreen - change under Capture > Hotkeys" [S1]; After capture: Save only /
Open editor / Edit on screen [S3; "Edit on screen" enabled in S5]; Upload to Copium when linked, Copy link after upload, static Copium
hint [S6]. Alerts tab: "Screenshot saved" toast switch + seconds [S6].
SettingsImport: new label ("screenshots", [ScreenshotsFolder, ScreenshotMode, ScreenshotCopyImage, ScreenshotUpload, ScreenshotCopyLink,
ScreenshotLoupe, ScreenshotEditor, CopiumImageUploadUrl]); ToastScreenshot joins "alerts"; SeededHotkeyDefaults, CopiumNudgeDismissed,
CopiumNudgeShown join `IsVolatile`. DevOptions.Isolate: ScreenshotsFolder outside the dev folder -> null (+ the "changed" line).

## A7. Upload

CopiumClient (minimal, S6):
- `internal static string ContentTypeFor(string path)`: .mp4 video/mp4, .png image/png, .jpg/.jpeg image/jpeg, .gif image/gif,
  .webp image/webp, else application/octet-stream. Replaces the hard-coded `video/mp4` (CopiumClient.cs:25).
- `UploadAsync(token, path, title, progress, ct, string? endpoint = null)`; `internal static string EndpointFor(MediaKind kind,
  string? imageOverride)` -> the override when it is an absolute https URL, else `UploadEndpoint`. Video callers unchanged.
- `ErrorMessage(code, body, noun = "Clip")`: images pass "Screenshot"; 400/415 for an image -> "Copium did not accept the image (...)".
- `ShareRefusalFor(size, 0, plus)` for images (duration 0 passes; the byte limit still applies).
- Title: "Screenshot yyyy-MM-dd HH:mm" (or the game name), like ShareAsync's title (AppController.cs:1237).
Unverified PNG support: no hidden feature flag - local save + clipboard always happen first, so an upload failure loses nothing. On
400/415 for an image set session flag `CopiumImagesRefused` (no further automatic uploads this session; toast "Copium did not accept the
image - saved locally"; the Upload button still allows a manual retry). Before merging S6 into `dev`, verify once with the user's token
on a `--dev --dev-token` instance. `CopiumImageUploadUrl` covers a different image host if Copium has one.

## A8. Testing strategy

In-exe `[UnitTest]` (pure, or UI-thread WPF that needs no window):
- Imaging: BgraImage crop/compose/alpha-fix/LooksBlack; BoxBlur (r=0 identity, constant image unchanged incl. borders, impulse
  symmetric, mass preserved +-1/channel); Pixelate block means; Highlight multiply math + max-mask overlap; StrokeMask capsule area within
  3%; SmartErase properties (A3); Spotlight (inside byte-identical; constant image outside = c*(1-dim) +-1; checkerboard local variance
  non-increasing with distance when Gradient; constant radius otherwise); Magnifier (2x nearest = pixel duplication; outline ring);
  Adjustments (0/0/0 identity, LUT monotonic, unsharp on constant unchanged, step-edge overshoot, clamping); Resample; Blend
  (premultiplied over on known values).
- Geometry: SelectionMath (normalize any drag direction; clamp to virtual bounds incl. negative origins; per-monitor intersection;
  px -> DIP -> px exact at scales 1.0/1.25/1.5/1.75/2.0); ToolbarPlacement (below/above/inside/clamped); ArrowGeometry (head vertices
  at known angles); freehand direction (jittered last 3 points -> within 5 degrees; L-shape -> final leg; short stroke fallback;
  degenerate -> no head); StrokeSmoothing keeps endpoints; HitTest distances.
- Editing: UndoStack (push/undo/redo/limit/redo truncation); RasterComposer incremental == from-scratch (seeded random sequences);
  Exporter (crop applied; a RectOutline at (10,10)-(30,30) width 2 lands on exact pixels via RTB at 96 dpi - proves no DPI scaling;
  no vectors -> no RTB).
- Service/settings: ScreenshotNaming (format, sanitizing, reserved names, " (n)"); ScreenshotHistory (add/update/prune/cap/corrupt json
  -> folder rebuild) under the runner's scratch DirOverride; Settings defaults + Normalize of bad strings/nulls; SeedHotkeyDefaults
  (fresh, existing-free, colliding keys, colliding action, already seeded, deleted-not-re-added, JSON round trip); `CurrentSchema == 3`
  guard test; SettingsImport labels/volatile; DevOptions.Isolate ScreenshotsFolder; CommandFor (unknown -> Unknown, never Clip);
  CommandFromArgs; ContentTypeFor/EndpointFor; CopiumNudge rules.
- Perf smoke (generous bound, logs actual): 3840x2160 gradient spotlight levels + blend < 1500 ms; smart erase 800x600 < 200 ms.
Manual GUI script (each slice lists its subset; run on `Clink.exe --dev --dev-hotkeys` so the installed Clink is untouched; a later
LL hook runs first, so the dev instance gets Ctrl+PrintScreen; `Clink.exe --dev --screenshot` drives it from a shell):
single monitor at 100%; mixed DPI (100% + 150%/175%); a monitor left of / above the primary (negative coords); cross-monitor drag;
Esc / right-click / Enter / arrow keys; loupe at edges; HDR monitor with an HDR game and the SDR desktop (compare with what you see);
borderless game in front (focus, Esc after a click); exclusive-fullscreen game (black-detection toast); recording running (overlay not in
the replay); paste into Paint / Discord / Word; toast buttons; each editor tool at 100% and 200% zoom, undo/redo of each, export
equality (the PNG equals what 100% shows); 4K spotlight drag smoothness; text editing; insert image (file, paste, recent); edit-last
live and after a restart (flattened); upload with/without token; nudge dismissal persisting; settings tab round trip.

## A9. AI background remover seam (design only)

```
enum BackgroundRemoverState { Unavailable, NeedsDownload, Downloading, Ready, Failed }
interface IBackgroundRemover {
  BackgroundRemoverState State { get; } string StatusText { get; } event Action? StateChanged;
  Task PrepareAsync(IProgress<double>? progress, CancellationToken ct);   // the model-on-demand download plugs in here
  Task<AlphaMask> ComputeMaskAsync(BgraImage image, CancellationToken ct);
}
sealed class UnavailableBackgroundRemover : IBackgroundRemover   // v1: State = Unavailable, "Not available in this version"
static class BackgroundRemovers { public static IBackgroundRemover Current { get; set; } = new UnavailableBackgroundRemover(); }
// later: IModelStore.EnsureAsync(ModelSpec(Name, Url, Sha256, Bytes, License)) -> %LOCALAPPDATA%\Clink\models\<name>\
// (https only, SHA-256 pinned, explicit consent showing size + licence, resumable .partial like the Updater)
```
Result = `BackgroundMask` raster object applied last in the raster band (transparent PNG); undo is free. Toolbar button hidden while
`Unavailable`. Licence caution: RMBG-1.4 is non-commercial (never default-download); u2net / ISNet / MODNet (Apache-2.0) and BiRefNet
(MIT) are permissive - re-check each weights file's licence at implementation. A runtime (ONNX Runtime) would be a second NuGet package
and a native DLL inside the single-file exe (IncludeNativeLibrariesForSelfExtract is already true): a separate decision for that slice.

Deferred, seams kept clean (not designed): GIF/video from a region (the region picker returns a `RegionResult` a recorder could
consume), scrolling capture, delayed capture (`ScreenshotTrigger` can carry a delay), auto/repeat capture, cursor capture, window
snapping in the overlay.

## Cross-cutting

- Security: file names from game titles sanitized (no separators, no `..`, reserved names); inserted images / pasted images are
  untrusted input decoded by WIC with dimension caps (16384 px/side, 100 MP); upload override URL must be absolute https (host logged,
  token never logged; DPAPI storage unchanged, Settings.cs:650-662); later model downloads: https + pinned SHA-256 + consent.
- Accessibility: every toolbar button has `AutomationProperties.Name` and a tooltip with its shortcut; all tools reachable by keyboard;
  overlay supports Enter (whole monitor), arrow-key pointer nudging, Esc; size label and handles use Palette brushes with contrast in
  both themes; no new animations beyond the existing toast fades.
- OS/platform: Windows 10 19041+ (GDI path and WDA_EXCLUDEFROMCAPTURE 2004+ both fine); WGC one-shot (S7) shows a yellow border on Win10.
  PerMonitorV2 assumed (manifest). OLE clipboard needs STA: UI thread only.
- PublishSingleFile: no new packages or native DLLs; WIC (PngBitmapEncoder), OLE clipboard and GDI are OS components; XAML is
  compiled into the exe as today. Nothing reads files next to the exe.
- Compatibility/migration: additive keys only, no CurrentSchema bump (see evidence), seeding recorded in `SeededHotkeyDefaults`; older
  Clink versions keep unknown keys (`[JsonExtensionData]`) and the hotkey rows.
- Rollback: the feature is additive. Revert the merge commit(s) on `dev` / `feature/screenshots`; settings stay harmless. Known downgrade
  wart: a Clink <= v0.3.1 reading the seeded `screenshot` rows runs them as "clip" (old fallthrough, AppController.cs:1039) - only on a
  manual downgrade (the updater never downgrades a stable install); the S1 fallthrough fix prevents this class of problem going forward.
- Logging: one `screenshot:` line per capture (monitors, source per monitor, HDR flags, rect, timings, file, size); no pixel data, no
  token.

---------------------------------------------------------------------------------------------------------------------------------

## B. Slice plan (one implementer, sequential; S2 and S3 may run in parallel worktrees)

Common acceptance for every slice: `.\build.ps1 -Configuration Debug` 0 warnings / 0 errors; `Clink.exe --unittest` all pass, also with
`CLINK_TESTS_NO_SKIP=1`; existing test count does not drop; manual checks run on `Clink.exe --dev --dev-hotkeys` (never the user's
installed Clink); commit on `feature/screenshots`. Release-config build + `--unittest` additionally at S1 and S6 (before dev merges).

### S1 - Capture to PNG (first dev-build try-out). Two commits allowed: S1a backend, S1b overlay/UI.
Create: `Screenshots\Imaging\BgraImage.cs, RectI.cs` (API frozen here; S2 builds on it), `Screenshots\Sources\IScreenshotSource.cs,
GdiScreenshotSource.cs, FrozenDesktop.cs`, `Screenshots\Geometry\SelectionMath.cs`, `Screenshots\ScreenshotService.cs` (mode none only),
`ScreenshotNaming.cs, ScreenshotHistory.cs, ScreenshotClipboard.cs, PngFile.cs`, `UI\Screenshots\RegionOverlayWindow.xaml(.cs)`
(selection, dim, size label, loupe, keys, cross-monitor), `Services\AppController.Screenshots.cs`, tests
`ScreenshotServiceTests.cs, ScreenshotSettingsTests.cs, ScreenshotGeometryTests.cs` (SelectionMath part).
Touch: `NativeMethods.txt`; `Settings.cs` (ScreenshotsFolder, ScreenshotMode stored, ScreenshotCopyImage, ScreenshotLoupe,
ToastScreenshot, SeededHotkeyDefaults, SeedHotkeyDefaults(), Normalize); `AppController.cs` (CommandFor + OnHotkey fix, CommandFromArgs,
RunCommand, RunStartupCommand, tray item + label, service construction in Start); `Hotkeys.cs` (GrantForeground); `HotkeyListEditor.xaml.cs`
(s_actions + "screenshot"); `ToastWindow.xaml(.cs)` (ShowScreenshot: kind "screenshot", Open + Folder buttons); `MainWindow.xaml(.cs)`
(Screenshots tab: folder, copy image, loupe, hotkey hint); `DevOptions.cs` (Isolate); `SettingsImport.cs` (labels/volatile).
New tests: BgraImage crop/compose/alpha/LooksBlack; SelectionMath set; ScreenshotNaming set; ScreenshotHistory set; Settings defaults/
Normalize/seeding set + `CurrentSchema == 3`; CommandFor (unknown never Clip); CommandFromArgs; SettingsImport label; DevOptions isolate.
Manual: Ctrl+PrintScreen -> frozen overlay on every monitor with no visible jump/blur when it appears; drag -> PNG in
`<ClipsFolder>\Screenshots` whose size equals the label; pasted into Paint pixel-identical to the source area; toast Open/Folder; Esc and
right-click save nothing; Enter = whole monitor; mixed-DPI + negative-coordinate monitor alignment; cross-monitor rect; tray item and
`--screenshot`; an existing settings file gains the Ctrl+PrintScreen row once, deleting it survives a restart; a hand-edited unknown
action logs and does not clip; HDR monitor result recorded in STATE.md (decides S7 priority); exclusive-fullscreen black toast; the
overlay is absent from a replay clip saved right after.
Risks: DPI placement, focus over games, PrintScreen key-down delivery, CAPTUREBLT cursor flicker.

### S2 - Pixel library (pure; parallel-safe with S3 in its own worktree)
Create only: `Screenshots\Imaging\BoxBlur.cs, Pixelate.cs, Highlight.cs, StrokeMask.cs, SmartErase.cs, Spotlight.cs (levels cache +
blend), Magnifier.cs, Adjustments.cs, Resample.cs` and `Tests\ScreenshotImagingTests.cs`. No other file touched.
Acceptance: the imaging test list in A8 incl. the SmartErase property tests and perf smoke (logged times). No UI.
Risks: 4K perf (mitigated by half-res levels + Parallel.For); rounding drift in tests (tolerances specified in A3/A8).

### S3 - Editor window + vector tools + edit-after + edit-last (parallel-safe with S2)
Create: `Screenshots\Geometry\ArrowGeometry.cs, StrokeSmoothing.cs, HitTest.cs, ToolbarPlacement.cs`, `Screenshots\Editing\
DocumentState.cs, UndoStack.cs, RasterComposer.cs (IRasterOp interface, base passthrough), Exporter.cs`, `Screenshots\Imaging\Blend.cs`,
`UI\Screenshots\EditorSession.cs, EditorCanvas.cs, EditorToolbar.xaml(.cs), ScreenshotEditorWindow.xaml(.cs)`, `Tests\ScreenshotEditorTests.cs`
(+ geometry tests appended to ScreenshotGeometryTests.cs).
Touch: ScreenshotService (mode after, EditLast, LastEdit), Settings (ScreenshotEditor options, seed `screenshot-edit-last`),
AppController.Screenshots (edit-last hotkey/tray/CLI), HotkeyListEditor s_actions, ToastWindow (Edit button), MainWindow Screenshots tab
(mode: Save only / Open editor).
Tools: select/move/resize/delete, pen, line, straight arrow, freehand arrow, rectangle outline, text, crop; undo/redo; copy, save, done.
Acceptance: geometry/undo/exporter tests (A8); manual: every tool at 100% and 200%, undo/redo each, the saved PNG matches the 100% view,
text UX (Enter/Shift+Enter/Esc/double-click), Ctrl+Shift+PrintScreen reopens the live document, after a restart opens the flattened PNG,
discard prompt.
Risks: RTB text/AA differences between screen and export; zoom math on high DPI; memory after repeated edits (check Task Manager after
10 editor sessions).

### S4 - Raster tools, adjustments, insert image (needs S2 + S3)
Create: `Screenshots\Editing\RasterOps.cs` (record -> op mapping, read margins), `UI\Screenshots\InsertImagePicker.xaml(.cs)`.
Touch: RasterComposer (below-cache, dirty regions, versioned background compute), EditorSession/EditorCanvas/EditorToolbar (blur,
pixelate, highlight rect/freehand, spotlight + gradient option, magnify, smart erase, insert image from recent/file/Ctrl+V, adjustments
flyout), Settings.ScreenshotEditor fields.
Acceptance: incremental == from-scratch composer test; ops-through-composer tests; manual: each tool incl. handles and undo; spotlight drag
on a 4K capture stays smooth (log < 300 ms per full rebuild, re-blend per move); smart erase on a gradient UI background and on text over a
flat panel; magnify outline; paste an image from the clipboard; adjustments preview and undo.
Risks: UI-thread stalls (all heavy work off-thread), memory with many raster objects (cap: warn at 50 raster objects).

### S5 - Edit-before in the overlay (needs S3; after S4 so the full toolset appears)
Touch: RegionOverlayWindow (editing phase, hosts EditorCanvas + compact EditorToolbar), EditorCanvas (crop handles with hit priority),
EditorToolbar (compact), ScreenshotService (mode before, cross-monitor fallback to the editor window), MainWindow mode option
"Edit on screen".
Acceptance: ToolbarPlacement tests already in S3; manual: mixed-DPI monitor editing at 1:1, toolbar placement at all four screen edges and
for a full-monitor selection, crop handles, Enter/Done commits, Esc cancels, cross-monitor selection opens the editor window instead,
annotations outside the crop are clipped in the PNG.
Risks: keyboard focus in the overlay over a game; toolbar overlapping the selection on small screens.
Implementation notes (as built):
- Contract: `IRegionPicker.PickAsync(desktop, loupe, ct, OverlayEditSetup? edit = null)`; a non-null `edit` (mode "before") turns the
  editing phase on. `RegionPickResult` gained `DocumentState? Document` (base = the monitor's frozen picture, crop = final crop in monitor
  pixels; `Rect` is that crop in virtual-screen pixels) and `bool SpansMonitors` (area over several monitors: no document, the service opens
  the editor window on the composed crop, header hint `ScreenshotService.SpanHint`). The pick completes on Done/Enter with the windows still
  open (the caller closes them, as before). Esc follows the editor window (`OverlayEditKeys.Decide`): it drops a drag, then deselects,
  then returns to the Select tool, and cancels the capture (no question; edits are in memory only) only when none of that is left. Enter
  and Space reach a focused toolbar button; a held (repeating) Enter does not finish the edit. After Done, Esc closes the windows.
- The pick follows what the overlay returned (`Document` / `SpansMonitors`), not the mode setting read afterwards. A black frozen
  picture under an edited crop gets the "Screenshot is black" toast like a plain capture.
- The idle watchdog is suspended while the Insert-image picker / file dialog is open and restarts when it closes; a second trigger still
  cancels. Closing a window between the selection commit and the start of editing ends the capture.
- Phase logic is the pure `Geometry\OverlayEditState` (selection committed -> Editing / SpansMonitors / Done, crop, toolbar position in the
  monitor's own physical pixels, idle timer); `RegionOverlayWindow.BeginEditing` hosts `EditorCanvas.OverlayMode` (fixed 1:1, crop handles
  always on and before every tool/object, dim drawn by the canvas) and `EditorToolbar.Compact` (no zoom/copy/save; Cancel and Done).
- The service exports with `Exporter` on the UI thread (WPF drawing), then the normal `SaveEditedAsync` (file on a worker, clipboard,
  one toast, `LastEdit` = the live document rebased to its crop (`DocumentState.Rebase`: base cut to the crop, objects moved, crop =
  whole), so edit-last reopens it with its objects, keeps no more than the crop in memory and cannot reveal what was cropped out; effects
  that read outside the crop (blur or sharpen at the edge, a magnifier source outside) lose those pixels on a re-edit; also spotlight
  dimming levels are half-resolution above 4 MP and full-resolution below it, pixelate blocks and smart-erase rings straddling the crop
  edge are built from the cut picture, and a spotlight's falloff percentage is relative to the new, shorter side).
  The edited document is not kept by the service after the rebase, and one LOH compaction runs (Background priority) after the save;
  a cancelled pick compacts when its windows close.
- Idle limits: 2 minutes without input while choosing the area, 20 minutes while editing; the watchdog cancelling an editing session
  shows the notice "Screenshot cancelled after 20 minutes without input". A cancel (Esc, second trigger, watchdog) that arrives while
  the Insert-image picker / native file dialog is modal is deferred and runs when the dialog returns. Esc inside the Adjust flyout only
  closes the flyout (overlay and editor window). Enter on a focused toggle button is Done; Enter on a real button presses it.
- Capture exclusion: tooltips and context menus of an excluded window, any window owned by one (the Insert-image picker and its
  tooltips) and the Adjust flyout are excluded from screen capture like the overlay itself. The native file dialog (Browse in the
  picker) cannot be excluded and may show in a recording.
- After the last overlay window of an editing session closes, the large-object heap is compacted and collected (as the editor window does).
- Known limitation (accepted): edits made in the overlay are lost on a quit / Windows session end (`SaveEditorForSessionEnd` covers the
  editor window only). Follow-up if wanted.

### S6 - Upload, toast actions, nudge, settings completion, AI seam
Create: `Screenshots\CopiumNudge.cs`, `Screenshots\Ai\IBackgroundRemover.cs` (seam + Unavailable implementation; hidden toolbar slot).
Touch: CopiumClient (ContentTypeFor, EndpointFor, ErrorMessage noun), ScreenshotService.UploadAsync (+ CopiumImagesRefused), ToastWindow
(uploading/uploaded/failed states, Copy link/Upload/Retry, nudge line), Settings (ScreenshotUpload, ScreenshotCopyLink, CopiumNudge*,
CopiumImageUploadUrl), MainWindow (Screenshots tab upload rows, Alerts toast row), SettingsImport, `ShareTests`-style tests in
ScreenshotServiceTests.
Acceptance: ContentTypeFor/EndpointFor/ErrorMessage/CopiumNudge tests; existing ShareTests unchanged and passing (video path untouched);
manual: no token -> nudge shows max 3 times, "Don't show again" persists; with token (dev instance `--dev-token`) PNG upload succeeds or
fails with the clear message and the file stays saved; link replaces the clipboard image; Release build + tests. User verifies Copium
PNG support before this merges into `dev`.
Risks: Copium refuses images; clipboard race between image copy and link copy (sequenced on the UI thread).

### S7 - (next iteration unless S1 shows HDR is unacceptable) WGC HDR source
`Screenshots\Sources\WgcScreenshotSource.cs` + a tone-map-to-BGRA8 compute shader (embedded resource like Convert.hlsl) + staging readback;
used for `IsHdr` monitors and as the all-black fallback. Not part of this iteration's plan.

Parallelism: only S2 || S3 (disjoint files: S2 owns the listed Imaging files + ScreenshotImagingTests.cs; S3 owns everything else in its
list, including Imaging\Blend.cs). Both branch from the S1 commit; integrate S2 first (pure), then S3. Every other slice touches
AppController/Settings/ToastWindow/MainWindow and must be sequential.
Dev merges: after S1 (first try-out), after S3, after S5, after S6 - each merge to `dev` publishes a dev build, so each needs the user's
go-ahead under the no-auto-push policy.

## C. Risk register

| # | Risk | Likelihood / impact | Mitigation |
|---|---|---|---|
| 1 | DPI / placement math wrong on mixed-DPI or negative-coordinate layouts (offset, blurry frozen image, wrong crop) | M / H | physical px end to end, GetCursorPos, SetWindowPos two-pass + GetWindowRect assert, BitmapSource dpi = 96*scale + NearestNeighbor, SelectionMath tests at 5 scales, manual matrix in S1 |
| 2 | HDR monitor: GDI clips HDR highlights / looks washed | H (user likely HDR) / M | documented; seam for WGC (S7); S1 manual check decides priority |
| 3 | Settings migration: a schema bump would break EnsureHotkeyModel (`older`, Settings.cs:514) and write pre-v3 copies | H if bumped / H | no bump; per-action `SeededHotkeyDefaults`; `CurrentSchema == 3` guard test; round-trip tests |
| 4 | Copium does not accept PNG on v.copium.pics | M / M | content type by extension, endpoint override, save + clipboard first, clear toast, session refusal flag, verify with the user's token before S6 merges |
| 5 | Overlay gets no keyboard focus (foreground lock), esp. over games | M / M | Activate + GrantForeground (tagged dummy key, never over the game), mouse click activates, right-click cancel always works |
| 6 | Unknown-action fallthrough clips; downgrade to <= v0.3.1 runs screenshot rows as clips | today H / L | CommandFor + Unknown ignored + log (S1); downgrade wart documented; updater never downgrades |
| 7 | Memory on 4K / multi-4K (LOH buffers, RenderTargetBitmap unmanaged memory) | M / M | export once per commit, crop-sized RTB in strips, drop caches when idle, LOH compaction + GC once on editor close, check after 10 sessions |
| 8 | Clipboard contention / ownership (another app holds it; data lost on exit) | M / L | UI thread only, SetDataObject(copy: true) flush, 5 x 60 ms retries, toast says "not copied" on failure |
| 9 | Hook thread -> UI timing / re-entrancy (double press, press during capture) | M / L | BeginInvoke only (never blocks the hook), IsBusy guard, overlay brought forward on repeat |
| 10 | Exclusive-fullscreen game -> black screenshot; overlay knocks the game out of exclusive mode | M / M | LooksBlack toast with guidance; WGC fallback in S7; documented |
| 11 | PrintScreen: key-down not delivered to the LL hook or Snipping Tool also fires | L / M | verify in S1; if broken, default to Ctrl+Shift+S-style combos (open question) |
| 12 | Effects too slow on 4K (spotlight, sharpen) | M / M | O(n) box blurs, half-res levels, box-independent level cache, dirty regions, background versioned compute, logged timings |
| 13 | RTB export differs from on-screen rendering (text AA, DPI) | M / L | Ideal formatting, Grayscale AA, 96-dpi RTB test with exact-pixel assertions |
| 14 | PublishSingleFile surprises | L / L | no new packages or native files; Release build + tests at S1 and S6 |

## Open questions for the user

1. Default "after capture" mode: Save only (recommended: fast over games; Edit is one click in the toast) or Open editor?
2. Default hotkeys Ctrl+PrintScreen / Ctrl+Shift+PrintScreen, auto-added for existing users when free - OK?
3. Does Copium accept PNG uploads at `v.copium.pics/api/upload`, or is there an image endpoint? Can you test once with your token?
4. After the S1 build: is the GDI screenshot acceptable on your HDR monitor, or should the WGC HDR path (S7) join this iteration?
5. Edit-last saves a new file (recommended) rather than overwriting the original - OK?
6. Clipboard: image only (recommended), or also the file (FileDrop) so Explorer/Discord paste it as a file?
7. File name: game name when a game is detected ("Overwatch 2026-10-24 21-04-11.png") or always "Screenshot ..."?
8. After a successful upload, should the link replace the image on the clipboard (ShareX default, recommended)?


## Decisions after review (2026-10-24)

User overrides:
- Default hotkeys: bare PrintScreen = screenshot, Shift+PrintScreen = screenshot-edit-last (seeded in S3).
- Master `ScreenshotsEnabled` toggle plus per-behaviour toggles, all default ON.
- Copium accepts PNG on the same `/api/upload` endpoint (content type chosen by extension).
- Screenshots are fully separate from the clip library.

Coordinator defaults:
- ScreenshotMode default is "none".
- Edit-last saves a new file (does not overwrite the original).
- Clipboard holds the image only; the link replaces it after upload (toggle).
- File name uses the game name only when the engine already knows it.

Review-driven changes:
- Trigger-while-open cancels (no BringToFront).
- 2-minute idle watchdog on the overlay.
- `IRegionPicker` is now `PickAsync(desktop, loupe, ct)` + `Cancel()` + `Close()`; Close completes on commit with windows open.
- `RectI.FromCornerPixels` is an inclusive band.
- `BgraImage` has no bitmap cache (`ToBitmapSource()` always fresh).
- Save = partial file written outside the lock, move inside the lock.
- LooksBlack warns only for a whole-monitor black or when a game is present.
- Alerts-tab toast row and upload UI deferred to S6.
- WGC HDR source = S7, pending the user's HDR check.

## S3 implementation notes (editor window, vector tools, edit-after, edit-last)

Deviations from the text above, all deliberate:
- `Adjustments` is `AdjustmentValues` (the pixel library has a type of that name in `Clink.Screenshots.Imaging`); `BackgroundMask` is not in the records (it needs `AlphaMask`; the A9 seam is unused in v1). `DocumentState` carries a `NextId` so ids stay unique across undo.
- `RasterComposer` is more than a passthrough: the adjustment stage (`Adjuster`) and the op mapping (`OpFor`, `IRasterOp`: `Affects`, `ReadMargin`, `Apply(src, dst, area)`) are seams; it caches the adjusted base and the output of every op (the below-cache) and `Compose(state, dirty)` recomputes only the dirty rectangle (inflated by each op's read margin) from the first changed op up. S4 fills the seams and adds the background compute; tests drive it with a made-up op.
- A rectangle outline is hit on its border (not its whole box), so what lies inside a big rectangle stays selectable.
- Chaikin corner cutting conserves the sum of the turning angles (each corner is split into smaller turns of the same sign); what it removes is sharpness. The tests assert the largest turn and the bending energy (sum of squared angles), not the plain sum.
- Freehand arrow direction window: `D = min(max(3 * headLength, 12), 40% of the stroke)`, but at least min(12, stroke length) so a 15-30 px stroke does not fit a line to 3 points.
- Export: shapes render together (tiled above 8192 px, byte-identical to a single render); every text note renders on a render target of its own on a 32-px lattice of document coordinates, because WPF's text rasterizer is not translation invariant in the last bit (1 level in a few glyph pixels at a tile edge). The result is blended in z-order, so a text note can sit between two shapes.
- Edit-after: Done saves (PNG, history, clipboard, toast); Esc / Discard / closing an unedited new capture saves nothing; closing with edits asks Save / Discard / Keep editing (Yes / No / Cancel). Save (Ctrl+S) keeps the editor open and, after the first time, replaces the file of that session; the original of an edited screenshot is never overwritten. Done on an untouched screenshot that came from a file just closes (no duplicate file).
- Opening a history entry needs: fully qualified, `.png`, no `:` after the drive letter, no `\\?\` / `\\.\`, an existing regular file (no directory, no link), inside the current screenshots folder or any folder screenshots were ever saved to (`ScreenshotHistory.Roots`: kept in `screenshots.json`, plus the folders of the entries). `--screenshot-edit <path.png>` (made absolute by the sending process, carried over the pipe as `screenshot-edit <path>`) is allowed anywhere but must be a readable PNG within 16384 px per side and 100 MP.
- Seeding: `screenshot-edit-last` = Shift+PrintScreen, per action (an install that already offered `screenshot` gets this one on its next start, with its own notice).

## S6 implementation notes (Copium upload, hint, settings, AI seam, export off the UI thread)

As built, with the deviations from the text above:
- `CopiumClient`: `ContentTypeFor` (adds .gif / .webp), `KindOf` (image by extension, everything else is a clip), `EndpointFor(MediaKind, override)` (absolute https only, anything else ignored with a warning; the host, never the path or token, is logged when it is used), `UploadAsync(..., string? imageEndpoint = null)`, `ErrorMessage(code, body, noun = "Clip")` (a "Screenshot" noun: 413 "Screenshot is larger than ...", 400 / 415 "Copium did not accept the image (...)"), and `CopiumHttpException : InvalidOperationException` carrying the HTTP status (clip callers still catch `InvalidOperationException`). `HttpOverride` is a test seam for a mock handler.
- `Settings`: `ScreenshotUpload` (true), `ScreenshotCopyLink` (true), `CopiumImageUploadUrl` (hidden; blank -> null, validated when used), `CopiumNudgeShown` (int), `CopiumNudgeDismissed`, `CopiumNudgeLastShownUtc`. `SettingsImport`: the first three under "screenshots", `ToastScreenshot` was already under "alerts", every `CopiumNudge*` key is volatile (a difference that says nothing). No schema change.
- `ScreenshotService.UploadAsync(entry, manual)`: uploads are queued (a task chain: one at a time, in the order asked). Upload runs after the file, history and clipboard image are done; it never blocks them. With a token and `ScreenshotUpload`, an automatic upload starts after the toast (`AnnounceSaved`, used by every save path: mode none / after / before, the editor's Done). Title = the file's stem. On success the entry gets `CopiumLink` / `CopiumPageUrl` / `CopiumId` (history updated and written), the link goes to the clipboard when `ScreenshotCopyLink` (replacing the image), the toast shows it. On failure a problem toast starts with "Saved locally." and the screenshot toast gets a Retry button. HTTP 400 / 415 sets `CopiumImagesRefused` (this process only): later automatic uploads are skipped silently, the toast offers Upload, a manual upload still runs. The size check is `AppController.ShareRefusalFor(size, 0, plus)` (size only). `CancelUploads(4 s)` is called from `AppController.Dispose` before the engine goes down: it cancels the token and waits for the worker task only (never for the UI thread, so it cannot deadlock), bounded like the recording exit.
- Deviation: the editor's **Save** (Ctrl+S, the editor stays open, the next Save replaces the same file) does not upload; **Done** does. A Save has no toast and no clipboard step, and a later Save writes a new history entry for the replaced file, which would lose the link. Mode "before" and "after" upload on their commit like a plain capture.
- Toast (`Toasts`, `ToastWindow`): the screenshot toast is `Open | Edit | Upload / Copy link | Folder`. `ScreenshotToastState.CanUpload` (a token and no automatic upload follows) makes the Upload button appear; an entry that has a link shows Copy link. `IScreenshotToasts.ShowScreenshotUploading(entry)` turns the toast that is still up into "Uploading screenshot..." with the percentage on its button (a new one is made if it is gone; nothing at all when the Screenshot saved switch is off) and returns an `IScreenshotUploadToast` (`Report`, `Succeeded(link, copied)`, `Failed()`). After an upload `Open` opens the Copium page (https only) instead of the file. The Alerts tab has the "Screenshot saved" switch, seconds and a Preview button.
- Hint (`CopiumNudge`, pure rules + counters): shown as a toast "Saved locally / Link your Copium account to share screenshots automatically" with Set up Copium (-> `ShowCopiumSetup`) and Don't show again, when a screenshot was saved, `ScreenshotUpload` is on, there is no token, it was not dismissed, was shown fewer than 3 times and not in the last 24 h (a last-shown time in the future after a clock change does not hold it back). `IScreenshotToasts.ShowCopiumNudge` returns false when the Screenshot saved switch is off: the hint is then not counted. The A5 text had the hint inside the screenshot toast as one line; a toast of its own is simpler and never makes the saved toast taller. Not shown for a black picture.
- Screenshots tab: "Upload to Copium when linked" (disabled until a token exists, with a link to the token box on the Clips tab), "Copy the link to the clipboard after uploading" (enabled only when upload is on and linked), a static "always saved locally" hint. `TokenSave_Click` refreshes the tab.
- AI seam: `Clink\Screenshots\Ai\IBackgroundRemover.cs` is the shape in the task (`IsAvailable`, `UnavailableReason`, `RemoveBackgroundAsync(BgraImage, IProgress<double>?, CancellationToken) -> AlphaMask`), `UnavailableBackgroundRemover`, `BackgroundRemovers.Current`. It replaces the larger A9 sketch (state machine, `PrepareAsync`) until a model store exists. No toolbar button, no model, no download; `BackgroundMask` was not added to `DocumentState` (not trivial). The licence note and the planned consent / pinned-hash download are in the file's comment.
- Export off the UI thread: `Exporter.ExportAsync(doc, composer)` computes the raster composite and cuts the crop on a worker (`Task.Run`) while the calling UI thread renders the vector layers (`DrawingVisual` + `RenderTargetBitmap` into `VectorLayer`s), then the blend runs on a worker. `Export` (synchronous) is the same three steps in a row, so the bytes are identical (tests compare). The vector layers are held until the blend (all tiles of a tiled export at once, bounded by the area the shapes touch). `EditorSession.ExportAsync()` caches per committed document, joins a running export of the same document and shares the cache with `Export()`. The editor window's Done / Save / Copy `await` it with the toolbar disabled meanwhile (Copy also builds the bitmap and PNG on a worker); the service's `SaveEditedAsync` makes the bitmap on a worker; the overlay commit (`CommitEditedAsync`) uses `ExportAsync` too. `Exporter.Probe` is a test seam (phase, thread).
- **S6 review fixes (security pass).**
  - *Endpoint*: `EndpointFor` accepts an override only when it is `https` on `copium.pics` or `*.copium.pics` (no user info); anything else is ignored with a warning, so the bearer token can never be sent elsewhere. `CopiumImageUploadUrl` is this machine's own key (`IsOwnKey`): an import never brings or offers it. When an override is used, a one-time notice toast names the host.
  - *Automatic upload consent (decision: the default stays ON)*: `ScreenshotUpload` and `ScreenshotCopyLink` default to on, so the first automatic upload on an install shows a one-time notice, "Screenshots are uploaded to your Copium account automatically — change this under Settings → Screenshots" (`CopiumAutoUploadNoticeShown`, saved, volatile for import). While an upload runs the toast's button is **Cancel** (a per-upload linked `CancellationTokenSource`): cancel shows Retry, nothing is linked, no problem toast, and the next queued upload goes on. A manual upload never shows the notice.
  - *Answer limits*: the answer's body is read through a linked token that times out 30 s after the headers (`CopiumClient.BodyTimeout`, a test seam) and is cut at 64 KB (so an endless or huge answer fails as "unexpected answer" / a timeout and never holds the upload queue). The `link` must be an https `copium.pics` address, else "unexpected answer"; `url`, `pageUrl`, `thumbUrl` that are not are dropped.
  - *Clipboard*: the clipboard's change counter (`GetClipboardSequenceNumber`) is recorded right after our image copy and kept with the queued upload; an automatic upload copies the link only if the counter is unchanged. With "copy image" off, only a manual upload copies the link. The toast then says "uploaded" (its Copy link button is there) and the log says "link not copied (clipboard changed)".
  - *Pausing*: only HTTP 415, or a 400 whose error text matches `type|format|mime|extension|unsupported`, sets `CopiumImagesRefused`. A 401 / 403 pauses automatic uploads with one "Copium token rejected" toast until the token changes. Other 400s are a plain "Copium refused the upload (...)".
  - *Paths*: `\??\`, `\\?\` (also `UNC`) and `\\.\` are device paths: `IsUnc` treats them as network paths, `IsOpenable` refuses them and wants a drive root (`X:\`) unless the path is inside a network folder the user chose; `KnownFolders` always drops device roots. `IsExplicitEditable` (the pipe's `screenshot-edit`) refuses network and device paths.
  - *Pipe*: server and client use `PipeOptions.CurrentUserOnly` and the per-session name `AppController.SessionPipeName` (`PipeName` + `.s<session id>`; `PipeName` itself is unchanged).
  - *Import*: a network (UNC / device) `ScreenshotsFolder`, `ClipsFolder` or `ArchivePath` in another copy is never taken over silently, for an offered import and for the fresh-install takeover alike: ours stays (the default when we have none), `Differences` does not offer it, and one notice names the setting at the next start (kept in `import-paths.notice` because an import restarts Clink).
  - *Pictures from outside*: `PngFile.Decode` reads the header, rewinds the same stream and decodes it with `BitmapCacheOption.None`, comparing the frame size before copying pixels; the clipboard "PNG" format uses `PngBitmapDecoder` only; `ImageInput.LoadFile` accepts only PNG / JPEG / BMP / GIF by their first bytes; a clipboard DIB with a header that does not parse, or with no positive width / height, is refused before `GetImage`.
  - *Privacy*: `LastEdit` (the live document, with whatever the user cropped out or covered) is dropped on `SystemEvents.SessionSwitch` and after 15 minutes without an edit-last / editor interaction. Before an upload the file's size and last-write time are compared with the values recorded at save; a mismatch skips the upload with a warning toast.
  - *Editor window capture*: `ScreenshotEditorWindow` is an `ICaptureExcludedWindow` (and so is its picture picker, which it owns) and sets `WDA_EXCLUDEFROMCAPTURE`, behind the hidden `ScreenshotEditorExcludeFromCapture` (default true, no UI). `ICaptureExcludedWindow.CaptureExcluded` lets a window opt out, tooltips included.
  - *Smaller*: rows with an unknown hotkey action are left out of `BindableHotkeys` (they used to swallow the key); clip links are opened by the shell only if absolute https; `ScreenshotHistory.Update` finds the entry by path and copies the link fields onto the listed instance (the upload keeps the history it started with); an upload that fails after Clink began closing is not a problem toast; a backwards clock restarts the hint's 24 h gap from now; `.mov` / `.mkv` / `.webm` upload as `video/*`; the hint's "Set up Copium" selects the Clips tab and focuses the token box (`CopiumSetupRequested`); a close asked for while the editor is saving runs when the save ends; the upload queue's tail is a `TaskCompletionSource` set before any work runs.
- Not verified here: the actual upload (no token on the dev instance); that Copium accepts PNG at `/api/upload` and that a 4K Done feels smooth are for the user's manual run on the dev build.

## S7 implementation notes (consent, cloud kill switch, feature visibility)

Supersedes parts of the S6 notes above: the automatic-upload default, the one-time notice and the toast hint.
- **Sharing is opt-in.** `ScreenshotUpload` now defaults to **false**. `Settings.CopiumConsent` (the user's yes) is per install and never imported, like `CopiumConsentDontAsk`, `CopiumConsentPromptedUtc`, `CopiumConsentPromptCount` and `CopiumConsentScreenshots` (the count of saved screenshots while undecided). `IsOwnKey` also covers `CopiumNudge*`, `CopiumAutoUploadNoticeShown` (S6's notice and the hint toast are gone), `ScreenshotEditorExcludeFromCapture` and `CloudSharingEnabled`. `ClipsEnabled` is importable (label "features").
- **`ConsentGate`** (`Services/ConsentGate.cs`, no WPF): `EnsureAsync(ConsentContext)`. Cloud sharing off: notice, refuse, no pop-up ever. Consent on file: true. Else the pop-up (`Prompt` seam), once per attempt (a second caller waits for the same answer), true only for Enable. Don't ask again stops only `Proactive` asks. Enable with no token opens the token setup, except for a clip context (its own no-token path, the share window, follows). The real pop-up is `UI/CopiumConsentWindow` (also the two-step "Allow cloud sharing?" confirmation): DangerTint warning panel with the fixed public-link text, an unchecked "Also upload every screenshot I take automatically" (only for a screenshot Upload and the proactive ask; topmost and capture-excluded then), Enable / Not now / Don't ask again. `AppController.EnsureCopiumConsentAsync` is the one entry; `ConsentContext` adds `TokenSaved` to the planned list (asked once right after a token is saved).
- **Where it applies.** `ShareAsync(entry, ..., context)` (clip list, toast, trim editor, share-last hotkey / tray / pipe) after the existing-link copy branch (a link is only copied, no network); the screenshot toast's Upload (asked before the button turns into Cancel), `ScreenshotService.UploadAsync(manual)`; `AnnounceSaved` (automatic upload needs `ScreenshotUpload` + consent + cloud on + token); turning on "Upload every screenshot" in the Screenshots tab (reverted when declined); saving a token. `UploadCoreAsync` and `ShareAsync` check kill switch + consent again. Deviation: with no token and "Not now", `ShareAsync` still opens the local share window (nothing is uploaded, so no yes is needed); cloud sharing off refuses in every case.
- **Proactive pop-up** (`CopiumNudge`, rules only): after the 2nd saved screenshot without consent and without Don't ask again, at most twice, at least 24 h apart, skipped (and tried again next time) while a game or full-screen app is in front (`ScreenshotService.GameInFront`). Upgrade: an install with a token and no consent gets the pop-up at its next explicit share; nothing uploads by itself.
- **Kill switch.** `CloudSharingEnabled` (default true): off at once; on only after the confirmation dialog (`AppController.SetCloudSharing`, `ConfirmCloudSharingOn` seam), logged both ways. Off hides the clip list's Share button (Copy link stays), the toasts' Share / Upload, the trim editor's Save & share, the share window's Set up Copium…, the tray's Share last clip; share-last (hotkey, tray, `--share-last`) says "Cloud sharing is turned off — Settings → App → Cloud sharing"; the Clips tab's token section is replaced by that line (the token is kept); the Screenshots tab's upload rows are disabled with it. The share-last hotkey stays bound (the press explains). Live via `CloudSharingChanged`.
- **Features** (Settings → App): `ClipsEnabled` and `ScreenshotsEnabled`; the last one on cannot be turned off (`Normalize` also refuses both false, keeping Screenshots). Clips off: `StartEngine` never starts (stops a running one), `Tick` does not retry, `clip` / `pause` / `record` / `share-last` rows are not bound (they stay in the list), the tray's clip, record, pause, share and folder items and the status line are hidden, the status dot is off, the Capture, Clips and Library tabs and the wide clips column are hidden, and `--clip` / `--record` / `--share-last` / `--trim-last` / pause say "Clips are turned off — Settings → App → Features". Crash recovery of abandoned recordings still runs at startup (harmless: it only adds a finished file to the library). Screenshots off: as before, plus the Screenshots tab is hidden. `AppController.SetClipsEnabled` / `SetScreenshotsEnabled` apply live (`FeaturesChanged`).
- **S6 leftovers.** `CopiumClient`: any absolute https `link` / `url` / `pageUrl` / `thumbUrl` (no user info) is accepted, only the upload endpoint is pinned; `SocketsHttpHandler.AllowAutoRedirect = false` and a 3xx is "unexpected redirect"; the type refusal needs whole words (`file type`, `mime`, `unsupported`, `extension`, `image format`) or a 415; a 403 pauses the automatic uploads only when its text mentions token / auth / key (401 always). DIB header sizes are limited to 12, 40, 52, 56, 64, 108, 124. The upload's file check allows 2 s of write-time difference. With the image copy off, `AnnounceSaved` records the clipboard sequence and the link copy uses the same gate. A close deferred during an editor save stands aside when the quit resolves the editor (`ResolvingQuit`). The import's network-folder rule also catches a mapped network drive (`GetDriveType` = remote, `ScreenshotService.IsNetworkFolder`).
- **S7 fix rounds.** `ConsentGate.ConsentChanged` (raised after Enable / Don't ask again and after `SetCloudSharing`) makes the Screenshots tab re-read its upload / copy-link boxes (`MainWindow.ScreenshotChecksFor`, also on `ShowFromTray`; skipped while the pop-up for that very box is up); `ScreenshotOptionChanged` writes `ScreenshotUpload` / `ScreenshotCopyLink` only from their own box, so a magnifier toggle cannot undo an auto-upload the pop-up enabled. The import compares and applies `ScreenshotUpload` against this install's consent (their `CopiumConsent` is replaced by ours first; no yes here: it is no difference and arrives false). An upload stopped by the kill switch is neutral: clip outcome `Declined` ("Not shared: cloud sharing is off"), screenshot toast without Upload / Retry (`IScreenshotUploadToast.Declined`); "Cloud sharing is off" shows once per flip, not once per queued screenshot. `IsTokenRejection` no longer matches a bare "key" (token / auth only).
- Open: the Capture tab holds the screenshot hotkeys, and the Clips tab holds the only Copium token box, so with Clips off neither can be edited until Clips is turned back on (the token link then says so). S8 reworks the library pane.

## S8 implementation notes (library: screenshots list)

- **Delete / Recycle Bin.** ScreenshotService.Recycler is Action<string, nint> (full path, owner window handle; the pane passes Window.GetWindow(pane), else Application.MainWindow, and   falls back to the OwnerHandle seam). The shell call is SHFileOperation with FOF_ALLOWUNDO | FOF_WANTNUKEWARNING: a file that cannot go to the Recycle Bin needs a second, explicit confirmation from Windows (declining leaves the file and reports 'not deleted'). The STA thread is joined **without a timeout** (the shell may be waiting on that dialog; a timeout would report a failure while the file is later deleted), one shell operation runs at a time, and the row's IsDeleting stays set until the call returns.
- **Unremovable rows.** An entry whose path is a directory or a link (exists, but is not an openable file) is removed from the list by 'Remove from list' without touching the disk.
- **Thumbnails.** A row has a version, bumped when its file changes; a decode that started at an older version is dropped.
- **Keyboard.** The screenshots ListBox has no tab stop of its own and TabNavigation / DirectionalNavigation = Continue, so Tab walks every row's buttons; text search is off.
