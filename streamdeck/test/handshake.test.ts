import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { statSync, utimesSync, writeFileSync } from "node:fs";
import { uptime } from "node:os";
import net from "node:net";
import { test } from "node:test";
import { KEY_MAX_AGE_MS, checkProof, decodeBase64, expectedProof, pipeNameOf, readKey } from "../src/handshake.ts";
import { ClinkClient } from "../src/pipe-client.ts";
import { type Conn, KEYDIR, STATE, FakeSleep, fakeServer, goodServer, proofOf, scanOf, statuses, waitFor } from "./helpers.ts";

const fast = { connectTimeoutMs: 300, helloTimeoutMs: 1000, ackTimeoutMs: 400, keyDir: KEYDIR, keyRetryMs: 10 };

// Shared with the C# tests: key 00..1f, nonce 20..3f, pipe "Clink.Remote.s1".
const GOLDEN_KEY = Buffer.from(Array.from({ length: 32 }, (_, i) => i));
const GOLDEN_NONCE = Buffer.from(Array.from({ length: 32 }, (_, i) => 0x20 + i));
const GOLDEN_PIPE = "Clink.Remote.s1";
const GOLDEN_B64 = "lOKtG+FastgN749UlxcLSdaZKnAFLrkQ3fdrJEgjeuw=";
const GOLDEN_HEX = "94e2ad1be15ab2d80def8f5497170b49d6992a70052eb910ddf76b2448237aec";

test("golden vector", () => {
  const p = expectedProof(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE);
  assert.equal(p.toString("base64"), GOLDEN_B64);
  assert.equal(p.toString("hex"), GOLDEN_HEX);
  assert.equal(proofOf(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE), GOLDEN_B64);
  assert.equal(checkProof(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE, GOLDEN_B64), null);
  assert.equal(checkProof(GOLDEN_KEY, "Clink.Remote.s2", GOLDEN_NONCE, GOLDEN_B64), "proofMismatch");
  assert.equal(checkProof(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE, undefined), "noProof");
  assert.equal(checkProof(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE, "%%%"), "badProof");
  assert.equal(checkProof(GOLDEN_KEY, GOLDEN_PIPE, GOLDEN_NONCE, Buffer.alloc(16).toString("base64")), "badProof");
});

test("pipe names: only Clink.Remote names reach a key path", () => {
  assert.equal(pipeNameOf("\\\\.\\pipe\\Clink.Remote.s1"), "Clink.Remote.s1");
  assert.equal(pipeNameOf("\\\\.\\pipe\\Clink.Remote.dev.0123abcd.s4"), "Clink.Remote.dev.0123abcd.s4");
  for (const bad of [
    "\\\\.\\pipe\\Clink.Control.s1",
    "\\\\.\\pipe\\Clink.Remote.s1\\..\\x",
    "\\\\.\\pipe\\..\\Clink.Remote.s1",
    "\\\\.\\pipe\\Clink.Remote.s1.key",
    "\\\\.\\pipe\\Clink.Remote.s",
    "C:\\Clink.Remote.s1",
    "Clink.Remote.s1",
  ]) {
    assert.equal(pipeNameOf(bad), null, bad);
  }
});

test("strict base64", () => {
  assert.notEqual(decodeBase64(Buffer.alloc(32).toString("base64")), null);
  for (const bad of ["", "!!!!", "abc", "AAAA AAAA", "AAAA\nAAAA", "=AAA"]) assert.equal(decodeBase64(bad), null, bad);
});

/** A started client plus a captured log; `clock` replaces the real backoff. */
function setup(srvPath: string) {
  const clock = new FakeSleep(fast.keyRetryMs);
  const logs: string[] = [];
  const count = { connects: 0 };
  const c = new ClinkClient({ scan: scanOf([srvPath]), sleep: clock.fn, log: (r) => logs.push(r), connect: (p) => (count.connects++, net.connect(p)), ...fast });
  const seen = statuses(c);
  const states: unknown[] = [];
  c.on("state", (s) => states.push(s));
  return { c, clock, logs, seen, states, count };
}

type Case = { name: string; reason: string; setup?: (srv: { keyFile: string }) => void; writeKey?: boolean; over: (c: Conn, nonce: Buffer) => Record<string, unknown> };

