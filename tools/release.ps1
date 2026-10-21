# Cuts a Clink release: sets <Version> in Clink\Clink.csproj, commits "Release vX.Y.Z",
# creates the annotated tag vX.Y.Z and pushes branch + tag. GitHub Actions
# (.github\workflows\release.yml) then builds Clink.exe and publishes the GitHub release.
#
#   .\tools\release.ps1 -Version 0.2.0
#   .\tools\release.ps1 -Version 0.2.0 -Message "Per-track gain, mono mic option"
#
# Refuses to run if the working tree has uncommitted changes (other than Clink.csproj,
# which this script edits) or if the tag already exists locally or on origin.
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$Message
)
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "'$Version' is not a version like 1.2.3 (or 1.2.3-beta.1). Leave out the leading 'v'."
}
$tag = "v$Version"
$root = Split-Path $PSScriptRoot -Parent
$projRel = 'Clink/Clink.csproj'
$proj = Join-Path $root 'Clink\Clink.csproj'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'git was not found on PATH. Install it with: winget install --id Git.Git -e   (then open a new terminal)'
}

# Runs git and returns its stdout. Check $LASTEXITCODE yourself; this never throws.
function Get-GitOutput([string[]]$GitArgs) {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & git @GitArgs } finally { $ErrorActionPreference = $eap }
}

# Runs git and throws if it fails.
function Invoke-Git([string[]]$GitArgs) {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & git @GitArgs } finally { $ErrorActionPreference = $eap }
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed (exit code $LASTEXITCODE)." }
}

Push-Location $root
try {
    # --- preconditions -------------------------------------------------------
    $top = Get-GitOutput @('rev-parse', '--show-toplevel')
    if ($LASTEXITCODE -ne 0 -or -not $top) { throw "$root is not a git repository. Run .\tools\publish-repo.ps1 first." }

    $origin = Get-GitOutput @('remote', 'get-url', 'origin')
    if ($LASTEXITCODE -ne 0 -or -not $origin) { throw "No 'origin' remote. Run .\tools\publish-repo.ps1 first." }

    $branch = Get-GitOutput @('symbolic-ref', '--quiet', '--short', 'HEAD')
    if ($LASTEXITCODE -ne 0 -or -not $branch) { throw 'HEAD is detached. Check out a branch first.' }

    $status = @(Get-GitOutput @('status', '--porcelain', '--untracked-files=no'))
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    $dirty = @($status | Where-Object { $_.Length -gt 3 -and $_.Substring(3) -ne $projRel })
    if ($dirty.Count -gt 0) {
        $dirty | ForEach-Object { Write-Host "  $_" }
        throw 'The working tree has uncommitted changes (listed above). Commit or stash them first.'
    }
    $untracked = @(Get-GitOutput @('ls-files', '--others', '--exclude-standard'))
    if ($untracked.Count -gt 0) {
        Write-Warning "Untracked files will not be part of this release: $($untracked -join ', ')"
    }

    if (Get-GitOutput @('tag', '--list', $tag)) { throw "Tag $tag already exists locally. Pick a new version." }
    $remoteTag = Get-GitOutput @('ls-remote', '--tags', 'origin', "refs/tags/$tag")
    if ($LASTEXITCODE -ne 0) { throw "Could not reach origin ($origin)." }
    if ($remoteTag) { throw "Tag $tag already exists on origin. Pick a new version." }

    # --- bump <Version> in Clink.csproj --------------------------------------
    $bytes = [System.IO.File]::ReadAllBytes($proj)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = New-Object System.Text.UTF8Encoding $hasBom
    $text = [System.IO.File]::ReadAllText($proj)
    $versionElement = New-Object System.Text.RegularExpressions.Regex '<Version>[^<]*</Version>'
    $match = $versionElement.Match($text)
    if (-not $match.Success) { throw "No <Version> element found in $proj." }
    $replacement = "<Version>$Version</Version>"
    if ($match.Value -ne $replacement) {
        $text = $versionElement.Replace($text, $replacement, 1)
        [System.IO.File]::WriteAllText($proj, $text, $encoding)
        Write-Host "Clink.csproj: $($match.Value) -> $replacement"
    }

    # --- commit, tag, push ---------------------------------------------------
    Invoke-Git @('add', '--', $projRel)
    Get-GitOutput @('diff', '--cached', '--quiet', '--', $projRel) | Out-Null
    if ($LASTEXITCODE -eq 1) {
        $commitArgs = @('commit', '-m', "Release $tag")
        if ($Message) { $commitArgs += @('-m', $Message) }
        Invoke-Git $commitArgs
    } else {
        Write-Host "Clink.csproj is already at $Version; tagging the current commit."
    }

    $tagMessage = "Clink $tag"
    if ($Message) { $tagMessage += "`n`n$Message" }
    Invoke-Git @('tag', '-a', $tag, '-m', $tagMessage)

    Write-Host "Pushing $branch and $tag to origin..."
    try {
        Invoke-Git @('push', 'origin', 'HEAD', '--follow-tags')
    } catch {
        Write-Host "Push failed. The commit and tag exist locally; after fixing the problem run: git push origin HEAD --follow-tags"
        throw
    }

    # --- where to watch ------------------------------------------------------
    $m = [regex]::Match($origin, 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$')
    Write-Host ''
    if ($m.Success) {
        $web = "https://github.com/$($m.Groups['owner'].Value)/$($m.Groups['repo'].Value)"
        Write-Host "Pushed $tag. GitHub Actions is building the release:"
        Write-Host "  $web/actions/workflows/release.yml"
        Write-Host 'When it finishes (a few minutes) the release is at:'
        Write-Host "  $web/releases/tag/$tag"
        Write-Host 'Running copies of Clink pick it up on their next update check.'
    } else {
        Write-Host "Pushed $tag to $origin. The release workflow runs there."
    }
} finally {
    Pop-Location
}
