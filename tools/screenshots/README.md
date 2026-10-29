# Documentation screenshots

`capture.ps1` produces fresh pictures of the current Clink UI for `docs/screenshots`: no TEST marker, drawn sample clips instead of real
ones, a recording header ("Recording · NeonDrift") without recording anything, and nothing ever shown on the screen.

## Requirements

- Windows PowerShell 5.1 (`powershell.exe`; the helper `ShotNative.cs` is compiled with `Add-Type -ReferencedAssemblies System.Drawing`, so it is C# 5 only).
- A built `Clink.exe` of this branch (`.\build.ps1 -Configuration Release`, or Debug). Without `-Exe`, the newer of the two is used.
- Run the game check first (the project's local game-check script): the script starts a real Clink process and must not run next to a game
  or a full-screen app.

## What it never does

- Sends input (no keys, no mouse, no clicks): it only uses UI Automation patterns (select a tab, toggle a chevron, invoke a button, resize).
- Activates a window or takes the foreground. A watcher samples the foreground window every 100 ms; if the test instance holds it
  for 200 ms in a row, the watcher stops that one process and the run ends.
- Captures the screen or the desktop. Each window is drawn by `PrintWindow` from its own contents.
- Touches your real clips, settings or account. The sample clips are drawn frames (`Clink.exe --dev-make-samples <folder> --dev`; it refuses to run without `--dev`) in a scratch
  `--dev` folder; the instance is `--dev --dev-quiet`, which starts paused, silent and off-screen.
- Records anything. `--dev-demo-status` only changes the header text (and shows the demo game as detected). The settings pictures show the
  normal install's locations (`%APPDATA%\Clink`, `%USERPROFILE%\Videos\Clink`) instead of the scratch folder; that is display only and never saved.
  `--dev-no-marker` cannot be combined with `--dev-quiet-record`. The sample generator is stopped after 120 s, and refuses a non-empty clips
  folder it did not make (it leaves a `.clink-samples` marker file in the folders it made).
- Overwrites `docs/screenshots`. Output goes to `-OutputDir`, and the script refuses a folder inside `docs/` of this checkout or of the main checkout (when this one is a git worktree). The check follows junctions and symbolic links to the final location, before anything is created or deleted; `-WorkDir` is checked the same way (it may not be inside `docs/` or the repository).
- Stops any process but the one it started, or deletes anything but the scratch dev folder it created. It never presses "Copy link" or
  the folder button.

## Usage

```powershell
.\tools\screenshots\capture.ps1 -OutputDir $env:TEMP\clink-shots
```

| Parameter | Meaning |
| --- | --- |
| `-Exe` | The `Clink.exe` to run. Default: the newer of the Release and Debug builds. |
| `-OutputDir` | Where the PNGs go. Default: `%TEMP%\clink-doc-shots\out`. Never inside `docs/` (links are followed). |
| `-WorkDir` | Scratch folder; the script makes `dev` in it (and deletes it again). Default: `%TEMP%\clink-doc-shots\work`. |
| `-Game` | The game named in the header. Default: `NeonDrift`. |
| `-WideSize` | `WIDTHxHEIGHT` in DIP for the wide layout. Default: 1600 wide, with the proportions of `docs/screenshots/main-window.png` (1600x914). |
| `-NarrowSize` | `WIDTHxHEIGHT` in DIP for the narrow layout. Default: `470x800`. |
| `-DumpTree` | Also writes `ui-tree.txt` (the UI Automation tree) and lists new windows while waiting. |

The PNG size is the DIP size times the window's DPI scale (a 200 % display gives 3200 px for the wide shots).

## Outputs

In order: `main-window.png`, `clip-tab.png` (Clips tab), `app-tab.png`, `alerts-tab.png`, `toast.png`, `trim-editor.png`,
`share-window.png`, `narrow.png` (Capture tab), `narrow-library.png`. The names follow the files already in `docs/screenshots`; a
picture that cannot be made is skipped with a warning. At the end the script prints the output folder, each PNG with its size, whether
the foreground watcher tripped, and the longest time the test instance held the foreground (in ms).

Copy the pictures you want into `docs/screenshots` by hand after looking at them.

## Troubleshooting

- **A PNG is blank (warning "is blank")**: the window was drawn black or transparent. Toast and share windows are normally kept out of
  captures with display affinity; that is skipped for those two windows only, in `--dev-quiet --dev-no-marker` instances (every other window stays excluded). If it still happens, re-run; if only that window,
  check that it is not minimised.
- **A tray icon of the test instance lingers** after the script stopped it: Windows removes a dead process's icon only when the mouse passes over it. Hover the tray area once.
- **Wrong size or blurry**: the script switches its own thread to per-monitor DPI awareness before resizing. If the log says
  "falling back to SetWindowPos", the window did not allow `TransformPattern.Resize`; the size is still set in physical pixels.
- **"timed out waiting for ..."**: the window did not appear in time (slow first start, or the sample index was not read). Look at the
  `samples.txt` log in the work folder and run with `-DumpTree`.
- **"STOPPED: the test instance took the foreground"**: the watcher stopped the instance. Nothing more is done; check what stole focus
  and run again when nothing else is going on.

## Known limits

- The game name must not start with `dev` or `-` (the dev switch parser would take it for an option).
- The sample clips have no audio.
- Only the first Preview, Trim and Share buttons are used; the toast is the "clip saved" one.
