# One-time setup: creates the GitHub repository for Clink and pushes this working copy to it.
#
#   .\tools\publish-repo.ps1                    -> public repo "clink" under your GitHub account
#   .\tools\publish-repo.ps1 -Name clink -Private
#
# Needs Git and the GitHub CLI, logged in:
#   winget install --id Git.Git -e
#   winget install --id GitHub.cli -e
#   gh auth login
#
# Clink's in-app updater reads GitHub Releases anonymously, so auto-update only works
# while the repository is public.
param(
    [string]$Name = 'clink',
    [switch]$Private
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

foreach ($tool in @(@{ Cmd = 'git'; Id = 'Git.Git' }, @{ Cmd = 'gh'; Id = 'GitHub.cli' })) {
    if (-not (Get-Command $tool.Cmd -ErrorAction SilentlyContinue)) {
        throw "$($tool.Cmd) was not found on PATH. Install it with: winget install --id $($tool.Id) -e   (then open a new terminal)"
    }
}

# Runs a program and returns its stdout. Check $LASTEXITCODE yourself; this never throws.
function Get-ToolOutput([string]$Exe, [string[]]$ToolArgs) {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @ToolArgs } finally { $ErrorActionPreference = $eap }
}

# Runs a program and throws if it fails.
function Invoke-Tool([string]$Exe, [string[]]$ToolArgs) {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @ToolArgs } finally { $ErrorActionPreference = $eap }
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($ToolArgs -join ' ') failed (exit code $LASTEXITCODE)." }
}

Push-Location $root
try {
    # --- GitHub login --------------------------------------------------------
    Write-Host 'Checking GitHub CLI login...'
    Get-ToolOutput 'gh' @('auth', 'status')
    if ($LASTEXITCODE -ne 0) { throw 'Not logged in to GitHub. Run: gh auth login   (answer yes to "Authenticate Git with your GitHub credentials")' }

    # --- local repository ----------------------------------------------------
    $top = Get-ToolOutput 'git' @('rev-parse', '--show-toplevel')
    if ($LASTEXITCODE -ne 0 -or -not $top) {
        Write-Host "Initializing a git repository in $root"
        Invoke-Tool 'git' @('init')
    } elseif ((Resolve-Path $top).Path.TrimEnd('\') -ne $root.TrimEnd('\')) {
        throw "$root is inside another git repository ($top). Expected the repository root to be $root."
    }

    $existing = Get-ToolOutput 'git' @('remote', 'get-url', 'origin')
    if ($LASTEXITCODE -eq 0 -and $existing) {
        throw "This repository already has an 'origin' remote ($existing). Nothing to create; push with: git push -u origin main"
    }

    $email = Get-ToolOutput 'git' @('config', 'user.email')
    if (-not $email) {
        throw 'git has no identity yet. Run:  git config --global user.name "Your Name"  and  git config --global user.email you@example.com'
    }

    Get-ToolOutput 'git' @('rev-parse', '--verify', '--quiet', 'HEAD') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # No commits yet: make sure the first branch is "main" (build.yml watches it) and commit everything.
        Invoke-Tool 'git' @('symbolic-ref', 'HEAD', 'refs/heads/main')
        Write-Host 'Creating the initial commit...'
        Invoke-Tool 'git' @('add', '-A')
        Invoke-Tool 'git' @('commit', '-m', 'Initial commit')
    } else {
        $dirty = @(Get-ToolOutput 'git' @('status', '--porcelain'))
        if ($dirty.Count -gt 0) { Write-Warning 'There are uncommitted changes; they will not be pushed until you commit them.' }
    }

    $branch = Get-ToolOutput 'git' @('rev-parse', '--abbrev-ref', 'HEAD')
    if ($branch -ne 'main') { Write-Warning "Current branch is '$branch'. The build workflow runs on pushes to 'main'." }

    # --- create and push -----------------------------------------------------
    $visibility = if ($Private) { '--private' } else { '--public' }
    Write-Host "Creating GitHub repository '$Name' ($($visibility.TrimStart('-'))) and pushing '$branch'..."
    try {
        Invoke-Tool 'gh' @('repo', 'create', $Name, $visibility, '--source', '.', '--remote', 'origin', '--push',
            '--description', 'Tiny Windows replay-buffer clipper for games')
    } catch {
        Write-Host 'If the repository was created but the push was rejected, run:  gh auth setup-git  then  git push -u origin main'
        throw
    }

    $url = Get-ToolOutput 'gh' @('repo', 'view', '--json', 'url', '--jq', '.url')
    if ($LASTEXITCODE -ne 0 -or -not $url) { $url = (Get-ToolOutput 'git' @('remote', 'get-url', 'origin')) -replace '\.git$', '' }
    $nameWithOwner = Get-ToolOutput 'gh' @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')
    if ($LASTEXITCODE -ne 0 -or -not $nameWithOwner) { $nameWithOwner = "<owner>/$Name" }

    # --- next steps ----------------------------------------------------------
    Write-Host ''
    Write-Host "Done. Repository: $url"
    Write-Host "  Build workflow:   $url/actions/workflows/build.yml   (compiles every push to main)"
    Write-Host "  Release workflow: $url/actions/workflows/release.yml (runs when a vX.Y.Z tag is pushed)"
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. Publish the first release:'
    Write-Host '         .\tools\release.ps1 -Version 0.1.0'
    Write-Host '     That tags v0.1.0 and pushes it. GitHub Actions builds Clink.exe, writes Clink.exe.sha256 and'
    Write-Host "     creates the release at $url/releases (takes a few minutes)."
    Write-Host '  2. Install Clink.exe from that release (a locally built exe does not know which repository to'
    Write-Host '     watch). Put it anywhere and run it; SmartScreen may warn once because the exe is not signed.'
    Write-Host '  3. Turn on Settings > Run at startup so Clink is recording as soon as you log in. It checks'
    Write-Host '     GitHub Releases on start and every 6 hours, checks a new Clink.exe against its .sha256 and'
    Write-Host '     installs it the next time it starts (or right away via "Update to vX.Y.Z" in the footer/tray).'
    if ($Private) {
        Write-Host ''
        Write-Warning "The repository is private. Clink's updater reads releases anonymously, so auto-update will not see them until the repository is public:"
        Write-Host "         gh repo edit $nameWithOwner --visibility public --accept-visibility-change-consequences"
    }
} finally {
    Pop-Location
}
