# Decisions

## Dev Build update channel
- Dev builds are immutable `vX.Y.Z-dev.N` prereleases (not a rolling tag), pruned to the newest 10.
- Version derivation: X.Y.Z = csproj version if above latest stable, else latest stable patch+1; N = max(run_number, highest existing dev.N + 1).
- The dev channel considers stable releases too; install rule is target > current (dev.N ordered numerically). A stable-channel install never installs a dev build.
- Leaving dev for an older stable release happens only on a manual check.
- `UseDevBuilds` and `PendingUpdateFrom` are per-installation settings and excluded from settings import.
- Clicking the footer version opens a popup ('Dev Build', 'Check for updates') instead of checking immediately.
