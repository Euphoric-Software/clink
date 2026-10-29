# Releasing Clink

Two parts: the **owner guide** (short, plain steps, first) and the **reference** (the spec, contract v2, from "What a release contains" on).
Tag `vX.Y.Z` runs `.github/workflows/release.yml`; a push to `dev` runs `.github/workflows/release-dev.yml`.

How it fits together (decided 2026-10-28):

- The app never contacts GitHub. Updates come only from R2: `clink-stable` on `dl.euphoric.software` and `clink-dev` on
  `dl-dev.euphoric.software` (`feedUrl` / `devFeedUrl` in `Clink/Assets/feed.json`). Release notes are a `notes.txt` on R2 next to the
  binaries; the app opens it in the browser. The website links to R2, not to GitHub.
- The GitHub repo becomes PRIVATE later (about 4 weeks after v0.4.2 ships; the date is not fixed) and stays on the GitHub Free plan. On
  Free private repos **environments, environment secrets, deployment policies and rulesets do not exist** (environment secrets are
  ignored), and Actions has $12 a month included and 500 MB of artifact storage. So nothing in the workflows uses an environment: keys
  and tokens are plain REPO secrets, signing is automatic in CI for both channels, and there is no offline signing.
- GitHub releases stay as the private archive, the audit trail and the downgrade "history floor" (the workflow reads them with the job
  token, which keeps working in a private repo).
- No timestamp or heartbeat service.

## Owner guide

### One-time setup

Do this once, in this order, before the first feed-on release. All of it is clicks except the keys (one script). Never paste a secret
into a chat, an issue or a commit.

**1. Cloudflare: buckets and hosts** (dash.cloudflare.com > R2 Object Storage)

1. Click **Create bucket**, name it `clink-stable`, **Create bucket**. Do it again for `clink-dev`.
2. Open `clink-stable` > **Settings** > **Custom Domains** > **Add** > `dl.euphoric.software` > **Continue**. For `clink-dev` add
   `dl-dev.euphoric.software`. (The `euphoric.software` zone must be in the same Cloudflare account as the buckets.)
3. In BOTH buckets: **Settings** > **Public Development URL** (R2.dev) > **Disable**. The custom domains are the only way in.
4. **Bucket Lock** (bucket > **Settings** > **Bucket Lock** > **Add rule**). This is the real write-once guarantee; the `--if-none-match`
   create and the download-and-hash comparison of an existing object are best effort:
   - `clink-stable`: prefix `clink/releases/`, retention INDEFINITE. Do not lock `clink/stable.json` or its `.sig` (they are replaced on every publish).
   - `clink-dev`: prefix `clink/dev/` (with the trailing slash), retention 30 days (pruning old dev folders then waits for the retention
     period; the prune only warns for objects that are still locked).
   - Check that the rules are really in place WITHOUT writing anything: `npx wrangler r2 bucket lock list clink-stable` (and `clink-dev`)
     is a read-only listing of the rules (it uses your own Cloudflare login, not the CI token). It must show exactly the prefix and
     retention above. The old probe (a scratch key under `clink/releases/` written twice with `--if-none-match`) is NOT used any more: it
     left an object behind that an INDEFINITE lock keeps for ever. If you want a live test anyway, use ONE clearly named key,
     `clink/releases/_lock-probe/x` in `clink-stable` (`clink/dev/_lock-probe/x` in `clink-dev`), with the owner's own token: a first
     `aws s3api put-object` creates it, then a plain overwrite AND a `delete-object` must both be REFUSED. That object stays locked for good
     in `clink-stable` (indefinite retention) and for 30 days in `clink-dev`; it is harmless (no release or manifest points at it, and the dev
     prune only touches `v...-dev.N` folders), but do it once, not on every release.
   - What Bucket Lock does NOT cover: `stable.json`, `stable.json.sig`, `dev.json` and `dev.json.sig` stay replaceable with a stolen token
     (the publish job has to replace them), so a stolen token can put an OLD, validly signed manifest back (replay) or delete a manifest.
     Replay is limited by the client, not by the bucket: it refuses a manifest older than the highest version it verified before, and
     one older than 90 days (the freeze and floor rules, review item NEW-2); the publish step's downgrade floors cover the publisher's side.
5. **CORS on `clink-stable` ONLY**: `clink-stable` > **Settings** > **CORS Policy** > **Add**, then paste (GET and HEAD from
   `https://euphoric.software` and nothing else; `clink-dev` has no CORS):
   ```json
   [{ "AllowedOrigins": ["https://euphoric.software"], "AllowedMethods": ["GET", "HEAD"], "AllowedHeaders": ["*"], "MaxAgeSeconds": 3600 }]
   ```
   R2's CORS settings cannot set a `Vary: Origin` response header, so a copy of `stable.json` that Cloudflare cached from a request WITHOUT
   an `Origin` header has no CORS headers and is then served to the site as it is. Add a cache rule or a transform rule (a response header
   `Vary: Origin`, or a cache key that includes the `Origin` header) for the `.json` paths of the stable host, then probe it: request
   `stable.json` once without an `Origin` header and once with `Origin: https://euphoric.software`; both answers must carry what the site needs.
6. No-transform rule: in the `euphoric.software` zone, **Rules** > **Configuration Rules** > **Create rule**, for the hosts
   `dl.euphoric.software` and `dl-dev.euphoric.software` where the URI path ends with `.json` or `.sig`: Rocket Loader, Polish, Email
   Obfuscation and Minify OFF (the signature covers the exact bytes). Then **Rules** > **Cache Rules** for the same paths: the edge TTL
   respects the origin's `Cache-Control` (the files are sent with `max-age=60`).
7. `nosniff` on the download hosts: in the `euphoric.software` zone, **Rules** > **Transform Rules** > **Modify Response Header** > **Create rule**,
   for the hosts `dl.euphoric.software` and `dl-dev.euphoric.software` (all paths): **Set static** header `X-Content-Type-Options` = `nosniff`.
   `notes.txt` is untrusted text that a browser opens from a trusted host; with this header (and the `text/plain; charset=utf-8` type that the
   upload sets) a browser never treats it as HTML. R2 itself cannot add the header, so the rule is what provides it. `upload-feed.ps1` checks
   the served `notes.txt`: a wrong `Content-Type` FAILS the publish, a missing `nosniff` is a `::warning::` (the rule is the owner's setup and
   cannot be relied on before it exists). After the first dev run, make sure the log has no `nosniff` warning; then the rule works.

**2. Cloudflare: R2 tokens, one per bucket** (R2 Object Storage > **Manage API Tokens** > **Create API token**)

1. Name `clink-stable-ci`, permission **Object Read & Write**, **Specify bucket(s)** = `clink-stable` only, TTL = custom, about 180 days
   (put the renewal date in your calendar). **Create API token**.
2. The next page shows the **Access Key ID**, the **Secret Access Key** and the S3 endpoint **once**. Keep the tab open until step 5 below is done.
3. Do the same for `clink-dev` (name `clink-dev-ci`, bucket `clink-dev` only). R2 tokens are per bucket, not per prefix: this is what keeps a
   leaked dev token away from the stable objects.

**3. Cloudflare: Zone Cache Purge token** (optional; the purge is non-fatal) - profile > **API Tokens** > **Create Token** > **Create Custom Token**:
permission Zone > Cache Purge > Purge, Zone Resources = Include > Specific zone > `euphoric.software`, with an expiry date. Also copy the
**Zone ID** (zone Overview page, right column). Know its blast radius: a Cloudflare purge token is scoped to a ZONE, not to a host or a path,
so a leaked token can purge the cache of the whole `euphoric.software` zone (every site on it). It cannot read or write any object.

**4. Keys** (on your PC, Windows PowerShell 5.1 is fine; pwsh 7 is NOT needed). Write the files to the USB drive (or any folder outside
the repo; the script refuses a path inside a git work tree):

```
powershell -NoProfile -File tools\new-feed-key.ps1 -Channel stable -PrivateKeyPath E:\keys\clink-stable.pem
powershell -NoProfile -File tools\new-feed-key.ps1 -Channel dev    -PrivateKeyPath E:\keys\clink-dev.pem
powershell -NoProfile -File tools\new-feed-key.ps1 -Channel stable -Next -PrivateKeyPath E:\keys\clink-stable-next.pem
powershell -NoProfile -File tools\new-feed-key.ps1 -Channel dev    -Next -PrivateKeyPath E:\keys\clink-dev-next.pem
```

Each run prints the PUBLIC key and a short summary of what goes where; it never prints the private key (it only goes into the .pem file).
The summary says it again: public values go into `Clink/Assets/feed.json`, the private values go into the repo secrets below, and the
"next" keys go into NO secret. Then **back up the `.pem` files to the USB drive** (a second copy of the drive, off-site, is better) and delete
any other copy. `feed.json` ends up with `stableKeys` = [stable, stable-next] and `devKeys` = [dev, dev-next] (no key may be in both lists).

**5. GitHub: repo secrets** (the repo > **Settings** > **Secrets and variables** > **Actions** > **Secrets** tab > **New repository secret**).
Use REPO secrets, never "Environments" (they do not exist on a private Free repo and their secrets would be ignored). Exactly these 12
names, no others:

| Secret name | Value | Where you get it |
| --- | --- | --- |
| `CLINK_FEED_STABLE_SIGNING_KEY` | the stable PRIVATE key (the PEM file as it is; one line of Base64 works too) | step 4: run the `Get-Content -Raw '<pem>' \| gh secret set CLINK_FEED_STABLE_SIGNING_KEY` line the stable summary printed. It goes from the file to GitHub without the clipboard and without being shown |
| `CLINK_FEED_DEV_SIGNING_KEY` | the dev PRIVATE key (same form) | step 4: the same for the dev key (`... \| gh secret set CLINK_FEED_DEV_SIGNING_KEY`) |
| `R2_STABLE_ACCESS_KEY_ID` | Access Key ID | step 2, the `clink-stable-ci` token |
| `R2_STABLE_SECRET_ACCESS_KEY` | Secret Access Key | step 2, the `clink-stable-ci` token |
| `R2_STABLE_ENDPOINT` | `https://<account-id>.r2.cloudflarestorage.com` (no bucket name, no trailing slash; `Check bucket secrets` accepts exactly `\Ahttps://[0-9a-f]{32}\.(eu\.)?r2\.cloudflarestorage\.com\z` and never prints the value) | step 2, the S3 endpoint on the token page |
| `R2_STABLE_BUCKET` | `clink-stable` | the bucket name |
| `R2_DEV_ACCESS_KEY_ID` | Access Key ID | step 2, the `clink-dev-ci` token |
| `R2_DEV_SECRET_ACCESS_KEY` | Secret Access Key | step 2, the `clink-dev-ci` token |
| `R2_DEV_ENDPOINT` | `https://<account-id>.r2.cloudflarestorage.com` | the same endpoint |
| `R2_DEV_BUCKET` | `clink-dev` | the bucket name |
| `CLOUDFLARE_API_TOKEN` | the purge token (optional) | step 3 |
| `CLOUDFLARE_ZONE_ID` | the Zone ID (optional) | step 3 |

