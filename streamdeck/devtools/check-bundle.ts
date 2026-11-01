// Pack guard: refuses a bundle that still has Clink dev-instance discovery switched on (the `build:dev` bundle).
// Usage: node devtools/check-bundle.ts [path/to/plugin.js]   (default: the plugin folder's bin/plugin.js)
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The compile-time flag as it lands in the bundle: `var devInstances = true;`, or `devInstances=!0` once minified. */
export const DEV_FLAG = /devInstances\s*=\s*(true|!0)/;

export const hasDevDiscovery = (bundleText: string): boolean => DEV_FLAG.test(bundleText);

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
  if (hasDevDiscovery(text)) {
    console.error("check-bundle: this bundle has dev-instance discovery on (a build:dev bundle). Run `npm run build` first. Not packing.");
    process.exit(1);
  }
  console.log("check-bundle: ok, no dev discovery in the bundle");
}