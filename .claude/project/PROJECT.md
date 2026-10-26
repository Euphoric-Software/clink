# Clink

Windows replay-buffer clipper. .NET 10 WPF app using Windows Graphics Capture,
D3D11 and Media Foundation for capture/encode; clips are MP4 (clk1 brand).

## Build
- `.\build.ps1 -Configuration Debug`
- `dotnet` may not be on PATH; fallback `%USERPROFILE%\.dotnet\dotnet.exe`
- No .sln file.

## Tests
- In-exe: `Clink.exe --unittest [filter]`
- 776 in-exe tests pass (dev, after merging the capture border notice)
- Manual GUI testing: `Clink.exe --dev <folder> --dev-hotkeys` isolates settings/folders from the installed app; never run the installed Clink (`C:\Users\Jason\OneDrive\Documents\Clink.exe`) for tests.
- Env: `CLINK_TESTS_NO_SKIP=1` (fail on skipped), `CLINK_EMULATE_ELST=1` (edit-list emulation)

## Misc
- Version: `<Version>` in `Clink/Clink.csproj` (currently 0.4.0; main at v0.3.1, dev line v0.3.2-dev.N)
- CI: `.github/workflows/build.yml`; `.github/workflows/release-dev.yml` (dev prereleases on push to `dev`)
- Remote push policy: `dev` — push allowed after each reviewed slice (user instruction 2026-10-25); `main` — always ask.

## Chat / session naming
- Title every Claude session `v<target version> <Topic>`, e.g. `v0.3.2 Yellow Outline Recording Indicator`.
- Target version = the next stable Clink version the branch ships in: csproj `<Version>` if the branch bumps it, otherwise latest stable release + 1 patch (release-dev.yml uses the same rule).
- Omit the `-dev.N` suffix: release-dev.yml assigns N from the workflow run number / last N+1 at build time, so it is not known when a chat starts.
- Pass this title as the session `name`/`title` whenever a coordinator starts a session.