That is 2 signing keys, 8 R2 values and 2 purge values. The workflows map them to the generic names the scripts read (`CLINK_FEED_SIGNING_KEY`,
`R2_*`), and only the three steps that need them (`Check bucket secrets`, `Make and sign the manifest`, `Publish to the update feed`) name
them. The **Variables** tab (next to Secrets) normally holds NO variable. The workflows read three, all optional and unset by default:

- `CLINK_FEED_KEY_PREFIX`: unset means `clink` (a staging fork sets `clink-staging`).
- `CLINK_FEED_BUCKET_RESET_VERSION`: an owner-only, temporary, reviewed override for ONE publish (see "Recovering a stuck feed"); remove it
  right after that publish.
- `CLINK_IMMUTABLE_RELEASES`: unset or `required` is the default (immutable releases and asset digests are REQUIRED); set it to `unavailable`
  only after the "Going private" check below says a private Free repo cannot offer them.

None of them may exist at organization level. The workflows read them only through `env:` and validate them: an unknown value of
`CLINK_IMMUTABLE_RELEASES` fails `Read feed configuration`, before the draft exists.
The old environments `feed-stable` and `feed-dev` are not referenced by any workflow, so nothing can read what is stored in them: do not put
anything in them, and delete them (Settings > Environments) when you next pass through, at the latest in the "Going private" list.
Delete any older copy of these names (organization level, or under those environments) and the old `CLINK_FEED_URL` /
`CLINK_FEED_PUBLIC_KEY` variables. Copium's R2 token is re-scoped to Copium's own bucket (no shared bucket or token).

**6. GitHub: repository and Actions settings** (rulesets exist only while the repo is public; the fork options appear once it is private)

- Rulesets, public repo only (Settings > Rules > Rulesets): `stable-tags` (ALL tags except `v*-dev.*`: restrict creation, update and deletion, block force pushes;
  bypass only for the repository admin role, none for GitHub Actions), `dev-tags` (tags `v*-dev.*`: restrict updates, block force pushes, so Actions
  may create and delete them), `main-dev-safety` (the default branch and `dev`: restrict deletions, block force pushes, no bypass, no
  pull-request requirement: solo flow) and `no-v-branches` (no branch may be named `v*`). They stop existing when the repo goes private.
- Settings > General > Releases > enable immutable releases (enabled 2026-10-28). The post-publish check requires it (see
  `CLINK_IMMUTABLE_RELEASES` in "Going private" for the one sanctioned exception).
- Settings > Actions > General > Workflow permissions: read-only default token.
- Fork pull requests, once the repo is private: Settings > Actions > General > Fork pull request workflows: keep
  "Send secrets to workflows from fork pull requests" and "Send write tokens to workflows from fork pull requests" OFF (the options appear
  only on a private repo; on a public repo fork runs never get either). A fork pull request must never be able to read a repo secret or
  write with the token.
- If the repository belongs to an organization: Organization settings > Member privileges > keep "Allow forking of private repositories" OFF.
  A private fork would copy the code (and, with the options above ON, could reach secrets) out of the owner's control.
- Two-factor authentication on the GitHub and Cloudflare accounts (both admins use it).

**Who can reach the secrets.** Without environments, EVERY person with write access to the repository can reach every repo secret: a
workflow on any branch can name them, and any code that runs in the publish job (an action, a script) could read them. The workflows keep
them step-scoped and refuse to publish unless the run is a pushed tag `vX.Y.Z` on `origin/main` (stable) or a push to `dev` (dev), and the
build and pull-request workflows never name a secret, but that is hygiene and a guard, not isolation. The owner accepts this: there are two
admins and both use 2FA. Keep the writer list at exactly those two, watch the Actions tab, and rotate everything on any doubt ("Integrity incident").

### The feed-on change

The feed is now ON for both channels: `Clink/Assets/feed.json` carries the URLs and the public keys shown below. A feed-less publish is
refused by both publish jobs (`Read feed configuration` throws before any draft exists and before any secret is read, so nothing reaches
GitHub or R2), because a feed-less release would give every client an update path that goes nowhere. The shipped file is:

```json
{ "schema": 1, "feedUrl": "https://dl.euphoric.software/clink/", "devFeedUrl": "https://dl-dev.euphoric.software/clink/",
  "stableKeys": ["<stable public key>", "<stable-next public key>"], "devKeys": ["<dev public key>", "<dev-next public key>"] }
```

- Both channels are enabled together: `feedUrl` plus at least one key in BOTH lists (a partly filled file fails CI by design).
- The keys are, in order, `stableKeys` = [stable, stable-next] and `devKeys` = [dev, dev-next].
- The buckets, the custom domains, the Bucket Lock rules, the 12 repo secrets (step 5) and the keys must all exist, or the publish job fails
  by design.
- **It cannot be switched off.** Once a release has carried `stable.json` / `dev.json`, both publish jobs refuse a feed-less publish
  (`Guard - a feed that was on stays on`), and every client that shipped with these URLs and keys trusts them for good: a wrong URL or key
  in a shipped build means a new installer download for every user. The URLs (canonical form, trailing `/`, path exactly `/clink/`) and the
  PUBLIC keys must therefore never change carelessly; a key is only ever replaced through the rotation steps.
- The feed-on change goes to `dev` FIRST (see "Staging plan"); look at the first dev run, and only then carry it to `main`.

### Releasing

- **Dev builds are automatic.** Push to `dev`. `release-dev.yml` builds, runs the unit tests, signs `dev.json` with the dev key, uploads to
  `clink-dev`, checks the public URLs, records a private GitHub prerelease and keeps the 10 newest. Watch it in the repo's Actions tab.
- **Stable**: merge to `main`, tag `vX.Y.Z`, push the tag. Everything else is automatic.
  1. Merge `dev` into `main` (the commit that is tagged must be on `origin/main`).
  2. On `main`: `.\tools\release.ps1 -Version X.Y.Z` bumps `<Version>` in `Clink/Clink.csproj`, commits, creates the annotated tag `vX.Y.Z` and
     pushes the branch and the tag. (Tagging by hand works too if `<Version>` already matches.)
  3. `release.yml` builds, checks the runtime is current, writes `notes.txt`, signs `stable.json`, uploads to `clink-stable`, checks the public URLs, drafts
     and verifies the GitHub release, publishes it and verifies it again. Watch it in the Actions tab; a red run never leaves a half-published feed
     (see "Recovering a stuck feed" and "Stale drafts").
- The build artifact is kept 1 day (`retention-days: 1`), and the separate `cleanup` job deletes it as soon as the run is over (see "Actions
  budget"), also after a failure. **After a failure that reached `Publish to the update feed`, publish a NEWER version** (dev: push again, a new N
  is picked; stable: bump `<Version>` and tag again). Neither re-run button helps there: "Re-run failed jobs" fails at `Download package artifact`
  (the artifact is already deleted), and "Re-run all jobs" rebuilds, which produces different bytes that conflict with the write-once objects
  the first attempt already put in the bucket. (A failure BEFORE that step wrote nothing to the bucket; "Re-run all jobs" is fine then, once
  any draft it left is deleted, see "Stale drafts".)
- Publish approval checklist (nobody approves any more, so look yourself before pushing a tag): the tagged commit is on `main`, the tag matches
  `<Version>`, the diff of `.github/`, `tools/`, `installer/` and `feed.json` since the last release was reviewed, and **Settings > Secrets and
  variables > Actions > Variables** shows what you expect: `CLINK_IMMUTABLE_RELEASES` (unset, unless "Going private" said `unavailable`),
  `CLINK_FEED_KEY_PREFIX` (unset) and `CLINK_FEED_BUCKET_RESET_VERSION` (unset, unless you are doing the one recovery publish it is for). **Stable tags only: read the
  titles of the pull requests since the last release** (`gh pr list --state merged --search "merged:>=<date of the last release>"`). GitHub's
  generated notes are built from them, and `notes.txt` is published on R2, i.e. PUBLIC for good (write-once). The step drops links to this repository
  and `@handle` mentions, but a title can still say something you would not put on the website: rename it (or fix the merged PR title) BEFORE the tag.

### Actions budget

One run (a stable tag or a dev push) is two `windows-latest` jobs, `build` and `publish`, plus a tiny `ubuntu-latest` job, `cleanup`. The build job
uploads the package (`Clink.exe` about 75 MiB self-contained, `Clink-framework.exe` about 32 MiB, `Clink-Setup.exe`, the notices and `package.json`:
**about 150 MB per run**). The GitHub Free plan allows 500 MB of artifact storage in total on a private repo, so only three runs' artifacts fit at
once. Two things keep it small: `retention-days: 1` (the backstop) and the `cleanup` job's one step, `Delete the build artifact`. That job runs after
`publish` whatever its result (`if: always() && needs.build.result == 'success'`), holds `actions: write` and NOTHING else (the publish job has no
`actions:` permission at all, so nothing in it can delete run logs, dispatch or disable workflows), has no checkout, no action, no secret and no
`tools/` code, and runs one inline, non-fatal (`continue-on-error: true`) step: the `artifact_id` output of the build job must be all digits, the
API must report that artifact as named `clink-package` AND made by this run (`workflow_run.id` equal to `GITHUB_RUN_ID`), and only then is it
deleted. Anything else, or any error, is a warning, never a failure. Minutes: Windows minutes count at a higher rate than Linux ones against the included allowance, and the first dev run with the feed
on takes a while (NuGet restores, the installer build, the runtime installer). Look at Settings > Billing > Usage after the first month and tighten
(fewer dev builds, a cheaper runner for the publish job) if it is close.

