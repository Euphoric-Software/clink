# Checks of a GitHub release against what the publish job built (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7.
# Used by release.yml and release-dev.yml three times per run, all by the release's numeric ID (never by tag):
#   1. "Verify the release assets"            right after softprops created the draft,
#   2. "Re-verify the release before publishing"  right before PATCH draft=false (after the feed upload: closes the window in which the
#      draft or its tag could have been changed),
#   3. "Verify the published release"         after the PATCH, re-reading the release (immutable, exact assets, tag at the built commit).
# A failed check of 2 or 3 DELETES the release (Invoke-ReleaseCheckOrDelete) and fails the run loudly: see docs/RELEASING.md, "Integrity incident".
# Every gh call goes through Invoke-GhApi (one place that merges stderr and resets $LASTEXITCODE); the script tests define a fake `gh` function.
# Nothing here reads a variable it could not rely on: every decision compares with -ceq / -cne / -cmatch against a literal or a parameter.

function Invoke-GhApi {
    # Runs `gh api <Arguments>`. Returns @{ Code; Text } (stdout and stderr as one text). A non-zero exit is an answer, not an exception.
    # $LASTEXITCODE is reset afterwards: the runner appends an exit-code check to every pwsh step, and a handled failure must not fail it.
    param([Parameter(Mandatory)] [string[]]$Arguments)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false     # a non-zero exit is an answer here (pwsh 7.4 would otherwise throw on it)
    try {
        $lines = @(& gh api @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $eap }
    $global:LASTEXITCODE = 0
    @{ Code = [int]$code; Text = ($lines -join "`n") }
}

function Get-RequiredReleaseAssets {
    # The seven asset names of every release, plus <channel>.json and its .sig when this build made them (feed enabled).
    param([Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel, [Parameter(Mandatory)] [string]$PackageDir)
    $names = @('Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'Clink-Setup.exe', 'Clink-Setup.exe.sha256', 'THIRD-PARTY-NOTICES.txt')
    if (Test-Path -LiteralPath (Join-Path $PackageDir "$Channel.json") -PathType Leaf) { $names += "$Channel.json", "$Channel.json.sig" }
    $names
}

function Get-ExpectedReleaseName {
    param([Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel, [Parameter(Mandatory)] [string]$Version)
    if ($Channel -ceq 'stable') { "Clink v$Version" } else { "Clink v$Version (dev)" }
}

function Get-ExpectedDevReleaseBody {
    # The body release-dev.yml passes to softprops ("body:" of the draft step); a test keeps the two in step.
    param([Parameter(Mandatory)] [string]$Commit)
    "Dev build of $Commit from the dev branch. Clink installs it only with Dev Build turned on (click the version in the footer)."
}

# H2. Get-ImmutableReleasesMode (CLINK_IMMUTABLE_RELEASES: unset/required, unavailable, anything else throws) lives in release-mode-lib.ps1,
# which has no gh in it, so that the workflows' "Read feed configuration" step can validate the variable early (L-2) without this library.
. (Join-Path $PSScriptRoot 'release-mode-lib.ps1')

function Assert-ReleaseMatches {
    # Pure check of a parsed release (GET repos/<repo>/releases/<id>) against the build. Throws on the first difference.
    #  - the ID is the one the draft step reported, and EXACTLY ONE release of the repository carries the tag vX (IdsWithTag: the ids of
    #    every release, drafts included, with that tag_name): softprops v3 reuses an existing draft of the same tag, so a release that
    #    somebody else made must not be adopted
    #  - created by the workflow's own token (author github-actions[bot]), with the expected name; dev: the expected body and
    #    target_commitish equal to the built commit (-Commit)
    #  - draft (before publishing) or published and IMMUTABLE (after); prerelease false for stable, true for dev
    #  - every required asset exactly once, uploaded, with the size and sha256 digest of the local file, and nothing else
    param(
        [Parameter(Mandatory)] $Release, [Parameter(Mandatory)] [AllowEmptyString()] [string]$ReleaseId,
        [Parameter(Mandatory)] [ValidateSet('draft', 'published')] [string]$State,
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [Parameter(Mandatory)] [string]$Version, [Parameter(Mandatory)] [string]$PackageDir,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$IdsWithTag,
        [string]$Commit = '',
        [string]$ImmutableMode = ''     # CLINK_IMMUTABLE_RELEASES as the workflow passes it (Get-ImmutableReleasesMode validates it)
    )
    $mode = Get-ImmutableReleasesMode -Value $ImmutableMode
    $tag = "v$Version"
    if (-not ([string]$Release.id -ceq $ReleaseId)) { throw "The release read back has id '$($Release.id)', expected $ReleaseId." }
    $ids = @($IdsWithTag)
    if ($ids.Count -ne 1 -or -not ($ids[0] -ceq $ReleaseId)) {
        throw "The tag $tag is carried by $($ids.Count) release(s) (ids: $($ids -join ', ')); expected exactly one, the release $ReleaseId this run created. Another release may have been adopted or created."
    }
    if ($State -ceq 'draft') {
        if ($Release.draft -ne $true) { throw "Release $ReleaseId is not a draft." }
    } else {
        if ($Release.draft -ne $false) { throw "Release $ReleaseId is still a draft after publishing." }
        if ($Release.immutable -ne $true) {
            if ($mode -ceq 'unavailable') { Write-Host "::warning::Release $ReleaseId is not immutable (immutable is '$($Release.immutable)'); accepted only because CLINK_IMMUTABLE_RELEASES=unavailable. Its assets and tag could still be changed by a writer." }
            else { throw "Release $ReleaseId is not immutable after publishing (immutable is '$($Release.immutable)'): its assets and tag could still be changed. Immutable releases must be enabled (docs/RELEASING.md); if a private Free repo cannot offer them, the owner sets the repo variable CLINK_IMMUTABLE_RELEASES to 'unavailable'." }
        }
    }
    if (-not ([string]$Release.author.login -ceq 'github-actions[bot]')) { throw "Release $ReleaseId was created by '$($Release.author.login)', not by github-actions[bot]." }
    if (-not ([string]$Release.tag_name -ceq $tag)) { throw "Release $ReleaseId has tag '$($Release.tag_name)', expected $tag." }
    $expectedName = Get-ExpectedReleaseName -Channel $Channel -Version $Version
    if (-not ([string]$Release.name -ceq $expectedName)) { throw "Release $ReleaseId is named '$($Release.name)', expected '$expectedName'." }
    if ($Channel -ceq 'stable') {
        if ($Release.prerelease -ne $false) { throw "Release $ReleaseId is flagged as a prerelease; the stable workflow must create a normal release." }
    } else {
        if ($Release.prerelease -ne $true) { throw "Release $ReleaseId is not flagged as a prerelease; a dev build must never be a normal release." }
        if ([string]::IsNullOrEmpty($Commit)) { throw 'The built commit is missing (-Commit).' }
        # The release (and the tag it will create on publish) must point at the commit the build job built.
        $builtCommit = [string]$Release.target_commitish
        if (-not ($builtCommit -ceq $Commit)) { throw "Release $ReleaseId points at '$builtCommit', not at the built commit $Commit." }
        $expectedBody = Get-ExpectedDevReleaseBody -Commit $Commit
        if (-not (([string]$Release.body).Trim() -ceq $expectedBody)) { throw "Release $ReleaseId has an unexpected body (not the one this workflow sets)." }
    }
    $required = @(Get-RequiredReleaseAssets -Channel $Channel -PackageDir $PackageDir)
    foreach ($name in $required) {
        $found = @($Release.assets | Where-Object { $_.name -ceq $name })
        if ($found.Count -ne 1) { throw "Release $tag lacks exactly one asset named $name (found $($found.Count); has: $((@($Release.assets) | ForEach-Object { $_.name }) -join ', '))" }
        $asset = $found[0]
        if (-not ($asset.state -ceq 'uploaded')) { throw "Asset $name is in state '$($asset.state)', expected 'uploaded'." }
        $local = Get-Item -LiteralPath (Join-Path $PackageDir $name)
        if ([int64]$asset.size -ne $local.Length) { throw "Asset $name has $($asset.size) bytes on GitHub, the local file has $($local.Length)." }
        # GitHub reports a digest for every asset (confirmed by the first real run); a missing one is a failure.
        # A digest that IS reported must always match. A missing one is a failure, except with CLINK_IMMUTABLE_RELEASES=unavailable (it is
        # not confirmed that a private Free repo reports one; the size is still compared and the upload step hash-checked the file).
        $digest = [string]$asset.digest
        if (-not $digest) {
            if ($mode -ceq 'unavailable') { Write-Host "::warning::GitHub reports no digest for asset $name; accepted only because CLINK_IMMUTABLE_RELEASES=unavailable (size compared only)."; continue }
            throw "GitHub reports no digest for asset $name; refusing a release whose upload cannot be checked."
        }
        $expected = 'sha256:' + (Get-FileHash -LiteralPath $local.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not ($digest -ceq $expected)) { throw "Asset $name has digest $digest on GitHub, expected $expected." }
    }
    # Nothing else may ride along: every asset must be one of the required names.
    $unexpected = @($Release.assets | Where-Object { $required -cnotcontains $_.name } | ForEach-Object { $_.name })
    if ($unexpected.Count -gt 0) { throw "Release $tag has unexpected assets: $($unexpected -join ', ')" }
}

function Get-ReleaseIdsForTag {
    # The ids of every release (drafts included: the token has contents: write) whose tag_name is exactly Tag.
    param([Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [string]$Tag)
    $r = Invoke-GhApi -Arguments @("repos/$Repository/releases?per_page=100", '--paginate', '--jq', '.[] | [(.id | tostring), .tag_name] | @tsv')
    if ($r.Code -ne 0) { throw "Could not list the releases: $($r.Text)" }
    $ids = @()
    foreach ($line in ($r.Text -split "`r?`n")) {
        $parts = $line.Split("`t")
        if ($parts.Count -eq 2 -and $parts[1] -ceq $Tag) { $ids += $parts[0] }
    }
    $ids
}

function Assert-ReleaseById {
    # Reads the release by its ID and checks it (Assert-ReleaseMatches). -Commit is the built commit (dev only).
    param(
        [Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [AllowEmptyString()] [string]$ReleaseId,
        [Parameter(Mandatory)] [ValidateSet('draft', 'published')] [string]$State,
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [Parameter(Mandatory)] [string]$Version, [Parameter(Mandatory)] [string]$PackageDir, [string]$Commit = '', [string]$ImmutableMode = ''
    )
    # Validated first, in every state, so a mistyped CLINK_IMMUTABLE_RELEASES fails at the first check (before anything is uploaded).
    [void](Get-ImmutableReleasesMode -Value $ImmutableMode)
    if ($ReleaseId -cnotmatch '\A[0-9]+\z') { throw 'The release ID is missing or not numeric.' }
    $r = Invoke-GhApi -Arguments @("repos/$Repository/releases/$ReleaseId")
    if ($r.Code -ne 0) { throw "Could not read release ${ReleaseId}: $($r.Text)" }
    $release = $r.Text | ConvertFrom-Json
    $ids = @(Get-ReleaseIdsForTag -Repository $Repository -Tag "v$Version")
    Assert-ReleaseMatches -Release $release -ReleaseId $ReleaseId -State $State -Channel $Channel -Version $Version -PackageDir $PackageDir -IdsWithTag $ids -Commit $Commit -ImmutableMode $ImmutableMode
    $count = @(Get-RequiredReleaseAssets -Channel $Channel -PackageDir $PackageDir).Count
    Write-Host "Release v$Version (id $ReleaseId, $State) has exactly the $count required assets and is the only release with its tag"
}

function Assert-GitTagMissing {
    # Dev: the tag must not exist yet (a draft creates none). Only HTTP 404 means "not there"; any other answer fails.
    param([Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [string]$Tag)
    $r = Invoke-GhApi -Arguments @("repos/$Repository/git/ref/tags/$Tag")
    if ($r.Code -eq 0) { throw "The tag $Tag already exists; a dev build creates its own tag at the built commit. Refusing to publish." }
    if ($r.Text -cnotmatch '\(HTTP 404\)') { throw "Could not check whether the tag $Tag exists (exit $($r.Code)): $($r.Text)" }
    Write-Host "The tag $Tag does not exist yet (HTTP 404), as expected"
}

function Get-GitTagTarget {
    # What the tag ref points at, with annotated tag objects dereferenced (at most 5 levels). The ref read is tried Attempts times,
    # DelaySeconds apart (a freshly created tag can take a moment to show). Returns @{ Type; Sha; Annotated } of the final object.
    param([Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [string]$Tag, [int]$Attempts = 1, [int]$DelaySeconds = 2)
    $last = ''
    for ($i = 1; $i -le $Attempts; $i++) {
        $r = Invoke-GhApi -Arguments @("repos/$Repository/git/ref/tags/$Tag")
        if ($r.Code -eq 0) {
            $obj = ($r.Text | ConvertFrom-Json).object
            $annotated = $false
            $depth = 0
            while ([string]$obj.type -ceq 'tag') {
                $annotated = $true
                $depth++
                if ($depth -gt 5) { throw "The tag $Tag nests more than 5 annotated tag objects." }
                if ([string]$obj.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw "The tag $Tag names an object with a malformed sha." }
                $t = Invoke-GhApi -Arguments @("repos/$Repository/git/tags/$($obj.sha)")
                if ($t.Code -ne 0) { throw "Could not read the annotated tag object of ${Tag}: $($t.Text)" }
                $obj = ($t.Text | ConvertFrom-Json).object
            }
            return [pscustomobject]@{ Type = [string]$obj.type; Sha = [string]$obj.sha; Annotated = $annotated }
        }
        $last = $r.Text
        if ($i -lt $Attempts -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }
    throw "Could not read the tag $Tag after $Attempts attempt(s): $last"
}

function Assert-GitTagAtCommit {
    # The tag must (after dereferencing) point at the built commit. A dev tag must be a plain tag (the publish creates it); a stable tag
    # is the owner's and may be annotated (-AllowAnnotated).
    param(
        [Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [string]$Tag, [Parameter(Mandatory)] [string]$Commit,
        [switch]$AllowAnnotated, [int]$Attempts = 3, [int]$DelaySeconds = 2
    )
    $target = Get-GitTagTarget -Repository $Repository -Tag $Tag -Attempts $Attempts -DelaySeconds $DelaySeconds
    if (-not ($target.Type -ceq 'commit')) { throw "The tag $Tag points at a '$($target.Type)' object, expected a commit." }
    if ($target.Annotated -and -not $AllowAnnotated) { throw "The tag $Tag is an annotated tag; expected a plain tag at a commit." }
    if (-not ($target.Sha -ceq $Commit)) { throw "The tag $Tag points at $($target.Sha), not at the built commit $Commit." }
    Write-Host "The tag $Tag points at the built commit $Commit"
}

function Remove-ReleaseById {
    # Deletes a release by ID. Returns $true when gh reported success.
    param([Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [AllowEmptyString()] [string]$ReleaseId)
    if ($ReleaseId -cnotmatch '\A[0-9]+\z') { return $false }
    $r = Invoke-GhApi -Arguments @('-X', 'DELETE', "repos/$Repository/releases/$ReleaseId")
    return ($r.Code -eq 0)
}

function Invoke-ReleaseCheckOrDelete {
    # Runs -Check. When it throws, the release is DELETED (the check says its content or tag cannot be trusted) and the run fails with
    # a loud incident message. The tag of an immutable release stays (GitHub refuses to remove it): RELEASING.md says what to do.
    param([Parameter(Mandatory)] [string]$Repository, [Parameter(Mandatory)] [AllowEmptyString()] [string]$ReleaseId, [Parameter(Mandatory)] [string]$What, [Parameter(Mandatory)] [scriptblock]$Check)
    try { & $Check }
    catch {
        $reason = $_.Exception.Message
        $deleted = Remove-ReleaseById -Repository $Repository -ReleaseId $ReleaseId
        $outcome = if ($deleted) { "Release $ReleaseId was DELETED." } else { "Release $ReleaseId could NOT be deleted: delete it by hand NOW." }
        $message = "RELEASE INTEGRITY INCIDENT ($What): $reason $outcome See docs/RELEASING.md, 'Integrity incident'."
        Write-Host "::error::$message"
        throw $message
    }
}