const rejected: Case[] = [
  { name: "wrong proof", reason: "proofMismatch", over: () => ({ proof: randomBytes(32).toString("base64") }) },
  { name: "missing proof", reason: "noProof", over: () => ({ proof: undefined }) },
  { name: "malformed proof (not base64)", reason: "badProof", over: () => ({ proof: "not base64 !!" }) },
  { name: "proof of the wrong length", reason: "badProof", over: () => ({ proof: randomBytes(16).toString("base64") }) },
  { name: "proof for another pipe name", reason: "proofMismatch", over: (c, n) => ({ proof: proofOf(c.key, "Clink.Remote.s9", n) }) },
  { name: "missing key file", reason: "keyMissing", writeKey: false, over: () => ({}) },
  { name: "key file not base64", reason: "keyBad", setup: (s) => writeFileSync(s.keyFile, "this is not base64 !!!"), over: () => ({}) },
  {
    name: "key file older than this boot",
    reason: "keyStale",
    setup: (s) => {
      const past = new Date(Date.now() - (uptime() + 7200) * 1000);
      utimesSync(s.keyFile, past, past);
    },
    over: () => ({}),
  },
  {
    name: "key file older than 300 s",
    reason: "keyStale",
    setup: (s) => {
      const past = new Date(Date.now() - (KEY_MAX_AGE_MS + 5000));
      utimesSync(s.keyFile, past, past);
    },
    over: () => ({}),
  },
  { name: "key file too large", reason: "keyBad", setup: (s) => writeFileSync(s.keyFile, "A".repeat(2000)), over: () => ({}) },
  { name: "key file wrong length", reason: "keyBad", setup: (s) => writeFileSync(s.keyFile, randomBytes(16).toString("base64")), over: () => ({}) },
];

for (const k of rejected) {
  test(`rejected: ${k.name}`, async () => {
    const srv = await fakeServer(
      (c) => {
        c.onMessage = (m) => {
          if (m.t === "hello") {
            // State lines before a verified welcome must be ignored too.
            c.send(STATE);
            c.welcome(m, k.over(c, Buffer.from(String(m.nonce), "base64")));
          } else if (m.t === "cmd") c.send({ t: "ack", id: m.id, ok: true });
        };
      },
      { writeKey: k.writeKey },
    );
    k.setup?.(srv);
    const { c, clock, logs, seen, states, count } = setup(srv.path);
    c.start();
    try {
      await waitFor(() => clock.delays.length === 1, "backoff after the failure");
      if (k.reason.startsWith("key")) assert.equal(count.connects, 0, "connect never called without a usable key");
      else assert.ok(count.connects >= 1);
      assert.deepEqual(clock.delays, [500]);
      assert.deepEqual(logs, [k.reason]);
      assert.equal(c.connectionStatus, "off");
      assert.ok(!seen.includes("connected"));
      assert.deepEqual(states, []);
      assert.deepEqual(await c.send("clip"), { ok: false, error: "notConnected" });
      await waitFor(() => srv.conns.length === 0 || srv.conns[0]!.closed, "closed");
      assert.ok(srv.conns.every((x) => x.received.every((m) => m.t === "hello")), "no cmd written");
      if (k.writeKey === false || k.reason === "keyStale" || k.name === "key file too large") assert.ok(srv.conns.every((x) => x.received.length === 0), "nothing sent without a key");
    } finally {
      c.stop();
      await srv.close();
    }
  });
}

test("a correct proof connects, and no cmd is written before it", async () => {
  const srv = await goodServer();
  const { c, logs, seen } = setup(srv.path);
  // A send while still connecting fails fast and writes nothing.
  const early = c.send("clip");
  c.start();
  try {
    assert.deepEqual(await early, { ok: false, error: "notConnected" });
    await waitFor(() => c.connectionStatus === "connected", "connected");
    assert.deepEqual(logs, []);
    assert.deepEqual(seen, ["connecting", "connected"]);
    assert.deepEqual(srv.conns[0]!.received.map((m) => m.t), ["hello"]);
    assert.deepEqual(await c.send("clip"), { ok: true });
  } finally {
    c.stop();
    await srv.close();
  }
});

test("each connection sends a fresh nonce and reads the key again", async () => {
  const srv = await goodServer();
  const { c, clock } = setup(srv.path);
  c.start();
  try {
    await waitFor(() => c.connectionStatus === "connected", "connected");
    srv.conns[0]!.socket.destroy();
    await waitFor(() => clock.delays.length === 1, "reconnect wait");
    // Replace the key between connections: the new connection must use the new file.
    srv.key.fill(7);
    writeFileSync(srv.keyFile, srv.key.toString("base64"));
    clock.release();
    await waitFor(() => srv.conns.length === 2 && c.connectionStatus === "connected", "reconnected");
    const [a, b] = srv.conns.map((x) => String(x.received[0]!.nonce));
    assert.notEqual(a, b);
    assert.equal(Buffer.from(a!, "base64").length, 32);
    assert.equal(Buffer.from(b!, "base64").length, 32);
  } finally {
    c.stop();
    await srv.close();
  }
});

