# Which "latest" flag a stable release gets when its draft is published (release.yml). Dot-source it. Windows PowerShell 5.1 and pwsh 7.
#
# Get-MakeLatestValue -Version 0.4.2 -LatestTag v0.4.1   ->  'true' (higher than the current latest stable release)
# Get-MakeLatestValue -Version 0.4.1 -LatestTag v0.4.1   ->  'false' (equal or lower: a re-cut or backport must not take "latest")
# Get-MakeLatestValue -Version 0.4.2 -LatestTag ''       ->  'true' (no latest release yet, GitHub answered 404)
# Anything that is not a plain X.Y.Z / vX.Y.Z throws, so the publish step fails instead of guessing.
function Get-MakeLatestValue {
    param(
        [Parameter(Mandatory)] [string]$Version,
        [string]$LatestTag
    )
    if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "Version '$Version' is not X.Y.Z." }
    if ([string]::IsNullOrWhiteSpace($LatestTag)) { return 'true' }
    $LatestTag = $LatestTag.Trim()
    if ($LatestTag -cnotmatch '\Av([0-9]+\.[0-9]+\.[0-9]+)\z') { throw "The current latest release tag '$LatestTag' is not vX.Y.Z; not guessing whether v$Version is newer." }
    if ([version]$Version -gt [version]$Matches[1]) { return 'true' }
    return 'false'
}
