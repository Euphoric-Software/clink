<#
  Standalone test of tools\check-runtime-freshness.ps1 (offline; no Pester). Exits 0 when all pass, 1 otherwise.
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\runtime-freshness.tests.ps1
  Works on Windows PowerShell 5.1 and PowerShell 7. Writes only to a fresh temp folder. Versions are made up (not tied to the pin).
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tool = Join-Path $repoRoot 'tools\check-runtime-freshness.ps1'
$hostExe = (Get-Process -Id $PID).Path
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('freshness-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

$script:Passed = 0
$script:Failed = 0

function Invoke-Tool {
    param([string[]]$Arguments)
    $output = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $tool @Arguments 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Output = $output }
}

function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail)
    if ($Condition) { $script:Passed++; Write-Host "PASS  $Name" }
    else { $script:Failed++; Write-Host "FAIL  $Name  $Detail" }
}

# A minimal project.assets.json with the given downloadDependencies entries (name -> version text, e.g. '[10.0.5, 10.0.5]').
function New-AssetsFile {
    param([string]$Name, [hashtable]$Packs, [switch]$NoFrameworks)
    $entries = @()
    foreach ($key in ($Packs.Keys | Sort-Object)) { $entries += ('{ "name": "' + $key + '", "version": "' + $Packs[$key] + '" }') }
    $json = if ($NoFrameworks) { '{ "version": 3, "project": { } }' }
            else { '{ "version": 3, "project": { "frameworks": { "net10.0-windows10.0.26100.0": { "downloadDependencies": [ ' + ($entries -join ', ') + ' ] } } } }' }
    $path = Join-Path $scratch $Name
    [System.IO.File]::WriteAllBytes($path, $utf8NoBom.GetBytes($json))
    return $path
}

$net = 'Microsoft.NETCore.App.Runtime.win-x64'
$desktop = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'

