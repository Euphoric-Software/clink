# Releasing Clink

This file is the canonical spec for how Clink is built, signed and published (contract v2). Tag `vX.Y.Z` (use
`tools/release.ps1`) runs `.github/workflows/release.yml`; a push to `dev` runs `release-dev.yml`.

## What a release contains

| Download | What it is |
| --- | --- |
| `Clink.exe` | The portable download: the SELF-CONTAINED single-file exe (compressed, about 75 MiB, needs no runtime). v0.4.1 and older updaters read exactly this name, so their bare-exe copies become portable self-contained copies on update. |
| `Clink.exe.sha256` | `<hash> *Clink.exe`. |
| `Clink-framework.exe` | The FRAMEWORK-DEPENDENT single-file exe (about 32 MiB, needs the .NET 10 Desktop Runtime). This is the exe the installer installs and what installed copies update to. |
| `Clink-framework.exe.sha256` | `<hash> *Clink-framework.exe`. |
| `Clink-Setup.exe` | Inno Setup installer (fixed name so `releases/latest/download/Clink-Setup.exe` always works; per user, no admin, fixed location, upgrades in place). It carries an optional task that installs the Microsoft .NET 10 Desktop Runtime when it is missing. |
| `Clink-Setup.exe.sha256` | `<hash> *Clink-Setup.exe` (what the website's "SHA-256 checksum" refers to for the installer). Attached to the GitHub release; the publish job's `Verify the release assets` step fails the run (before the feed upload and before the draft is published) unless the release carries all of these names: `Clink.exe`, `Clink.exe.sha256`, `Clink-framework.exe`, `Clink-framework.exe.sha256`, `Clink-Setup.exe`, `Clink-Setup.exe.sha256`, `THIRD-PARTY-NOTICES.txt`, so the site's `releases/latest/download/...` links cannot silently 404. The step reads the draft by its release ID (`gh api repos/.../releases/<id>`, the ID of the draft the previous step created, passed only through `env`), requires it to still be a draft with tag `vX.Y.Z`, and for each of the seven names requires exactly one asset of that exact (case-sensitive) name, in state `uploaded`, with the size of the local file and `sha256:` plus the local file's hash as GitHub's `digest` (a missing digest fails the step), and the release must carry no other asset. |
| `THIRD-PARTY-NOTICES.txt` | The third-party licence notices (the same bytes the installer installs next to `Clink.exe` and the app shows in About). Attached to the GitHub release only; it is not part of the feed bucket or the manifest. |
| `stable.json`, `stable.json.sig` (`dev.json`, ...) | Signed update manifest, only when the feed is enabled for that channel. |

There is no portable zip and no `Clink.portable` marker.

## Contract v2

### Trust anchors: `Clink/Assets/feed.json`

Committed and embedded in `Clink.exe`; the only place the app learns where the feed is and whom to trust:

```json
{ "schema": 1, "feedUrl": "https://dl.example/clink/", "devFeedUrl": "https://dl-dev.example/clink/",
  "stableKeys": ["<Base64 DER SPKI, ECDSA P-256>"], "devKeys": ["<another Base64 DER SPKI, ECDSA P-256>"] }
```

- Both channels are enabled together. The file is in exactly one of three states:
  - **off**: `feedUrl` is `""`, `devFeedUrl` is `""` or absent, and `stableKeys` and `devKeys` are both empty. The workflows publish
    only the GitHub release (with the installer) and log a warning.
  - **on**: `feedUrl` is valid AND `stableKeys` and `devKeys` are BOTH non-empty, every key is a valid ECDSA P-256 SPKI, and the
    two lists share no key. `feedUrl` must be https with a host, no user info (`@`), no query (`?`), no fragment (`#`), and end
    with `/`. `devFeedUrl` is optional; when it is non-empty it must follow exactly the same rules.
  - anything else that names a URL or a key (one channel only, keys without a URL, a `devFeedUrl` without a `feedUrl`, overlapping
    or malformed keys, a bad URL or `devFeedUrl`) is **misconfigured**: the client applies no updates from the feed, and CI
    (`Get-FeedConfig` in `tools/feed-lib.ps1`, which validates exactly like the client) fails the build.
- `devFeedUrl` (optional string, schema stays 1) is the base of the dev channel; absent or `""` means the dev channel uses `feedUrl`.
  Where the files live: `stable.json`, its `.sig` and the stable binaries under `feedUrl`; `dev.json`, its `.sig` and the dev binaries
  under the dev base (`devFeedUrl`, or `feedUrl` when that is empty). `Get-FeedConfig -Channel <channel>` returns that base as
  `FeedUrl`, and `make-manifest.ps1` (binary URLs must sit under it), `upload-feed.ps1` (public checks and cache purge) and the
  workflows' `feed_url` all use it. A dev client reads BOTH `stable.json` (from `feedUrl`) and `dev.json` (from the dev base).
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
  "notesUrl": "https://github.com/Euphoric-Software/clink/releases/tag/v0.4.2", "minRuntime": "10.0.0",
  "assets": { "exe":          { "url": ".../releases/v0.4.2/Clink.exe",              "size": 1, "sha256": "<64 hex>" },
              "exeFramework": { "url": ".../releases/v0.4.2/Clink-framework.exe",    "size": 1, "sha256": "<64 hex>" },
              "installer":    { "url": ".../releases/v0.4.2/Clink-Setup.exe",   "size": 1, "sha256": "<64 hex>" } } }
