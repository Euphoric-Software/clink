# CLINK_IMMUTABLE_RELEASES (dot-source this file). No gh, no network: the workflows' "Read feed configuration" step loads it to fail a
# mistyped value before the draft exists (L-2), and tools/lib/release-verify-lib.ps1 loads it for the three release checks.
# Windows PowerShell 5.1 and PowerShell 7, no modules.

function Get-ImmutableReleasesMode {
    # H2. The repo variable CLINK_IMMUTABLE_RELEASES (passed through env:, never read from the repo by the lib): unset or empty or
    # 'required' = the post-publish check REQUIRES immutable releases and a digest on every asset (the default, and the only mode while the
    # repo is public); 'unavailable' = the owner found that a private Free repo does not offer them: only those two assertions are skipped,
    # each with a warning annotation. Anything else (a typo, another case, white space) THROWS, so a mistyped variable can never weaken the check.
    param([AllowNull()] [AllowEmptyString()] [string]$Value)
    $v = [string]$Value
    if ($v.Length -eq 0 -or $v -ceq 'required') { return 'required' }
    if ($v -ceq 'unavailable') { return 'unavailable' }
    throw "CLINK_IMMUTABLE_RELEASES must be unset, 'required' or 'unavailable' (exactly, lower case); refusing to guess what it was meant to say."
}
