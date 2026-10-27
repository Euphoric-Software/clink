# Releasing Clink

This file is the canonical spec for how Clink is built, signed and published (contract v2). Tag `vX.Y.Z` (use
`tools/release.ps1`) runs `.github/workflows/release.yml`; a push to `dev` runs `release-dev.yml`.

## What a release contains

| Download | What it is |
| --- | --- |
| `Clink.exe` | The portable download: the plain single-file exe (framework-dependent, needs the .NET 10 Desktop Runtime). |
| `Clink.exe.sha256` | `<hash> *Clink.exe`. v0.4.1 and older updaters read exactly these two names. |
| `Clink-X.Y.Z-setup.exe` | Inno Setup installer (per user, no admin, fixed location, upgrades in place). |
| `stable.json`, `stable.json.sig` (`dev.json`, ...) | Signed update manifest, only when the feed is enabled for that channel. |

There is no portable zip and no `Clink.portable` marker.

## Contract v2

### Trust anchors: `Clink/Assets/feed.json`

Committed and embedded in `Clink.exe`; the only place the app learns where the feed is and whom to trust:

```json
{ "schema": 1, "feedUrl": "https://downloads.example/clink/", "stableKeys": ["<Base64 DER SPKI, ECDSA P-256>"], "devKeys": [] }
```

- Both channels are enabled together. The file is in exactly one of three states:
  - **off**: `feedUrl` is `""` and `stableKeys` and `devKeys` are both empty. The workflows publish only the GitHub release
    (with the installer) and log a warning.
  - **on**: `feedUrl` is valid AND `stableKeys` and `devKeys` are BOTH non-empty, every key is a valid ECDSA P-256 SPKI, and the
    two lists share no key. `feedUrl` must be https with a host, no user info (`@`), no query (`?`), no fragment (`#`), and end
    with `/`.
  - anything else that names a URL or a key (one channel only, keys without a URL, overlapping or malformed keys, a bad URL)
    is **misconfigured**: the client applies no updates from the feed, and CI (`Get-FeedConfig` in `tools/feed-lib.ps1`,
    which validates exactly like the client) fails the build.
- A feed manifest the client refuses (bad signature, wrong channel, downgrade) falls back to the SIGNED GitHub release path,
  so a compromised bucket cannot push an update that the embedded keys do not sign.
- Several keys per channel are allowed (rotation). A manifest is valid if its signature verifies under any listed key.
- The build job runs the built exe's test `Embedded_feed_trust_matches_ci_expectation` with `CLINK_EXPECT_FEED=on|off` taken from
  `Get-FeedConfig`, so the exe that ships embeds the feed state CI believes it does.
- There are no `CLINK_FEED_URL` / `CLINK_FEED_PUBLIC_KEY` variables and no `-p:UpdateFeed*` build properties.
- Enabling the feed is a reviewed commit to this file (see below). Nothing in CI settings can change what the app trusts.

### Manifest (`stable.json` / `dev.json`)

```json
{ "schema": 1, "channel": "stable", "version": "0.4.2", "tag": "v0.4.2", "published": "2026-10-26T12:00:00.0000000Z",
  "notesUrl": "https://github.com/Euphoric-Software/clink/releases/tag/v0.4.2", "minRuntime": "10.0",
  "assets": { "exe":       { "url": ".../releases/v0.4.2/Clink.exe",            "size": 1, "sha256": "<64 hex>" },
              "installer": { "url": ".../releases/v0.4.2/Clink-0.4.2-setup.exe", "size": 1, "sha256": "<64 hex>" } } }
```

Signature file: one line of Base64, ECDSA P-256 / SHA-256 over the exact manifest bytes (UTF-8, no BOM), IEEE P1363
`r||s`, 64 bytes. Separate keys per channel: the stable key signs `stable.json`, the dev key signs `dev.json`.

### Bucket layout (under `<prefix>`, validated `^clink(-staging)?$`)

