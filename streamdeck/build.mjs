// Bundles src/plugin.ts into the .sdPlugin folder's bin/plugin.js (Stream Deck's bundled Node 24, ESM).
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { build } from "esbuild";

// ws (inside the SDK) is CommonJS and calls require("events") and friends. In ESM output esbuild turns those into a
// __require shim that throws "Dynamic require is not supported". Giving the bundle a real require fixes that.
const REQUIRE_BANNER = "import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);";

// `--dev` builds the local-testing bundle that also finds Clink dev instances. Release builds (default) never do.
const dev = process.argv.includes("--dev");

// `--outdir=<dir>` (default: the plugin folder's bin/). Tests build into a temp folder so `npm test` never rewrites the linked bin/.
const outArg = process.argv.find((a) => a.startsWith("--outdir="));
const outdir = outArg ? outArg.slice("--outdir=".length) : "software.euphoric.clink.sdPlugin/bin";
if (outdir.length === 0) throw new Error("--outdir needs a folder");

await build({
  entryPoints: ["src/plugin.ts"],
  outfile: path.join(outdir, "plugin.js"),
  bundle: true,
  platform: "node",
  target: "node24",
  format: "esm",
  sourcemap: false,
  legalComments: "none",
  banner: { js: REQUIRE_BANNER },
  define: { __CLINK_DEV_INSTANCES__: dev ? "true" : "false", __CLINK_BUILD__: JSON.stringify(dev ? "clink-build:dev" : "clink-build:release") },
  logLevel: "info",
});

// The output folder sits in the installed plugin folder, which has no package.json above it: mark it ESM so Node loads plugin.js as a module.
mkdirSync(outdir, { recursive: true });
writeFileSync(path.join(outdir, "package.json"), '{"type":"module"}\n');