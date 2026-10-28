<#
.SYNOPSIS
  Builds and checks THIRD-PARTY-NOTICES.txt (the one superset notices file of Clink).

.DESCRIPTION
  tools\notices\sources.json lists every notice in order: where its text comes from and the SHA-256 it must have.

  -Generate  Writes THIRD-PARTY-NOTICES.txt from sources.json: NuGet cache files ($env:NUGET_PACKAGES or
             ~\.nuget\packages), the repo's LICENSE, and https://raw.githubusercontent.com files (pinned to a full
             commit or tag) fetched into a fresh empty temp folder. Every source's raw SHA-256 is verified first; a
             mismatch stops the run. The output is deterministic (UTF-8 without BOM, CRLF).
  -Update    -RuntimeVersion 10.0.N: re-pins rows 1-5 (the runtime packs and the WPF / WinForms notices of the same
             dotnet/dotnet commit, read from the pack's *.versions.txt) and prints old and new hashes. It only
             writes sources.json when -Accept is added. Afterwards run -Generate and review the diff.
  -Check     Offline, case-sensitive. Verifies the file's sections against sources.json (order, header blocks, SHA-256
             of each body) and the repo's LICENSE (-RepoRoot) against its pinned hash. With -AssetsFile
             <project.assets.json> it also parses the JSON and requires the downloadDependencies versions of the two
             runtime packs to equal runtimeVersion, of Microsoft.Windows.SDK.NET.Ref to equal section [8]'s version and
             the Microsoft.Windows.CsWin32 library to equal section [6b]'s version. A file without project.frameworks,
             without downloadDependencies, or without either runtime pack FAILS (CI must be looking at the self-contained
             restore), as does a missing Microsoft.Windows.SDK.NET.Ref download dependency or a missing Microsoft.Windows.CsWin32 library;
             -AllowMissingRuntimePacks turns all of those into notes (local use only, never used by CI).
             Exit 1 when something is off: after a runtime drift it prints the -Update command to run, for other
             failures it suggests -Generate.

  Works on Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding()]
