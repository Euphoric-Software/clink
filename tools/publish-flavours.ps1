# Publishes both Clink flavours with SEPARATE intermediate and output folders (a shared obj/bin mixed the flavours and produced an
# exe that failed with 0x80008096):
#   dist\sc\Clink.exe   self-contained, compressed single file (the portable download)
#   dist\fx\Clink.exe   framework-dependent single file (the installer's exe; needs the .NET 10 Desktop Runtime)
# Folders obj_sc, bin_sc, obj_fx, bin_fx live next to Clink.csproj (the csproj excludes obj_*/bin_* from compilation, .gitignore
# ignores them). Each flavour's own folders are deleted before it is published.
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [string]$Version,      # -p:Version=<Version> when given (CI)
    [string]$UpdateRepo,   # -p:UpdateRepo=<owner/repo> when given (CI)
    [string]$MinStable,    # -p:ClinkMinStable=<X.Y.Z>: the embedded stable floor seed (CI: newest published stable). Empty = no seed (local builds, dry run)
    [string]$MinDev,       # -p:ClinkMinDev=<X.Y.Z or X.Y.Z-dev.N>: the embedded dev floor seed. Empty = no seed
    [string]$OutRoot,      # default <repo>\dist; must be inside the repo (its sc and fx subfolders are deleted first)
    [string]$DotNet        # default: dotnet on PATH if it satisfies global.json, else %USERPROFILE%\.dotnet\dotnet.exe
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projDir = Join-Path $root 'Clink'
$proj = Join-Path $projDir 'Clink.csproj'
function Resolve-OutRoot {
    # Full path of the output root; refuses anything that is not strictly inside the repo (the flavour folders in it get deleted).
    param([string]$Path, [string]$RepoRoot)
    $full = [System.IO.Path]::GetFullPath($(if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path (Get-Location).ProviderPath $Path })).TrimEnd('\', '/')
    $repoFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    if (-not $full.StartsWith($repoFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) { throw "-OutRoot '$full' must be inside the repo '$repoFull'." }
    $full
}

function Remove-FlavourDirectory {
    # Deletes a folder tree, but refuses when the folder or anything inside it is a reparse point (junction or symlink), so the
    # delete can never reach through a link into another folder.
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to delete '$Path': it is a reparse point (junction or symlink)." }
    if (-not $item.PSIsContainer) { Remove-Item -LiteralPath $Path -Force; return }
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($item.FullName)
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        foreach ($entry in (New-Object System.IO.DirectoryInfo $dir).EnumerateFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to delete '$Path': '$($entry.FullName)' inside it is a reparse point." }
            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry.FullName) }
        }
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Assert-NoReparseParent {
    # Refuses a reparse point (junction or symlink) on -Path or on any existing folder between -Path and the repo root (the root
    # itself is the caller's checkout and is not checked): the flavour folders are deleted below -Path, so a link on the way
    # could redirect that delete outside the repo.
    param([string]$Path, [string]$RepoRoot)
    $repoFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $dir = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    while ($dir.Length -gt $repoFull.Length -and $dir.StartsWith($repoFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $dir) {
            if (((Get-Item -LiteralPath $dir -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "'$dir' is a reparse point (junction or symlink); refusing to publish below it." }
        }
        $dir = Split-Path -Parent $dir
    }
}

function Test-DotNetSdk {
    # True when "<dotnet> --version", run in the repo root, succeeds: that honours global.json and fails when the pinned SDK is missing.
    param([string]$Exe, [string]$WorkingDirectory)
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $Exe --version 2>$null
        return ($LASTEXITCODE -eq 0 -and [bool]$output)
    } catch { return $false } finally { Pop-Location }
}

if (-not $OutRoot) { $OutRoot = Join-Path $root 'dist' }
$OutRoot = Resolve-OutRoot -Path $OutRoot -RepoRoot $root
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "'$Version' is not a valid version" }
if ($UpdateRepo -and $UpdateRepo -notmatch '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$') { throw "'$UpdateRepo' is not an owner/repo" }

# The seeds become MSBuild property values: exactly a version and nothing else (a ';' would add a property). \A..\z, not ^..$.
foreach ($seed in @(@('MinStable', $MinStable, $false), @('MinDev', $MinDev, $true))) {
    if ($seed[1] -and $seed[1] -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?\z') { throw "-$($seed[0]) is not a version (X.Y.Z or X.Y.Z-dev.N)." }
    if ($seed[1] -and -not $seed[2] -and $seed[1].Contains('-dev.')) { throw "-$($seed[0]) must be a stable version (X.Y.Z)." }
}

if (-not $DotNet) {
    $DotNet = 'dotnet'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-DotNetSdk -Exe 'dotnet' -WorkingDirectory $root)) {
        $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
        if ((Test-Path -LiteralPath $local) -and (Test-DotNetSdk -Exe $local -WorkingDirectory $root)) { $DotNet = $local }
        else { throw "No .NET SDK satisfying global.json found (PATH dotnet or $local). Install the pinned SDK, e.g. dotnet-install.ps1 -Version <x>." }
    }
}

Assert-NoReparseParent -Path $OutRoot -RepoRoot $root
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$OutRoot = Resolve-OutRoot -Path (Resolve-Path -LiteralPath $OutRoot).Path -RepoRoot $root
Assert-NoReparseParent -Path $OutRoot -RepoRoot $root
$flavours = @(
    [pscustomobject]@{ Name = 'sc'; SelfContained = 'true' },
    [pscustomobject]@{ Name = 'fx'; SelfContained = 'false' }
)
# Every dotnet call runs with the repo root as the working directory, so global.json (the pinned SDK) applies whatever the caller's
# current directory is.
Push-Location -LiteralPath $root
try {
foreach ($f in $flavours) {
    $out = Join-Path $OutRoot $f.Name
    foreach ($dir in (Join-Path $projDir "obj_$($f.Name)"), (Join-Path $projDir "bin_$($f.Name)"), $out) {
        Remove-FlavourDirectory -Path $dir
    }
    $arguments = @('publish', $proj, '-c', $Configuration, "-p:SelfContained=$($f.SelfContained)",
        "-p:BaseIntermediateOutputPath=obj_$($f.Name)\", "-p:OutputPath=bin_$($f.Name)\", '-o', $out)
    if ($f.SelfContained -eq 'true') { $arguments += '-p:EnableCompressionInSingleFile=true' }
    if ($Version) { $arguments += "-p:Version=$Version" }
    if ($UpdateRepo) { $arguments += "-p:UpdateRepo=$UpdateRepo" }
    if ($MinStable) { $arguments += "-p:ClinkMinStable=$MinStable" }
    if ($MinDev) { $arguments += "-p:ClinkMinDev=$MinDev" }
    Write-Host "== publish $($f.Name) (SelfContained=$($f.SelfContained)) -> $out"
    & $DotNet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($($f.Name)) failed with exit code $LASTEXITCODE" }
    $exe = Join-Path $out 'Clink.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "$exe was not produced" }
    '{0}: {1:N0} bytes' -f $exe, (Get-Item -LiteralPath $exe).Length
}
} finally { Pop-Location }
