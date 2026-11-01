// Pack guard: accepts only a production bundle. It must carry the release marker (injected by `build.mjs` without --dev) and
// must not have Clink dev-instance discovery switched on (the `build:dev` bundle) or the dev marker.
// Usage: node devtools/check-bundle.ts [path/to/plugin.js]   (default: the plugin folder's bin/plugin.js)
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The compile-time flag as it lands in the bundle: `var devInstances = true;`, or `devInstances=!0` once minified. */
export const DEV_FLAG = /devInstances\s*=\s*(true|!0)/;
export const RELEASE_MARKER = "clink-build:release";
export const DEV_MARKER = "clink-build:dev";

export const hasDevDiscovery = (bundleText: string): boolean => DEV_FLAG.test(bundleText);

/** null: fine to pack. Otherwise why the bundle is refused. */
export function refusal(bundleText: string): string | null {
  if (hasDevDiscovery(bundleText)) return "this bundle has dev-instance discovery on (a build:dev bundle)";
  if (bundleText.includes(DEV_MARKER)) return "this bundle carries the dev build marker";
  if (!bundleText.includes(RELEASE_MARKER)) return "this bundle has no release build marker (not built by `npm run build`)";
  return null;
}

const here = path.dirname(fileURLToPath(import.meta.url));
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const file = process.argv[2] ?? path.join(here, "..", "software.euphoric.clink.sdPlugin", "bin", "plugin.js");
  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch {
    console.error(`check-bundle: cannot read ${file}`);
    process.exit(2);
  }
  const why = refusal(text);
  if (why) {
    console.error(`check-bundle: ${why}. Run \`npm run build\` first. Not packing.`);
    process.exit(1);
  }
  console.log("check-bundle: ok, release bundle without dev discovery");
}