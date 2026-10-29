# Regression guard on the client source (read-only; the C# files are only READ as text, nothing is built or run): the app never contacts
# GitHub, and the updater makes its HTTP requests through one client. Standalone: exits 0 when everything passed, 1 otherwise.
# Windows PowerShell 5.1 and pwsh 7, no modules. The rules live in tools/lib/client-guard-lib.ps1 (build.yml uses the same file).
#   1. No client source mentions "github" in any case (code, string or comment). Client source = every Clink\**\*.cs, recursively, except
#      Clink\Tests\** and build output folders (bin*, obj*); Clink\MainWindow.xaml.cs is part of it.
#   2. Clink\Services\Updater.cs creates at most ONE HttpClient and at most ONE SocketsHttpHandler (`new HttpClient(`, and the
#      target-typed `HttpClient x = new(` form, are both counted).
# Strictness: until the client slice that REMOVES the GitHub update path (S6) is on this branch, the source legitimately still has that path
# (and its second HttpClient). Its marker is a DEFINITION of VerifyGitHubReleaseAsync (a Task<...> method of that name; a comment, a call or a
# cref that only names it does not count). While a definition exists both checks are reported as SKIPPED, loudly, and the test passes; the
# moment it is gone both are strict. CLINK_GUARD_STRICT=1 makes them strict immediately; build.yml sets it by itself once the definition is gone.
# -Root <folder>: check another tree (the script tests run the guard on scratch trees); default: this repository.
param([string]$Root)
$ErrorActionPreference = 'Stop'
$repo = if ($Root) { (Resolve-Path -LiteralPath $Root).Path } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\client-guard-lib.ps1')
$servicesDir = Join-Path $repo 'Clink\Services'
$mainWindow = Join-Path $repo 'Clink\MainWindow.xaml.cs'
$updaterPath = Join-Path $servicesDir 'Updater.cs'
$script:failures = New-Object System.Collections.Generic.List[string]
$script:passed = 0

$files = @(Get-ClientGuardSourceFiles -Root $repo)
if (-not (Test-Path -LiteralPath $mainWindow -PathType Leaf)) { Write-Host "note: $mainWindow does not exist (renamed?); every other client source is still checked" }
if (@($files | Where-Object { $_ -like '*\Services\*' }).Count -eq 0) { $script:failures.Add("no sources found under $servicesDir; the guard has nothing to look at") }

$legacyFiles = @($files | Where-Object { Test-ClientLegacyDefinition -Text ([System.IO.File]::ReadAllText($_)) })
$legacy = $legacyFiles.Count -gt 0
$strict = ($env:CLINK_GUARD_STRICT -ceq '1') -or -not $legacy
$why = if ($legacy) { "the legacy GitHub update path is still defined (VerifyGitHubReleaseAsync in $(($legacyFiles | ForEach-Object { $_.Substring($repo.Length + 1) }) -join ', '))" } else { '' }
Write-Host "client sources: $($files.Count) file(s); mode: $(if ($strict) { 'STRICT' } else { 'lenient while ' + $why })"

# 1. No "github" anywhere (case-insensitive).
$hits = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $lineNo = 0
    foreach ($line in ([System.IO.File]::ReadAllText($f) -split "`r?`n")) {
        $lineNo++
        if ($line -match '(?i)github') { $hits.Add("$($f.Substring($repo.Length + 1)):${lineNo}: $($line.Trim())") }
    }
}
if ($hits.Count -eq 0) { $script:passed++; Write-Host "PASS  no 'github' in $($files.Count) client source file(s) (Clink\**\*.cs without Tests, bin*, obj*)" }
elseif ($strict) { foreach ($h in $hits) { $script:failures.Add("mentions GitHub: $h") } }
else { Write-Host "(skipped: $($hits.Count) mention(s) of GitHub remain because $why; set CLINK_GUARD_STRICT=1 to enforce)" }

# 2. One HTTP client in Updater.cs.
if (Test-Path -LiteralPath $updaterPath -PathType Leaf) {
    $count = Get-ClientHttpCreationCount -Text ([System.IO.File]::ReadAllText($updaterPath))
    $problem = ''
    if ($count.Clients -gt 1) { $problem = "Updater.cs creates $($count.Clients) HttpClient instances (at most 1: one client for every update request)" }
    elseif ($count.Handlers -gt 1) { $problem = "Updater.cs creates $($count.Handlers) SocketsHttpHandler instances (at most 1)" }
    if (-not $problem) { $script:passed++; Write-Host "PASS  Updater.cs: $($count.Clients) HttpClient and $($count.Handlers) SocketsHttpHandler creation(s) (at most one of each)" }
    elseif ($strict) { $script:failures.Add($problem) }
    else { Write-Host "(skipped: $problem, because $why; set CLINK_GUARD_STRICT=1 to enforce)" }
} else {
    Write-Host 'note: Clink\Services\Updater.cs does not exist (renamed?); the single-HTTP-client check was not run'
}

# The guard itself must catch what it is for (so a refactor of this script cannot silently turn it off).
$sample = 'var a = new HttpClient(h); var b = new HttpClient (h2); private static readonly HttpClient s_x = new() { }; HttpClient? y = new(h3); var s = new SocketsHttpHandler { }; internal static readonly SocketsHttpHandler H = new() { };'
$sampleCount = Get-ClientHttpCreationCount -Text $sample
if ($sampleCount.Clients -ne 4 -or $sampleCount.Handlers -ne 2) { $script:failures.Add("self-check: the HttpClient / SocketsHttpHandler patterns count $($sampleCount.Clients)/$($sampleCount.Handlers), expected 4/2") }
if ((Get-ClientHttpCreationCount -Text 'HttpClient x = s_shared; // HttpClient.Timeout').Clients -ne 0) { $script:failures.Add('self-check: a use of HttpClient was counted as a creation') }
if (-not (Test-ClientLegacyDefinition -Text "    private async Task<UpdateInfo?> VerifyGitHubReleaseAsync(UpdateInfo info, CancellationToken ct)")) { $script:failures.Add('self-check: the legacy definition is not recognised') }
foreach ($mention in '    /// The signed path (<see cref="VerifyGitHubReleaseAsync"/>) picks the release', '            info = await VerifyGitHubReleaseAsync(info, assets, trust, ct).ConfigureAwait(false);', '    // Task<UpdateInfo?> VerifyGitHubReleaseAsync( was removed') {
    if (Test-ClientLegacyDefinition -Text $mention) { $script:failures.Add("self-check: a mention was taken for the legacy definition: $($mention.Trim())") }
}
if (-not ('api.GitHub.com' -match '(?i)github') -or -not ('GITHUB' -match '(?i)github')) { $script:failures.Add('self-check: the github pattern is not case-insensitive') }

if ($script:failures.Count -gt 0) {
    foreach ($f in $script:failures) { Write-Host "FAIL  $f" }
    Write-Host "$($script:passed) passed, $($script:failures.Count) failed"
    exit 1
}
Write-Host "$($script:passed) passed, 0 failed"
exit 0