### Staging plan

A PUBLIC fork would expose the private source, so there is no public staging fork and no staging mode in the workflows. Options:

1. **Recommended, simplest and safe: the first dev push with the feed ON is the staging run for the dev channel**, before any stable feed
   publish. Merge the feed-on commit to `dev` only. The dev run uses the real dev bucket, host and key but never touches `clink-stable`,
   `stable.json` or any stable user; only users who ticked "Dev Build" can see it. Pass criteria: the log shows `no dev floor (first publish)`,
   `public URL ok` for all six objects including `notes.txt`, `Verify the published release` and `Verify the published tag` are green, the bucket copies
   are byte-equal to the release assets, `https://dl-dev.euphoric.software/clink/dev.json` and its `.sig` are served with `max-age=60`, the Notes
   link opens, `notes.txt` is served as `text/plain; charset=utf-8` with `nosniff` (no `nosniff` warning in the log), and a second dev push moves the feed forward (the downgrade floor works). Then merge to `main` and tag. What the dev run cannot prove:
   the stable token and bucket, the stable Bucket Lock and the stable CORS rule. Check those by hand before tagging: `npx wrangler r2 bucket lock list clink-stable`
   shows the `clink/releases/` rule with indefinite retention (read-only; setup step 1.4 explains the optional live probe and what it leaves behind), and the CORS probe in setup step 1.5 passes.
   **Cloudflare bot and WAF rules must not challenge the runners.** `upload-feed.ps1` GETs every object at its PUBLIC URL from a GitHub-hosted runner
   and fails closed on anything but status 200 with the right bytes, so a Bot Fight Mode or WAF challenge (a 403 or a challenge page for a datacenter IP)
   fails the publish after the write-once objects are already in the bucket. Keep Bot Fight Mode (and Super Bot Fight Mode) OFF for the zone, or add a
   WAF skip rule for the hosts `dl.euphoric.software` and `dl-dev.euphoric.software`, and do not put a managed challenge or a rate limit on those hosts.
   The first dev run shows it: a `Public URL check failed ... status 403` (or 503) there means a Cloudflare rule, not the bucket.
   **The stable-only paths run for the FIRST time on `v0.4.2` itself.** The dev workflow never executes the stable notes step (`generate-notes`, the cleaning
   and link filter, the mojibake guard), the `merge-base` check against `origin/main`, the annotated-tag dereferencing, `make_latest`, or the stable release checks. They are
   unit-tested against fakes, but the real run is the tag. Watch it step by step and treat each line below as a pass criterion; any miss is a stop (the draft stays for you to look at, see "Stale drafts"):
   - `Guard - stable tag, commit on main, release not yet there` is green (the `merge-base` check really found the commit on `origin/main`).
   - `Write the release notes (notes.txt)` writes a notes file of plausible size whose text is readable: no `?`/replacement characters, no repository
     link, no `@handle`; open `releases/v0.4.2/notes.txt` on the R2 host afterwards and read it.
   - `Decide whether this release becomes latest` prints which path it took (a release answered, or HTTP 404) and `make_latest=true` for `0.4.2` (there is no higher stable release).
   - `Verify the release assets` is green and logs the exact required asset count; a stable draft is NOT a prerelease.
   - `Re-verify the release before publishing` is green, including the tag check: the log says the tag points at the built commit. If you tagged with `tools/release.ps1`
     the tag is ANNOTATED: the check dereferences it (`Get-GitTagTarget`); a `tag points at ... object` failure means the dereferencing is wrong, not that the tag is.
   - `Verify the published release` is green: immutable (or the `CLINK_IMMUTABLE_RELEASES=unavailable` warning, only if you set it on purpose) and every asset's `digest` is
     present and equal (a `no digest` failure means GitHub does not report digests for this repo: stop and read "Going private").
   - On GitHub the release is the **Latest** one, not a draft, and carries the 7 assets (+ `stable.json`/`.sig`); `https://dl.euphoric.software/clink/stable.json` serves `0.4.2` and the
     app of a dev build with "Dev Build" off sees it.
   - The `cleanup` job's step `Delete the build artifact` ends green or with one warning, and Settings > Actions shows no `clink-package` artifact left.
2. **Optional, heavier: a PRIVATE fork under the owner's personal account** (Free plan; repo secrets work there) with throwaway keys, two
   throwaway buckets and `CLINK_FEED_KEY_PREFIX=clink-staging` (a `feedUrl` whose path is exactly `/clink-staging/`; the publish job refuses a mismatch
   before anything is signed or drafted). Use it only to rehearse the STABLE path (a fork tag) before the real tag. Never copy a real key, token or
   bucket into it, keep it private, and delete it afterwards with its buckets, tokens and keys.

### Going private

Do this when the date is fixed, in this order. Nothing in the workflows needs to change.

- [ ] Before: ship v0.4.2 stable, and let users hop. A v0.4.1 copy has the old updater (it talks to GitHub); a private repo makes its
      update check, and every old GitHub download link, fail with 404. **v0.4.1 users must re-download** (the installer or `Clink.exe`
      from `dl.euphoric.software`) if this happens before they hop to a v0.4.2-or-later build. Point the website at R2 first (that PR comes separately).
- [ ] Settings > General > Danger Zone > Change visibility > private. **Stars and watchers are lost** (they do not come back if it goes public again).
- [ ] Delete the environments `feed-stable` and `feed-dev` if they are still there (Settings > Environments > delete). They are unused by the workflows now and, on a
      private Free repo, their secrets are ignored. Confirm the 12 repo secrets of step 5 exist.
- [ ] Fork settings: Settings > Actions > General > Fork pull request workflows: "Send secrets to workflows from fork pull requests" and "Send write tokens
      to workflows from fork pull requests" stay OFF. If the repo is in an organization: Organization settings > Member privileges: "Allow forking of
      private repositories" stays OFF.
- [ ] Rulesets and branch protection stop applying: any writer can push a `v*` tag. The workflow still refuses anything but a pushed tag
      `vX.Y.Z` whose commit is on `origin/main`, and the post-publish checks still verify the tag. Keep the writer list at two.
- [ ] Immutable releases, FIRST thing after the visibility change and BEFORE the next push or tag: check that Settings > General > Releases still offers
      "immutable releases" on a private Free repo, and that it is still switched on. `Verify the published release` FAILS (and DELETES the release, dev
      prereleases too) when a published release is not immutable, so on a repo that cannot offer it every publish would delete itself.
      - Still offered and on: change nothing (the repo variable `CLINK_IMMUTABLE_RELEASES` stays unset).
      - Not offered: set the repository variable **`CLINK_IMMUTABLE_RELEASES` = `unavailable`** (Settings > Secrets and variables > Actions > Variables) BEFORE the
        next push. Then only two assertions are skipped, each with a `::warning::` in the log: "the published release is immutable" and "every asset reports a
        digest" (it is not confirmed that a private Free repo reports digests; a digest that IS reported must still equal the local file, and sizes, names,
        author, draft state and the tag checks are all still enforced). Without immutability a writer could edit assets or move the tag of a published release
        after the checks: that is the price, keep the writer list at two and read the warnings.
      - The variable accepts exactly `required` (the default, same as unset) or `unavailable`; anything else fails the first release check.
      - One variable covers both skipped assertions; there is no way to relax only one. After the first publish with `unavailable`, look at the log: if it shows only
        the "not immutable" warning and no "no digest" warning, digests do work and only immutability is missing (the variable stays, for immutability).
      - Re-check Settings > General > Releases now and then; when immutable releases become available again, switch them on and DELETE the variable.
- [ ] Retention and quota: artifacts are kept 1 day (`retention-days: 1`) and deleted by the `cleanup` job at the end of the run (about 150 MB per run, "Actions budget");
      Actions has $12 a month included and 500 MB of artifact storage.
      Look at Settings > Billing > Usage after the first month (a stable or dev run uses two `windows-latest` jobs).
- [ ] After: push to `dev` once and watch it go green; check that `gh api repos/<repo>/releases` (the history floor) still lists the releases.

## Reference

The rest of this file is the spec (contract v2): what a release contains, the trust anchors, the workflows, the keys and the runbooks.

## What a release contains