```

- `exe` is the self-contained exe (what portable copies take); `exeFramework` is what framework (installed) copies take, unless
  the manifest lacks it or `minRuntime` is newer than every installed Microsoft.WindowsDesktop.App runtime (then they fall
  back to `exe`). A client rejects the WHOLE manifest when `exeFramework` is present but invalid (https url, positive size,
  64 lowercase hex sha256), so it is always written from `package.json`, never by hand.
- `minRuntime` is `X.Y.Z` (clients parse it as a version). `tools/package.ps1` reads it from the Microsoft.WindowsDesktop.App
  version in the framework build's `Clink.runtimeconfig.json` (today `10.0.0`) and records it in `package.json`;
  `tools/make-manifest.ps1` copies it.

Signature file: one line of Base64, ECDSA P-256 / SHA-256 over the exact manifest bytes (UTF-8, no BOM), IEEE P1363
`r||s`, 64 bytes. Separate keys per channel: the stable key signs `stable.json`, the dev key signs `dev.json`.

### Two buckets (B2) and the object layout

Each channel has its own bucket on its own host, so a leaked dev token cannot touch the stable objects:

| Channel | Bucket | Suggested host (the owner confirms) | feed.json | GitHub environment |
| --- | --- | --- | --- | --- |
| stable | `clink-stable` | `dl.euphoric.software` | `feedUrl` = `https://dl.euphoric.software/clink/` | `feed-stable` |
| dev | `clink-dev` | `dl-dev.euphoric.software` | `devFeedUrl` = `https://dl-dev.euphoric.software/clink/` | `feed-dev` |

The hosts are one label deep, so the Universal SSL certificate covers them; the zone must be in the same Cloudflare account as the
buckets (custom domains need that). Each host is a custom domain of its bucket; `r2.dev` is off on both.

Inside a bucket, under `<prefix>` (validated `^clink(-staging)?$`, normally `clink`):

```
clink-stable:  <prefix>/releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-framework.exe, Clink-framework.exe.sha256,
                                          Clink-Setup.exe}
               <prefix>/stable.json, stable.json.sig
clink-dev:     <prefix>/dev/vX.Y.Z-dev.N/{same}                 (10 newest kept)
               <prefix>/dev.json, dev.json.sig
```

No `latest/` aliases. Versioned objects are write-once (immutable, `Cache-Control: public, max-age=31536000, immutable`,
`sha256` object metadata). Manifests and signatures use `Cache-Control: public, max-age=60`.

`upload-feed.ps1` writes the key `<prefix>/<folder>/<file>` but checks the public URL `<base><folder>/<file>`, so the PATH of the
channel's base URL must end in `/<prefix>/` (the host is mapped to the bucket root). `upload-feed.ps1` checks this
(`Assert-FeedBaseMatchesPrefix`) BEFORE any bucket access and fails with "Nothing was written" when it does not hold. A staging
run uses `CLINK_FEED_KEY_PREFIX=clink-staging` and a base path ending in `/clink-staging/`.

### GitHub releases

Every GitHub release carries `<channel>.json` and `<channel>.json.sig` (byte-identical to the bucket copies) when the feed is
enabled for that channel. The app's GitHub fallback verifies them against the same embedded keys.

## Workflow shape

Both workflows have two jobs.

CI note: the runner appends `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }` to every `shell: pwsh` step, so a step that handles a non-zero native exit itself (for example `gh release view` returning 1 for "not found", or a 404 from `gh api`) must set `$global:LASTEXITCODE = 0` right after capturing the code, or the job fails anyway.

1. `build` (`contents: read`, no secrets, no environment): checkout, `actions/setup-dotnet` followed by a step that verifies the SDK
   (`tools/lib/dotnet-sdk-lib.ps1`: the resolved dotnet host (muxer, `dotnet.exe`) has a Valid Authenticode signature, an exact observed Microsoft signer subject and a
   chain ending in the Microsoft Root Certificate Authority 2011 thumbprint, and the last non-empty line of `dotnet --version` (run with
   `DOTNET_NOLOGO=1`) equals the version `global.json` pins; both allow-lists contain only values that were observed, and a new value is added in a reviewed commit; `build.yml`'s two jobs run the same step), version (the stable workflow accepts only
   `refs/tags/vX.Y.Z`: it has NO `workflow_dispatch` and no branch trigger, and it refuses when `gh release view vX.Y.Z` succeeds), publish BOTH flavours with
   `tools/publish-flavours.ps1` (`dist/sc` self-contained, `dist/fx` framework-dependent; separate `obj_*`/`bin_*` folders, because
   a shared one mixed the flavours and produced an exe that failed with 0x80008096), run `tools/check-published.ps1` on both
   (size guards; `--packaging-check`, `--preflight`, the embedded-feed self-test, `--unittest ThirdPartyNotices` and ProductVersion on
   each exe, plus the N-1 regression below; every child process gets 90 s), install the
   pinned Inno Setup, run `tools/package.ps1` (both exes, both sha256 files, the setup exe built from the FRAMEWORK exe, the notices
   file, `package.json`), record the SHA-256 values (including `notices_sha256` and `pkg_sha256`, the hash of `package.json` itself) as job outputs, upload the package as an artifact.
   Before the package step the build job also runs `make-notices.ps1 -Check -AssetsFile Clink/obj_sc/project.assets.json`, resolves the
   runtime installer and checks runtime freshness (next sections). The dev workflow also builds
   and runs the unit tests first (on the bin build: 5 tests fail by design when `--unittest` runs from a published exe).
   `build.yml` has a no-secrets "Package dry run" job that does the same without the installer.
