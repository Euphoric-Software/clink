# Clink brand reference

One page with everything needed to make something that looks like Clink: which logo file to use where, the colours, the fonts, the words and the interface language. Clink's brand is part of the Euphoric Software system. The logo artwork is in this folder: vector masters (SVG) with PNG renders. The app's own icon is `Clink/Assets/clink.ico`, which `tools/make-icon.ps1` builds from the icon SVGs.

## Logo system

| Variant | Use | Files (in this folder unless a path is given) |
| --- | --- | --- |
| Primary logo (the wordmark: the ring C with a hollow four-point spark in place of a dot, then "link") | Hero, headers, marketing. Made for dark backgrounds | `clink-wordmark-on-dark.svg`: the vector master; the ring is Spark Blue `#6F9CC4`, the letters and the spark are Light `#E9EBF2`. PNG render: `clink-wordmark-on-dark.png` (1800 x 669 px) |
| Light-background logo | Light backgrounds and print | `clink-wordmark-on-light.svg`: the ring is Software Blue `#4D7EA8`, the letters and the spark are Charcoal `#272932`. PNG render: `clink-wordmark-on-light.png` (1800 x 669 px) |
| App icon (dark badge) | Compact use | `clink-icon.svg`: the vector master (a Charcoal `#272932` rounded square with the Spark Blue ring and a Light spark); `clink-icon-512.png`: its transparent 512 px render |
| App icon (light badge) | Compact use on dark surfaces where the dark badge would vanish | `clink-icon-light.svg`: a Light `#E9EBF2` rounded square with the Software Blue ring and a Charcoal spark |
| Mark only | A symbol on its own, over any background you control | `clink-mark.svg`: the ring and spark with a transparent background |
| Small-size cuts | The icon drawn at exactly 16, 20, 24, 28, 30 or 32 px | `clink-icon-16.svg`, `clink-icon-20.svg`, `clink-icon-24.svg`, `clink-icon-28.svg`, `clink-icon-30.svg` and `clink-icon-32.svg`: `clink-icon.svg` with the spark filled solid, because its hollow centre turns grey below 36 px |
| Windows icon | Desktop, taskbar, launchers | `Clink/Assets/clink.ico`: 20 frames (16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 54, 56, 60, 64, 72, 80, 84, 96, 128 and 256 px), the sizes Windows asks for at 100 to 250 % display scaling. Generated from the icon SVGs by `tools/make-icon.ps1` |

Which asset where: `clink-wordmark-on-dark.svg` on Deep, Charcoal or any other dark background; `clink-wordmark-on-light.svg` on white and light backgrounds and in print; `clink-icon.svg` (or `clink-icon-512.png`) where only the mark fits, using the small-size cut (16 to 32 px) when it is drawn at exactly that size; `clink.ico` for the executable, the window, taskbar and tray icons. Use the SVGs wherever SVG is accepted and the PNGs only where a raster is needed (`clink-wordmark-on-dark.png` and `clink-wordmark-on-light.png`, 1800 x 669 px, scale down from these). `tools/make-icon.ps1` (Windows PowerShell 5.1, nothing else to install) rebuilds `clink.ico` and `clink-icon-512.png` from the icon SVGs, with the same bytes on every run.

In the app: the main window's header shows the text "Clink", not the wordmark, because the spark blurs at about 20 px. The executable (`<ApplicationIcon>` in `Clink/Clink.csproj`), the main, trim and share windows and the tray icon carry the app icon. `Clink/Services/AppIcons.cs` gives them the frame of exactly the size the shell draws (the small icon and the tray icon at 16 / 20 / 24 / 28 / 32 px, the taskbar button at 1.5 times that, at 100 / 125 / 150 / 175 / 200 % scaling) instead of letting the shell shrink a bigger one, so the tray shows the small-size cuts at 100 to 200 %. The README switches between the two wordmarks with a `<picture>` element (`prefers-color-scheme`): `clink-wordmark-on-dark.svg` for dark and `clink-wordmark-on-light.svg` for light.

## Family

Clink sits beside Euphoric Software and Copium.pics. The spark is the shared signature. The Clink spark is an interpretation of Euphoric's symbol: if an official mark is supplied, replace the master and the six small-size cuts and rerun `tools/make-icon.ps1`.

## Colour palette

| Name | Hex | Role |
| --- | --- | --- |
| Deep | `#1B1D24` | Backgrounds |
| Charcoal | `#272932` | Surfaces and cards |
| Spark Blue | `#6F9CC4` | Primary accent |
| Software Blue | `#4D7EA8` | Secondary and brand blue; artwork on light backgrounds |
| Light | `#E9EBF2` | Text and highlights |
| Muted | `#9EA4B3` | Secondary text |
| Link Blue | `#8EB1D2` on dark, `#365F85` on light | Links and focus rings |

On the Light theme the accent fill is `#44739C` (Software Blue deepened so white text on it reads 5.0:1).

## Typography

- **Inter**: primary UI font.
- **Sora**: display font for headings and brand moments.

Do not bundle or download the fonts; reference them by name with fallbacks, e.g. `Inter, Segoe UI Variable, Segoe UI`. Euphoric's Outfit is reserved for the Copium wordmark.

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

- Primary button: Spark Blue fill with Deep text.
- Secondary button: Charcoal with a subtle outline.
- Toggles: Spark Blue when on, grey when off.
- Dropdowns: on Charcoal.
- Tabs / chips: the active one is filled.
- Status indicators: green check "Capturing"; amber dot "Waiting for a game..."
- Toast: "Clip saved" / "Your clip is on disk."
