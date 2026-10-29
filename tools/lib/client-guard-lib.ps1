# The client-source guard's rules (dot-source this file). Used by tools/tests/client-source-guard.tests.ps1 and by build.yml's script-test
# step (which turns the guard strict, CLINK_GUARD_STRICT=1, once Test-ClientLegacyUpdatePath is false), so the two cannot disagree about
# what "the legacy GitHub update path is still there" means. Read-only: C# files are read as text, nothing is built or run.
# Windows PowerShell 5.1 and PowerShell 7, no modules.

# A DEFINITION of the legacy method (S6 removes it): optional modifiers, Task<...> or ValueTask<...>, the name, an opening parenthesis, at the
# start of a line. A call, a <see cref=...> or a comment that only names the method does not match.
$script:ClientLegacyDefinitionPattern = '(?m)^[ \t]*(?:(?:private|public|internal|protected|static|async|override|virtual|sealed|new|unsafe|extern)[ \t]+)*(?:System\.Threading\.Tasks\.)?(?:Task|ValueTask)[ \t]*<[^;{}()\r\n]*>[ \t]+VerifyGitHubReleaseAsync[ \t]*\('

# One HTTP client: `new HttpClient(`, and the target-typed forms `HttpClient x = new(` / `HttpClient? x = new(` and a property
# `HttpClient X { get; } = new(`. The same three forms for SocketsHttpHandler.
$script:ClientHttpClientPattern = 'new\s+(?:global::)?(?:System\.Net\.Http\.)?HttpClient\s*\(|\b(?:System\.Net\.Http\.)?HttpClient\??\s+\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\('
$script:ClientHandlerPattern = 'new\s+(?:global::)?(?:System\.Net\.Http\.)?SocketsHttpHandler\b|\b(?:System\.Net\.Http\.)?SocketsHttpHandler\??\s+\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\('

function Get-ClientGuardSourceFiles {
    # Every Clink\**\*.cs under Root, recursively, EXCEPT Clink\Tests\** (the unit tests may name GitHub and make clients) and build output
    # folders (any folder named bin, obj, or bin_* / obj_* / bin-* / obj-*, where generated sources live). Sorted full paths.
    param([Parameter(Mandatory)] [string]$Root)
    $clink = Join-Path $Root 'Clink'
    if (-not (Test-Path -LiteralPath $clink -PathType Container)) { return @() }
    $base = (Resolve-Path -LiteralPath $clink).Path.TrimEnd('\', '/')
    @(Get-ChildItem -LiteralPath $base -Recurse -Filter '*.cs' -File | ForEach-Object { $_.FullName } | Where-Object {
            $rel = $_.Substring($base.Length + 1)
            $segments = @($rel -split '[\\/]')
            $dirs = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
            -not ($segments.Count -gt 1 -and $segments[0] -ieq 'Tests') -and -not (@($dirs | Where-Object { $_ -imatch '\A(bin|obj)([_-].*)?\z' }).Count -gt 0)
        } | Sort-Object)
}

function Test-ClientLegacyDefinition {
    # $true when the text DEFINES VerifyGitHubReleaseAsync (see ClientLegacyDefinitionPattern).
    param([AllowNull()] [AllowEmptyString()] [string]$Text)
    [regex]::IsMatch([string]$Text, $script:ClientLegacyDefinitionPattern)
}

function Test-ClientLegacyUpdatePath {
    # $true while any scanned client source (Get-ClientGuardSourceFiles) still defines the legacy GitHub update method.
    param([Parameter(Mandatory)] [string]$Root)
    foreach ($f in (Get-ClientGuardSourceFiles -Root $Root)) {
        if (Test-ClientLegacyDefinition -Text ([System.IO.File]::ReadAllText($f))) { return $true }
    }
    $false
}

function Get-ClientHttpCreationCount {
    # How many HttpClient and SocketsHttpHandler instances the text creates (every form above). Returns @{ Clients; Handlers }.
    param([AllowNull()] [AllowEmptyString()] [string]$Text)
    @{ Clients = [regex]::Matches([string]$Text, $script:ClientHttpClientPattern).Count; Handlers = [regex]::Matches([string]$Text, $script:ClientHandlerPattern).Count }
}