| Download | What it is |
| --- | --- |
| `Clink.exe` | The portable download: the SELF-CONTAINED single-file exe (compressed, about 75 MiB, needs no runtime). v0.4.1 and older updaters read exactly this name, so their bare-exe copies become portable self-contained copies on update. |
| `Clink.exe.sha256` | `<hash> *Clink.exe`. |
| `Clink-framework.exe` | The FRAMEWORK-DEPENDENT single-file exe (about 32 MiB, needs the .NET 10 Desktop Runtime). This is the exe the installer installs and what installed copies update to. |
| `Clink-framework.exe.sha256` | `<hash> *Clink-framework.exe`. |
| `Clink-Setup.exe` | Inno Setup installer (fixed name; the website links to the R2 copy under `feedUrl`, never to GitHub; per user, no admin, fixed location, upgrades in place). It carries an optional task that installs the Microsoft .NET 10 Desktop Runtime when it is missing. |
| `Clink-Setup.exe.sha256` | `<hash> *Clink-Setup.exe` (what the website's "SHA-256 checksum" refers to for the installer). Attached to the GitHub release; the publish job's `Verify the release assets` step fails the run (before the feed upload and before the draft is published) unless the release carries all of these names: `Clink.exe`, `Clink.exe.sha256`, `Clink-framework.exe`, `Clink-framework.exe.sha256`, `Clink-Setup.exe`, `Clink-Setup.exe.sha256`, `THIRD-PARTY-NOTICES.txt`, so the private archive is always complete. The step reads the draft by its release ID (`gh api repos/.../releases/<id>`, the ID of the draft the previous step created, passed only through `env`), requires it to still be a draft with tag `vX.Y.Z`, and for each of the seven names requires exactly one asset of that exact (case-sensitive) name, in state `uploaded`, with the size of the local file and `sha256:` plus the local file's hash as GitHub's `digest` (a missing digest fails the step), and the release must carry no other asset. |
| `THIRD-PARTY-NOTICES.txt` | The third-party licence notices (the same bytes the installer installs next to `Clink.exe` and the app shows in About). Attached to the GitHub release only; it is not part of the feed bucket or the manifest. |
| `stable.json`, `stable.json.sig` (`dev.json`, ...) | Signed update manifest, only when the feed is enabled for that channel. |
| `notes.txt` | The release notes as plain UTF-8 text, on R2 ONLY (next to the binaries; see "Release notes"). It is NOT a GitHub release asset: the asset set above is unchanged. |

There is no portable zip and no `Clink.portable` marker.

## Contract v2

### Trust anchors: `Clink/Assets/feed.json`

Committed and embedded in `Clink.exe`; the only place the app learns where the feed is and whom to trust:

```json
{ "schema": 1, "feedUrl": "https://dl.example/clink/", "devFeedUrl": "https://dl-dev.example/clink/",
  "stableKeys": ["<Base64 DER SPKI, ECDSA P-256>"], "devKeys": ["<another Base64 DER SPKI, ECDSA P-256>"] }
```

- Both channels are enabled together. The file is in exactly one of three states:
  - **off**: `feedUrl` is `""`, `devFeedUrl` is `""` or absent, and `stableKeys` and `devKeys` are both empty. The build jobs still run, but
    both publish jobs REFUSE to publish (`Read feed configuration` throws: no draft, no GitHub release, no upload).
  - **on**: `feedUrl` is valid AND `stableKeys` and `devKeys` are BOTH non-empty, every key is a valid ECDSA P-256 SPKI, and the
    two lists share no key. `feedUrl` must be https with a host, no user info (`@`), no query (`?`), no fragment (`#`), and end
    with `/`. `devFeedUrl` is optional; when it is not `""` it must follow exactly the same rules.
  - anything else that names a URL or a key (one channel only, keys without a URL, a `devFeedUrl` without a `feedUrl`, overlapping
    or malformed keys, a bad URL or `devFeedUrl`) is **misconfigured**: the client applies no updates from the feed, and CI
    (`Get-FeedConfig` in `tools/feed-lib.ps1`) fails the build.
- `Get-FeedConfig` follows the client's rules (`FeedTrust.Create` / `Parse` / `ValidFeedUrl` in `Clink/Services/UpdateFeed.cs`) and is
  checked against them with shared fixtures (`tools/tests/feed-trust-cases.json`, run through `Get-FeedConfig` by
  `tools/tests/feed-config.tests.ps1` and through `FeedTrust.Parse` by the C# unit tests). The exact rules:
  - `feedUrl` counts as listed unless it is empty or only white space (then it is empty). `devFeedUrl` counts as listed unless it is
    `""`, `null` or absent, so a white-space-only `devFeedUrl` is listed and invalid (empty): **misconfigured**, never "use feedUrl".
  - A URL is trimmed, then must be an absolute `https` URL with a host, no `?`, `#`, query, fragment or user info, and both the text
    and its path end in `/`. A `devFeedUrl` without a `feedUrl` is misconfigured. Two different bases where one starts with the
    other (compared normalised and ordinally) are misconfigured (nested): a binary under the inner base would pass the outer
    channel's "under my base" check.
  - **Canonical URLs only (stricter than the client, on purpose).** The client turns a URL into `Uri.AbsoluteUri` (lower-case host
    and scheme, default port dropped, dot segments removed, escaped plain characters decoded). CI accepts a URL only when the
    trimmed text already equals that form and holds no escaped plain character, so the base that CI reads, the manifest URLs it
    signs and the bases the client resolves can never silently differ. `https://DL.example/clink/` or `https://dl.example:443/clink/`
    fail the build with the canonical spelling in the message.
  - A property whose name matches a known key (`schema`, `feedUrl`, `devFeedUrl`, `stableKeys`, `devKeys`) case-insensitively but not
    exactly (`FeedUrl`) is refused (PowerShell would read it, the client ignores it). `feedUrl` and `devFeedUrl` must be strings or
    `null`; the key lists must be lists of strings; `schema` must be the integer `1` (not `"1"`, `1.0` or `true`).
  - Resolved bases: stable = `feedUrl`; dev = `devFeedUrl`, or `feedUrl` when `devFeedUrl` is empty.
- `devFeedUrl` (optional string, schema stays 1) is the base of the dev channel; absent or `""` means the dev channel uses `feedUrl`.
  Where the files live: `stable.json`, its `.sig` and the stable binaries under `feedUrl`; `dev.json`, its `.sig` and the dev binaries
  under the dev base (`devFeedUrl`, or `feedUrl` when that is empty). `Get-FeedConfig -Channel <channel>` returns that base as
  `FeedUrl`, and `make-manifest.ps1` (binary URLs must sit under it), `upload-feed.ps1` (public checks and cache purge) and the
  workflows' `feed_url` all use it. A dev client reads BOTH `stable.json` (from `feedUrl`) and `dev.json` (from the dev base).
- A feed manifest the client refuses (bad signature, wrong channel, downgrade) is ignored: there is no GitHub fallback (the app never
  contacts GitHub), and a compromised bucket cannot push an update that the embedded keys do not sign.
- Several keys per channel are allowed (rotation). A manifest is valid if its signature verifies under any listed key.
- The build job runs the built exe's test `Embedded_feed_trust_matches_ci_expectation` with `CLINK_EXPECT_FEED=on|off` taken from
  `Get-FeedConfig`, so the exe that ships embeds the feed state CI believes it does. `tools/check-published.ps1` also takes `-ExpectFeedUrl`
  and `-ExpectDevFeedUrl` and sets `CLINK_EXPECT_FEED_URL` (the normalised stable base) and `CLINK_EXPECT_DEV_FEED_URL` (the normalised dev
  base: `devFeedUrl`, or `feedUrl` when that is empty) for that test run: with `-ExpectFeed on` BOTH are required (the C# side requires both when
  on), with `-ExpectFeed off` or no `-ExpectFeed` they must not be given and are left unset (`Get-FeedSelfTestEnvironment` in `tools/feed-lib.ps1`).
  `build.yml`, `release.yml` and `release-dev.yml` pass them from `Get-FeedConfig -Channel stable` / `-Channel dev` when the mode is on. The dev
  workflow's `Unit tests` step sets the same two variables for the bin build. The test compares them ordinally with the bases `FeedTrust`
  resolved from the embedded file, and only when they are set (an empty value is the same as unset, so with the feed off only the state is compared).
- There are no `CLINK_FEED_URL` / `CLINK_FEED_PUBLIC_KEY` variables and no `-p:UpdateFeed*` build properties.
- Enabling the feed is a reviewed commit to this file (see below). Nothing in CI settings can change what the app trusts.

### Manifest (`stable.json` / `dev.json`)

```json
{ "schema": 1, "channel": "stable", "version": "0.4.2", "tag": "v0.4.2", "published": "2026-10-26T12:00:00.0000000Z",
  "notesUrl": "https://dl.example/clink/releases/v0.4.2/notes.txt", "minRuntime": "10.0.0",
  "assets": { "exe":          { "url": ".../releases/v0.4.2/Clink.exe",              "size": 1, "sha256": "<64 hex>" },
              "exeFramework": { "url": ".../releases/v0.4.2/Clink-framework.exe",    "size": 1, "sha256": "<64 hex>" },
              "installer":    { "url": ".../releases/v0.4.2/Clink-Setup.exe",   "size": 1, "sha256": "<64 hex>" } } }
```

- `exe` is the self-contained exe (what portable copies take); `exeFramework` is what framework (installed) copies take, unless
  the manifest lacks it or `minRuntime` is newer than every installed Microsoft.WindowsDesktop.App runtime (then they fall
  back to `exe`). A client rejects the WHOLE manifest when `exeFramework` is present but invalid (https url, positive size,
  64 lowercase hex sha256), so it is always written from `package.json`, never by hand.
- `notesUrl` is `<channel base>releases/<tag>/notes.txt` (stable) or `<dev base>dev/<tag>/notes.txt` (dev). `make-manifest.ps1` sets it itself (it
  is not a parameter) and validates it with `Test-FeedAssetUrlUnder` like the binaries. Contract with the client: it opens `notesUrl` in the
  browser only when it is https and under the manifest's base.
- `minRuntime` is `X.Y.Z` (clients parse it as a version). `tools/package.ps1` reads it from the Microsoft.WindowsDesktop.App
  version in the framework build's `Clink.runtimeconfig.json` (today `10.0.0`) and records it in `package.json`;
  `tools/make-manifest.ps1` copies it.

Signature file: one line of Base64, ECDSA P-256 / SHA-256 over the exact manifest bytes (UTF-8, no BOM), IEEE P1363
`r||s`, 64 bytes. Separate keys per channel: the stable key signs `stable.json`, the dev key signs `dev.json`.

### Two buckets (B2) and the object layout

Each channel has its own bucket on its own host, so a leaked dev token cannot touch the stable objects:

| Channel | Bucket | Host | feed.json | Repo secrets |
| --- | --- | --- | --- | --- |
| stable | `clink-stable` | `dl.euphoric.software` | `feedUrl` = `https://dl.euphoric.software/clink/` | `CLINK_FEED_STABLE_SIGNING_KEY`, `R2_STABLE_*` |
| dev | `clink-dev` | `dl-dev.euphoric.software` | `devFeedUrl` = `https://dl-dev.euphoric.software/clink/` | `CLINK_FEED_DEV_SIGNING_KEY`, `R2_DEV_*` |

The hosts are one label deep, so the Universal SSL certificate covers them; the zone must be in the same Cloudflare account as the
buckets (custom domains need that). Each host is a custom domain of its bucket; `r2.dev` is off on both. (The old GitHub environments
`feed-stable` / `feed-dev` are gone: see "Going private" and "Who can reach the secrets".)

Inside a bucket, under `<prefix>` (validated `^clink(-staging)?$`, normally `clink`):

```
clink-stable:  <prefix>/releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-framework.exe, Clink-framework.exe.sha256,
                                          Clink-Setup.exe, notes.txt}
               <prefix>/stable.json, stable.json.sig
clink-dev:     <prefix>/dev/vX.Y.Z-dev.N/{same}                 (10 newest kept)
               <prefix>/dev.json, dev.json.sig
```

No `latest/` aliases. Versioned objects are write-once (immutable, `Cache-Control: public, max-age=31536000, immutable`,
`sha256` object metadata). Manifests and signatures use `Cache-Control: public, max-age=60`.

`upload-feed.ps1` writes the key `<prefix>/<folder>/<file>` but checks the public URL `<base><folder>/<file>`, so the PATH of the
channel's base URL must be EXACTLY `/<prefix>/` (the host is mapped to the bucket root; `/some/clink/` would need the keys
`some/clink/...` and is refused). The check (`Assert-FeedBaseMatchesPrefix`, an exact case-sensitive comparison, with the prefix from
`Get-FeedKeyPrefix`) runs twice: in the publish job's `Read feed configuration` step (with `CLINK_FEED_KEY_PREFIX` from the
repository variable), so a mismatch fails before anything is signed and before the draft release exists, and again first thing in
`upload-feed.ps1`, before any bucket access. Both fail with "Nothing was signed or uploaded by this step". A staging run uses
`CLINK_FEED_KEY_PREFIX=clink-staging` and a base path of exactly `/clink-staging/`.