```
<prefix>/releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-X.Y.Z-setup.exe}
<prefix>/dev/vX.Y.Z-dev.N/{same}                 (10 newest kept)
<prefix>/stable.json, stable.json.sig, dev.json, dev.json.sig
```

No `latest/` aliases. Versioned objects are write-once (immutable, `Cache-Control: public, max-age=31536000, immutable`,
`sha256` object metadata). Manifests and signatures use `Cache-Control: public, max-age=60`.

### GitHub releases

Every GitHub release carries `<channel>.json` and `<channel>.json.sig` (byte-identical to the bucket copies) when the feed is
enabled for that channel. The app's GitHub fallback verifies them against the same embedded keys.

## Workflow shape

Both workflows have two jobs.

1. `build` (`contents: read`, no secrets, no environment): checkout, version (the stable workflow accepts only `refs/tags/vX.Y.Z`
   or a manual run on `main`, and refuses when `gh release view vX.Y.Z` succeeds), publish `Clink.exe`, run the embedded-feed
   test, install the pinned Inno Setup, run `tools/package.ps1` (exe, sha256, setup exe, `package.json`), record the SHA-256 values as job outputs, upload the
   package as an artifact. The dev workflow also builds and runs the unit tests first.
2. `publish` (`needs: build`, environment `feed-stable` / `feed-dev`, `contents: write`, fixed concurrency group
   `feed-stable-publish` / `feed-dev-publish` with `cancel-in-progress: false`). Guards first, before any secret is used:
   the stable job accepts only `^refs/tags/v\d+\.\d+\.\d+$` or a manual run on `main` and checks
   `git merge-base --is-ancestor $GITHUB_SHA origin/main` (full-history checkout); the dev job accepts only `refs/heads/dev`;
   both refuse when a GitHub release `vX.Y.Z` already exists (draft or published). Then: download the artifact, verify
   `Clink.exe` and the setup exe against the build job's outputs, read `Clink/Assets/feed.json` from the checkout.
   The GitHub release is created as a DRAFT with all assets, then `upload-feed.ps1` runs (if the feed is on), and only then
   `gh release edit vX.Y.Z --draft=false` publishes it. A failed bucket upload therefore never leaves a visible release.
   - Feed enabled for the channel: require the environment secrets (fail if missing), make and sign the manifest
     (`tools/make-manifest.ps1`; the key is only in that step's environment; the signature is verified against the keys in
     `feed.json` before it is written; a mismatch fails the run), create the GitHub release with all assets including the
     manifest and signature, then run `tools/upload-feed.ps1`.
   - Feed not enabled: warn and create the GitHub release (exe, sha256, setup exe) without manifest assets.
   - `upload-feed.ps1`: validates the prefix; refuses when the bucket's current `<channel>.json` is a higher version; for each
     versioned object does a `head-object` (same `sha256` metadata: skip; different: FAIL); uploads with a conditional create
     (`aws s3api put-object ... --if-none-match "*"`, falling back to a plain copy with a warning on an aws cli that lacks the
     option; this is best effort, **R2 Bucket Lock on `releases/` is the real write-once guarantee**); HEAD-checks every asset's
     PUBLIC URL (`feedUrl` + path) for status 200 and the right `Content-Length`; then uploads the manifest, then the `.sig`;
     optionally purges the two manifest URLs from Cloudflare (non-fatal). Dev only: prunes old `<prefix>/dev/vX.Y.Z-dev.N/`
     folders (strict regex, exit code checked).
   - Dev only: old GitHub prereleases are pruned (10 kept) in a step with `if: ${{ !cancelled() }}`.

The installer comes from a pinned Inno Setup release: `innosetup-6.7.3.exe` from
`https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/`, verified against a hard-coded SHA-256
(`9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`) before it runs. Inno Setup 6.3 or newer is required for
`x64compatible`. To bump it: download the new installer into an empty folder, check its Authenticode signature, compute the
SHA-256, change both values in both workflows in a reviewed commit. Every action is pinned by full commit SHA; Dependabot
proposes bumps.

