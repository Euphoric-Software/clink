import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { readdir } from "node:fs/promises";
import net from "node:net";
import { test } from "node:test";
import { PIPE_DIR, matchControl, matchRemote, scanPipes } from "../src/discovery.ts";

const names = [
  "Clink.Remote.s1",
  "Clink.Remote.s12",
  "Clink.Remote.dev.0123abcd.s1",
  "Clink.Remote.dev.0123ABCD.s1",
  "Clink.Remote.dev.0123abc.s1",
  "Clink.Remote.s",
  "Clink.Remote.s1x",
  "xClink.Remote.s1",
  "Clink.Control.s2",
  "Clink.Control.dev.s2",
  "Clink.Control.dev.0123abcd.s3",
  "other",
];

test("remote regex: release pipes only by default", () => {
  assert.deepEqual(matchRemote(names, false), ["Clink.Remote.s1", "Clink.Remote.s12"]);
});

test("remote regex: dev instances only with the setting", () => {
  assert.deepEqual(matchRemote(names, true), ["Clink.Remote.dev.0123abcd.s1", "Clink.Remote.s1", "Clink.Remote.s12"]);
});

test("control regex", () => {
  assert.deepEqual(matchControl(names), ["Clink.Control.s2"]);
});

test("control regex: dev Control pipes only with devInstances", () => {
  assert.deepEqual(matchControl(names, true), ["Clink.Control.s2", "Clink.Control.dev.0123abcd.s3"]);
});

test("scanPipes reads the pipe directory through the injected readdir and returns full paths", async () => {
  let asked = "";
  const scan = await scanPipes(async (d) => ((asked = d), names), false);
  assert.equal(asked, PIPE_DIR);
  assert.deepEqual(scan.remote, [PIPE_DIR + "Clink.Remote.s1", PIPE_DIR + "Clink.Remote.s12"]);
  assert.deepEqual(scan.control, [PIPE_DIR + "Clink.Control.s2"]);
});

test("a failing readdir counts as nothing found", async () => {
  const scan = await scanPipes(async () => { throw new Error("denied"); }, true);
  assert.deepEqual(scan, { remote: [], control: [] });
});

test("the real readdir lists a live dev pipe", async () => {
  const name = `Clink.Remote.dev.${randomBytes(4).toString("hex")}.s1`;
  const server = net.createServer();
  await new Promise<void>((res) => server.listen(PIPE_DIR + name, res));
  try {
    const scan = await scanPipes((dir) => readdir(dir), true);
    assert.ok(scan.remote.includes(PIPE_DIR + name), "dev pipe listed");
    const release = await scanPipes((dir) => readdir(dir), false);
    assert.ok(!release.remote.includes(PIPE_DIR + name), "not listed without devInstances");
  } finally {
    await new Promise<void>((res) => server.close(() => res()));
  }
});
