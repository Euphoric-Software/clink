import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";

// Smoke test of the BUILT bundle. It builds first, then runs `node bin/plugin.js` in a throw-away folder with no Stream Deck
// launch arguments. Everything that matters here happens while the module graph loads (every import and every CommonJS
// require() inside the bundled SDK and ws runs at load, before plugin.ts reaches `streamDeck.connect()`). The SDK then
// cannot reach Stream Deck and exits or errors, which we ignore: we only fail on module-load errors, so a bundle that
// cannot load ("Dynamic require of ... is not supported", missing module, syntax error) is caught, a missing connection is not.
const root = path.resolve(import.meta.dirname, "..");
const bin = path.join(root, "software.euphoric.clink.sdPlugin", "bin");

const buildBundle = (...args: string[]): string => {
  execFileSync(process.execPath, ["build.mjs", ...args], { cwd: root, stdio: "pipe" });
  return readFileSync(path.join(bin, "plugin.js"), "utf8");
};

test("the dev bundle enables dev instances and the production bundle does not", () => {
  const dev = buildBundle("--dev");
  assert.ok(!dev.includes("__CLINK_DEV_INSTANCES__"), "flag must be replaced at build time");
  assert.match(dev, /var devInstances = true;/);
  const prod = buildBundle();
  assert.ok(!prod.includes("__CLINK_DEV_INSTANCES__"), "flag must be replaced at build time");
  assert.match(prod, /var devInstances = false;/);
});

test("the built bundle loads (module graph) and bin/package.json marks it ESM", async () => {
  buildBundle();
  assert.deepEqual(JSON.parse(readFileSync(path.join(bin, "package.json"), "utf8")), { type: "module" });
  assert.ok(existsSync(path.join(bin, "plugin.js")));

  const cwd = mkdtempSync(path.join(tmpdir(), "clink-sd-smoke-"));
  try {
    const out = await new Promise<string>((resolve) => {
      const child = spawn(process.execPath, [path.join(bin, "plugin.js")], { cwd, stdio: ["ignore", "pipe", "pipe"] });
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