2. `publish` (`needs: build`, environment `feed-stable` / `feed-dev`, `contents: write`, fixed concurrency group
   `feed-stable-publish` / `feed-dev-publish` with `cancel-in-progress: false`). Guards first, before any secret is used:
   the stable job accepts only `^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$` and checks
   `git merge-base --is-ancestor $GITHUB_SHA origin/main` (full-history checkout; `main` is written out in the workflow, it is not read
   from the repository's default-branch setting); the dev job accepts only `refs/heads/dev`;
   both refuse when a GitHub release `vX.Y.Z` already exists (draft or published; see "Stale drafts"), and the dev job also refuses
   when the git tag `vX.Y.Z-dev.N` already exists (`gh api .../git/ref/tags/v...` must answer HTTP 404; any other answer fails), so the
   release is created together with its tag at the built commit. Then: download the artifact, verify
   `package.json`, `Clink.exe`, `Clink-framework.exe`, the setup exe and `THIRD-PARTY-NOTICES.txt` against the build job's outputs (including the single `Clink-Setup.exe.sha256` line check), read `Clink/Assets/feed.json` from the checkout.
   The GitHub release is created as a DRAFT with all assets, the "Verify the release assets" step checks them (exactly once each, uploaded, size and
   SHA-256 digest; a missing digest FAILS the run; any asset that is not on the required list FAILS the run; the channel manifest
   `stable.json`/`dev.json` and its `.sig` when the build produced them; `prerelease` is false for stable and true for dev; for dev the
   draft's `target_commitish` must equal `GITHUB_SHA`), then `upload-feed.ps1` runs (if the feed is on), and only then
   the draft is published by release ID (`gh api -X PATCH repos/.../releases/<id> -F draft=false -f make_latest=...`). A failed bucket upload therefore never leaves a visible release.
   After publishing, the dev job reads the new tag (`git/ref/tags/vX.Y.Z-dev.N`) and fails unless it is a plain tag (type `commit`) at `GITHUB_SHA`.
   The write token (`contents: write`) is NOT job-wide: every step that calls `gh` names `GH_TOKEN: ${{ github.token }}` in its own `env`, and the
   steps that hold secrets or call aws (`Make and sign the manifest`, `Check bucket secrets`, `Publish to the update feed`) never see it.
   The "latest" flag is set explicitly and is computed BEFORE the feed upload (step `Decide whether this release becomes latest`, `id: latest`, so a
   failure stops the job while nothing is public): the stable job (`tools/lib/release-latest-lib.ps1`, `Get-MakeLatestValue`) sets `make_latest=true` only when
   the version is higher than the current latest stable release (`releases/latest`; only `HTTP 404` means none yet, which counts as higher; any other lookup
   failure or an unparsable tag fails the run; stderr is never parsed as the tag; the step logs which path it took, a release
   answered (200) or HTTP 404, and the dev version step logs the same), so
   re-cutting or backporting an older version never takes over `releases/latest/download/...`. The value reaches the publish step only through
   `env: MAKE_LATEST`, which re-validates it as exactly `true` or `false`. The dev job always passes `make_latest=false`.
   - Feed enabled for the channel: require the environment secrets (fail if missing), make and sign the manifest
     (`tools/make-manifest.ps1`; the key is only in that step's environment; the signature is verified against the keys in
     `feed.json` before it is written; a mismatch fails the run), create the GitHub release with all assets including the
     manifest and signature, then run `tools/upload-feed.ps1`. Before the draft is created, the step `Find the GitHub history floor` lists
     the earlier releases (`gh api repos/<repo>/releases --paginate`, GET only) and computes the highest NON-DRAFT release of the channel
     that carried `<channel>.json`; `upload-feed.ps1` receives it as `-HistoryFloor`.
   - Feed not enabled: first the step `Guard - a feed that was on stays on` lists the assets of every earlier release of the repository
     (`gh api repos/<repo>/releases --paginate`, GET only) and FAILS when one carried `<channel>.json` (see "The feed cannot be switched off");
     otherwise warn and create the GitHub release (exe, sha256, setup exe) without manifest assets.
   - `make-manifest.ps1` binds `package.json` to the manifest: `files.exe.file` must be exactly `Clink.exe`, `files.exeFramework.file`
     `Clink-framework.exe` and `files.installer.file` `Clink-Setup.exe` (case-sensitive), the `exe` and `exeFramework` hashes must differ,
     and `minRuntime` must be `10.x.y` (the app targets `net10.0-windows`; raise the constant in `make-manifest.ps1` with the `TargetFramework`).
     Each file is then re-hashed against `package.json`, and the publish job has already compared `package.json` itself with the build job's `pkg_sha256`.
   - `upload-feed.ps1` (entry point, `#requires -Version 7`; the decisions are in `tools/lib/feed-publish-lib.ps1` and are tested with a fake aws in
     `tools/tests/feed.tests.ps1`): validates the prefix; takes the downgrade floor from the bucket's current `<channel>.json` ONLY after
     downloading `<channel>.json.sig` and verifying it against the channel's keys in `feed.json` (the keys the app trusts; a missing or
     invalid signature, an unpaired file or a manifest for another channel fails the run; no manifest and no signature at all is the first
     publish, which is logged and allowed); the effective floor is the HIGHER of that verified bucket floor and the GitHub history floor (a
     bucket that was emptied or rolled back cannot lower it); with NEITHER bucket file but a history floor the run is REFUSED unless the
     owner has set `CLINK_FEED_BUCKET_RESET_VERSION` to exactly that release's version (see "Recovering a stuck feed"); refuses when the
     bucket floor is higher than this version or this version is not newer than the history floor; a bucket manifest or `.sig` larger than
     1 MB is refused before it is read; for each versioned object DOWNLOADS the
     existing object and compares its real SHA-256 and size (never `x-amz-meta-sha256`, never `Content-Length`): same bytes = skip,
     different = FAIL; otherwise uploads with a conditional create (`aws s3api put-object ... --if-none-match "*"`). An aws cli that lacks
     the option FAILS the run (the version is logged; there is no fallback to a plain copy). R2 Bucket Lock on `releases/` remains the real
     write-once guarantee. Then GETs every asset's PUBLIC URL (the channel's base + path, no redirects) and requires status 200 and the same SHA-256
     and size of the served bytes; then uploads the manifest, then the `.sig`; optionally purges the two manifest URLs (under the channel's
     base) from Cloudflare (non-fatal). Dev only: prunes old `<prefix>/dev/vX.Y.Z-dev.N/` folders (strict regex, exit code checked). The step has `timeout-minutes: 30` and each public-URL request a 600 s timeout.
     Before anything is written it checks that the path of the channel's base URL ends in `/<prefix>/` (see "Two buckets").
   - Dev only: old GitHub releases are pruned in a step with `if: success()` (a failed run never prunes). It lists every release
     (`--paginate`), keeps the 10 newest PUBLISHED dev releases (strict `\Av...-dev.N\z` tag match; drafts left by failed runs are neither
     counted nor deleted), deletes the older ones with `gh release delete` (no `--cleanup-tag`) and then deletes the tag ref with
     `gh api -X DELETE`. With immutable releases the tag of a published release cannot be removed: that is logged as a warning and the
     loop continues; a release that cannot be deleted is counted and fails the step at the end (`continue-on-error: true`, so only an
     annotation). Watch the first prune of an immutable dev release (the oldest of 11 being `v0.4.2-dev.11`).
   - The old `Release` workflow on `origin/main` still has `workflow_dispatch` with `contents: write` and no environment, until
     `v0.4.2` is on `main`: nobody presses "Run workflow" on it before then.