## Owner setup checklist

GitHub (repo Settings). Create BOTH environments (with their protection rules and secrets) BEFORE the first push of these
workflows reaches `dev`: the first run auto-creates a missing environment WITHOUT any protection.

- [ ] Environment `feed-stable`: required reviewer (the owner), "Allow administrators to bypass" OFF, deployment rule matching
      ONLY stable tags (a pattern such as `v[0-9]*.[0-9]*.[0-9]*` with the dev tags excluded; add `main` only if manual releases
      are wanted; never `dev` or `v*-dev.*`). Environment secrets: `CLINK_FEED_SIGNING_KEY` (the stable key), `R2_ACCESS_KEY_ID`,
      `R2_SECRET_ACCESS_KEY`, `R2_ENDPOINT`, `R2_BUCKET`; optional `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ZONE_ID`.
- [ ] Environment `feed-dev`: deployment rule limited to the `dev` branch, "Allow administrators to bypass" OFF. Same secret
      names, with the dev signing key.
- [ ] Delete every repo-level and organization-level copy of the feed, R2 and Cloudflare secrets (`CLINK_FEED_SIGNING_KEY`,
      `R2_*`, `CLOUDFLARE_*`) and the old `CLINK_FEED_URL` / `CLINK_FEED_PUBLIC_KEY` / `CLINK_FEED_*` variables. Only the two
      environments hold them.
- [ ] Two tag rulesets (Settings > Rules > Rulesets):
      1. Stable: target tags `v*`, exclude `v*-dev.*`; restrict creation, update and deletion to the owner; NO bypass for
         GitHub Actions (a workflow must not be able to create or move a stable tag).
      2. Dev: target tags `v*-dev.*`; GitHub Actions may create and delete (the dev workflow publishes and prunes them).
- [ ] Enable immutable releases (Settings > General > Releases): once a draft is published its tag and assets are locked,
      which complements the draft-then-publish flow.
- [ ] Branch protection on `main` and `dev` (pull request, review, no force push).
- [ ] `.github/CODEOWNERS` assigning the owner to `.github/workflows/**`, `tools/**`, `installer/**`, `Clink/Clink.csproj`,
      `Clink/Services/Update*.cs`, `Clink/Assets/feed.json` and `.github/CODEOWNERS` itself, with "Require review from Code
      Owners" enabled in the `main` and `dev` branch protection. (No active CODEOWNERS file is shipped; add it in the same
      reviewed change as the owner handle.)
- [ ] Settings > Actions > General > Workflow permissions: read-only default token.

Reviewer checklist for each publish approval (the environment reviewer is the last gate before secrets are used):

- [ ] The deployment's commit SHA is on `main` (stable) or `dev` (dev builds).
- [ ] The tag matches the version in `Clink/Clink.csproj` and the release you intend.
- [ ] The diff of `.github/` and `tools/` (and `installer/`, `feed.json`) since the last release was reviewed.
Cloudflare:

- [ ] A dedicated Clink bucket on a custom domain (the `feedUrl` host); the `r2.dev` public URL turned off.
- [ ] A bucket-scoped Object Read & Write API token for that bucket only, with an expiry date.
- [ ] Copium's token re-scoped to Copium's own bucket (no shared bucket or token).
- [ ] Purge token limited to Cache Purge on that zone.
- [ ] Bucket Lock on `<prefix>/releases/` (retention rule) so released objects cannot be changed even with a stolen token.
      This is the real write-once guarantee; the `--if-none-match` create and the `head-object` check are best effort. Lock
      `<prefix>/dev/` too if you accept that pruning old dev folders then waits for the retention period (or exclude it).
- [ ] Optional hardening (recommended, not required now): separate buckets and API tokens per channel, so the dev token cannot
      touch stable objects. The `feedUrl` contract stays single: a manifest the client refuses falls back to the signed GitHub
      release, so a shared bucket is acceptable today.