try {
    $current = New-AssetsFile 'current.json' @{ $net = '[10.0.5, 10.0.5]'; $desktop = '[10.0.5, 10.0.5]'; 'Microsoft.AspNetCore.App.Runtime.win-x64' = '[10.0.1, 10.0.1]' }

    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.5')
    Assert-That 'Equal versions are ok' ($r.Code -eq 0 -and $r.Output -match 'OK:' -and $r.Output -notmatch '::warning::') $r.Output
    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.5', '-FailOnStale')
    Assert-That 'Equal versions are ok with -FailOnStale' ($r.Code -eq 0) $r.Output

    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.6')
    Assert-That 'Behind: a ::warning:: line and exit 0 without -FailOnStale' ($r.Code -eq 0 -and $r.Output -match '::warning::' -and $r.Output -match 'make-notices\.ps1 -Update -RuntimeVersion 10\.0\.6 -Accept' -and $r.Output -match 'global\.json') $r.Output
    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.6', '-FailOnStale')
    Assert-That 'Behind with -FailOnStale: exit 1, names global.json and make-notices -Update, no ::warning::' ($r.Code -eq 1 -and $r.Output -match 'global\.json' -and $r.Output -match '-Update -RuntimeVersion 10\.0\.6 -Accept' -and $r.Output -match 'MinimumRuntimeVersion' -and $r.Output -notmatch '::warning::') $r.Output

    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.4', '-FailOnStale')
    Assert-That 'Ahead of latest (metadata lags) is ok even with -FailOnStale' ($r.Code -eq 0) $r.Output
    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '9.9.99', '-FailOnStale')
    Assert-That 'A lower major is ok' ($r.Code -eq 0) $r.Output
    $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', '10.0.10', '-FailOnStale')
    Assert-That 'Versions compare numerically (10.0.5 is behind 10.0.10)' ($r.Code -eq 1) $r.Output

    $oneStale = New-AssetsFile 'one-stale.json' @{ $net = '[10.0.6, 10.0.6]'; $desktop = '[10.0.5, 10.0.5]' }
    $r = Invoke-Tool @('-AssetsFile', $oneStale, '-LatestVersion', '10.0.6', '-FailOnStale')
    Assert-That 'One stale pack of two fails and names it' ($r.Code -eq 1 -and $r.Output -match [regex]::Escape("$desktop 10.0.5")) $r.Output

    $noDesktop = New-AssetsFile 'no-desktop.json' @{ $net = '[10.0.5, 10.0.5]' }
    foreach ($extra in @(@(), @('-FailOnStale'))) {
        $r = Invoke-Tool (@('-AssetsFile', $noDesktop, '-LatestVersion', '10.0.5') + $extra)
        Assert-That "A missing runtime pack is a failure ($($extra -join ' '))" ($r.Code -eq 1 -and $r.Output -match [regex]::Escape($desktop)) $r.Output
    }
    $noNet = New-AssetsFile 'no-net.json' @{ $desktop = '[10.0.5, 10.0.5]' }
    $r = Invoke-Tool @('-AssetsFile', $noNet, '-LatestVersion', '10.0.5')
    Assert-That 'The other missing pack is a failure too' ($r.Code -eq 1 -and $r.Output -match [regex]::Escape($net)) $r.Output
    $none = New-AssetsFile 'none.json' @{}
    $r = Invoke-Tool @('-AssetsFile', $none, '-LatestVersion', '10.0.5')
    Assert-That 'No downloadDependencies at all is a failure' ($r.Code -eq 1) $r.Output
    $noFrameworks = New-AssetsFile 'no-frameworks.json' @{} -NoFrameworks
    $r = Invoke-Tool @('-AssetsFile', $noFrameworks, '-LatestVersion', '10.0.5')
    Assert-That 'No project.frameworks is a failure' ($r.Code -eq 1 -and $r.Output -match 'project\.frameworks') $r.Output

    $range = New-AssetsFile 'range.json' @{ $net = '[10.0.5, 10.0.9)'; $desktop = '[10.0.5, 10.0.5]' }
    $r = Invoke-Tool @('-AssetsFile', $range, '-LatestVersion', '10.0.5')
    Assert-That 'A version range that is not an exact pin is a failure' ($r.Code -eq 1 -and $r.Output -match 'exact') $r.Output
    $pre = New-AssetsFile 'pre.json' @{ $net = '[10.0.5-rc.1, 10.0.5-rc.1]'; $desktop = '[10.0.5, 10.0.5]' }
    $r = Invoke-Tool @('-AssetsFile', $pre, '-LatestVersion', '10.0.5')
    Assert-That 'A pre-release pin is a failure' ($r.Code -eq 1) $r.Output

    $r = Invoke-Tool @('-AssetsFile', (Join-Path $scratch 'missing.json'), '-LatestVersion', '10.0.5')
    Assert-That 'A missing assets file is a failure' ($r.Code -eq 1) $r.Output
    $broken = Join-Path $scratch 'broken.json'
    [System.IO.File]::WriteAllBytes($broken, $utf8NoBom.GetBytes('{ not json'))
    $r = Invoke-Tool @('-AssetsFile', $broken, '-LatestVersion', '10.0.5')
    Assert-That 'A broken assets file is a failure' ($r.Code -eq 1) $r.Output
    foreach ($bad in '10.0', '10.0.5-rc.1', 'abc', '10.0.5.1') {
        $r = Invoke-Tool @('-AssetsFile', $current, '-LatestVersion', $bad)
        Assert-That "A bad -LatestVersion '$bad' is a failure" ($r.Code -ne 0) $r.Output
    }

    # The real restore of the repo (when there is one): the script must read it without error (ok or stale, never "missing").
    $real = Join-Path $repoRoot 'Clink\obj_sc\project.assets.json'
    if (Test-Path -LiteralPath $real) {
        $r = Invoke-Tool @('-AssetsFile', $real, '-LatestVersion', '10.0.0')
        Assert-That 'The real obj_sc restore is readable and not behind 10.0.0' ($r.Code -eq 0) $r.Output
    }

    $bytes = [System.IO.File]::ReadAllBytes($tool)
    Assert-That 'The script has no BOM and only ASCII' (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF) -and -not @($bytes | Where-Object { $_ -gt 127 }).Count) ''
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("runtime freshness tests: {0} passed, {1} failed" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
exit 0
