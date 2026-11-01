import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { DEV_FLAG, DEV_MARKER, RELEASE_MARKER, hasDevDiscovery, refusal } from "../devtools/check-bundle.ts";

// Smoke test of the BUILT bundle. It builds into a throw-away folder (`build.mjs --outdir`, so `npm test` never rewrites the
// linked bin/ of the plugin folder), then runs `node plugin.js` from there with no Stream Deck launch arguments. Everything
// that matters happens while the module graph loads (every import and every CommonJS require() inside the bundled SDK and ws
// runs at load, before plugin.ts reaches `streamDeck.connect()`). The SDK then cannot reach Stream Deck and exits or errors,
// which we ignore: we only fail on module-load errors, so a bundle that cannot load ("Dynamic require of ... is not
// supported", missing module, syntax error) is caught, a missing connection is not.
const root = path.resolve(import.meta.dirname, "..");
const linkedBin = path.join(root, "software.euphoric.clink.sdPlugin", "bin", "plugin.js");
const binStamp = (): string => (existsSync(linkedBin) ? `${statSync(linkedBin).mtimeMs}:${statSync(linkedBin).size}` : "absent");
const before = binStamp();

const work = mkdtempSync(path.join(tmpdir(), "clink-sd-bundle-"));
after(() => rmSync(work, { recursive: true, force: true }));
let n = 0;

const buildBundle = (...args: string[]): { dir: string; text: string } => {
  const dir = path.join(work, `out${n++}`);
  execFileSync(process.execPath, ["build.mjs", `--outdir=${dir}`, ...args], { cwd: root, stdio: "pipe" });
  return { dir, text: readFileSync(path.join(dir, "plugin.js"), "utf8") };
};

test("the dev bundle enables dev instances and the production bundle does not", () => {
  const dev = buildBundle("--dev").text;
  assert.ok(!dev.includes("__CLINK_DEV_INSTANCES__"), "flag must be replaced at build time");
  assert.match(dev, /devInstances\s*=\s*(true|!0)/, "the flag survives as a literal (also when minified)");
  assert.ok(hasDevDiscovery(dev));
  assert.ok(dev.includes(DEV_MARKER) && !dev.includes(RELEASE_MARKER), "dev build marker");
  const prod = buildBundle().text;
  assert.ok(!prod.includes("__CLINK_DEV_INSTANCES__"), "flag must be replaced at build time");
  assert.match(prod, /devInstances\s*=\s*(false|!1)/);
  assert.ok(!hasDevDiscovery(prod));
  assert.ok(prod.includes(RELEASE_MARKER) && !prod.includes(DEV_MARKER), "release build marker survives in the bundle");
  assert.equal(refusal(prod), null);
  assert.ok(refusal(dev));
});

test("the pack guard recognises plain and minified dev flags and nothing else", () => {
  for (const yes of ["var devInstances = true;", "let devInstances=true;", "devInstances=!0", "devInstances =\n  !0;"]) assert.ok(DEV_FLAG.test(yes), yes);
  for (const no of ["var devInstances = false;", "devInstances=!1", "s.devInstances === true", "x.devInstances===!0"]) assert.ok(!DEV_FLAG.test(no), no);
});

test("check-bundle exits 1 for a dev bundle and 0 for a production bundle", () => {
  const run = (file: string): number => {
    try {
      execFileSync(process.execPath, ["devtools/check-bundle.ts", file], { cwd: root, stdio: "pipe" });
      return 0;
    } catch (e) {
      return (e as { status: number }).status;
    }
  };
  const dev = path.join(work, "dev.js");
  const prod = path.join(work, "prod.js");
  const unmarked = path.join(work, "unmarked.js");
  const devMarked = path.join(work, "dev-marked.js");
  writeFileSync(dev, `var devInstances = true;\nvar m = "${DEV_MARKER}";\n`);
  writeFileSync(prod, `var devInstances = false;\nvar m = "${RELEASE_MARKER}";\n`);
  writeFileSync(unmarked, "var devInstances = false;\n");
  writeFileSync(devMarked, `var devInstances = false;\nvar m = "${RELEASE_MARKER}", d = "${DEV_MARKER}";\n`);
  assert.equal(run(dev), 1, "dev refused");
  assert.equal(run(prod), 0, "release ok");
  assert.equal(run(unmarked), 1, "marker missing refused");
  assert.equal(run(devMarked), 1, "dev marker refused even beside a release marker");
  assert.equal(run(path.join(work, "missing.js")), 2);
});

test("--outdir puts the bundle and its package.json in that folder", () => {
  const { dir } = buildBundle();
  assert.deepEqual(JSON.parse(readFileSync(path.join(dir, "package.json"), "utf8")), { type: "module" });
  assert.ok(existsSync(path.join(dir, "plugin.js")));
});

test("the built bundle loads (module graph) from a throw-away folder", async () => {
  const { dir } = buildBundle();
  const cwd = mkdtempSync(path.join(tmpdir(), "clink-sd-smoke-"));
  try {
    const out = await new Promise<string>((resolve) => {
      const child = spawn(process.execPath, [path.join(dir, "plugin.js")], { cwd, stdio: ["ignore", "pipe", "pipe"] });
      let text = "";
      child.stdout.on("data", (d) => (text += d));
      child.stderr.on("data", (d) => (text += d));
      const timer = setTimeout(() => child.kill(), 4000);
      child.on("close", () => (clearTimeout(timer), resolve(text)));
      child.on("error", (e) => (clearTimeout(timer), resolve(String(e))));
    });
    for (const bad of ["Dynamic require", "ERR_MODULE_NOT_FOUND", "Cannot find module", "ERR_REQUIRE_ESM", "SyntaxError", "ReferenceError"]) {
      assert.ok(!out.includes(bad), `bundle failed to load (${bad}):\n${out}`);
    }
  } finally {
    rmSync(cwd, { recursive: true, force: true });
  }
});

test("pack stages a copy, builds the production bundle there, passes the guard, and leaves the linked bin/ alone", () => {
  const dist = path.join(work, "dist");
  const linkedBefore = binStamp();
  execFileSync(process.execPath, ["devtools/pack.ts", `--dist=${dist}`, "--stage-only"], { cwd: root, stdio: "pipe" });
  const staged = path.join(dist, "pack-stage", "software.euphoric.clink.sdPlugin");
  assert.ok(existsSync(path.join(staged, "manifest.json")), "plugin folder copied");
  assert.ok(existsSync(path.join(staged, "imgs")));
  const bundle = readFileSync(path.join(staged, "bin", "plugin.js"), "utf8");
  assert.equal(refusal(bundle), null, "the staged bundle is a release bundle");
  assert.equal(binStamp(), linkedBefore, "linked bin/ untouched by the staging step");
});

test("npm test left the linked bin/ alone", () => {
  assert.equal(binStamp(), before);
});