The installer comes from a pinned Inno Setup release: `innosetup-6.7.3.exe` from
`https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/`, verified against a hard-coded SHA-256
(`9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`) before it runs. Inno Setup 6.3 or newer is required for
`x64compatible`. To bump it: download the new installer into an empty folder, check its Authenticode signature, compute the
SHA-256, change both values in both workflows in a reviewed commit. Every action is pinned by full commit SHA; Dependabot
proposes bumps. Every pinned action runs on Node 24 (`runs.using: node24` in the `action.yml` at the pinned commit; GitHub flags
Node 20 actions): `actions/checkout` v5.1.0, `actions/setup-dotnet` v5.4.0, `actions/cache` v5.1.0, `actions/upload-artifact` v7.0.2,
`actions/download-artifact` v8.0.2 and `softprops/action-gh-release` v3.0.3. When bumping one, read its `action.yml` at the new commit
(`gh api repos/<owner>/<repo>/contents/action.yml?ref=<sha>`), confirm `runs.using` and, for `softprops/action-gh-release`, that the
`id` output the workflows use is still set; review a Dependabot bump of it with extra care (it runs with `contents: write`).

### The feed cannot be switched off

Once a release of a channel has carried `<channel>.json`, the feed is on for good. A publish with the feed disabled in `feed.json` would
leave the bucket's last manifest (and every client that trusts it) frozen, a replay target for an old, still validly signed manifest. Both
publish jobs therefore refuse a feed-less publish when any earlier release of the repository (drafts included) has an asset named
`stable.json` (stable) or `dev.json` (dev). To recover, restore `feedUrl` and the keys in `Clink/Assets/feed.json`. Deleting old releases to
get around the check is not a supported way to turn the feed off.

## Owner setup checklist

GitHub (repo Settings). Create BOTH environments (with their protection rules and secrets) BEFORE the first push of these
workflows reaches `dev`: the first run auto-creates a missing environment WITHOUT any protection.

- [ ] Environment `feed-stable`: required reviewer (the owner), "Allow administrators to bypass" OFF, deployment rule "Selected
      branches and tags" with ONLY Tag-type rules (not Branch-type) for these patterns (GitHub's pattern language cannot exclude, so they are character classes that
      cannot match a `-dev.` tag; a final tag is `vX.Y.Z` with one or two digits per part): `v[0-9].[0-9].[0-9]`,
      `v[0-9].[0-9].[0-9][0-9]`, `v[0-9].[0-9][0-9].[0-9]`, `v[0-9].[0-9][0-9].[0-9][0-9]`. Do NOT add `main` (the stable workflow has
      no manual trigger) and never `dev`, `v*` or `v*-dev.*`. Add `v[0-9][0-9]...` patterns only when a major version reaches 10.
- [ ] Environment `feed-dev`: deployment rule limited to the `dev` branch (a Branch-type rule), "Allow administrators to bypass" OFF.
- [ ] The environment VARIABLE `CLINK_FEED_BUCKET_RESET_VERSION` is NOT set in `feed-stable` or `feed-dev` in normal operation. It is an
      owner-only, temporary, reviewed override for one publish (see "Recovering a stuck feed"); remove it right after.
- [ ] Environment secrets, one set per environment, with DIFFERENT names per channel (the workflows map them to the generic names the
      scripts read, `CLINK_FEED_SIGNING_KEY` and `R2_*`):

      | Environment | Signing key (PEM or one-line Base64) | R2 credentials |
      | --- | --- | --- |
      | `feed-stable` (`release.yml`) | `CLINK_FEED_STABLE_SIGNING_KEY` | `R2_STABLE_ACCESS_KEY_ID`, `R2_STABLE_SECRET_ACCESS_KEY`, `R2_STABLE_ENDPOINT`, `R2_STABLE_BUCKET` |
      | `feed-dev` (`release-dev.yml`) | `CLINK_FEED_DEV_SIGNING_KEY` | `R2_DEV_ACCESS_KEY_ID`, `R2_DEV_SECRET_ACCESS_KEY`, `R2_DEV_ENDPOINT`, `R2_DEV_BUCKET` |

      The purge token is also an environment secret, never a repository or organization one: `CLOUDFLARE_API_TOKEN` (Zone Cache Purge on the
      `euphoric.software` zone only, with an expiry date; one token per environment is preferred) and `CLOUDFLARE_ZONE_ID`, under the same
      names in both environments. Both are optional (the purge is non-fatal). The R2 tokens are per bucket (see "Cloudflare").
- [ ] Environment VARIABLES `CLINK_FEED_KEY_PREFIX` and `CLINK_FEED_BUCKET_RESET_VERSION` stay unset in normal operation, and neither exists at
      repository or organization level (a repo-level `CLINK_FEED_BUCKET_RESET_VERSION` would apply to every publish). The prefix defaults to `clink`.
- [ ] Delete every repo-level and organization-level copy of the feed, R2 and Cloudflare secrets (`CLINK_FEED_SIGNING_KEY`,
      `CLINK_FEED_STABLE_SIGNING_KEY`, `CLINK_FEED_DEV_SIGNING_KEY`, `R2_*`, `CLOUDFLARE_*`) and the old `CLINK_FEED_URL` / `CLINK_FEED_PUBLIC_KEY` / `CLINK_FEED_*` variables. Only the two
      environments hold them.
- [ ] Rulesets (Settings > Rules > Rulesets); these are the ones in use:
      1. `stable-tags`: target ALL tags, exclude `v*-dev.*`; restrict creation, update and deletion and block force pushes; the only bypass is the
         repository admin role (the owner). GitHub Actions has NO bypass (a workflow must not be able to create or move a stable tag).
      2. `dev-tags`: target tags `v*-dev.*`; restrict updates and block force pushes only, so GitHub Actions may create and delete them (the dev
         workflow publishes and prunes them).
      3. `main-dev-safety`: target the default branch and `dev`; restrict deletions and block force pushes; no bypass; NO pull-request or review
         requirement, because the owner pushes to `dev` directly (a solo flow).
      4. `no-v-branches` (a branch ruleset): restrict creation of branches named `v*`, so no branch can be named like a stable tag and be
         confused with one.