### Release notes (`notes.txt`)

Plain UTF-8 text, no BOM, at most 64 KB, no control character except LF and TAB (CR is removed), one final LF (`tools/lib/notes-lib.ps1`:
`ConvertTo-FeedNotesText`, `Write-FeedNotesFile`, `Assert-FeedNotesFile`). The step `Write the release notes (notes.txt)` writes
`dist/package/notes.txt` in both publish jobs:

- Stable: ALWAYS GitHub's generated release notes for the tag (`gh api -X POST repos/<repo>/releases/generate-notes`, which only returns text
  and creates nothing), in this order: generated, cleaned (`ConvertTo-FeedNotesText`: control, zero-width and bidi characters out first, so none can hide a link from
  the filter), the links to this private repository dropped (`Remove-FeedNotesRepoLinks`: the `in <url>` tails and the "Full Changelog" line, then
  a final pass over the whole text for ANY remaining link of the repository, in every spelling: `https://`, `http://` or no scheme, `www.`,
  `github.com/<owner>/<repo>` with or without `.git`, any path, a sentence-final `.` kept outside the link; every `@handle` mention and the "New
  Contributors" lines; the spaces a removal leaves are tidied), then the guard: the step sets the console to UTF-8 before `gh` runs and FAILS when
  the text holds U+FFFD or the "a-circumflex, euro" mojibake sequence (a damaged text must not become write-once). The same file is the release
  body (`body_path`), so the archive and R2 say the same. **Nothing already published is ever read back** (no GET of the public `notes.txt`, no
  bucket read in this step): anyone with bucket write but no signing key could plant `releases/<next tag>/notes.txt` ahead of time, and reusing it
  would have signed a `notesUrl` pointing at their text and made it the release body. If a `notes.txt` already exists for the tag,
  `upload-feed.ps1`'s write-once preflight decides: the same bytes are skipped, different bytes FAIL the run before anything is written.
- Dev: the fixed dev body (`Get-FeedDevNotesText`, equal to `Get-ExpectedDevReleaseBody`, which the verify steps compare the release body with).

`make-manifest.ps1` refuses a package without a valid `notes.txt`. `upload-feed.ps1` uploads it write-once as one more versioned asset
(`<prefix>/releases/<tag>/notes.txt`, `<prefix>/dev/<tag>/notes.txt`; conditional create, same-bytes skip, a different file under the same
tag fails; covered by the same Bucket Lock prefix) and GETs it at its public URL: status 200 and the same SHA-256 and size, like the binaries.
It requires the manifest's `notesUrl` to be exactly that public URL. `notes.txt` is not added to the GitHub release assets (the asset set and
the verify steps are unchanged). If the upload fails with "already exists with different content" for `notes.txt` (a planted file, or notes left
by an earlier attempt of the same version whose generated text differed), **do NOT hand-publish the draft** (the bucket and the draft disagree
about what was released): publish a NEWER version, as for every failed run that reached the feed upload. Served headers: `notes.txt` must come
with `Content-Type: text/plain; charset=utf-8` (the publish fails otherwise) and `X-Content-Type-Options: nosniff` (a warning until the Transform Rule of setup
step 1.7 exists).

### GitHub releases (the private archive)

Every GitHub release carries `<channel>.json` and `<channel>.json.sig` (byte-identical to the bucket copies) when the feed is
enabled for that channel. The releases are the archive, the audit trail and the history floor (below); no client reads them.

## Workflow shape

Both workflows have three jobs: `build`, `publish` and `cleanup` (the last one only deletes the build artifact; see "Actions budget").

CI note: the runner appends `if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }` to every `shell: pwsh` step, so a step that handles a non-zero native exit itself (for example `gh release view` returning 1 for "not found", or a 404 from `gh api`) must set `$global:LASTEXITCODE = 0` right after capturing the code, or the job fails anyway.

