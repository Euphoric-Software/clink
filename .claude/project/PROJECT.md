# Clink

Windows replay-buffer clipper. .NET 10 WPF app using Windows Graphics Capture,
D3D11 and Media Foundation for capture/encode; clips are MP4 (clk1 brand).

## Build
- `.\build.ps1 -Configuration Debug`
- `dotnet` may not be on PATH; fallback `%USERPROFILE%\.dotnet\dotnet.exe`
- No .sln file.

## Tests
- In-exe: `Clink.exe --unittest [filter]`
- 763 in-exe tests pass (as of 6795381, feature/screenshots)
- Manual GUI testing: `Clink.exe --dev <folder> --dev-hotkeys` isolates settings/folders from the installed app; never run the installed Clink (`C:\Users\Jason\OneDrive\Documents\Clink.exe`) for tests.
- Env: `CLINK_TESTS_NO_SKIP=1` (fail on skipped), `CLINK_EMULATE_ELST=1` (edit-list emulation)

## Misc
- Version: `<Version>` in `Clink/Clink.csproj` (currently 0.3.0)
- CI: `.github/workflows/build.yml`; `.github/workflows/release-dev.yml` (dev prereleases on push to `dev`)
- Remote push policy: `dev` — push allowed after each reviewed slice (user instruction 2026-10-25); `main` — always ask.

