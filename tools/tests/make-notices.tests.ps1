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

    # project.assets.json: a trimmed real self-contained restore (downloadDependencies) and copies with other versions. The fixture's
    # versions are rewritten at test time to what sources.json pins (runtimeVersion, section [8], section [6b]), so a re-pin of the
    # notices does not break these tests; the drift versions are derived from the pins.
    $sources = $sourcesText | ConvertFrom-Json
    $runtimeVersion = [string]$sources.runtimeVersion
    $sdkSection = @($sources.sections | Where-Object { $_.id -ceq '8' })[0]
    $cswinSection = @($sources.sections | Where-Object { $_.id -ceq '6b' })[0]
    $runtimeParts = $runtimeVersion.Split('.')
    $driftVersion = '{0}.{1}.{2}' -f $runtimeParts[0], $runtimeParts[1], ([int]$runtimeParts[2] + 1)
    $sdkDriftVersion = [string]$sdkSection.version + '9'
    $cswinDriftVersion = [string]$cswinSection.version + '9'
    $fixture = Join-Path $PSScriptRoot 'fixtures\project.assets.self-contained.json'
    $fixtureText = [System.IO.File]::ReadAllText($fixture, $utf8NoBom)
    $fixtureText = [regex]::Replace($fixtureText, '\[[0-9]+\.[0-9]+\.[0-9]+, [0-9]+\.[0-9]+\.[0-9]+\]', ('[' + $runtimeVersion + ', ' + $runtimeVersion + ']'))
    $fixtureText = [regex]::Replace($fixtureText, '("Microsoft\.Windows\.SDK\.NET\.Ref",\s*"version":\s*")[^"]*(")', ('${1}[' + $sdkSection.version + ', ' + $sdkSection.version + ']${2}'))
    $fixtureText = [regex]::Replace($fixtureText, '"Microsoft\.Windows\.CsWin32/[^"]*"', ('"Microsoft.Windows.CsWin32/' + $cswinSection.version + '"'))
    # A scratch NuGet package folder holding the two restored runtime packs' versions.txt (line 1 = vmrCommit, line 2 = runtime version),
    # named by "packageFolders" of the assets file (the fixture has none: it is injected here).
    $vmrCommit = [string]$sources.vmrCommit
    $packFolder = Join-Path $scratch 'packages'
    foreach ($pair in @(@('microsoft.netcore.app.runtime.win-x64', 'Microsoft.NETCore.App.versions.txt'), @('microsoft.windowsdesktop.app.runtime.win-x64', 'Microsoft.WindowsDesktop.App.versions.txt'))) {
        $packDirectory = Join-Path (Join-Path $packFolder $pair[0]) $runtimeVersion
        New-Item -ItemType Directory -Path $packDirectory -Force | Out-Null
        Write-TextFile -Path (Join-Path $packDirectory $pair[1]) -Text ($vmrCommit + "`n" + $runtimeVersion + "`n" + "some-other-line`n")
    }
    function Add-PackageFolder {
        param([string]$Text, [string]$Folder)
        $json = $Folder.Replace('\', '\\')
        return ('{ "packageFolders": { "' + $json + '\\": {} },' + $Text.TrimStart().Substring(1))
    }
    $fixtureText = Add-PackageFolder -Text $fixtureText -Folder $packFolder
    $fixtureNormalized = Join-Path $scratch 'assets-pinned.json'
    Write-TextFile -Path $fixtureNormalized -Text $fixtureText
    Assert-That 'The normalised fixture is at the pinned runtime version' ($fixtureText.Contains('"[' + $runtimeVersion + ', ' + $runtimeVersion + ']"') -and $fixtureText.Contains('"[' + $sdkSection.version + ', ' + $sdkSection.version + ']"')) ''
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check passes when the restored runtime matches' ($result.Code -eq 0) $result.Output

    $badAssets = Join-Path $scratch 'assets-drift.json'
    Write-TextFile -Path $badAssets -Text $fixtureText.Replace('[' + $runtimeVersion + ', ' + $runtimeVersion + ']', '[' + $driftVersion + ', ' + $driftVersion + ']')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badAssets)
    $driftPattern = [regex]::Escape($driftVersion)
    Assert-That 'Check fails when the restored runtime differs' ($result.Code -eq 1 -and $result.Output -match ('Microsoft\.NETCore\.App\.Runtime\.win-x64 ' + $driftPattern) -and $result.Output -match ('Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64 ' + $driftPattern)) $result.Output
    Assert-That 'A runtime drift prints -Update -RuntimeVersion <restored version>' ($result.Output -match ('-Update -RuntimeVersion ' + $driftPattern + ' -Accept')) $result.Output

    $badSdk = Join-Path $scratch 'assets-sdk.json'
    Write-TextFile -Path $badSdk -Text $fixtureText.Replace('[' + $sdkSection.version + ', ' + $sdkSection.version + ']', '[' + $sdkDriftVersion + ', ' + $sdkDriftVersion + ']')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badSdk)
    Assert-That 'Check fails when Microsoft.Windows.SDK.NET.Ref differs from section [8]' ($result.Code -eq 1 -and $result.Output -match ('SDK\.NET\.Ref ' + [regex]::Escape($sdkDriftVersion))) $result.Output

    $badCsWin32 = Join-Path $scratch 'assets-cswin32.json'
    Write-TextFile -Path $badCsWin32 -Text $fixtureText.Replace('Microsoft.Windows.CsWin32/' + $cswinSection.version, 'Microsoft.Windows.CsWin32/' + $cswinDriftVersion)
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $badCsWin32)
    Assert-That 'Check fails when CsWin32 differs from section [6b]' ($result.Code -eq 1 -and $result.Output -match ('CsWin32 ' + [regex]::Escape($cswinDriftVersion))) $result.Output

    # A missing SDK reference download dependency or a missing CsWin32 library is a problem too (same rule as the runtime packs).
    $noSdk = Join-Path $scratch 'assets-no-sdk.json'
    Write-TextFile -Path $noSdk -Text ($fixtureText -replace '"name":\s*"Microsoft\.Windows\.SDK\.NET\.Ref"', '"name": "Other.Sdk.Ref"')
    Assert-That 'The no-SDK fixture really lacks the SDK reference' ((Get-Content -Raw -LiteralPath $noSdk) -notmatch 'Windows\.SDK\.NET\.Ref') ''
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $noSdk)
    Assert-That 'Check FAILS when Microsoft.Windows.SDK.NET.Ref is not in downloadDependencies' ($result.Code -eq 1 -and $result.Output -match 'Microsoft\.Windows\.SDK\.NET\.Ref is not in downloadDependencies') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $noSdk, '-AllowMissingRuntimePacks')
    Assert-That '-AllowMissingRuntimePacks turns the missing SDK reference into a note' ($result.Code -eq 0 -and $result.Output -match 'SDK\.NET\.Ref is not in downloadDependencies') $result.Output

    $noCsWin32 = Join-Path $scratch 'assets-no-cswin32.json'
    Write-TextFile -Path $noCsWin32 -Text $fixtureText.Replace('Microsoft.Windows.CsWin32/' + $cswinSection.version, 'Other.Library/1.0.0')
    Assert-That 'The no-CsWin32 fixture really lacks the library' ((Get-Content -Raw -LiteralPath $noCsWin32) -notmatch 'CsWin32/') ''
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $noCsWin32)
    Assert-That 'Check FAILS when Microsoft.Windows.CsWin32 is not in libraries' ($result.Code -eq 1 -and $result.Output -match 'Microsoft\.Windows\.CsWin32 is not in libraries') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $noCsWin32, '-AllowMissingRuntimePacks')
    Assert-That '-AllowMissingRuntimePacks turns the missing CsWin32 library into a note' ($result.Code -eq 0 -and $result.Output -match 'CsWin32 is not in libraries') $result.Output

    # A restore without the runtime packs (a framework-dependent restore) is a FAILURE, not a note, unless -AllowMissingRuntimePacks.
    $fxAssets = Join-Path $scratch 'assets-fx.json'
    Write-TextFile -Path $fxAssets -Text ('{ "version": 3, "libraries": { "Microsoft.Windows.CsWin32/' + $cswinSection.version + '": { "type": "package" } }, "project": { "frameworks": { "net10.0-windows10.0.26100.0": { } } } }')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fxAssets)
    Assert-That 'Check FAILS with no downloadDependencies / no runtime pack' ($result.Code -eq 1 -and $result.Output -match 'no downloadDependencies' -and $result.Output -match 'Microsoft\.NETCore\.App\.Runtime\.win-x64 is not in downloadDependencies') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fxAssets, '-AllowMissingRuntimePacks')
    Assert-That '-AllowMissingRuntimePacks turns the missing runtime packs into notes' ($result.Code -eq 0 -and $result.Output -match 'not in downloadDependencies') $result.Output

    $emptyAssets = Join-Path $scratch 'assets-empty.json'
    Write-TextFile -Path $emptyAssets -Text '{}'
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $emptyAssets)
    Assert-That 'Check FAILS when the assets file is {}' ($result.Code -eq 1 -and $result.Output -match 'no project\.frameworks') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $emptyAssets, '-AllowMissingRuntimePacks')
    Assert-That '-AllowMissingRuntimePacks accepts {} (local use only)' ($result.Code -eq 0) $result.Output

    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', (Join-Path $repoRoot 'tools\notices\sources.json'))
    Assert-That 'Check FAILS when sources.json is passed as the assets file' ($result.Code -eq 1) $result.Output

    $oneFramework = Join-Path $scratch 'assets-one-pack.json'
    Write-TextFile -Path $oneFramework -Text ($fixtureText -replace '(?s)\{\s*"name":\s*"Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64",\s*"version":\s*"[^"]*"\s*\}', '{ "name": "Other.Pack", "version": "[1.0.0, 1.0.0]" }')
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $oneFramework)
    Assert-That 'Check FAILS when only one of the two runtime packs is restored' ($result.Code -eq 1 -and $result.Output -match 'Microsoft\.WindowsDesktop\.App\.Runtime\.win-x64 is not in downloadDependencies') $result.Output
    $brokenAssets = Join-Path $scratch 'assets-broken.json'
    Write-TextFile -Path $brokenAssets -Text '{ not json'
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $brokenAssets)
    Assert-That 'Check fails when the assets file is not JSON' ($result.Code -eq 1) $result.Output

    # vmrCommit binding. (a) The restored packs' versions.txt must name the pinned commit and runtime version.
    $netVersionsPath = Join-Path (Join-Path (Join-Path $packFolder 'microsoft.netcore.app.runtime.win-x64') $runtimeVersion) 'Microsoft.NETCore.App.versions.txt'
    $desktopVersionsPath = Join-Path (Join-Path (Join-Path $packFolder 'microsoft.windowsdesktop.app.runtime.win-x64') $runtimeVersion) 'Microsoft.WindowsDesktop.App.versions.txt'
    $otherCommit = ('0' * 40)
    Write-TextFile -Path $netVersionsPath -Text ($otherCommit + "`n" + $runtimeVersion + "`n")
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS when a restored pack names another commit than vmrCommit' ($result.Code -eq 1 -and $result.Output -match ('names commit ' + $otherCommit) -and $result.Output -match 'vmrCommit') $result.Output
    Write-TextFile -Path $netVersionsPath -Text ($vmrCommit + "`n10.0.99`n")
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS when line 2 of a restored pack is not the runtime version' ($result.Code -eq 1 -and $result.Output -match 'names runtime 10\.0\.99') $result.Output
    Write-TextFile -Path $netVersionsPath -Text "not-a-commit`n$runtimeVersion`n"
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS when line 1 of a restored pack is not a 40-hex commit' ($result.Code -eq 1 -and $result.Output -match 'not a 40-hex commit') $result.Output
    Write-TextFile -Path $netVersionsPath -Text ($vmrCommit.ToUpperInvariant() + "`n" + $runtimeVersion + "`n")
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS on an upper-case commit (case-sensitive)' ($result.Code -eq 1) $result.Output
    Write-TextFile -Path $desktopVersionsPath -Text ($otherCommit + "`n" + $runtimeVersion + "`n")
    Write-TextFile -Path $netVersionsPath -Text ($vmrCommit + "`n" + $runtimeVersion + "`n")
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS when only the desktop pack names another commit' ($result.Code -eq 1 -and $result.Output -match 'WindowsDesktop\.App\.Runtime\.win-x64 .*names commit') $result.Output
    Remove-Item -LiteralPath $desktopVersionsPath -Force
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check FAILS when a restored pack has no versions.txt' ($result.Code -eq 1 -and $result.Output -match 'WindowsDesktop\.App\.versions\.txt not found') $result.Output
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized, '-AllowMissingRuntimePacks')
    Assert-That '-AllowMissingRuntimePacks turns the missing versions.txt into a note' ($result.Code -eq 0 -and $result.Output -match 'vmrCommit not compared') $result.Output
    Write-TextFile -Path $desktopVersionsPath -Text ($vmrCommit + "`n" + $runtimeVersion + "`n")
    $result = Invoke-Tool -Arguments @('-Check', '-AssetsFile', $fixtureNormalized)
    Assert-That 'Check passes again once both packs name vmrCommit and the runtime version' ($result.Code -eq 0) $result.Output

    # (b) sources.json itself: vmrCommit must be a full commit and every dotnet/dotnet URL must carry it.
    $badVmr = Join-Path $scratch 'sources-bad-vmr.json'
    Write-TextFile -Path $badVmr -Text $sourcesText.Replace('"vmrCommit": "' + $vmrCommit + '"', '"vmrCommit": "' + $otherCommit + '"')
    $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $badVmr)
    Assert-That 'Check FAILS when the dotnet/dotnet URLs name another commit than vmrCommit' ($result.Code -eq 1 -and $result.Output -match ('names dotnet/dotnet commit ' + $vmrCommit)) $result.Output
    $shortVmr = Join-Path $scratch 'sources-short-vmr.json'
    Write-TextFile -Path $shortVmr -Text $sourcesText.Replace('"vmrCommit": "' + $vmrCommit + '"', '"vmrCommit": "main"')
    $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $shortVmr)
    Assert-That 'Check FAILS when vmrCommit is not a 40-hex commit' ($result.Code -eq 1 -and $result.Output -match 'vmrCommit is not a 40-hex commit') $result.Output
    $oneUrl = Join-Path $scratch 'sources-one-url.json'
    Write-TextFile -Path $oneUrl -Text ([regex]::Replace($sourcesText, ('dotnet/dotnet/' + $vmrCommit + '/src/wpf'), ('dotnet/dotnet/' + $otherCommit + '/src/wpf')))
    $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $oneUrl)
    Assert-That 'Check FAILS when one dotnet/dotnet URL names another commit' ($result.Code -eq 1 -and $result.Output -match ('names dotnet/dotnet commit ' + $otherCommit)) $result.Output

    # (b2) Sections 4 (WPF) and 5 (WinForms) are bound by section to the exact URL; every URL section must be a plain pinned URL.
    $wpfUrl = "https://raw.githubusercontent.com/dotnet/dotnet/$vmrCommit/src/wpf/THIRD-PARTY-NOTICES.TXT"
    $cswinUrl = 'https://raw.githubusercontent.com/microsoft/CsWin32/a54876f658937b3c0c45f599e82df9582a97258f/LICENSE'
    Assert-That 'the fixture finds the WPF and CsWin32 URLs in sources.json' ($sourcesText.Contains('"' + $wpfUrl + '"') -and $sourcesText.Contains('"' + $cswinUrl + '"')) 'sources.json changed shape'
    $variants = [ordered]@{
        'owner in other case'      = @($wpfUrl, "https://raw.githubusercontent.com/Dotnet/dotnet/$otherCommit/src/wpf/THIRD-PARTY-NOTICES.TXT", 'Section \[4\]')
        'wpf repository'           = @($wpfUrl, "https://raw.githubusercontent.com/dotnet/wpf/$vmrCommit/THIRD-PARTY-NOTICES.TXT", 'Section \[4\]')
        'leading space'            = @($wpfUrl, (' ' + $wpfUrl), 'Section \[4\]')
        'host in upper case'       = @($wpfUrl, $wpfUrl.Replace('raw.githubusercontent.com', 'RAW.githubusercontent.com'), 'Section \[4\]')
        'percent-encoded path'     = @($wpfUrl, $wpfUrl.Replace('/src/wpf/', '/src/%77pf/'), 'Section \[4\]')
        'file name in other case'  = @($wpfUrl, $wpfUrl.Replace('NOTICES.TXT', 'NOTICES.txt'), 'Section \[4\]')
        'winforms path for wpf'    = @($wpfUrl, $wpfUrl.Replace('/src/wpf/', '/src/winforms/'), 'Section \[4\]')
        'query string'             = @($wpfUrl, ($wpfUrl + '?x=1'), 'Section \[4\]')
        'fragment'                 = @($wpfUrl, ($wpfUrl + '#x'), 'Section \[4\]')
        'dotdot'                   = @($wpfUrl, $wpfUrl.Replace('/src/wpf/', '/src/../src/wpf/'), 'Section \[4\]')
        'double slash'             = @($wpfUrl, $wpfUrl.Replace('/src/wpf/', '/src//wpf/'), 'Section \[4\]')
        'other section: percent'   = @($cswinUrl, $cswinUrl.Replace('/LICENSE', '/LICEN%53E'), 'Section \[6\]')
        'other section: query'     = @($cswinUrl, ($cswinUrl + '?raw=1'), 'Section \[6\]')
        'other section: dotdot'    = @($cswinUrl, $cswinUrl.Replace('/LICENSE', '/x/../LICENSE'), 'Section \[6\]')
        'other section: double slash' = @($cswinUrl, $cswinUrl.Replace('/LICENSE', '//LICENSE'), 'Section \[6\]')
        'other section: space'     = @($cswinUrl, ($cswinUrl + ' '), 'Section \[6\]')
        'other section: fragment'  = @($cswinUrl, ($cswinUrl + '#top'), 'Section \[6\]')
    }
    $variantIndex = 0
    foreach ($name in $variants.Keys) {
        $variantIndex++
        $variant = $variants[$name]
        $variantFile = Join-Path $scratch "sources-url-variant-$variantIndex.json"
        Write-TextFile -Path $variantFile -Text $sourcesText.Replace('"' + $variant[0] + '"', '"' + $variant[1] + '"')
        $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $variantFile)
        Assert-That "Check FAILS for the URL variant: $name" ($result.Code -eq 1 -and $result.Output -match $variant[2]) $result.Output
    }
    # A bound section that is missing: drop section 5 (WinForms) by renaming its id.
    $noWinForms = Join-Path $scratch 'sources-no-winforms.json'
    $winformsUrl = "https://raw.githubusercontent.com/dotnet/dotnet/$vmrCommit/src/winforms/THIRD-PARTY-NOTICES.TXT"
    $winformsIdMatch = [regex]::Match($sourcesText, '"id"\s*:\s*"5"(?=[^{}]*' + [regex]::Escape($winformsUrl) + ')')
    Assert-That 'the fixture finds section 5 (WinForms) in sources.json' $winformsIdMatch.Success 'sources.json changed shape'
    Write-TextFile -Path $noWinForms -Text $sourcesText.Remove($winformsIdMatch.Index, $winformsIdMatch.Length).Insert($winformsIdMatch.Index, '"id": "5x"')
    $result = Invoke-Tool -Arguments @('-Check', '-SourcesFile', $noWinForms)
    Assert-That 'Check FAILS when the WinForms section (5) is missing' ($result.Code -eq 1 -and $result.Output -match 'Section \[5\] \(the winforms notices') $result.Output

    # (c) -Update refuses a pack whose line 2 is not the requested runtime version, or whose two packs disagree (before any download).
    $savedNuGet = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = $packFolder
        Write-TextFile -Path $netVersionsPath -Text ($vmrCommit + "`n10.0.99`n")
        $result = Invoke-Tool -Arguments @('-Update', '-RuntimeVersion', $runtimeVersion)
        Assert-That '-Update refuses a pack whose line 2 differs from -RuntimeVersion' ($result.Code -ne 0 -and $result.Output -match 'not the requested runtime') $result.Output
        Write-TextFile -Path $netVersionsPath -Text ($vmrCommit + "`n" + $runtimeVersion + "`n")
        Write-TextFile -Path $desktopVersionsPath -Text ($otherCommit + "`n" + $runtimeVersion + "`n")
        $result = Invoke-Tool -Arguments @('-Update', '-RuntimeVersion', $runtimeVersion)
        Assert-That '-Update refuses two packs naming different commits' ($result.Code -ne 0 -and $result.Output -match 'different commits') $result.Output
        Write-TextFile -Path $desktopVersionsPath -Text ($vmrCommit + "`n" + $runtimeVersion + "`n")
    }
    finally { $env:NUGET_PACKAGES = $savedNuGet }

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