1. `build` (`contents: read`, no secrets, no environment): checkout, `Fresh NuGet folders` (see "NuGet trust policy"), `actions/setup-dotnet` followed by a step that verifies the SDK
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
   Right after `Publish both flavours` the build job runs `Assert-NuGetRestoreTrusted` over every `project.assets.json` it produced.
   **Embedded floor seeds (M1).** The step `Find the embedded floor seeds` (build job, the read token, before the build) lists the releases (`gh api ... --paginate`,
   GET only; tag, draft flag, asset names and author login per release) and reuses `Get-FeedHistoryFloor` to find the newest PUBLISHED stable and the newest
   PUBLISHED dev release that carried a manifest. **Only releases whose `author.login` is exactly `github-actions[bot]` count (L-4)**: a release a person
   created by hand is no evidence of what the feed shipped and can neither raise nor lower a seed or the history floor. Each value must match
   `\A[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?\z` (a stable seed also must not be a dev version) or the step fails: that is what keeps a `;` out of MSBuild.
   They reach `tools/publish-flavours.ps1` as `-MinStable` / `-MinDev` (it validates again) and become `-p:ClinkMinStable=` / `-p:ClinkMinDev=`; the dev workflow passes the
   same properties to the unit-tested `dotnet build`, so the tested binary is the shipped one. Empty means no seed (the first release, `build.yml`'s dry run, a local build).
   The seeds are also build-job outputs (`min_stable`, `min_dev`): the publish job's `Find the GitHub history floor` REFUSES when the floor it computes for its
   channel differs from the seed the build job embedded for that channel (`Assert-FeedFloorMatchesSeed`), so a release created, published or deleted
   between the two steps cannot ship binaries with a wrong seed. That refusal comes before anything is signed or drafted: "Re-run all jobs" once no other
   release of the channel is in progress. Push stable tags one at a time and wait for the previous run to finish: a waiting publish can be cancelled by a newer
   one, and a seed/floor mismatch needs "Re-run all jobs".
   A build's own-channel floor seed must be strictly lower than the version being built (`Assert-FeedSeedBelowVersion`); an equal seed means the version is already used.
   Only each channel's own seed is checked against its floor, deliberately: checking the other channel's seed would make stable runs fail on every dev push.
   The cross-channel seed in a binary can be slightly stale, which only weakens that floor.
   **The client honours the seeds only once slice S6 lands:** S6 defines the optional MSBuild properties `ClinkMinStable` and `ClinkMinDev`, which become
   `AssemblyMetadata` that the updater reads; then it uses the higher of its settings floor and the embedded seed. Until S6 is merged, the properties are
   passed but nothing in the client reads them. `check-published.ps1` does not assert the embedded values yet (the exe's self-test cannot
   expose them without a C# change): a listed follow-up. Also a follow-up: the seeds and the history floor trust the release LISTING (tag, author, asset
   names); downloading each counted release's `<channel>.json` and verifying its signature against `feed.json` before it counts is not done yet.
   Before the package step the build job also runs `make-notices.ps1 -Check -AssetsFile Clink/obj_sc/project.assets.json`, resolves the
   runtime installer and checks runtime freshness (next sections). The dev workflow also builds
   and runs the unit tests first (on the bin build: 5 tests fail by design when `--unittest` runs from a published exe).
   `build.yml` has a no-secrets "Package dry run" job that does the same without the installer.
2. `publish` (`needs: build`, NO environment, `contents: write` ONLY, no `actions:` permission (the artifact is deleted by job 3), fixed concurrency group
   `feed-stable-publish` / `feed-dev-publish` with `cancel-in-progress: false`). Strong guards without environments, in two layers, both
   before any secret is used. Layer 1, a job-level `if:` evaluated before any step runs: stable = `github.event_name == 'push'` and a ref
   starting `refs/tags/v`; dev = `github.ref == 'refs/heads/dev'` and a push or `workflow_dispatch`. Layer 2, the first steps: the stable
   job accepts only a push event and `\Arefs/tags/v[0-9]+\.[0-9]+\.[0-9]+\z` (case-sensitive, anchored, ASCII digits) and checks
   `git merge-base --is-ancestor $GITHUB_SHA origin/main` (full-history checkout; `main` is written out in the workflow, it is not read
   from the repository's default-branch setting); the dev job accepts only a push or dispatch event on `refs/heads/dev`;
   both refuse when a GitHub release `vX.Y.Z` already exists (draft or published; see "Stale drafts"), and the dev job also refuses
   when the git tag `vX.Y.Z-dev.N` already exists (`gh api .../git/ref/tags/v...` must answer HTTP 404; any other answer fails), so the
   release is created together with its tag at the built commit. `Read feed configuration` and the three release checks read `CLINK_IMMUTABLE_RELEASES`
   (`vars`, through `env:`; unset or `required` = strict; exactly `unavailable` skips only the "immutable" and "reports a digest" assertions, each with a
   `::warning::`; any other value already fails `Read feed configuration` (`Get-ImmutableReleasesMode`), before anything is signed, drafted or uploaded,
   and the three checks validate it again; see "Going private"). Then: download the artifact, verify
   `package.json`, `Clink.exe`, `Clink-framework.exe`, the setup exe and `THIRD-PARTY-NOTICES.txt` against the build job's outputs (including the single `Clink-Setup.exe.sha256` line check), read `Clink/Assets/feed.json` from the checkout.
   The GitHub release is created as a DRAFT with all assets (`softprops/action-gh-release`), the "Verify the release assets" step checks it
   by its release ID (`tools/lib/release-verify-lib.ps1`, `Assert-ReleaseById`): exactly one release of the repository (drafts included)
   carries the tag `vX.Y.Z` and it is the ID the draft step reported; it was created by `github-actions[bot]`, has the expected name
   (`Clink vX.Y.Z`, dev: `Clink vX.Y.Z-dev.N (dev)`) and, for dev, the expected body; every required asset exists exactly once, uploaded,
   with the size and SHA-256 digest of the local file (a missing digest FAILS the run; any asset that is not on the required list FAILS
   the run; the channel manifest `stable.json`/`dev.json` and its `.sig` when the build produced them); `prerelease` is false for stable
   and true for dev; for dev the draft's `target_commitish` must equal `GITHUB_SHA` and the tag `vX.Y.Z-dev.N` must still not exist (HTTP 404:
   a draft creates no tag). Then `upload-feed.ps1` runs (if the feed is on). **Adoption lock (L-1):** softprops v3 REUSES an existing draft
   of the same tag, so the guard `release not yet there` (it runs before the draft step) is the protection against adopting someone
   else's release, and the by-ID check with the tag-uniqueness rule is the second lock; keep both. (Replacing softprops with
   `gh api` calls that create the draft and upload by ID is a listed follow-up.)
   **Check again before publishing and after (M-1, TOCTOU):** the first check is minutes old by the time the feed upload finished, so the step
   `Re-verify the release before publishing` repeats it right before the PATCH (stable: also that the owner's tag still points at
   `GITHUB_SHA`, annotated tags dereferenced; dev: that the tag still does not exist). Only then the draft is published by release ID
   (`gh api -X PATCH repos/.../releases/<id> -F draft=false -f make_latest=...`). A failed bucket upload therefore never leaves a visible
   release. The step `Verify the published release` then re-reads the release by ID: not a draft, `immutable` true, the same name, author,
   assets, sizes and digests, still the only release of its tag; and the tag is read again (3 attempts, 2 s apart: a new tag can take a
   moment) and must point at `GITHUB_SHA` (stable: the owner's tag, annotated or not, dereferenced to the commit; dev: a plain tag, type
   `commit`). If the second or third check fails, the run DELETES the release by ID (`Invoke-ReleaseCheckOrDelete`) and fails loudly:
   see "Integrity incident". (`gh release verify-asset` can additionally check an asset against the immutable release attestation; the
   workflows do not run it today.)
   The write token (`contents: write`) is not put in the job-wide environment: every step that calls `gh` (directly, or through
   `release-verify-lib.ps1`) names `GH_TOKEN: ${{ github.token }}` in its own `env`, and the steps that hold secrets or call aws
   (`Make and sign the manifest`, `Check bucket secrets`, `Publish to the update feed`) do not get it in their environment. That is
   hygiene, not isolation: the job's token exists for the whole job and any code that runs in it (an action, a script) can obtain it.
   Job split (a draft job, a feed job with the secrets, a publish job) is the follow-up that would isolate it. The repo secrets are the
   same: only the three steps named in the owner guide read them, `build.yml` (pull requests) and the build jobs never name one (a test enforces
   it), but every repo writer can reach them (see "Who can reach the secrets").
   The "latest" flag is set explicitly and is computed BEFORE the feed upload (step `Decide whether this release becomes latest`, `id: latest`, so a
   failure stops the job while nothing is public): the stable job (`tools/lib/release-latest-lib.ps1`, `Get-MakeLatestValue`) sets `make_latest=true` only when
   the version is higher than the current latest stable release (`releases/latest`; only `HTTP 404` means none yet, which counts as higher; any other lookup
   failure or an unparsable tag fails the run; stderr is never parsed as the tag; the step logs which path it took, a release
   answered (200) or HTTP 404, and the dev version step logs the same), so
   re-cutting or backporting an older version never becomes the repository's latest release (which the dev version step reads). The value reaches the publish step only through
   `env: MAKE_LATEST`, which re-validates it as exactly `true` or `false`. The dev job always passes `make_latest=false`.
   - Feed enabled for the channel: require the repo secrets (fail if missing), write `notes.txt` (see "Release notes"), make and sign the manifest
     (`tools/make-manifest.ps1`; the key is only in that step's environment; the signature is verified against the keys in
     `feed.json` before it is written; a mismatch fails the run), create the GitHub release with all assets including the
     manifest and signature, then run `tools/upload-feed.ps1`. Before the draft is created, the step `Find the GitHub history floor` lists
     the earlier releases (`gh api repos/<repo>/releases --paginate`, GET only) and computes the highest NON-DRAFT release of the channel,
     made by `github-actions[bot]`, that carried `<channel>.json`; it must equal the build job's seed for the channel (see "Embedded floor
     seeds" above) or the step fails; `upload-feed.ps1` receives it as `-HistoryFloor`.
   - Feed not enabled (the shipped `feed.json` is on, so this only happens if the file is broken): the step `Read feed configuration` THROWS
     (refusing to publish) before the draft and before any secret step; nothing is published to GitHub or R2 and the build job's result is
     untouched. The step `Guard - a feed that was on stays on` (it lists the assets of every earlier release, GET only, and FAILS when one
     carried `<channel>.json`, see "The feed cannot be switched off") is kept as defence in depth: it is skipped while the feed is on, and
     enforces the rule if the refusal is ever relaxed.
   - `make-manifest.ps1` binds `package.json` to the manifest: `files.exe.file` must be exactly `Clink.exe`, `files.exeFramework.file`
     `Clink-framework.exe` and `files.installer.file` `Clink-Setup.exe` (case-sensitive), the `exe` and `exeFramework` hashes must differ,
     and `minRuntime` must be `10.x.y` (the app targets `net10.0-windows`; raise the constant in `make-manifest.ps1` with the `TargetFramework`).
     Each file is then re-hashed against `package.json`, and the publish job has already compared `package.json` itself with the build job's `pkg_sha256`.
     Every binary URL (`BaseUrl` plus the fixed file name) must also pass `Test-FeedAssetUrlUnder`, the port of the client's `IsUnder`: strictly
     inside the channel's base, same host and port, https, no `//` (empty segment), no %-escape of a plain character, no dot segment, no backslash (also
     `%5C`), no query, fragment or user info; so `make-manifest.ps1` never signs a URL that every client refuses (it is a little stricter: a literal
     `.`/`..` segment or backslash that `Uri` would normalise is refused as well).
   - `upload-feed.ps1` (entry point, `#requires -Version 7`; the decisions are in `tools/lib/feed-publish-lib.ps1` and are tested with a fake aws in
     `tools/tests/feed.tests.ps1`): validates the prefix; takes the downgrade floor from the bucket's current `<channel>.json` ONLY after
     downloading `<channel>.json.sig` and verifying it against the channel's keys in `feed.json` (the keys the app trusts; a missing or
     invalid signature, an unpaired file or a manifest for another channel fails the run; no manifest and no signature at all is the first
     publish, which is logged and allowed); the effective floor is the HIGHER of that verified bucket floor and the GitHub history floor (a
     bucket that was emptied or rolled back cannot lower it); with NEITHER bucket file but a history floor the run is REFUSED unless the
     owner has set `CLINK_FEED_BUCKET_RESET_VERSION` to exactly that release's version (see "Recovering a stuck feed"); refuses when the
     bucket floor is higher than this version or this version is not newer than the history floor; a bucket manifest or `.sig` larger than
     1 MB is refused before it is read; then a PREFLIGHT over EVERY versioned object (the binaries, the `.sha256` files, `notes.txt`;
     `Publish-FeedVersionedAssets`, L-5) DOWNLOADS each existing object and compares its real SHA-256 and size (never `x-amz-meta-sha256`, never
     `Content-Length`): same bytes = skip, different = FAIL, and that failure comes before ANY object is written (a conflict on the last asset
     leaves the bucket untouched); only then are the missing ones uploaded with a conditional create (`aws s3api put-object ... --if-none-match "*"`). An aws cli that lacks
     the option FAILS the run (the version is logged; there is no fallback to a plain copy). R2 Bucket Lock on `releases/` remains the real
     write-once guarantee. Then GETs every asset's PUBLIC URL (the channel's base + path, no redirects) and requires status 200 and the same SHA-256
     and size of the served bytes; then uploads the manifest, then the `.sig`; optionally purges the two manifest URLs (under the channel's
     base) from Cloudflare (non-fatal). Dev only: prunes old `<prefix>/dev/vX.Y.Z-dev.N/` folders (strict regex, N as Int64; this is housekeeping after dev.json is live, so every
     error in it, a listing or parse error included, is only a warning). The step has `timeout-minutes: 30` and each public-URL request a 600 s timeout.
     Before anything is written it checks that the path of the channel's base URL is exactly `/<prefix>/` (see "Two buckets").
   - Dev only: old GitHub releases are pruned in a step with `if: success()` (a failed run never prunes). It lists every release
     (`--paginate`), keeps the 10 newest PUBLISHED dev PRERELEASES (strict `\Av...-dev.N\z` tag match, and `prerelease` must be true: a
     published release that is not flagged as a prerelease is never deleted, whatever its tag looks like; drafts left by failed runs are neither
     counted nor deleted), deletes the older ones with `gh release delete` (no `--cleanup-tag`) and then deletes the tag ref with
     `gh api -X DELETE`. With immutable releases the tag of a published release cannot be removed: that is logged as a warning and the
     loop continues; a release that cannot be deleted is counted and fails the step at the end (`continue-on-error: true`, so only an
     annotation). Watch the first prune of an immutable dev release (the oldest of 11 being `v0.4.2-dev.11`).
   - The old `Release` workflow on `origin/main` still has `workflow_dispatch` with `contents: write` and no environment, until
     `v0.4.2` is on `main`: nobody presses "Run workflow" on it before then.
3. `cleanup` (`needs: [build, publish]`, `if: always() && needs.build.result == 'success'`, `runs-on: ubuntu-latest`, `permissions: actions: write` and
   nothing else): one inline pwsh step, `Delete the build artifact`, with `continue-on-error: true`; no checkout, no `uses:`, no secret, no `tools/` code.
   It checks that the build job's `artifact_id` is all digits, GETs `repos/<repo>/actions/artifacts/<id>`, requires `name` = `clink-package` and
   `workflow_run.id` = `GITHUB_RUN_ID`, and only then DELETEs it; anything else is a warning (see "Actions budget").

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
publish jobs therefore refuse a feed-less publish: always (`Read feed configuration` throws if `feed.json` is ever off), and, as a second lock that
stays even if that refusal is relaxed, when any earlier release of the repository (drafts included) has an asset named
`stable.json` (stable) or `dev.json` (dev). To recover, restore `feedUrl` and the keys in `Clink/Assets/feed.json`. Deleting old releases to
get around the check is not a supported way to turn the feed off.