- [ ] Enable immutable releases (Settings > General > Releases): once a draft is published its tag and assets are locked,
      which complements the draft-then-publish flow. (Enabled 2026-10-28. It also means the tag of an immutable dev release cannot be deleted
      when the prune step deletes the release; that is a warning, see "Workflow shape".)
- [ ] `.github/CODEOWNERS` with required code-owner review is NOT part of the solo flow (it would need a pull request for every change). Add it,
      assigning the owner to `.github/workflows/**`, `tools/**`, `installer/**`, `Clink/Clink.csproj`, `Clink/Services/Update*.cs`,
      `Clink/Assets/feed.json` and `.github/CODEOWNERS` itself, only together with a pull-request rule and a second reviewer. Until then the
      gates are the `feed-stable` required reviewer, the tag rulesets and the reviewer checklist below.
- [ ] Settings > Actions > General > Workflow permissions: read-only default token.

Reviewer checklist for each publish approval (the environment reviewer is the last gate before secrets are used):

- [ ] The deployment's commit SHA is on `main` (stable, run from an owner-created `vX.Y.Z` tag) or `dev` (dev builds).
- [ ] The tag matches the version in `Clink/Clink.csproj` and the release you intend.
- [ ] The diff of `.github/` and `tools/` (and `installer/`, `feed.json`) since the last release was reviewed.

Cloudflare (decided 2026-10-28: two buckets, "B2"; see "Two buckets (B2) and the object layout"):

- [ ] Two buckets, `clink-stable` and `clink-dev`, each on its own custom domain (suggested `dl.euphoric.software` -> `clink-stable` and
      `dl-dev.euphoric.software` -> `clink-dev`, pending the owner's confirmation of the host names and that the `euphoric.software` zone
      is in the same Cloudflare account as the buckets). The `r2.dev` public URL is turned off on both. `feed.json`: `feedUrl` is
      `https://<stable host>/clink/`, `devFeedUrl` is `https://<dev host>/clink/` (the path must end in the key prefix, normally `/clink/`).
- [ ] One bucket-scoped Object Read & Write API token PER BUCKET, each with an expiry date (about 180 days, with a renewal reminder). R2
      tokens are per bucket, not per prefix, so this is what keeps a leaked dev token away from the stable bucket. The stable token goes
      into the `feed-stable` secrets `R2_STABLE_ACCESS_KEY_ID`, `R2_STABLE_SECRET_ACCESS_KEY`, `R2_STABLE_ENDPOINT`
      (`https://<account-id>.r2.cloudflarestorage.com`) and `R2_STABLE_BUCKET` (`clink-stable`); the dev token into `feed-dev` as `R2_DEV_*`
      (`clink-dev`). The signing keys are per channel too (`CLINK_FEED_STABLE_SIGNING_KEY`, `CLINK_FEED_DEV_SIGNING_KEY`).
- [ ] Copium's token re-scoped to Copium's own bucket (no shared bucket or token).
- [ ] Purge token: Zone Cache Purge on the `euphoric.software` zone only, with an expiry date, as the environment secrets
      `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ZONE_ID` (one token per environment preferred). The purge URLs are the two manifest URLs under
      the channel's own base.
- [ ] Bucket Lock (a retention rule) so released objects cannot be changed even with a stolen token. This is the real write-once guarantee;
      the `--if-none-match` create and the download-and-hash comparison of an existing object are best effort:
      - `clink-stable`: prefix `clink/releases/`, retention INDEFINITE. Do not lock `clink/stable.json` or its `.sig` (they are replaced on every publish).
      - `clink-dev`: prefix `clink/dev/`, retention 30 days (pruning old dev folders then waits for the retention period; the prune only
        warns for objects that are still locked).
- [ ] CORS on `clink-stable` ONLY, for GET and HEAD from the origin `https://euphoric.software` (nothing else: the `www` host redirects to the
      apex and the app itself needs no CORS), `MaxAgeSeconds` 3600, with `Vary: Origin`. The site reads the stable feed only; `clink-dev` has no CORS.
- [ ] No-transform rule: a configuration rule for the hosts `dl` and `dl-dev` on the `.json` and `.sig` paths with Rocket Loader, Polish, Email
      Obfuscation and Minify OFF (the signature covers the exact bytes), and a cache rule that respects the origin's `Cache-Control`.
- [ ] Hardware-key 2FA on the Cloudflare and GitHub accounts.

The `feedUrl` contract is NOT single any more: stable and dev clients both read `stable.json` from `feedUrl`; only the dev channel's files
(`dev.json`, its `.sig`, the dev binaries) are under `devFeedUrl`. A manifest the client refuses still falls back to the signed GitHub release.

## Keys

Generate one pair per channel, on a trusted machine, outside any git work tree (the script refuses otherwise; `*.pem` is also
git-ignored):

```
powershell -File tools\new-feed-key.ps1 -Channel stable -PrivateKeyPath D:\keys\clink-stable.pem
powershell -File tools\new-feed-key.ps1 -Channel dev    -PrivateKeyPath D:\keys\clink-dev.pem
```

Do it on an offline PC with an encrypted removable drive outside any git tree, and generate a second stable key (`clink-stable-2.pem`,
the "next" key of "Rotation") in the same session. `feed.json` then lists `stableKeys` = [stable-1, stable-2] and `devKeys` = [dev-1]; only
stable-1 and dev-1 become secrets, stable-2 never goes online. Keep two encrypted backups, one off-site, with the passphrase in a password manager.

