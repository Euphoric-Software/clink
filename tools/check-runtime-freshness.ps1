<#
.SYNOPSIS
  Checks that the .NET runtime bundled into the self-contained Clink.exe is not older than the latest runtime.
.DESCRIPTION
  Reads the two runtime packs the self-contained publish restored (Microsoft.NETCore.App.Runtime.win-x64 and
  Microsoft.WindowsDesktop.App.Runtime.win-x64, exact "[x, x]" pins in project.frameworks.<tfm>.downloadDependencies of the
  obj_sc project.assets.json) and compares their versions with -LatestVersion (the "version" tools/resolve-dotnet-runtime.ps1
  printed). Equal or newer: ok (exit 0). Older: with -FailOnStale exit 1 with the bump steps, otherwise a GitHub
  "::warning::" line and exit 0. A missing pack, an unreadable file or a version that is not x.y.z is always a failure (exit 1).
  Offline; works on Windows PowerShell 5.1 and PowerShell 7.
.PARAMETER AssetsFile
  Clink\obj_sc\project.assets.json (written by tools/publish-flavours.ps1).
.PARAMETER LatestVersion
  x.y.z, the latest .NET 10 runtime (the resolver's version).
.PARAMETER FailOnStale
  Stable releases: a stale bundled runtime fails the run. Dev builds leave this off and only warn.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$LatestVersion,
    [switch]$FailOnStale
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$packs = @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')

function Get-JsonMember {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

try {
    if ($LatestVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "-LatestVersion '$LatestVersion' is not x.y.z." }
    $latest = [version]$LatestVersion
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AssetsFile)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Assets file not found: $path" }
    $assets = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json

    $found = @{}
    $frameworks = Get-JsonMember -Object (Get-JsonMember -Object $assets -Name 'project') -Name 'frameworks'
    if ($null -eq $frameworks) { throw "$path has no project.frameworks (is this the obj_sc project.assets.json of a self-contained restore?)." }
    foreach ($framework in $frameworks.PSObject.Properties) {
        foreach ($entry in @(Get-JsonMember -Object $framework.Value -Name 'downloadDependencies')) {
            if ($null -eq $entry) { continue }
            $name = [string](Get-JsonMember -Object $entry -Name 'name')
            if ($packs -cnotcontains $name) { continue }
            $range = [string](Get-JsonMember -Object $entry -Name 'version')
            $pin = [regex]::Match($range, '\A\[\s*([0-9]+\.[0-9]+\.[0-9]+)\s*,\s*([0-9]+\.[0-9]+\.[0-9]+)\s*\]\z')
            if (-not $pin.Success -or $pin.Groups[1].Value -cne $pin.Groups[2].Value) {
                throw "$name has a downloadDependencies version that is not an exact x.y.z pin: '$range'"
            }
            $pinned = $pin.Groups[1].Value
            if (-not $found.ContainsKey($name)) { $found[$name] = @() }
            if ($found[$name] -cnotcontains $pinned) { $found[$name] += $pinned }
        }
    }
    foreach ($pack in $packs) {
        if (-not $found.ContainsKey($pack)) { throw "$pack is not in downloadDependencies of ${path}: this is not the restore of a self-contained publish, so the bundled runtime cannot be checked." }
    }

    $stale = @()
    foreach ($pack in $packs) {
        foreach ($version in $found[$pack]) {
            Write-Host "$pack $version (latest $LatestVersion)"
            if ([version]$version -lt $latest) { $stale += "$pack $version" }
        }
    }
    if ($stale.Count -eq 0) {
        Write-Host "OK: the bundled runtime is current (latest $LatestVersion)."
        exit 0
    }
    $fix = "The self-contained Clink.exe would bundle an outdated .NET runtime ($($stale -join '; ')); the latest is $LatestVersion. " +
        "To fix: install a .NET SDK that ships runtime $LatestVersion and set it in global.json; restore and publish (tools\publish-flavours.ps1); " +
        "run tools\notices\make-notices.ps1 -Update -RuntimeVersion $LatestVersion -Accept, then -Generate, review the diff and run -Check; " +
        "raise `$script:MinimumRuntimeVersion in tools\lib\dotnet-runtime-lib.ps1 to $LatestVersion; commit all of it together (docs/RELEASING.md, Runtime freshness)."
    if ($FailOnStale) {
        Write-Host "::error::Bundled .NET runtime is stale: $fix"
        exit 1
    }
    Write-Host "::warning::Bundled .NET runtime is behind: $fix"
    exit 0
}
catch {
    Write-Host "check-runtime-freshness failed: $($_.Exception.Message)"
    exit 1
}