- [ ] CORS for GET and HEAD from `https://euphoric.software` (and `https://www.euphoric.software` if used), with `Vary: Origin`.
- [ ] A cache rule for `*.json` and `*.sig` that applies no transformations (no minify, no Rocket Loader, no Polish).
- [ ] Hardware-key 2FA on the Cloudflare and GitHub accounts.

## Keys

Generate one pair per channel, on a trusted machine, outside any git work tree (the script refuses otherwise; `*.pem` is also
git-ignored):

```
powershell -File tools\new-feed-key.ps1 -Channel stable -PrivateKeyPath D:\keys\clink-stable.pem
powershell -File tools\new-feed-key.ps1 -Channel dev    -PrivateKeyPath D:\keys\clink-dev.pem
```

For each key: add the printed public key to `stableKeys` / `devKeys` in `Clink/Assets/feed.json`; put the key into
`CLINK_FEED_SIGNING_KEY` of the matching environment; keep an encrypted offline backup; delete the working copy. Store the
secret as the PEM file in one-line Base64 (it survives the secret editor's newline handling):
`[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\keys\clink-stable.pem'))`. `tools/make-manifest.ps1` accepts either the
plain PEM or that Base64 text. Until the working copy is deleted, restrict its ACL to your account
(`new-feed-key.ps1` prints the `icacls` command).

### Enabling the feed

A reviewed pull request that sets `feedUrl` and adds at least one public key to BOTH `stableKeys` and `devKeys` in
`Clink/Assets/feed.json` (both channels are enabled together; the client treats a partial file as misconfigured and CI fails
it). The next release build embeds the new anchors and the publish jobs start signing and uploading. Merge order matters:
both environments, their secrets and the bucket must exist before the commit lands, or the publish job fails by design.

### Rotation

Because clients only trust the keys embedded in their own build, rotate in two releases:

1. Generate an offline "next" stable key now and keep it offline. Ship its public key in `stableKeys` alongside the active one.
2. To rotate: put the next key into the `feed-stable` secret and remove the old public key from `feed.json` in a later release
   (clients that already carry the next key keep updating). Generate a fresh offline next key and add it.
3. A leaked or lost key with no pre-shipped successor means shipping a new build (installer download) with the new key; the
   old key stays trusted by old clients until they update, so treat a leak as an incident (revoke the bucket token, publish a
   fixed release by hand).

## Dry runs

Only in a fork (or a scratch repo) with a throwaway key pair and `clink-staging` as the key prefix (variable
`CLINK_FEED_KEY_PREFIX=clink-staging`, a `feedUrl` that ends with the staging path). Never put a real key or the production
bucket into a fork. Locally you can check the pieces without any bucket:

```
.\build.ps1 -Package                      # dist\package (the setup exe needs Inno Setup 6.3+, otherwise it is skipped with a warning)
$env:CLINK_FEED_SIGNING_KEY = Get-Content -Raw D:\keys\throwaway.pem
try {
  powershell -File tools\make-manifest.ps1 -Channel dev -Version 0.4.2-dev.1 -Tag v0.4.2-dev.1 `
    -BaseUrl https://downloads.example/clink/dev/v0.4.2-dev.1/ -NotesUrl https://example/notes -PackageDir dist\package -FeedJson <feed.json with the throwaway public keys>
} finally {
  Remove-Item Env:\CLINK_FEED_SIGNING_KEY -ErrorAction SilentlyContinue   # the child process clears its own copy; clear this shell's too
}
```

## Local packaging

`.\build.ps1 -Package` publishes (Release by default; it warns for Debug) and runs `tools/package.ps1` into `dist\package`:
`Clink.exe`, `Clink.exe.sha256`, `Clink-<ver>-setup.exe` (when `ISCC.exe` is found), `package.json`.

## Recommended next step: Authenticode

Sign `Clink.exe` and the setup exe with a code-signing certificate (or Azure Trusted Signing) in the `build` or a third job so
SmartScreen reputation builds up and users can verify the publisher. The feed signature protects update integrity; it does not
replace Authenticode for first-time downloads.