test("rejected: a replayed proof from an earlier connection", async () => {
  let old: string | undefined;
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => {
      if (m.t !== "hello") return;
      if (old === undefined) {
        old = proofOf(c.key, c.pipeName, Buffer.from(String(m.nonce), "base64"));
        c.welcome(m);
        c.send(STATE);
        setTimeout(() => c.socket.destroy(), 20);
      } else c.welcome(m, { proof: old });
    };
  });
  const { c, clock, logs, seen } = setup(srv.path);
  c.start();
  try {
    await waitFor(() => c.connectionStatus === "connected", "first connection");
    await waitFor(() => clock.delays.length === 1, "dropped");
    clock.release();
    await waitFor(() => srv.conns.length === 2 && clock.delays.length === 2, "second attempt");
    assert.deepEqual(logs, ["proofMismatch"]);
    assert.equal(c.connectionStatus, "off");
    assert.equal(seen.filter((s) => s === "connected").length, 1);
    assert.ok(srv.conns[1]!.received.every((m) => m.t === "hello"));
  } finally {
    c.stop();
    await srv.close();
  }
});

test("a key file that appears late succeeds on the retry", async () => {
  const srv = await fakeServer(
    (c) => {
      c.onMessage = (m) => {
        if (m.t === "hello") {
          c.welcome(m);
          c.send(STATE);
        }
      };
    },
    { writeKey: false },
  );
  const { c, clock, logs } = setup(srv.path);
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "first attempt failed");
    assert.deepEqual(logs, ["keyMissing"]);
    assert.equal(c.connectionStatus, "off");
    writeFileSync(srv.keyFile, srv.key.toString("base64"));
    clock.release();
    await waitFor(() => c.connectionStatus === "connected", "connected on retry");
    assert.deepEqual(logs, ["keyMissing"]);
  } finally {
    c.stop();
    await srv.close();
  }
});

test("a path that is not a Clink.Remote pipe is never opened for a key", async () => {
  const logs: string[] = [];
  const clock = new FakeSleep(fast.keyRetryMs);
  let hellos = 0;
  let connects = 0;
  const c = new ClinkClient({
    scan: scanOf(["\\\\.\\pipe\\Clink.Remote.s1\\..\\evil"]),
    sleep: clock.fn,
    log: (r) => logs.push(r),
    connect: () => {
      connects++;
      const s = new net.Socket();
      s.write = ((): boolean => (hellos++, true)) as never;
      return s;
    },
    ...fast,
  });
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "failed");
    assert.deepEqual(logs, ["badPipeName"]);
    assert.equal(hellos, 0);
    assert.equal(connects, 0, "no socket opened for a non-matching name");
    assert.equal(c.connectionStatus, "off");
  } finally {
    c.stop();
  }
});

test("older Clink: only a Control pipe listed gives updateClink and is never connected to", async () => {
  let connects = 0;
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({
    scan: scanOf([], ["\\\\.\\pipe\\Clink.Control.s1"]),
    sleep: clock.fn,
    connect: () => (connects++, new net.Socket()),
    ...fast,
  });
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "first scan");
    assert.equal(c.connectionStatus, "off");
    clock.release();
    await waitFor(() => c.connectionStatus === "updateClink" && clock.delays.length === 2, "updateClink");
    assert.deepEqual(clock.delays, [2000, 4000]);
    assert.deepEqual(await c.send("clip"), { ok: false, error: "notConnected" });
    assert.equal(connects, 0);
  } finally {
    c.stop();
  }
});

test("nothing listed gives off", async () => {
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({ scan: scanOf([], []), sleep: clock.fn, ...fast });
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "scan");
    assert.equal(c.connectionStatus, "off");
  } finally {
    c.stop();
  }
});

