# Releasing Clink

Tag `vX.Y.Z` (use `tools/release.ps1`) runs `.github/workflows/release.yml`; a push to `dev` runs `release-dev.yml`.
Both publish a GitHub release with `Clink.exe` and `Clink.exe.sha256` (what v0.4.1 and older updaters read), then add
`Clink-<ver>-setup.exe` and `Clink-<ver>-portable.zip`, then (if configured) publish the signed update feed.

## Secrets and variables (repo Settings > Secrets and variables > Actions)

| Name | Kind | Purpose |
| --- | --- | --- |
| `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY` | secret | S3 API token for the bucket (Object Read & Write) |
| `R2_ENDPOINT` | secret | `https://<account-id>.r2.cloudflarestorage.com` |
| `R2_BUCKET` | secret | bucket name |
| `CLINK_FEED_SIGNING_KEY` | secret | ECDSA P-256 private key, PKCS#8 PEM (see below) |
| `CLINK_FEED_URL` | variable | public feed base URL, ends with `/`, e.g. `https://cdn.example/clink/` |
| `CLINK_FEED_PUBLIC_KEY` | variable | Base64 SPKI public key, baked into Clink.exe |
| `CLINK_FEED_KEY_PREFIX` | variable, optional | bucket key prefix the feed URL maps to; default `clink` |
| `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ZONE_ID` | secrets, optional | purge the manifest/latest URLs after upload |

If any of the six required R2/signing/URL values is missing the feed step logs a `::warning::` and is skipped; the
GitHub release still ships. `CLINK_FEED_URL` / `CLINK_FEED_PUBLIC_KEY` empty means Clink.exe is built without the feed.

## Bucket layout (under the `clink/` prefix)

```
clink/releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-X.Y.Z-setup.exe, Clink-X.Y.Z-portable.zip}
clink/dev/vX.Y.Z-dev.N/{same four files}        (10 newest kept)
clink/latest/{Clink-setup.exe, Clink-portable.zip}   (stable only, stable download links)
clink/stable.json, stable.json.sig, dev.json, dev.json.sig
```

Upload order: versioned assets, then `latest/` (stable), then manifest and signature last, so a manifest never points at
a missing file.

## Cache-Control

- Versioned assets: `public, max-age=31536000, immutable`
- `latest/*`, `*.json`, `*.json.sig`: `public, max-age=60`

## Bucket CORS (website downloads and manifest reads)

```json
[{ "AllowedOrigins": ["https://euphoric.software"], "AllowedMethods": ["GET", "HEAD"], "AllowedHeaders": ["*"], "MaxAgeSeconds": 3600 }]
```

## Signing key

1. `pwsh tools/new-feed-key.ps1 -PrivateKeyPath D:\keys\clink-feed.pem` (path must be outside the repo).
2. Add the whole PEM as secret `CLINK_FEED_SIGNING_KEY`; add the printed public key as variable `CLINK_FEED_PUBLIC_KEY`;
   set variable `CLINK_FEED_URL`.
3. Store the PEM safely, then delete the local copy. Losing it means shipping a new build with a new public key.

## Dry run against a staging prefix

In a fork or temporarily, set `CLINK_FEED_KEY_PREFIX=clink-staging` and `CLINK_FEED_URL=https://cdn.example/clink-staging/`,
then push to `dev`. The feed lands under `clink-staging/` (including `dev.json`) and the built exe reads that prefix.
Locally: run `./build.ps1 -Package` (output in `dist/package/`), then export `CLINK_FEED_SIGNING_KEY` and run
`pwsh tools/make-manifest.ps1` to inspect a manifest without uploading. Remove the staging prefix afterwards and
restore the variables.

## Local packaging

`./build.ps1 -Package` publishes and runs `tools/package.ps1` into `dist/package/`. The installer needs Inno Setup 6
(`ISCC.exe`); without it the script warns and skips only the setup. CI installs Inno Setup via Chocolatey if missing.
