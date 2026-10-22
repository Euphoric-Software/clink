# Clink brand reference

One page with everything needed to make something that looks like Clink: which logo file to use where, the colours, the fonts, the words and the interface language. The logo artwork is in this folder: vector masters (SVG) with PNG renders. The app's own icon is `Clink/Assets/clink.ico`, which `tools/make-icon.ps1` builds from the icon SVGs.

## Logo system

| Variant | Use | Files (in this folder unless a path is given) |
| --- | --- | --- |
| Primary logo (the wordmark: the ring C with its dot, then "link") | Hero, headers, marketing. Made for dark backgrounds | `clink-wordmark-on-dark.svg`: the vector master; the ring is cyan, the letters and the dot in the C are `#FCFCFC`. PNG renders: `clink-wordmark-on-dark-1200.png` (1200 x 446 px) and `clink-wordmark-on-dark@2x.png` (215 x 80 px, the 2x export for a 40 px tall logo) |
| Icon only | Compact use | `clink-icon.svg`: the vector master (a `#111111` rounded square with the cyan ring and a `#FCFCFC` dot); `clink-icon-512.png`: its transparent 512 px render. For the smallest sizes there are pixel-hinted cuts: `clink-icon-16.svg`, `clink-icon-20.svg`, `clink-icon-24.svg` and `clink-icon-28.svg` |
| Light-background logo | Light backgrounds and print | `clink-wordmark-on-light.svg`: the wordmark with its white parts (the letters and the dot in the C) in Midnight `#0A0E14`; the cyan ring stays cyan. PNG renders: `clink-wordmark-on-light-1200.png` (1200 x 446 px) and `clink-wordmark-on-light@2x.png` (215 x 80 px) |
| App icon | Desktop, taskbar, launchers | `Clink/Assets/clink.ico`: 20 frames (16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 54, 56, 60, 64, 72, 80, 84, 96, 128 and 256 px), the sizes Windows asks for at 100 to 250 % display scaling. Generated from the icon SVGs by `tools/make-icon.ps1` |

Which asset where: `clink-wordmark-on-dark.svg` on Midnight, Slate or any other dark background; `clink-wordmark-on-light.svg` on white and light backgrounds and in print; `clink-icon.svg` (or `clink-icon-512.png`) where only the mark fits, using the pixel-hinted `clink-icon-16.svg`, `clink-icon-20.svg`, `clink-icon-24.svg` or `clink-icon-28.svg` when it is drawn at exactly that size; `clink.ico` for the executable, the window, taskbar and tray icons. Use the SVGs wherever SVG is accepted and the PNGs only where a raster is needed: the 1200 px one where a large image is wanted, the `@2x` one (80 px tall) for a logo shown 40 px tall. `tools/make-icon.ps1` (Windows PowerShell 5.1, nothing else to install) rebuilds `clink.ico` and `clink-icon-512.png` from the icon SVGs, with the same bytes on every run.

In the app: the main window's header shows the text "Clink", not the wordmark, because the wordmark's i-dot blurs at about 20 px. The executable (`<ApplicationIcon>` in `Clink/Clink.csproj`), the main, trim and share windows and the tray icon carry the app icon. `Clink/Services/AppIcons.cs` gives them the frame of exactly the size the shell draws (the small icon and the tray icon at 16 / 20 / 24 / 28 / 32 px, the taskbar button at 1.5 times that, at 100 / 125 / 150 / 175 / 200 % scaling) instead of letting the shell shrink a bigger one, so the tray shows the pixel-hinted cuts at 100 to 175 %. The README switches between the two wordmarks with a `<picture>` element (`prefers-color-scheme`): `clink-wordmark-on-dark.svg` for dark and `clink-wordmark-on-light.svg` for light.

## Colour palette

| Name | Hex | Role |
| --- | --- | --- |
| Midnight | `#0A0E14` | Primary background |
| Slate | `#1A1F2B` | Surfaces and cards |
| Electric Cyan | `#22D3EE` | Primary accent |
| Blue Cyan | `#3B82F6` | Secondary accent |
| Cool White | `#EAF2FF` | Text and highlights |

The supplied icon and wordmark artwork samples cyan at about `#36CAFA`; the UI accent uses the brand board's `#22D3EE`.

## Typography

- **Inter**: primary UI font.
- **Sora**: display font for headings and brand moments.

Do not bundle or download the fonts; reference them by name with fallbacks, e.g. `Inter, Segoe UI Variable, Segoe UI`.

## Brand keywords

- **Fast**: instant capture, minimal delay.
- **Lightweight**: high performance, low overhead.
- **Precise**: capture what matters.
- **Creator-ready**: built for sharing, clipping and content.
- **Game-focused**: designed for gamers, by gamers.

## Taglines

- Primary: "Press a key. Hear a click. The clip is on disk."
- Secondary: "Capture your best moments."
- Secondary: "Same game. More moments."

## UI language

- Primary button: Electric Cyan fill with dark text.
- Secondary button: Slate with a subtle outline.
- Toggles: cyan when on, grey when off.
- Dropdowns: on Slate.
- Tabs / chips: the active one is filled.
- Status indicators: green check "Capturing"; amber dot "Waiting for a game..."
- Toast: "Clip saved" / "Your clip is on disk."