param(
    [switch]$Generate,
    [switch]$Update,
    [switch]$Check,
    [string]$RuntimeVersion,
    [switch]$Accept,
    [string]$AssetsFile,
    [switch]$AllowMissingRuntimePacks,
    [string]$NoticesFile,
    [string]$SourcesFile,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Every path parameter is made absolute against the session's current location once, here, so .NET file calls never
# fall back on the process working directory.
function Resolve-FullPath {
    param([string]$Path)
    if (-not $Path) { return $Path }
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$RepoRoot = Resolve-FullPath $RepoRoot
if (-not $NoticesFile) { $NoticesFile = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.txt' }
if (-not $SourcesFile) { $SourcesFile = Join-Path $PSScriptRoot 'sources.json' }
$NoticesFile = Resolve-FullPath $NoticesFile
$SourcesFile = Resolve-FullPath $SourcesFile
$AssetsFile = Resolve-FullPath $AssetsFile
$script:RepoRoot = $RepoRoot

# The rule line of a header block: exactly 80 "=" (ThirdPartyNotices.RuleLine in Clink uses the same; both parse the same file).
$script:RuleLine = '=' * 80
$script:Utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------- helpers

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($hasher.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

# The "text" form of a source: UTF-8 without BOM, line ends normalised to LF.
function ConvertTo-NormalizedText {
    param([byte[]]$Bytes)
    $text = $script:Utf8Strict.GetString($Bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-TextSha256Hex {
    param([string]$Text)
    return Get-Sha256Hex -Bytes ($script:Utf8NoBom.GetBytes($Text))
}

function Get-NuGetRoot {
    if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
    return (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages')
}

function Get-NuGetFilePath {
    param([string]$Package, [string]$Version, [string]$RelativePath)
    return (Join-Path (Join-Path (Join-Path (Get-NuGetRoot) $Package.ToLowerInvariant()) $Version) $RelativePath)
}

function Read-Sources {
    $json = [System.IO.File]::ReadAllText($SourcesFile, $script:Utf8NoBom)
    return ($json | ConvertFrom-Json)
}

function Get-IncludedInText {
    param($Section)
    # A case-sensitive map; an unknown code is an error, never silently dropped.
    $names = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $names.Add('SC', 'self-contained Clink.exe')
    $names.Add('FX', 'Clink-framework.exe')
    $names.Add('SETUP', 'setup.exe')
    $names.Add('NONE', 'none (build-time only; listed for information)')
    $codes = @($Section.includedIn)
    if ($codes.Count -eq 0) { throw "Section $($Section.id) has no includedIn code" }
    $list = @()
    foreach ($code in $codes) {
        if (-not $names.ContainsKey([string]$code)) { throw "Unknown includedIn code '$code' in section $($Section.id)" }
        $list += $names[[string]$code]
    }
    if ($codes.Count -gt 1 -and ($codes -ccontains 'NONE')) { throw "includedIn NONE must be the only code in section $($Section.id)" }
    return ($list -join ', ')
}

function Get-SourceLine {
    param($Section)
    switch ($Section.kind) {
        'nuget'   { return ('NuGet package {0} {1}, file {2}' -f $Section.package, $Section.version, $Section.path) }
        'url'     { return $Section.url }
        'repo'    { return ('Clink repository, file {0}' -f $Section.path) }
        'pointer' { return ('Pointer to {0} {1}' -f $Section.package, $Section.version) }
        default   { throw "Unknown kind '$($Section.kind)' in section $($Section.id)" }
    }
}

# The header block of a section as LF text, ending with the closing rule line and a newline.
function Get-HeaderText {
    param($Section)
    return ($script:RuleLine + "`n" + ('[{0}] {1}' -f $Section.id, $Section.title) + "`n" +
        'Included in: ' + (Get-IncludedInText -Section $Section) + "`n" +
        'Source: ' + (Get-SourceLine -Section $Section) + "`n" + $script:RuleLine + "`n")
}

function Get-PrefaceText {
    param($Sources)
    return ("Clink third-party notices`n" +
        "`n" +
        "Clink is free software under the MIT licence (section [0]). It includes or builds on the third-party`n" +
        "components below. Each section says which download carries it.`n" +
        "`n" +
        "Pinned .NET runtime version (self-contained Clink.exe): $($Sources.runtimeVersion)`n" +
        "`n")
}

function Get-PointerBodyText {
    param($Section)
    return ((@($Section.body) -join "`n") + "`n")
}

# The exact body text a section has in the file (LF), from its already-verified source bytes.
function Get-SectionBodyText {
    param($Section, [string]$FetchDirectory)
    switch ($Section.kind) {
        'pointer' { return Get-PointerBodyText -Section $Section }
        'repo'    { return ConvertTo-NormalizedText -Bytes ([System.IO.File]::ReadAllBytes((Join-Path $script:RepoRoot $Section.path))) }
        'nuget'   { return ConvertTo-NormalizedText -Bytes ([System.IO.File]::ReadAllBytes((Get-NuGetFilePath $Section.package $Section.version $Section.path))) }
        'url'     { return ConvertTo-NormalizedText -Bytes ([System.IO.File]::ReadAllBytes((Join-Path $FetchDirectory ('section-' + $Section.id)))) }
    }
}

# One section's text as it sits in the file (LF): header, blank line, body, and one blank separator line.
function Get-SectionFileText {
    param($Section, [string]$BodyText)
    $body = $BodyText
    if (-not $body.EndsWith("`n")) { $body += "`n" }
    return ((Get-HeaderText -Section $Section) + "`n" + $body + "`n")
}

function Assert-PinnedUrl {
    param([string]$Url)
    if (-not $Url.StartsWith('https://raw.githubusercontent.com/')) { throw "Not a raw.githubusercontent.com https URL: $Url" }
    if ($Url -cnotmatch '^https://raw\.githubusercontent\.com/[^/]+/[^/]+/([0-9a-f]{40}|[A-Za-z0-9._-]+)/') { throw "URL is not pinned to a commit or tag: $Url" }
    if ($Url -cmatch '/(main|master|dev|HEAD)/') { throw "URL is pinned to a moving branch: $Url" }
}

# A new, empty folder under the temp path for downloads (never executed; only read as bytes).
function New-FetchDirectory {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-notices-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    if (@(Get-ChildItem -LiteralPath $folder -Force).Count -ne 0) { throw "Fetch folder is not empty: $folder" }
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    return $folder
}

# One pinned download: no redirects (raw.githubusercontent.com serves a pinned path directly), 60 s timeout.
function Save-PinnedUrl {
    param([string]$Url, [string]$Target)
    Assert-PinnedUrl -Url $Url
    Invoke-WebRequest -Uri $Url -OutFile $Target -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 60
}

# Fetches the url sections into the given folder.
function Get-UrlSources {
    param($Sections, [string]$Folder)
    foreach ($section in $Sections) {
        if ($section.kind -cne 'url') { continue }
        Save-PinnedUrl -Url $section.url -Target (Join-Path $Folder ('section-' + $section.id))
    }
}

# Throws when the source bytes of a section do not have the pinned hashes.
function Assert-SourceHashes {
    param($Section, [string]$FetchDirectory)
    if ($Section.kind -eq 'pointer') {
        $actual = Get-TextSha256Hex -Text (Get-PointerBodyText -Section $Section)
        if ($actual -cne $Section.sha256Text) { throw "Section [$($Section.id)] pointer text hash is $actual, sources.json says $($Section.sha256Text)" }
        return
    }
    if ($Section.kind -ceq 'repo') { $path = Join-Path $script:RepoRoot $Section.path }
    elseif ($Section.kind -eq 'nuget') { $path = Get-NuGetFilePath $Section.package $Section.version $Section.path }
    else { $path = Join-Path $FetchDirectory ('section-' + $Section.id) }
    if (-not (Test-Path -LiteralPath $path)) { throw "Section [$($Section.id)] source file is missing: $path" }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($Section.kind -ne 'repo' -and $Section.sha256Raw) {
        $raw = Get-Sha256Hex -Bytes $bytes
        if ($raw -cne $Section.sha256Raw) { throw "Section [$($Section.id)] raw SHA-256 is $raw, sources.json says $($Section.sha256Raw). Not accepted." }
    }
    $text = Get-TextSha256Hex -Text (ConvertTo-NormalizedText -Bytes $bytes)
    if ($text -cne $Section.sha256Text) { throw "Section [$($Section.id)] text SHA-256 is $text, sources.json says $($Section.sha256Text). Not accepted." }
}

# ---------------------------------------------------------------- Generate

function Invoke-Generate {
    $sources = Read-Sources
    $fetchDirectory = New-FetchDirectory
    try {
        Get-UrlSources -Sections $sources.sections -Folder $fetchDirectory
        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append((Get-PrefaceText -Sources $sources))
        foreach ($section in $sources.sections) {
            Assert-SourceHashes -Section $section -FetchDirectory $fetchDirectory
            [void]$builder.Append((Get-SectionFileText -Section $section -BodyText (Get-SectionBodyText -Section $section -FetchDirectory $fetchDirectory)))
        }
        $crlf = $builder.ToString().Replace("`n", "`r`n")
        $outBytes = $script:Utf8NoBom.GetBytes($crlf)
        [System.IO.File]::WriteAllBytes($NoticesFile, $outBytes)
        Write-Host ("Wrote {0} ({1} sections, {2} characters, {3} bytes)" -f $NoticesFile, @($sources.sections).Count, $crlf.Length, $outBytes.Length)
    }
    finally {
        Remove-Item -LiteralPath $fetchDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- Check

# Splits the file text (LF) into sections: @{ Header; Id; Body }. Also returns the preface (text before the first header).
function Split-NoticesText {
    param([string]$Text)
    $lines = $Text.Split("`n")
    $starts = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i + 4 -lt $lines.Length; $i++) {
        if ($lines[$i] -ceq $script:RuleLine -and $lines[$i + 1] -cmatch '^\[[^\]]+\] .+$' -and $lines[$i + 2].StartsWith('Included in: ', [StringComparison]::Ordinal) -and
            $lines[$i + 3].StartsWith('Source: ', [StringComparison]::Ordinal) -and $lines[$i + 4] -ceq $script:RuleLine) { $starts.Add($i) }
    }
    $result = @{ Preface = ''; Sections = @() }
    if ($starts.Count -eq 0) { $result.Preface = $Text; return $result }
    $result.Preface = (($lines[0..($starts[0] - 1)]) -join "`n")
    if ($starts[0] -eq 0) { $result.Preface = '' } else { $result.Preface += "`n" }
    $found = @()
    for ($s = 0; $s -lt $starts.Count; $s++) {
        $first = $starts[$s]
        if ($s + 1 -lt $starts.Count) { $end = $starts[$s + 1] - 1 } else { $end = $lines.Length - 1 }
        $headerText = (($lines[$first..($first + 4)]) -join "`n") + "`n"
        $id = ($lines[$first + 1] -creplace '^\[([^\]]+)\].*$', '$1')
        $regionLines = @()
        if ($end -ge $first + 5) { $regionLines = $lines[($first + 5)..$end] }
        $region = ($regionLines -join "`n")
        $found += @{ Header = $headerText; Id = $id; Region = $region }
    }
    $result.Sections = $found
    return $result
}

# Reads a restore's project.assets.json (parsed as JSON, not searched as text). Returns @{ Downloads = name -> list of versions
# from project.frameworks.<tfm>.downloadDependencies ("[10.0.14, 10.0.14]" ranges, exact pins); Libraries = names of "libraries" }.
function Read-AssetsFile {
    param([string]$Path)
    $assets = [System.IO.File]::ReadAllText($Path, $script:Utf8NoBom) | ConvertFrom-Json
    $downloads = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[string]]'
    $badRanges = New-Object System.Collections.Generic.List[string]
    $project = Get-JsonMember -Object $assets -Name 'project'
    $frameworks = Get-JsonMember -Object $project -Name 'frameworks'
    $hasFrameworks = ($null -ne $frameworks -and @($frameworks.PSObject.Properties).Count -gt 0)
    $hasDownloads = $false
    if ($null -ne $frameworks) {
        foreach ($framework in $frameworks.PSObject.Properties) {
            $list = Get-JsonMember -Object $framework.Value -Name 'downloadDependencies'
            foreach ($entry in @($list)) {
                if ($null -eq $entry) { continue }
                $name = [string](Get-JsonMember -Object $entry -Name 'name')
                $range = [string](Get-JsonMember -Object $entry -Name 'version')
                if (-not $name) { continue }
                $hasDownloads = $true
                $version = $null
                if ($range -cmatch '^\[\s*([^,\s\]]+)\s*,\s*([^,\s\]]+)\s*\]$' -and $Matches[1] -ceq $Matches[2]) { $version = $Matches[1] }
                elseif ($range -cmatch '^[0-9][^,\s\[\]()]*$') { $version = $range }
                if ($null -eq $version) { $badRanges.Add("$name '$range'"); continue }
                if (-not $downloads.ContainsKey($name)) { $downloads.Add($name, (New-Object 'System.Collections.Generic.List[string]')) }
                if (-not $downloads[$name].Contains($version)) { $downloads[$name].Add($version) }
            }
        }
    }
    $libraries = New-Object System.Collections.Generic.List[string]
    $libraryObject = Get-JsonMember -Object $assets -Name 'libraries'
    if ($null -ne $libraryObject) { foreach ($property in $libraryObject.PSObject.Properties) { $libraries.Add($property.Name) } }
    return @{ Downloads = $downloads; Libraries = $libraries; BadRanges = $badRanges; HasFrameworks = $hasFrameworks; HasDownloads = $hasDownloads }
}

# $Object.$Name, or $null when the member is absent (strict mode throws on a missing property).
function Get-JsonMember {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Compares the restore with the pins of sources.json. Adds to $Problems; adds restored runtime versions that differ to $RuntimeDrift.
function Test-AssetsAgainstSources {
    param($Sources, [string]$Path, $Problems, $RuntimeDrift)
    if (-not (Test-Path -LiteralPath $Path)) { $Problems.Add("Assets file not found: $Path"); return }
    $assets = $null
    try { $assets = Read-AssetsFile -Path $Path } catch { $Problems.Add("Assets file is not readable JSON: $Path ($($_.Exception.Message))"); return }
    foreach ($bad in $assets.BadRanges) { $Problems.Add("Assets file: downloadDependencies version is not an exact pin: $bad") }
    $downloads = $assets.Downloads
    # CI must be looking at the self-contained restore: a missing section is a failure unless -AllowMissingRuntimePacks.
    if (-not $assets.HasFrameworks) {
        if ($AllowMissingRuntimePacks) { Write-Host 'note: the assets file has no project.frameworks; nothing to compare.' }
        else { $Problems.Add("Assets file has no project.frameworks (is it the obj_sc project.assets.json of a restore?): $Path") }
    }
    elseif (-not $assets.HasDownloads) {
        if ($AllowMissingRuntimePacks) { Write-Host 'note: the assets file has no downloadDependencies; nothing to compare.' }
        else { $Problems.Add("Assets file has no downloadDependencies in any framework (not the restore of a self-contained publish): $Path") }
    }
    foreach ($package in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        if (-not $downloads.ContainsKey($package)) {
            if ($AllowMissingRuntimePacks) { Write-Host "note: $package is not in downloadDependencies of any framework in the assets file; nothing to compare." }
            else { $Problems.Add("$package is not in downloadDependencies of any framework in the assets file: the bundled runtime cannot be checked.") }
            continue
        }
        foreach ($version in $downloads[$package]) {
            if ($version -cne [string]$Sources.runtimeVersion) {
                $Problems.Add("$package $version is restored (downloadDependencies) but the notices are for .NET runtime $($Sources.runtimeVersion).")
                if (-not $RuntimeDrift.Contains($version)) { $RuntimeDrift.Add($version) }
            }
        }
    }
    $sdkSection = @($Sources.sections | Where-Object { $_.id -ceq '8' })
    if ($sdkSection.Count -eq 1 -and $downloads.ContainsKey([string]$sdkSection[0].package)) {
        foreach ($version in $downloads[[string]$sdkSection[0].package]) {
            if ($version -cne [string]$sdkSection[0].version) { $Problems.Add("$($sdkSection[0].package) $version is restored but section [8] is for $($sdkSection[0].version): update section [8] in sources.json, then -Generate.") }
        }
    }
    elseif ($sdkSection.Count -eq 1) {
        if ($AllowMissingRuntimePacks) { Write-Host "note: $($sdkSection[0].package) is not in downloadDependencies of any framework in the assets file; nothing to compare." }
        else { $Problems.Add("$($sdkSection[0].package) is not in downloadDependencies of any framework in the assets file: the SDK reference cannot be checked against section [8].") }
    }
    $cswinSection = @($Sources.sections | Where-Object { $_.id -ceq '6b' })
    if ($cswinSection.Count -eq 1) {
        $prefix = [string]$cswinSection[0].package + '/'
        $restored = @($assets.Libraries | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | ForEach-Object { $_.Substring($prefix.Length) })
        if ($restored.Count -eq 0) {
            if ($AllowMissingRuntimePacks) { Write-Host "note: $($cswinSection[0].package) is not in libraries of the assets file; nothing to compare." }
            else { $Problems.Add("$($cswinSection[0].package) is not in libraries of the assets file: it cannot be checked against sections [6] and [6b].") }
        }
        foreach ($version in $restored) {
            if ($version -cne [string]$cswinSection[0].version) { $Problems.Add("$($cswinSection[0].package) $version is restored but sections [6] and [6b] are for $($cswinSection[0].version): update them in sources.json, then -Generate.") }
        }
    }
}

function Invoke-Check {
    $problems = New-Object System.Collections.Generic.List[string]
    $runtimeDrift = New-Object System.Collections.Generic.List[string]
    $sources = Read-Sources
    if (-not (Test-Path -LiteralPath $NoticesFile)) {
        $problems.Add("File is missing: $NoticesFile")
    }
    else {
        $bytes = [System.IO.File]::ReadAllBytes($NoticesFile)
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $problems.Add('File starts with a BOM.') }
        $text = $null
        try { $text = ConvertTo-NormalizedText -Bytes $bytes } catch { $problems.Add('File is not valid UTF-8.') }
        if ($null -ne $text) {
            $crlfBytes = $script:Utf8NoBom.GetBytes($text.Replace("`n", "`r`n"))
            if ((Get-Sha256Hex -Bytes $crlfBytes) -cne (Get-Sha256Hex -Bytes $bytes) -and -not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF)) { $problems.Add('File line ends are not CRLF throughout.') }
            if (-not $text.EndsWith("`n`n", [StringComparison]::Ordinal)) { $problems.Add('File does not end with a blank line.') }
            else { $text = $text.Substring(0, $text.Length - 1) }
            $parts = Split-NoticesText -Text $text
            if ($parts.Preface -cne (Get-PrefaceText -Sources $sources)) { $problems.Add('The text before the first section differs from what sources.json produces.') }
            $expected = @($sources.sections)
            $actual = @($parts.Sections)
            $ids = ($actual | ForEach-Object { $_.Id }) -join ','
            $wantIds = ($expected | ForEach-Object { $_.id }) -join ','
            if ($ids -cne $wantIds) { $problems.Add("Section order is [$ids], expected [$wantIds].") }
            else {
                for ($i = 0; $i -lt $expected.Count; $i++) {
                    $section = $expected[$i]
                    $found = $actual[$i]
                    if ($found.Header -cne (Get-HeaderText -Section $section)) { $problems.Add("Section [$($section.id)]: header differs from sources.json.") }
                    # The region is the blank line after the header, the body, and the blank separator line.
                    $region = $found.Region
                    if ($region.StartsWith("`n", [StringComparison]::Ordinal)) { $region = $region.Substring(1) } else { $problems.Add("Section [$($section.id)]: no blank line after the header.") }
                    # The region is the body as the generator wrote it: the source text, plus one LF when the source did not end with one.
                    $ok = ((Get-TextSha256Hex -Text $region) -ceq $section.sha256Text)
                    if (-not $ok -and $region.EndsWith("`n", [StringComparison]::Ordinal)) { $ok = ((Get-TextSha256Hex -Text $region.Substring(0, $region.Length - 1)) -ceq $section.sha256Text) }
                    if (-not $ok) { $problems.Add("Section [$($section.id)] $($section.title): body does not match its pinned SHA-256.") }
                }
            }
        }
    }
    # The repo's own licence (kind "repo") is the source of its section; it must still have the pinned hash (offline).
    foreach ($section in @($sources.sections)) {
        if ($section.kind -cne 'repo') { continue }
        $repoPath = Join-Path $script:RepoRoot $section.path
        if (-not (Test-Path -LiteralPath $repoPath)) { $problems.Add("Section [$($section.id)]: repo file is missing: $repoPath"); continue }
        $repoHash = Get-TextSha256Hex -Text (ConvertTo-NormalizedText -Bytes ([System.IO.File]::ReadAllBytes($repoPath)))
        if ($repoHash -cne $section.sha256Text) { $problems.Add("Section [$($section.id)]: repo file $($section.path) has SHA-256 $repoHash, sources.json says $($section.sha256Text).") }
    }
    if ($AssetsFile) { Test-AssetsAgainstSources -Sources $sources -Path $AssetsFile -Problems $problems -RuntimeDrift $runtimeDrift }
    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Host "FAIL: $problem" }
        $scriptPath = 'powershell -NoProfile -ExecutionPolicy Bypass -File tools\notices\make-notices.ps1'
        if ($runtimeDrift.Count -gt 0) {
            Write-Host "To re-pin the notices to the runtime in use, run: $scriptPath -Update -RuntimeVersion $($runtimeDrift[0]) -Accept   (then -Generate, and review the diff)"
        }
        else {
            Write-Host "To rebuild the file from sources.json, run: $scriptPath -Generate   (then review the diff)"
        }
        return 1
    }
    Write-Host ("OK: {0} sections verified against {1}" -f @($sources.sections).Count, (Split-Path -Leaf $SourcesFile))
    return 0
}

# ---------------------------------------------------------------- Update

function Invoke-Update {
    if (-not $RuntimeVersion -or $RuntimeVersion -notmatch '^\d+\.\d+\.\d+$') { throw '-Update needs -RuntimeVersion 10.0.N' }
    $sources = Read-Sources
    $netPack = 'Microsoft.NETCore.App.Runtime.win-x64'
    $desktopPack = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
    $netVersions = Get-NuGetFilePath $netPack $RuntimeVersion 'Microsoft.NETCore.App.versions.txt'
    $desktopVersions = Get-NuGetFilePath $desktopPack $RuntimeVersion 'Microsoft.WindowsDesktop.App.versions.txt'
    foreach ($path in @($netVersions, $desktopVersions)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Not in the NuGet cache: $path. Restore a self-contained build for $RuntimeVersion first." }
    }
    $newCommit = ([System.IO.File]::ReadAllLines($netVersions)[0]).Trim()
    $desktopCommit = ([System.IO.File]::ReadAllLines($desktopVersions)[0]).Trim()
    if ($newCommit -notmatch '^[0-9a-f]{40}$') { throw "First line of $netVersions is not a commit: $newCommit" }
    if ($newCommit -ne $desktopCommit) { throw "The two runtime packs name different commits ($newCommit vs $desktopCommit)." }
    $oldCommit = [string]$sources.vmrCommit
    $fetchDirectory = New-FetchDirectory
    try {
        Write-Host "runtimeVersion: $($sources.runtimeVersion) -> $RuntimeVersion"
        Write-Host "vmrCommit:      $oldCommit -> $newCommit"
        $changed = $false
        foreach ($section in $sources.sections) {
            $isPack = ($section.kind -eq 'nuget' -and ($section.package -eq $netPack -or $section.package -eq $desktopPack))
            $isVmrUrl = ($section.kind -eq 'url' -and $section.url.Contains($oldCommit))
            if (-not ($isPack -or $isVmrUrl)) { continue }
            if ($isPack) {
                $path = Get-NuGetFilePath $section.package $RuntimeVersion $section.path
                if (-not (Test-Path -LiteralPath $path)) { throw "Not in the NuGet cache: $path" }
                $bytes = [System.IO.File]::ReadAllBytes($path)
            }
            else {
                $newUrl = $section.url.Replace($oldCommit, $newCommit)
                $target = Join-Path $fetchDirectory ('section-' + $section.id)
                Save-PinnedUrl -Url $newUrl -Target $target
                $bytes = [System.IO.File]::ReadAllBytes($target)
            }
            $newRaw = Get-Sha256Hex -Bytes $bytes
            $newText = Get-TextSha256Hex -Text (ConvertTo-NormalizedText -Bytes $bytes)
            Write-Host ("[{0}] {1}" -f $section.id, $section.title)
            Write-Host ("    raw : {0} -> {1}" -f $section.sha256Raw, $newRaw)
            Write-Host ("    text: {0} -> {1}" -f $section.sha256Text, $newText)
            if ($newRaw -ne $section.sha256Raw -or $newText -ne $section.sha256Text) { $changed = $true }
            if ($Accept) {
                if ($isPack) { $section.version = $RuntimeVersion } else { $section.url = $newUrl }
                $section.sha256Raw = $newRaw
                $section.sha256Text = $newText
            }
        }
        if (-not $Accept) {
            Write-Host 'Nothing written. Review the hashes above (a changed hash means the licence text itself changed: read it), then re-run with -Accept.'
            return
        }
        $sources.runtimeVersion = $RuntimeVersion
        $sources.vmrCommit = $newCommit
        # Keep the file readable: rewrite only the changed values in the original text, so the layout and the other rows stay put.
        $original = [System.IO.File]::ReadAllText($SourcesFile, $script:Utf8NoBom)
        $rewritten = Update-SourcesText -Original $original -Sources $sources -OldCommit $oldCommit
        [System.IO.File]::WriteAllBytes($SourcesFile, $script:Utf8NoBom.GetBytes($rewritten))
        Write-Host "sources.json updated (changed: $changed). Now run -Generate, review the diff of THIRD-PARTY-NOTICES.txt, and run -Check."
    }
    finally {
        Remove-Item -LiteralPath $fetchDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Rewrites the pinned values of sources.json in place (so the hand-formatted file keeps its layout).
function Update-SourcesText {
    param([string]$Original, $Sources, [string]$OldCommit)
    $text = $Original
    $text = [regex]::Replace($text, '("runtimeVersion"\s*:\s*")[^"]*(")', ('${1}' + $Sources.runtimeVersion + '${2}'))
    $text = [regex]::Replace($text, '("vmrCommit"\s*:\s*")[^"]*(")', ('${1}' + $Sources.vmrCommit + '${2}'))
    $text = $text.Replace($OldCommit, [string]$Sources.vmrCommit)
    foreach ($section in $Sources.sections) {
        if ($section.kind -eq 'pointer' -or $section.kind -eq 'repo') { continue }
        $pattern = '("id"\s*:\s*"' + [regex]::Escape($section.id) + '"[\s\S]*?"sha256Raw"\s*:\s*")[^"]*("[\s\S]*?"sha256Text"\s*:\s*")[^"]*(")'
        $text = [regex]::Replace($text, $pattern, ('${1}' + $section.sha256Raw + '${2}' + $section.sha256Text + '${3}'), 1)
        if ($section.kind -eq 'nuget' -and ($section.package -like 'Microsoft.NETCore.App.Runtime*' -or $section.package -like 'Microsoft.WindowsDesktop.App.Runtime*')) {
            $versionPattern = '("id"\s*:\s*"' + [regex]::Escape($section.id) + '"[\s\S]*?"version"\s*:\s*")[^"]*(")'
            $text = [regex]::Replace($text, $versionPattern, ('${1}' + $section.version + '${2}'), 1)
        }
    }
    return $text
}

# ---------------------------------------------------------------- main

$modes = @($Generate, $Update, $Check) | Where-Object { $_ }
if (@($modes).Count -ne 1) {
    Write-Host 'Usage: make-notices.ps1 -Generate | -Update -RuntimeVersion 10.0.N [-Accept] | -Check [-AssetsFile <project.assets.json> [-AllowMissingRuntimePacks]] [-NoticesFile <path>]'
    exit 2
}
try {
    if ($Generate) { Invoke-Generate; exit 0 }
    if ($Update) { Invoke-Update; exit 0 }
    $code = Invoke-Check
    exit $code
}
catch {
    Write-Host ("ERROR: " + $_.Exception.Message)
    exit 1
}