## Keys

The owner guide has the steps ("One-time setup" 4 and 5, "The feed-on change"). The reference points:

- One ECDSA P-256 pair per channel (`tools/new-feed-key.ps1 -Channel stable|dev`), plus a "next" pair per channel (`-Next`), generated on a
  trusted machine and written to a folder outside every git work tree (the script refuses otherwise; `*.pem` is also git-ignored). The script
  works on Windows PowerShell 5.1 and PowerShell 7, prints the PUBLIC key (Base64 SPKI) and a short owner summary, and never prints private key
  material: the private key goes only into the `.pem` file.
- The PUBLIC keys go into `stableKeys` / `devKeys` in `Clink/Assets/feed.json`, the active and the next one of each channel. The active
  PRIVATE keys go into the repo secrets `CLINK_FEED_STABLE_SIGNING_KEY` and `CLINK_FEED_DEV_SIGNING_KEY`, straight from the file, without the clipboard:
  `Get-Content -Raw 'E:\keys\clink-stable.pem' | gh secret set CLINK_FEED_STABLE_SIGNING_KEY` (`new-feed-key.ps1` prints the exact line). A clipboard, and
  Windows' clipboard history, would keep the key; if you ever did copy one, clear the clipboard and its history (Win+V > Clear all) afterwards.
  `tools/make-manifest.ps1` accepts either the plain PEM or the same PEM as one line of Base64. The next keys go into NO secret.
- Back up the `.pem` files to the USB drive (a second copy off-site is better), then delete the working copies. Until then, on an NTFS drive, restrict the
  file's ACL to your account (`new-feed-key.ps1` prints the `icacls` command only when the drive is NTFS or ReFS). A USB stick that is exFAT or FAT32 has no ACLs at
  all (`icacls` cannot protect a file there): keep such a drive with you, and delete the file from any other place as soon as it is backed up.
- Signing is automatic in CI for both channels with the repo-secret keys; there is no offline signing.

### Enabling the feed

`Clink/Assets/feed.json` now sets `feedUrl`, `devFeedUrl` and the keys of BOTH channels (see the owner guide, "The feed-on change"). Every
build embeds these anchors and the publish jobs sign and upload. The buckets, the repo secrets and the keys must exist, or the publish job
fails by design. Once a build with this file ships, the URLs and keys are baked into installed clients and cannot be taken back.

### Rotation

Because clients only trust the keys embedded in their own build, rotate in two releases:

1. Generate an offline "next" stable key now and keep it offline. Ship its public key in `stableKeys` alongside the active one.
2. To rotate: put the next key's private value into the repo secret `CLINK_FEED_STABLE_SIGNING_KEY` (or `CLINK_FEED_DEV_SIGNING_KEY`) and remove the old public key from `feed.json` in a later release
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
  the same `Cache-Control` and content type. Then publish a newer version (re-running the failed run cannot work: its artifact is deleted and
  a rebuild conflicts with the write-once objects; see "Releasing").
- **Leaked or removed key** (the key that signed the bucket's manifest was removed from `feed.json`, or must be distrusted, so the bucket's
  manifest no longer verifies). The owner, in this order: (1) deletes `<prefix>/<channel>.json` and `<prefix>/<channel>.json.sig` from the
  bucket; (2) sets the repository variable `CLINK_FEED_BUCKET_RESET_VERSION` (Settings > Secrets and variables > Actions > Variables) to the version of the newest
  PUBLISHED release that carried `<channel>.json` (the refusal prints it); (3) publishes a newer version, signed with the new key listed in
  `feed.json`; (4) removes the variable. With the variable set and NEITHER file in the bucket, the publish accepts the GitHub history floor
  instead of the (deleted) bucket floor; it is ignored when the bucket has a manifest, and it never lowers the history floor. It is an
  owner-only, temporary, reviewed override: set it only for the one publish, never leave it in place.
- **A failed FIRST feed-on run.** The first run with the feed enabled leaves a DRAFT release that carries `<channel>.json`, and possibly
  objects (even the manifest) in the bucket. A feed-less publish is refused outright now (`Read feed configuration`), and the defence-in-depth guard
  `a feed that was on stays on` counts drafts too, so the failed draft also blocks any future relaxation of that refusal. If the feed is to stay off after all,
  the owner first deletes that draft release (and its tag if it was created) and cleans the bucket objects of the failed version; nothing publishes until
  the feed is on. To retry with the feed on, publish a newer version
  (the versioned objects of the failed version stay write-once and are compared by content).
- **A re-run of the same version is refused while its draft exists.** A failed run leaves a DRAFT release `vX.Y.Z` (and the workflow refuses to
  touch it). Either publish a NEWER version, or, after checking that the draft holds exactly the assets of that build, publish it by hand
  (`gh release edit vX.Y.Z --draft=false`), THEN CHECK THE TAG REF AND THE DIGESTS: `gh api repos/<repo>/git/ref/tags/vX.Y.Z` must point
  (annotated tags: after `git/tags/<sha>`) at the commit that was built, and every asset's `digest` in `gh api repos/<repo>/releases/<id>`
  must equal the hash in the release's OWN `.sha256` assets (`Clink.exe.sha256`, `Clink-framework.exe.sha256`, `Clink-Setup.exe.sha256`: the first 64-hex token of
  each; the build artifact is deleted by the `cleanup` job at the end of the run and expires after a day anyway, so it is not there to compare with).
  A manual publish skips the workflow's own checks, so these two checks are yours. For a dev build the git tag does not exist while the release is a draft, so the tag guard does not fire.
  Exception: if the run failed because `notes.txt` already exists with different content, do NOT publish the draft by hand at all; publish a newer version.
  Re-running is not an option once the run reached `Publish to the update feed`: "Re-run failed jobs" fails at `Download package artifact` (the
  artifact is deleted) and "Re-run all jobs" rebuilds, and the new bytes conflict with the write-once objects of the first attempt.
- **The bucket upload succeeded but publishing the GitHub release failed.** The bucket already serves the new manifest while the GitHub
  release is still a draft. Check the bucket copies of `<channel>.json` and its `.sig` against the draft's assets, then publish the verified
  draft by hand (`gh release edit vX.Y.Z --draft=false`; for stable add `--latest` only when this version is the highest), then check the
  tag ref and the digests as above. Do not re-run: the run would be refused for the existing draft, and a newer version is only needed when
  the draft is not trustworthy.

### Integrity incident

The steps `Re-verify the release before publishing` and `Verify the published release` (and, for dev, `Verify the published tag`) DELETE the
release by ID and fail the run with `RELEASE INTEGRITY INCIDENT (...)` when the release does not match what the build produced: another
author, name or body, a second release with the same tag, a changed or extra asset, a wrong digest or size, a published release that is not
immutable, a tag that moved or appeared. Treat it as an incident, not as a flaky run:

