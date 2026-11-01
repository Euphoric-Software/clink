// `npm run pack`: packs a production build WITHOUT touching the plugin folder's bin/ (that folder is linked into Stream Deck).
//   1. copy the plugin folder (without bin/ and logs/) to <dist>/pack-stage/software.euphoric.clink.sdPlugin
//   2. build the production bundle into the stage's bin/ (build.mjs --outdir=)
//   3. run the guard (check-bundle.ts) on that file
//   4. streamdeck pack <stage> --output <dist>/streamdeck
// Options: --dist=<dir> (default: ../dist, which is gitignored), --stage-only (stop after step 3; tests use it).
import { execFileSync } from "node:child_process";
import { cpSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const PLUGIN = "software.euphoric.clink.sdPlugin";
const root = path.resolve(import.meta.dirname, "..");

/** Steps 1 to 3. Returns the staged plugin folder; throws when the guard refuses the bundle. */
export function stage(dist: string): string {
  const stageRoot = path.join(dist, "pack-stage");
  const staged = path.join(stageRoot, PLUGIN);
  rmSync(stageRoot, { recursive: true, force: true });
  mkdirSync(stageRoot, { recursive: true });
  cpSync(path.join(root, PLUGIN), staged, { recursive: true, filter: (src) => !["bin", "logs"].includes(path.basename(src)) || path.dirname(src) !== path.join(root, PLUGIN) });
  const bin = path.join(staged, "bin");
  execFileSync(process.execPath, ["build.mjs", `--outdir=${bin}`], { cwd: root, stdio: "inherit" });
  execFileSync(process.execPath, ["devtools/check-bundle.ts", path.join(bin, "plugin.js")], { cwd: root, stdio: "inherit" });
  return staged;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const arg = (name: string): string | undefined => process.argv.find((a) => a.startsWith(`--${name}=`))?.slice(name.length + 3);
  const dist = path.resolve(root, arg("dist") ?? path.join("..", "dist"));
  try {
    const staged = stage(dist);
    if (process.argv.includes("--stage-only")) {
      console.log(`pack: staged and checked ${staged}`);
    } else {
      const outDir = path.join(dist, "streamdeck");
      rmSync(path.join(outDir, `${PLUGIN.replace(/\.sdPlugin$/, "")}.streamDeckPlugin`), { force: true });
      const cli = path.join(root, "node_modules", "@elgato", "cli", "bin", "streamdeck.mjs");
      execFileSync(process.execPath, [cli, "pack", staged, "--output", outDir, "--force"], { cwd: root, stdio: "inherit" });
    }
  } finally {
    // --stage-only leaves the stage for the tests that call it; a real pack never does.
    if (!process.argv.includes("--stage-only")) rmSync(path.join(dist, "pack-stage"), { recursive: true, force: true });
  }
}