For each key: add the printed public key to `stableKeys` / `devKeys` in `Clink/Assets/feed.json`; put the key into
`CLINK_FEED_STABLE_SIGNING_KEY` (`feed-stable`) or `CLINK_FEED_DEV_SIGNING_KEY` (`feed-dev`), the environment secret of the matching channel; keep an encrypted offline backup; delete the working copy. Store the
secret as the PEM file in one-line Base64 (it survives the secret editor's newline handling):
`[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\keys\clink-stable.pem'))`. `tools/make-manifest.ps1` accepts either the
plain PEM or that Base64 text. Until the working copy is deleted, restrict its ACL to your account
(`new-feed-key.ps1` prints the `icacls` command).

### Enabling the feed

A reviewed pull request that sets `feedUrl`, `devFeedUrl` (the dev host) and adds at least one public key to BOTH `stableKeys` and `devKeys` in
`Clink/Assets/feed.json` (both channels are enabled together; the client treats a partial file as misconfigured and CI fails
it). The next release build embeds the new anchors and the publish jobs start signing and uploading. Merge order matters:
both environments, their secrets and both buckets must exist before the commit lands, or the publish job fails by design. Before it,
check that no existing release or draft already carries `stable.json` or `dev.json`. `feed.json` can still change freely until the first
feed-on build ships; after that the URLs and keys are baked into installed clients.

### Rotation

Because clients only trust the keys embedded in their own build, rotate in two releases:

1. Generate an offline "next" stable key now and keep it offline. Ship its public key in `stableKeys` alongside the active one.
2. To rotate: put the next key into the `feed-stable` secret and remove the old public key from `feed.json` in a later release
   (clients that already carry the next key keep updating). Generate a fresh offline next key and add it.
3. A leaked or lost key with no pre-shipped successor means shipping a new build (installer download) with the new key; the
   old key stays trusted by old clients until they update, so treat a leak as an incident (revoke the bucket token, publish a
   fixed release by hand).
4. The publish step verifies the bucket's current manifest against the keys in `feed.json` before it uses it as the downgrade floor, so a key that
   is removed from `feed.json` can no longer vouch for the manifest already in the bucket. Therefore switch the secret to the next key and publish one
   release with BOTH keys still listed first; remove the old public key in a later release (otherwise the publish fails closed on the old manifest).

### Recovering a stuck feed

The publish step fails closed, so some failures leave the feed refusing every later publish until the owner acts. Find the case below.
The message of the refusal ends with "or a previous publish was interrupted between the manifest and its .sig; see RELEASING.md
'Recovering a stuck feed'".

- **Torn pair** (the manifest `<channel>.json` was uploaded but its `<channel>.json.sig` was not; the run failed or was cancelled between
  the two uploads). The next publish refuses with "has no ... .sig". Fix: download the matching `<channel>.json.sig` from the assets of the
  DRAFT release that the failed run created (the draft carries the manifest and the signature that were made together), check that the
  `<channel>.json` of that draft is byte-identical to the one in the bucket, and upload that `.sig` to `<prefix>/<channel>.json.sig` with
  the same `Cache-Control` and content type. Then re-run, or publish a newer version.
- **Leaked or removed key** (the key that signed the bucket's manifest was removed from `feed.json`, or must be distrusted, so the bucket's
  manifest no longer verifies). The owner, in this order: (1) deletes `<prefix>/<channel>.json` and `<prefix>/<channel>.json.sig` from the
  bucket; (2) sets the environment variable `CLINK_FEED_BUCKET_RESET_VERSION` of `feed-stable` / `feed-dev` to the version of the newest
  PUBLISHED release that carried `<channel>.json` (the refusal prints it); (3) publishes a newer version, signed with the new key listed in
  `feed.json`; (4) removes the variable. With the variable set and NEITHER file in the bucket, the publish accepts the GitHub history floor
  instead of the (deleted) bucket floor; it is ignored when the bucket has a manifest, and it never lowers the history floor. It is an
  owner-only, temporary, reviewed override: set it only for the one publish, never leave it in place.
- **A failed FIRST feed-on run.** The first run with the feed enabled leaves a DRAFT release that carries `<channel>.json`, and possibly
  objects (even the manifest) in the bucket. The guard `a feed that was on stays on` counts drafts, so a later feed-less publish is refused
  while that draft exists. If the feed is to stay off after all, the owner first deletes that draft release (and its tag if it was created)
  and cleans the bucket objects of the failed version, and only then publishes feed-less. To retry with the feed on, publish a newer version
  (the versioned objects of the failed version stay write-once and are compared by content).
- **A re-run of the same version is refused while its draft exists.** A failed run leaves a DRAFT release `vX.Y.Z` (and the workflow refuses to
  touch it). Either publish a NEWER version, or, after checking that the draft holds exactly the assets of that build, publish it by hand
  (`gh release edit vX.Y.Z --draft=false`). For a dev build the git tag does not exist while the release is a draft, so the tag guard does not fire.
- **The bucket upload succeeded but publishing the GitHub release failed.** The bucket already serves the new manifest while the GitHub
  release is still a draft. Check the bucket copies of `<channel>.json` and its `.sig` against the draft's assets, then publish the verified
  draft by hand (`gh release edit vX.Y.Z --draft=false`; for stable add `--latest` only when this version is the highest). Do not re-run:
  the run would be refused for the existing draft, and a newer version is only needed when the draft is not trustworthy.

### Stale drafts

A failed or cancelled publish run leaves a DRAFT release. A draft is invisible to the build job's read token, so the build job's choice of the
next dev number `N` (above every PUBLISHED dev tag) cannot see it. The publish job's guard (`gh release view vX.Y.Z`, which finds drafts) then
refuses to touch a draft with the same version. Look at the draft (its assets show how far the run got), delete it
(`gh release delete vX.Y.Z --yes`; a draft has no tag yet) and re-run, or let the next push pick a higher `N`. The dev prune step neither counts nor
deletes drafts, so stale drafts accumulate until someone deletes them. Note that the guard `a feed that was on stays on` counts drafts too.

## Dry runs

### Staging dry run (before the feed is turned on)

NOT in this repository: the workflows hard-code the `feed-dev` / `feed-stable` environments and the real secret names, and a staging
`feed.json` on `dev` would ship a real prerelease that trusts throwaway keys. Use a PUBLIC fork under the owner's personal account (environments
and their secrets work in public forks on the Free plan), with throwaway keys and `clink-staging` as the key prefix (environment variable
`CLINK_FEED_KEY_PREFIX=clink-staging`, a `feedUrl` whose path ends in `/clink-staging/`; the publish step refuses a mismatch before it writes anything).
Never put a real key or a production bucket or token into the fork. Setup: two staging buckets with `r2.dev` on (`feedUrl` / `devFeedUrl` =
`https://pub-<id>.r2.dev/clink-staging/`), 7-day tokens, Bucket Lock on `clink-staging/releases/` for 1 day. Pass criteria:

