<#
  Standalone test of tools\notices\make-notices.ps1 -Check (offline; no Pester). Exits 0 when all pass, 1 otherwise.
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\make-notices.tests.ps1
  Works on Windows PowerShell 5.1 and PowerShell 7. Writes only to a fresh temp folder.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tool = Join-Path $repoRoot 'tools\notices\make-notices.ps1'
$committed = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'
$hostExe = (Get-Process -Id $PID).Path
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('notices-tests-' + [Guid]::NewGuid().ToString('N'))
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

function Write-TextFile {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllBytes($Path, $utf8NoBom.GetBytes($Text))
}

try {
    $original = [System.IO.File]::ReadAllText($committed, $utf8NoBom)

    $result = Invoke-Tool -Arguments @('-Check')
    Assert-That 'Check passes on the committed file' ($result.Code -eq 0) $result.Output

    # A body changed by one character.
    $tamperedPath = Join-Path $scratch 'tampered.txt'
    $at = $original.IndexOf('Permission is hereby granted')
    $tampered = $original.Substring(0, $at) + 'Permission is hereby Granted' + $original.Substring($at + 'Permission is hereby granted'.Length)
    Write-TextFile -Path $tamperedPath -Text $tampered
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $tamperedPath)
    Assert-That 'Check fails on a tampered body' ($result.Code -eq 1) $result.Output

    # A header changed (the claimed source).
    $headerPath = Join-Path $scratch 'header.txt'
    Write-TextFile -Path $headerPath -Text ($original.Replace("`r`nSource: NuGet package", "`r`nSource: Elsewhere package"))
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $headerPath)
    Assert-That 'Check fails on a changed header' ($result.Code -eq 1) $result.Output

    # Sections 1 and 2 swapped.
    $parts = [regex]::Split($original, '(?=={80}\r\n\[)')
    $swapped = ($parts[0] + $parts[2] + $parts[1] + (($parts | Select-Object -Skip 3) -join ''))
    $orderPath = Join-Path $scratch 'order.txt'
    Write-TextFile -Path $orderPath -Text $swapped
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $orderPath)
    Assert-That 'Check fails on sections out of order' ($result.Code -eq 1) $result.Output

    # LF-only line ends.
    $lfPath = Join-Path $scratch 'lf.txt'
    Write-TextFile -Path $lfPath -Text $original.Replace("`r`n", "`n")
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $lfPath)
    Assert-That 'Check fails on LF-only line ends' ($result.Code -eq 1) $result.Output

    # An empty file.
    $emptyPath = Join-Path $scratch 'empty.txt'
    Write-TextFile -Path $emptyPath -Text ''
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $emptyPath)
    Assert-That 'Check fails on an empty file' ($result.Code -eq 1) $result.Output

    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $tamperedPath)
    Assert-That 'A tampered body suggests -Generate, not -Update' ($result.Output -match '-Generate' -and $result.Output -notmatch '-Update') $result.Output

    # BOM and CRLF failures also suggest -Generate.
    $bomPath = Join-Path $scratch 'bom.txt'
    [System.IO.File]::WriteAllBytes($bomPath, [byte[]](0xEF, 0xBB, 0xBF) + $utf8NoBom.GetBytes($original))
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $bomPath)
    Assert-That 'Check fails on a BOM and suggests -Generate' ($result.Code -eq 1 -and $result.Output -match 'BOM' -and $result.Output -match '-Generate') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $lfPath)
    Assert-That 'LF-only line ends suggest -Generate' ($result.Output -match '-Generate' -and $result.Output -notmatch '-Update') $result.Output

    # Comparisons are case-sensitive: a header and a preface that differ only in case fail.
    $caseHeaderPath = Join-Path $scratch 'case-header.txt'
    Write-TextFile -Path $caseHeaderPath -Text ($original.Replace("`r`n[1] .NET Runtime licence`r`n", "`r`n[1] .NET runtime licence`r`n"))
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $caseHeaderPath)
    Assert-That 'Check fails when only the case of a header title changes' ($result.Code -eq 1 -and $result.Output -match 'header differs') $result.Output
    $casePrefacePath = Join-Path $scratch 'case-preface.txt'
    Write-TextFile -Path $casePrefacePath -Text ($original.Replace('Pinned .NET runtime version', 'pinned .NET runtime version'))
    $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', $casePrefacePath)
    Assert-That 'Check fails when only the case of the preface changes' ($result.Code -eq 1) $result.Output

    # A relative -NoticesFile is resolved against the current location.
    $relativeDirectory = Join-Path $scratch 'relative'
    New-Item -ItemType Directory -Path $relativeDirectory | Out-Null
    Copy-Item -LiteralPath $committed -Destination (Join-Path $relativeDirectory 'n.txt')
    Push-Location -LiteralPath $relativeDirectory
    try { $result = Invoke-Tool -Arguments @('-Check', '-NoticesFile', '.\n.txt') } finally { Pop-Location }
    Assert-That 'Check resolves a relative -NoticesFile' ($result.Code -eq 0) $result.Output

    # The repo LICENSE is hashed too (offline): a scratch repo root with a changed LICENSE fails.
    $repoCopy = Join-Path $scratch 'repo'
    New-Item -ItemType Directory -Path $repoCopy | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $repoCopy 'LICENSE')
    $result = Invoke-Tool -Arguments @('-Check', '-RepoRoot', $repoCopy, '-NoticesFile', $committed)
    Assert-That 'Check passes with a copy of the repo LICENSE' ($result.Code -eq 0) $result.Output
    $licenseText = [System.IO.File]::ReadAllText((Join-Path $repoCopy 'LICENSE'), $utf8NoBom)
    Write-TextFile -Path (Join-Path $repoCopy 'LICENSE') -Text ($licenseText.Replace('Permission is hereby granted', 'Permission is hereby Granted'))
    $result = Invoke-Tool -Arguments @('-Check', '-RepoRoot', $repoCopy, '-NoticesFile', $committed)
    Assert-That 'Check fails when the repo LICENSE differs from its pinned hash' ($result.Code -eq 1 -and $result.Output -match 'repo file LICENSE') $result.Output

    # Build-time-only sections render "none"; an unknown includedIn code is an error.
    Assert-That 'Build-time-only sections say none in the file' ($original -match '\[6b\][^\r]*\r\nIncluded in: none \(build-time only; listed for information\)\r\n' -and $original -match '\[9\][^\r]*\r\nIncluded in: none \(build-time only; listed for information\)\r\n') ''
    $sourcesText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'tools\notices\sources.json'), $utf8NoBom)
    $badSources = Join-Path $scratch 'sources-bad-code.json'
    Write-TextFile -Path $badSources -Text $sourcesText.Replace('"includedIn": ["NONE"]', '"includedIn": ["none"]')
    $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $badSources)
    Assert-That 'An unknown includedIn code (wrong case) is an error' ($result.Code -eq 1 -and $result.Output -match 'Unknown includedIn code') $result.Output

    # project.assets.json: a trimmed real self-contained restore (downloadDependencies) and a copy with another runtime version.
    $sources = $sourcesText | ConvertFrom-Json
    $fixture = Join-Path $PSScriptRoot 'fixtures\project.assets.self-contained.json'
    $fixtureText = [System.IO.File]::ReadAllText($fixture, $utf8NoBom)
    Assert-That 'The fixture is at the pinned runtime version' ($fixtureText.Contains('"[' + $sources.runtimeVersion + ', ' + $sources.runtimeVersion + ']"')) ''
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixture)
    Assert-That 'Check passes when the restored runtime matches' ($result.Code -eq 0) $result.Output

    $badAssets = Join-Path $scratch 'assets-10.0.14.json'
    Write-TextFile -Path $badAssets -Text $fixtureText.Replace('[' + $sources.runtimeVersion + ', ' + $sources.runtimeVersion + ']', '[10.0.14, 10.0.14]')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badAssets)
    Assert-That 'Check fails when the restored runtime differs' ($result.Code -eq 1 -and $result.Output -match 'Microsoft\.NETCore\.App\.Runtime\.win-x64 10\.0\.14' -and $result.Output -match 'Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64 10\.0\.14') $result.Output
    Assert-That 'A runtime drift prints -Update -RuntimeVersion <restored version>' ($result.Output -match '-Update -RuntimeVersion 10\.0\.14 -Accept') $result.Output

    $sdkSection = @($sources.sections | Where-Object { $_.id -ceq '8' })[0]
    $badSdk = Join-Path $scratch 'assets-sdk.json'
    Write-TextFile -Path $badSdk -Text $fixtureText.Replace('[' + $sdkSection.version + ', ' + $sdkSection.version + ']', '[10.0.26100.99, 10.0.26100.99]')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badSdk)
    Assert-That 'Check fails when Microsoft.Windows.SDK.NET.Ref differs from section [8]' ($result.Code -eq 1 -and $result.Output -match 'SDK\.NET\.Ref 10\.0\.26100\.99') $result.Output

    $badCsWin32 = Join-Path $scratch 'assets-cswin32.json'
    Write-TextFile -Path $badCsWin32 -Text $fixtureText.Replace('Microsoft.Windows.CsWin32/0.3.366', 'Microsoft.Windows.CsWin32/0.3.999')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badCsWin32)
    Assert-That 'Check fails when CsWin32 differs from section [6b]' ($result.Code -eq 1 -and $result.Output -match 'CsWin32 0\.3\.999') $result.Output

    $fxAssets = Join-Path $scratch 'assets-fx.json'
    Write-TextFile -Path $fxAssets -Text '{ "version": 3, "libraries": { "Microsoft.Windows.CsWin32/0.3.366": { "type": "package" } }, "project": { "frameworks": { "net10.0-windows10.0.26100.0": { } } } }'
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fxAssets)
    Assert-That 'Check passes with no runtime pack, and says it is not in downloadDependencies' ($result.Code -eq 0 -and $result.Output -match 'not in downloadDependencies') $result.Output

    $brokenAssets = Join-Path $scratch 'assets-broken.json'
    Write-TextFile -Path $brokenAssets -Text '{ not json'
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $brokenAssets)
    Assert-That 'Check fails when the assets file is not JSON' ($result.Code -eq 1) $result.Output

    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', (Join-Path $scratch 'missing.json'))
    Assert-That 'Check fails when the assets file does not exist' ($result.Code -eq 1) $result.Output
    $result = Invoke-Tool -Arguments @()
    Assert-That 'No mode prints usage and exits 2' ($result.Code -eq 2) $result.Output
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ("make-notices tests: {0} passed, {1} failed" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
exit 0
