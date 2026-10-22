# Clink brand reference

One page with everything needed to make something that looks like Clink: which logo file to use where, the colours, the fonts, the words and the interface language. The artwork the app ships is in `Clink/Assets/`; the copies in this folder are for the README and the docs.

## Logo system

| Variant | Use | File |
| --- | --- | --- |
| Primary logo (icon + "Clink" wordmark) | Hero, headers, marketing. Made for dark backgrounds | `wordmark-dark.png` (here and in `Clink/Assets/`) |
| Icon only | Compact use | `icon-256.png` (here and in `Clink/Assets/`, which also has `icon-64.png` and `icon-32.png`) |
| Monochrome | Light backgrounds and print | `wordmark-light.png`: the wordmark with its white parts (the letters and the dot in the C) in Midnight `#0A0E14`; the cyan ring stays cyan |
| App icon | Desktop, taskbar, launchers | `Clink/Assets/clink.ico` (16, 20, 24, 32, 40, 48, 64, 128 and 256 px) |

Which asset where: `wordmark-dark.png` on Midnight, Slate or any other dark background; `wordmark-light.png` on white and light backgrounds and in print; `icon-256.png` (or the 64 / 32 px versions) where only the mark fits; `clink.ico` for the executable, the window, taskbar and tray icons.

In the app: the main window's header shows the wordmark, and the executable, the main and trim windows and the tray icon carry the app icon. The README switches between the two wordmarks with a `<picture>` element (`prefers-color-scheme`).

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