test("a Remote pipe wins over a Control pipe", async () => {
  const srv = await goodServer();
  const c = new ClinkClient({ scan: scanOf([srv.path], ["\\\\.\\pipe\\Clink.Control.s1"]), ...fast });
  c.start();
  try {
    await waitFor(() => c.connectionStatus === "connected", "connected");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("pipes without a usable key are skipped without connecting", async () => {
  for (const prepare of [
    (_f: string) => {}, // missing
    (f: string) => writeFileSync(f, "A".repeat(2000)), // too large
    (f: string) => {
      const past = new Date(Date.now() - (uptime() + 7200) * 1000);
      writeFileSync(f, randomBytes(32).toString("base64"));
      utimesSync(f, past, past);
    }, // stale
  ]) {
    const srv = await fakeServer(() => {}, { writeKey: false });
    prepare(srv.keyFile);
    let connects = 0;
    const clock = new FakeSleep(fast.keyRetryMs);
    const c = new ClinkClient({ scan: scanOf([srv.path]), sleep: clock.fn, connect: (p) => (connects++, net.connect(p)), ...fast });
    c.start();
    try {
      await waitFor(() => clock.delays.length === 1, "skipped");
      assert.equal(connects, 0);
      assert.equal(srv.conns.length, 0);
      assert.equal(c.connectionStatus, "off");
    } finally {
      c.stop();
      await srv.close();
    }
  }
});

test("a key that shows up within the silent retry window is used and nothing is logged", async () => {
  const srv = await fakeServer(
    (c) => {
      c.onMessage = (m) => m.t === "hello" && c.welcome(m);
    },
    { writeKey: false },
  );
  const logs: string[] = [];
  const clock = new FakeSleep(fast.keyRetryMs);
  clock.holdRetry = true;
  const c = new ClinkClient({ scan: scanOf([srv.path]), sleep: clock.fn, log: (r) => logs.push(r), ...fast });
  const seen = statuses(c);
  c.start();
  try {
    await waitFor(() => clock.retryHeld, "silent retry wait started");
    assert.deepEqual(logs, []);
    writeFileSync(srv.keyFile, srv.key.toString("base64"));
    clock.releaseRetry();
    await waitFor(() => c.connectionStatus === "connected", "connected");
    assert.deepEqual(logs, []);
    assert.deepEqual(clock.delays, []);
    assert.ok(!seen.includes("off"));
  } finally {
    c.stop();
    await srv.close();
  }
});

test("key freshness: fresh is ok, older than 300 s is keyStale, also with a huge uptime (Fast Startup)", async () => {
  const srv = await fakeServer(() => {});
  try {
    const mtime = statSync(srv.keyFile).mtimeMs;
    const at = (ageMs: number, uptimeS: number) => readKey(KEYDIR, srv.name, { now: mtime + ageMs, uptimeS });
    assert.ok("key" in (await at(1000, 3600)));
    assert.ok("key" in (await at(KEY_MAX_AGE_MS - 1000, 3600)));
    assert.deepEqual(await at(KEY_MAX_AGE_MS + 1000, 3600), { fail: "keyStale" });
    // Fast Startup: uptime counts days while the key (old, but after the nominal boot) is 10 minutes old.
    assert.deepEqual(await at(600_000, 10_000_000), { fail: "keyStale" });
    assert.ok("key" in (await at(1000, 10_000_000)));
  } finally {
    await srv.close();
  }
});

test("a pipe without a usable key: each reason is logged once until it changes, status stays off, connect is never called", async () => {
  const srv = await fakeServer(
    (c) => {
      c.onMessage = (m) => m.t === "hello" && c.welcome(m);
    },
    { writeKey: false },
  );
  const clock = new FakeSleep(fast.keyRetryMs);
  const logs: string[] = [];
  let skew = 0;
  let connects = 0;
  const c = new ClinkClient({
    scan: scanOf([srv.path]),
    sleep: clock.fn,
    log: (r) => logs.push(r),
    now: () => Date.now() + skew,
    connect: (p) => (connects++, net.connect(p)),
    ...fast,
  });
  const seen = statuses(c);
  const next = async (n: number): Promise<void> => {
    clock.release();
    await waitFor(() => clock.delays.length === n, `scan ${n}`);
  };
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "scan 1");
    await next(2);
    await next(3);
    assert.deepEqual(logs, ["keyMissing"], "missing is logged once");
    writeFileSync(srv.keyFile, srv.key.toString("base64"));
    skew = KEY_MAX_AGE_MS + 5000;
    await next(4);
    await next(5);
    assert.deepEqual(logs, ["keyMissing", "keyStale"], "stale is logged once, after the change");
    assert.equal(connects, 0);
    assert.deepEqual(seen, [], "no status flapping while there is no key");
    assert.equal(c.connectionStatus, "off");
    skew = 0;
    clock.release();
    await waitFor(() => c.connectionStatus === "connected", "connected once the key is fresh");
    assert.deepEqual(logs, ["keyMissing", "keyStale"]);
    assert.equal(connects, 1);
    assert.deepEqual(seen, ["connecting", "connected"]);
  } finally {
    c.stop();
    await srv.close();
  }
});
test("bye proto before the welcome: updatePlugin on the normal backoff", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.send({ t: "bye", reason: "proto" });
  });
  const { c, clock } = setup(srv.path);
  c.start();
  try {
    await waitFor(() => c.connectionStatus === "updatePlugin" && clock.delays.length === 1, "updatePlugin");
    assert.deepEqual(clock.delays, [500]);
  } finally {
    c.stop();
    await srv.close();
  }
});