1. The first dev publish logs "no dev floor (first publish)" and the bucket copies are byte-equal to the release assets.
2. After deleting `dev.json` and `dev.json.sig` from the bucket, the next publish is refused.
3. Setting `CLINK_FEED_BUCKET_RESET_VERSION` allows it; then unset the variable.
4. A garbage `GH_TOKEN` in the history step makes it fail, not report "empty".
5. `aws s3api put-object --if-none-match "*"` answers 412 on an existing key and succeeds on a new one (R2 honours the header).
6. A fork tag `v0.4.2` publishes `stable.json`; overwriting or deleting under `releases/` is refused.
7. The dev token gets 403 on the stable bucket.

Then, against the production hosts, probe the CORS header on the cached copy of `stable.json` and that no transform changes the bytes. Revoke
and delete everything staging created afterwards.

Locally you can check the pieces without any bucket:

```
.\build.ps1 -Package                      # dist\sc, dist\fx, dist\package (the setup exe needs Inno Setup 6.3+, otherwise it is skipped with a warning)
$env:CLINK_FEED_SIGNING_KEY = Get-Content -Raw D:\keys\throwaway.pem
try {
  powershell -File tools\make-manifest.ps1 -Channel dev -Version 0.4.2-dev.1 -Tag v0.4.2-dev.1 `
    -BaseUrl https://downloads.example/clink/dev/v0.4.2-dev.1/ -NotesUrl https://example/notes -PackageDir dist\package -FeedJson <feed.json with the throwaway public keys>
} finally {
  Remove-Item Env:\CLINK_FEED_SIGNING_KEY -ErrorAction SilentlyContinue   # the child process clears its own copy; clear this shell's too
}
```

## Local packaging

`.\build.ps1 -Package` publishes both flavours (Release by default; it warns for Debug) into `dist\sc` and `dist\fx` and runs
`tools/package.ps1` into `dist\package`: `Clink.exe`, `Clink.exe.sha256`, `Clink-framework.exe`, `Clink-framework.exe.sha256`,
`Clink-Setup.exe` (when `ISCC.exe` is found; `tools/package.ps1 -SkipInstaller` skips it even then, `-RequireInstaller` fails when it is missing), `THIRD-PARTY-NOTICES.txt` and `package.json`. A local installer
has no .NET runtime task unless the runtime inputs are passed (see Installer inputs below); the script warns about it. A real ISCC output
of 1 MiB or less is refused (`-SkipSetupSizeCheck` is for the script tests' fake ISCC only). To check the published
exes: `tools\check-published.ps1 -ScExe dist\sc\Clink.exe -FxExe dist\fx\Clink.exe`.

Uninstall: the installer removes only the updater's own `Clink.exe.bak/.new/.failed` beside the exe and the Framework copy's `updates\fx` files
(then the empty folders). Copies that switched to the self-contained flavour keep their downloads in `%LOCALAPPDATA%\Clink\updates` after
uninstall, by design: that folder is shared with portable copies. `installer-script.tests.ps1` enforces this with an allowlist for
`[UninstallDelete]` and rejects `[InstallDelete]`, `[UninstallRun]` and `DelTree(`.

## Pinned SDK (`global.json`)

`global.json` pins the .NET SDK to an exact version with `"rollForward": "disable"`, and every workflow installs it with
`actions/setup-dotnet` `global-json-file: global.json`. The SDK that builds a release therefore only changes in a reviewed
commit. Bump it deliberately, once per release, together with the third-party notices (the runtime pack and SDK versions the
notices describe). The procedure is the one list under "Runtime freshness gate" below (install the SDK, set `global.json`, publish,
re-pin and regenerate the notices, raise `MinimumRuntimeVersion` when the runtime moved, then run `tools\tests\run-script-tests.ps1` and
the unit tests, and commit together). Check that the version exists for the runner image (`actions/setup-dotnet` downloads it); the
next workflow run proves it.

`build.ps1` and `tools/publish-flavours.ps1` probe `dotnet --version` run in the repo root (which honours `global.json`): the
`dotnet` on PATH is used when it satisfies the pin, otherwise `%USERPROFILE%\.dotnet\dotnet.exe` is tried, and when neither does
they fail with "No .NET SDK satisfying global.json found". When bumping the pin, install the new SDK side by side with
`dotnet-install.ps1 -Version <x>` (an exact version; the old one can stay). Nested worktrees under `.claude\worktrees` inherit the
parent checkout's `global.json` once `dev` has it (the SDK resolver walks up the directory tree), so a worktree on an older
branch is pinned too.

## Installer inputs (ISCC)

`tools/package.ps1` feeds the Inno Setup compiler `/DAppVersion`, `/DAppFileVersion`, `/DOutDir`,
`/DSourceExe=<OutDir>\Clink-framework.exe` (the framework-dependent exe), `/DNoticesFile=<OutDir>\THIRD-PARTY-NOTICES.txt` (an absolute
path; this copy is also the release asset) and, from the runtime inputs, `/DDotNetRuntimeUrl`, `/DDotNetRuntimeSha256` and
`/DDotNetRuntimeVersion`. The runtime inputs are mandatory under `-RequireInstaller`: the script refuses without them, before it writes
anything. Without `-RequireInstaller` and without them it warns and builds the installer WITHOUT the runtime task. Pass them as
`-RuntimeInfoPath <json>` (the JSON printed by `tools/resolve-dotnet-runtime.ps1`) or as `-DotNetRuntimeUrl`, `-DotNetRuntimeSha256`
and `-DotNetRuntimeVersion` together. `package.ps1` validates them with `tools/lib/dotnet-runtime-lib.ps1` before ISCC sees them: a
plain `x.y.z` version at or above the lib's `MinimumRuntimeVersion`, the URL equal to the `builds.dotnet.microsoft.com` URL built for
that version, a 64-hex SHA-256, no quote, space or control character. `-NoticesPath` (default `<repo>\THIRD-PARTY-NOTICES.txt`) must
exist; it is copied to `-OutDir` and listed in `package.json` as `files.notices`. `package.ps1` also refuses inputs whose ProductVersion
differs from `-Version` (`-SkipVersionCheck` is for the script tests only) and reads the default `minRuntime` from exactly one
`Clink\bin_fx\**\Clink.runtimeconfig.json` (`-RuntimeConfigPath` or `-MinRuntime` override).

## .NET runtime for the installer

`tools/resolve-dotnet-runtime.ps1` picks the latest .NET 10 Desktop Runtime (win-x64) installer from Microsoft's release metadata
(`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`; only that host is allowed, and redirects are not
followed when fetching that metadata file), downloads it into a scratch folder, checks the SHA-512 from the metadata and the Authenticode signature (exact signer subject
and root thumbprint allow-listed in `tools/lib/dotnet-runtime-lib.ps1`), computes the SHA-256 Inno Setup needs and deletes the file. The
file is never executed. The release workflows run it in the step `Resolve the .NET runtime installer` (`id: runtime`): it prints
`{version,url,sha256,sha512,size}` as JSON (saved to `$RUNNER_TEMP\dotnet-runtime.json`, which `package.ps1 -RuntimeInfoPath` reads) and
writes the same values to `GITHUB_OUTPUT` (the freshness step uses `steps.runtime.outputs.version`). A latest version below
`MinimumRuntimeVersion` in the lib is refused, so a stale or rolled-back `releases.json` cannot make the installer ship an old runtime.

## Runtime freshness gate

The self-contained `Clink.exe` bundles the runtime pack that the pinned SDK (`global.json`) restores, so a .NET security release does
not reach it until the pin moves. `tools/check-runtime-freshness.ps1 -AssetsFile Clink/obj_sc/project.assets.json -LatestVersion <resolver
version> [-FailOnStale]` reads the bundled `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64`
versions (exact `[x, x]` pins in `downloadDependencies`; a missing pack or file is a failure) and compares them with the latest runtime:
equal or newer is ok; older FAILS a stable release (`release.yml` passes `-FailOnStale`) and only prints a `::warning::` for a dev build
(`release-dev.yml`). `build.yml`'s package dry run does not run it (it does not run the resolver, so it has no latest version; the resolver is the part that
makes no network call there, the pinned Inno Setup installer is still downloaded), but it compiles the real installer with the pinned Inno Setup and the committed floor as runtime version (dummy SHA-256, never uploaded); it does run `make-notices.ps1 -Check -AssetsFile ...`.

Bump procedure when it fails or warns (one reviewed commit):

1. Install an SDK that ships the new runtime (`dotnet-install.ps1 -Version <x>`) and set it in `global.json`.
2. Publish once (`.\build.ps1 -Package` or `tools\publish-flavours.ps1`) so the new runtime packs are in the NuGet cache, then run
   `tools\notices\make-notices.ps1 -Update -RuntimeVersion <new> -Accept` (read the printed hash changes: a changed hash means the licence
   text changed), `-Generate`, review the diff of `THIRD-PARTY-NOTICES.txt`, and `-Check -AssetsFile Clink\obj_sc\project.assets.json`.
3. Raise `$script:MinimumRuntimeVersion` in `tools/lib/dotnet-runtime-lib.ps1` to the new version and run the script tests.

## Third-party notices

`THIRD-PARTY-NOTICES.txt` is generated from `tools/notices/sources.json` (`make-notices.ps1 -Generate`) and verified offline by `-Check`.
Both release workflows and the dry run run `-Check -AssetsFile Clink/obj_sc/project.assets.json` after the publish: the file's sections
must match their pinned hashes and the restore must use the pinned runtime packs, SDK reference and CsWin32. The assets file must hold
both runtime packs (`-AllowMissingRuntimePacks` exists for local use and is never used by CI). `package.ps1` copies the file into the
package, the installer installs that copy, `package.json` records its SHA-256 (`files.notices`), the build job exposes it as
`notices_sha256`, and the publish job re-hashes the artifact copy before the GitHub release is created with the file as an asset.

## N-1 regression check

`tools/check-published.ps1` copies `Clink-framework.exe` into a fresh folder next to a garbage `hostfxr.dll`, points `DOTNET_ROOT` (and
`DOTNET_ROOT_X64`) at an empty folder for that run only, and requires `--packaging-check Framework` and `--preflight` to pass. The
framework build uses `AppHostDotNetSearch=Global`, so the apphost must ignore both the planted DLL and the environment variable; a
failure means the exe would load a runtime from a place that is not the registered install.

## Script tests

`tools\tests\run-script-tests.ps1` (Windows PowerShell 5.1 and pwsh 7, no modules) parses every script under `tools\` and
`build.ps1`, runs every `tools\tests\*.tests.ps1`, and `packaging.tests.ps1` covers `package.ps1` (including the runtime and notices
wiring against a fake `ISCC.cmd`; Inno Setup is not needed), `make-manifest.ps1` (with a throwaway key made inside the test),
`check-published.ps1`'s size guards and timeout handling, and text assertions on the workflows (pinned `uses:`, the release asset names
including the notices, the resolver / notices / freshness / Inno steps, `global-json-file`, no `${{ }}` inside `run:` scripts).
`feed.tests.ps1` covers `tools/lib/feed-publish-lib.ps1` and `upload-feed.ps1`'s decisions with a fake aws (lying metadata, size/hash mismatch,
bad or missing floor signature, first publish, the GitHub history floor and the reset override, `--if-none-match` unsupported; the real aws
invoker runs against a fake `aws.cmd` on PATH), `Get-FeedConfig` including `devFeedUrl`, the base-URL/prefix guard, the `make-manifest.ps1`
binding rules, the prune step of `release-dev.yml` (run against a fake `gh`) and text assertions on the workflows (step-scoped tokens, the
tag and `target_commitish` checks, the digest and unexpected-asset checks, the hard-coded `main`, Node 24 pins).
`runtime-freshness.tests.ps1`, `make-notices.tests.ps1`, `resolve-dotnet-runtime.tests.ps1` and `installer-script.tests.ps1` cover the
other scripts.

## Recommended next step: Authenticode

Sign `Clink.exe` and the setup exe with a code-signing certificate (or Azure Trusted Signing) in the `build` or a third job so
SmartScreen reputation builds up and users can verify the publisher. The feed signature protects update integrity; it does not
replace Authenticode for first-time downloads.