1. Read the message: it names the failed check and whether the release was deleted ("Release <id> was DELETED" or "could NOT be deleted:
   delete it by hand NOW"). If it could not be deleted, delete it first (`gh api -X DELETE repos/<repo>/releases/<id>`).
2. The bucket may already carry the new version (the feed upload runs before the second check): a deleted release leaves the feed's
   manifest pointing at binaries whose GitHub copy is gone. Compare the bucket's `<channel>.json` and the versioned objects with the
   hashes in the run log (the cleanup job always deletes the build artifact, so it cannot be used): the build job's `Package` step prints the
   `.sha256` lines, and the publish job logs `Package verified (...)`; both cover `Clink.exe` and `Clink-framework.exe` only. The setup's hash is in the env
   header of the step `Verify the package matches the build job` (`SETUP_SHA256`). For `notes.txt` and the rest, compare against the sha256 fields of the signed manifest
   after checking its signature. If they match those hashes, the next publish of a NEWER version replaces them; if not, follow "Recovering a stuck feed"
   and treat the bucket token as leaked (rotate it).
3. An immutable release keeps its tag for good (GitHub refuses to remove it), so that version number is used up: the next publish needs a newer
   version. A dev run picks a higher N by itself (it counts tags too).
4. Find out who or what changed the release (the repository's audit log, the release's `author`, the run's token use). A stolen
   `GITHUB_TOKEN` or a compromised action in the job are the likely causes; rotate whatever the job could reach (the repo secrets).
5. A failed FIRST check ("Verify the release assets") does not delete anything: the draft stays for you to look at (see "Stale drafts").

### Stale drafts

A failed or cancelled publish run leaves a DRAFT release. A draft is invisible to the build job's read token, so the build job's choice of the
next dev number `N` (above every dev N of a published release, read from every page of the listing, and above every dev git tag, because an
immutable release keeps its tag after the release is deleted, so a pruned number never comes back) cannot see it. The publish job's guard (`gh release view vX.Y.Z`, which finds drafts) then
refuses to touch a draft with the same version. Look at the draft (its assets show how far the run got) and the log, delete the draft
(`gh release delete vX.Y.Z --yes`; a draft has no tag yet), then: if the run failed BEFORE `Publish to the update feed` (nothing reached the
bucket), "Re-run all jobs" works; if it reached that step, publish a NEWER version (stable: bump `<Version>` and tag; dev: push again, or let
the next push pick a higher `N`). "Re-run failed jobs" never works after a publish failure: the `cleanup` job has already deleted the artifact,
so `Download package artifact` fails. The dev prune step neither counts nor
deletes drafts, so stale drafts accumulate until someone deletes them. Note that the guard `a feed that was on stays on` counts drafts too.

## Dry runs

There is no staging mode and no public staging fork (a public fork would expose the private source): see "Staging plan" in the owner guide.

Locally you can check the pieces without any bucket:

```
.\build.ps1 -Package                      # dist\sc, dist\fx, dist\package (the setup exe needs Inno Setup 6.3+, otherwise it is skipped with a warning)
# make-manifest.ps1 needs dist\package\notes.txt (plain UTF-8, one final LF; tools\lib\notes-lib.ps1 writes a valid one) and derives notesUrl itself
# (<BaseUrl>notes.txt): there is no -NotesUrl parameter. The BaseUrl must sit under the feedUrl / devFeedUrl of the feed.json you pass.
. .\tools\lib\notes-lib.ps1
[void](Write-FeedNotesFile -Text 'Local dry run' -Path dist\package\notes.txt)
$env:CLINK_FEED_SIGNING_KEY = Get-Content -Raw D:\keys\throwaway.pem
try {
  powershell -File tools\make-manifest.ps1 -Channel dev -Version 0.4.2-dev.1 -Tag v0.4.2-dev.1 `
    -BaseUrl https://downloads.example/clink/dev/v0.4.2-dev.1/ -PackageDir dist\package -FeedJson <feed.json with the throwaway public keys>
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

Dependabot's `dotnet-sdk` entry (`.github/dependabot.yml`) opens a PR when a newer SDK exists in the same major version (major bumps are ignored).
Treat such a PR as a REMINDER, not as something to merge: it is expected to FAIL the notices gate (`make-notices.ps1 -Check`, the
ThirdPartyNotices check), because a new SDK bundles a newer runtime pack than the one the notices describe. The maintainer then either follows
the "Runtime freshness gate" procedure on that branch (notices `-Update`, `MinimumRuntimeVersion`, and so on) or closes the PR and does the bump by hand.

`build.ps1` and `tools/publish-flavours.ps1` probe `dotnet --version` run in the repo root (which honours `global.json`): the
`dotnet` on PATH is used when it satisfies the pin, otherwise `%USERPROFILE%\.dotnet\dotnet.exe` is tried, and when neither does
they fail with "No .NET SDK satisfying global.json found". When bumping the pin, install the new SDK side by side with
`dotnet-install.ps1 -Version <x>` (an exact version; the old one can stay). Nested worktrees under `.claude\worktrees` inherit the
parent checkout's `global.json` once `dev` has it (the SDK resolver walks up the directory tree), so a worktree on an older
branch is pinned too.

## NuGet trust policy

The repo-root `nuget.config` makes nuget.org the only package source (`<clear/>` plus one source, and a package source mapping of `*`),
clears the fallback package folders, sets `signatureValidationMode` to `require` and lists the Microsoft Corporation AUTHOR signing
certificates in `trustedSigners` (SHA-256 fingerprints). A package that is unsigned, signed by anyone else, or served from another source
fails the restore. Settings in a file can be bypassed from outside it, so the release build jobs add two things (`tools/lib/nuget-restore-lib.ps1`,
tested by `tools/tests/nuget-restore.tests.ps1`):

- **Fresh folders.** The first steps of the build jobs (`Fresh NuGet folders`) create `$RUNNER_TEMP\nuget-<run id>-<attempt>\packages` and
  `...\http-cache` (refusing a folder that already exists) and write them to `GITHUB_ENV` as `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH`. A package
  that is already in a package folder is trusted without a signature check, so nothing from the runner image or an earlier job may be there. The
  release workflows never use `actions/cache` (a test enforces it). `build.yml` (pull requests, no secrets) keeps a cache of `~/.nuget/packages`, keyed on
  the `*.csproj` files, `global.json` and `nuget.config` with NO restore-keys, so a changed pin, source or trusted signer starts from an empty folder.
- **The restore record is checked, not the config.** After `Publish both flavours` (release builds, also `Clink/obj` in the dev build; `build.yml`'s package
  dry run) `Assert-NuGetRestoreTrusted` reads every `project.assets.json` and requires: `project.restore.sources` is exactly
  `https://api.nuget.org/v3/index.json`; no fallback folder; exactly one package folder (in `packageFolders` and `project.restore.packagesPath`),
  equal to `NUGET_PACKAGES` (trailing separator and case ignored); `project.restore.configFilePaths` includes the repo `nuget.config` (a
  `--configfile` or `RestoreConfigFile` restore bypasses it, and the MSBuild properties `RestoreSources`, `RestoreFallbackFolders` and
  `RestoreAdditionalProjectFallbackFolders` and the environment variable `NUGET_FALLBACK_PACKAGES` are not undone by `<clear/>`: they show
  up in the record); and the `.nupkg.metadata` of every package the restore used (libraries and exact-pinned runtime packs) names nuget.org as its source.

The SDK's implicit `library-packs` restore source (`<NetCoreRoot>\library-packs`, present on the CI runner) is turned off in `Clink.csproj` (`DisableImplicitLibraryPacksFolder`) so restore uses nuget.org only; the CI assertion would catch it otherwise.

Never lower `signatureValidationMode`, never add a source or an unsigned signer to make a restore pass.

### Certificate rotation runbook (NU3034 on a new Microsoft certificate)

Microsoft signs different packages with different certificates and rotates them. When one is replaced, the restore of the new package fails with
`NU3034`. That error does NOT print the fingerprint, and a package that fails verification is never extracted, so get the file yourself:

1. Download `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg` into a new, empty scratch folder; the id and the version are
   written in LOWER CASE, the version in its normalised form (this is the flat container's naming).
2. Run `dotnet nuget verify` with the all-signatures option on the file (the flag has two hyphens) and read the signer certificate's SHA-256 fingerprint.
3. Check the signer's Subject: `CN=Microsoft Corporation, O=Microsoft Corporation` (with the usual L, S and C), the issuer, and the chain
   root (a Microsoft root, not an untrusted one). Compare the fingerprint from TWO independent sources: your own verify output and a second
   one (a second download from another machine, or the certificate details shown for the same package on nuget.org), and watch
   `dotnet/announcements` for a note about a signing change.
4. Add the new fingerprint next to the old one in `nuget.config`, in a reviewed change (that loud failure is the point). Remove an old one only when
   no pinned package needs it any more.
5. Never lower `signatureValidationMode` or set `allowUntrustedRoot` to get past the failure.

## Installer inputs (ISCC)

`tools/package.ps1` feeds the Inno Setup compiler `/DAppVersion`, `/DAppFileVersion`, `/DOutDir`,
`/DSourceExe=<OutDir>\Clink-framework.exe` (the framework-dependent exe), `/DNoticesFile=<OutDir>\THIRD-PARTY-NOTICES.txt` (an absolute
path; this copy is also the release asset) and, from the runtime inputs, `/DDotNetRuntimeUrl`, `/DDotNetRuntimeSha256` and
`/DDotNetRuntimeVersion`, plus `/DAppProductVersion`: the setup's ProductVersion is `<Version>+<first 12 hex of the commit>` (the commit comes
from the framework exe's ProductVersion metadata; `-ProductVersion` overrides it), with a hard error over Inno's 50 characters. The runtime inputs are mandatory under `-RequireInstaller`: the script refuses without them, before it writes
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
`feed-config.tests.ps1` runs the shared fixtures of `tools/tests/feed-trust-cases.json` through `Get-FeedConfig` (the C# unit tests run the same file through
`FeedTrust.Parse`) and covers the canonical-URL rule and `Test-FeedAssetUrlUnder` (the port of the client's `IsUnder`). `release-verify.tests.ps1` covers
`tools/lib/release-verify-lib.ps1` and runs the real verify, re-verify and post-publish steps of both workflows against a fake `gh` (adopted release,
author, name, body, immutability, digests, annotated tags, retries, delete-on-failure). `nuget-restore.tests.ps1` covers `tools/lib/nuget-restore-lib.ps1`.
`runtime-freshness.tests.ps1`, `make-notices.tests.ps1`, `resolve-dotnet-runtime.tests.ps1` and `installer-script.tests.ps1` cover the
other scripts. `private-ci.tests.ps1` covers the private-CI slice: no `environment:` key anywhere, the job guards, secrets only in the three publish-job
steps (never in `build.yml` or a build job), `retention-days: 1` on every artifact upload, `notes.txt` (generation limits, `notesUrl`, the write-once
upload and the public byte check against a fake bucket), the `check-published.ps1` feed expectations and `new-feed-key.ps1` (dev and stable, `-Next`, the
owner summary, no private key material in the output), and the repair-round checks: the feed-off refusal of both publish jobs (run against a scratch `feed.json`),
the embedded floor seeds (the version regex, the wiring to `publish-flavours.ps1` and the dev build, the `github-actions[bot]` author filter, the
seed-versus-history-floor refusal run against a fake `gh`), the served-header checks of `notes.txt`, the stable notes step run for real against a fake
`gh` (always generated, never read back, cleaned before the link filter, mojibake, privacy filter in all its URL spellings), a planted `notes.txt` refused
by the write-once check, the upload preflight (a conflict on the last asset writes nothing), `CLINK_IMMUTABLE_RELEASES` validated in `Read feed
configuration`, the `cleanup` job (exactly `actions: write`, no `uses:`, no secret, the ownership check against a fake `gh`: another run's or another
name's artifact is never deleted), the client-source guard on scratch trees, and the endpoint check.
`release-verify.tests.ps1` also covers `CLINK_IMMUTABLE_RELEASES` (unset and `required` fail and delete, `unavailable` passes and warns, garbage fails).
`client-source-guard.tests.ps1` (rules in `tools/lib/client-guard-lib.ps1`) reads every `Clink\**\*.cs` except `Clink\Tests\**` and build output folders
(`bin*`, `obj*`), `Clink\MainWindow.xaml.cs` included, as text and asserts that none mentions "github" and that `Updater.cs` creates at most one
`HttpClient` and one `SocketsHttpHandler` (`new HttpClient(` and the target-typed `HttpClient x = new(` both count). While a client source still
DEFINES the legacy GitHub update method (`Task<...> VerifyGitHubReleaseAsync(`; a comment, call or `cref` that only names it does not count) both
checks are reported as skipped, and `CLINK_GUARD_STRICT=1` enforces them at once. `build.yml`'s script-test step always sets `CLINK_GUARD_STRICT: '1'`
as a plain env entry, so CI runs the guard strict. `run-script-tests.ps1` picks up every `*.tests.ps1` in `tools\tests` by itself. Under Windows PowerShell 5.1 (the
developer PC) one test skips: `upload-feed.ps1 needs PowerShell 7` in `feed.tests.ps1` (the run of `upload-feed.ps1` with a fake `aws` on PATH); CI runs it
under pwsh 7. The packaging tests build their "outside the repo" folder under the root of the repo's drive, never under TEMP (TEMP may be inside the repo).

## Recommended next step: Authenticode

Sign `Clink.exe` and the setup exe with a code-signing certificate (or Azure Trusted Signing) in the `build` or a third job so
SmartScreen reputation builds up and users can verify the publisher. The feed signature protects update integrity; it does not
replace Authenticode for first-time downloads.
