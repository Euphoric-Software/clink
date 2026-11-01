import assert from "node:assert/strict";
import net from "node:net";
import { test } from "node:test";
import { ClinkClient } from "../src/pipe-client.ts";
import type { ClinkEvent, ClinkState } from "../src/protocol.ts";
import { MAX_LINE_BYTES } from "../src/protocol.ts";
import { KEYDIR, STATE, failingSocket, fakeServer, goodServer, keyedPipe, scanOf, statuses, waitFor, FakeSleep } from "./helpers.ts";

const fast = { connectTimeoutMs: 300, helloTimeoutMs: 1000, ackTimeoutMs: 400, keyDir: KEYDIR, keyRetryMs: 10 };

async function connected(c: ClinkClient): Promise<void> {
  await waitFor(() => c.connectionStatus === "connected", "connected");
}

test("handshake: hello goes out, welcome makes it connected", async () => {
  const srv = await goodServer();
  const c = new ClinkClient({ scan: scanOf([srv.path]), clientVersion: "7.7", ...fast });
  const seen = statuses(c);
  let info: unknown;
  c.on("welcome", (i) => (info = i));
  c.start();
  try {
    await connected(c);
    assert.deepEqual(seen, ["connecting", "connected"]);
    const hello = srv.conns[0]!.received[0]!;
    const { nonce, ...rest } = hello;
    assert.deepEqual(rest, { t: "hello", proto: 1, client: "clink-streamdeck", v: "7.7" });
    assert.equal(Buffer.from(String(nonce), "base64").length, 32);
    assert.deepEqual(info, { version: "9.9.9", dev: true, elevated: false, verbs: ["clip", "record"] });
  } finally {
    c.stop();
    await srv.close();
  }
});

test("a server that is not Clink (wrong app) is not accepted", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.welcome(m, { app: "Other" });
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast });
  c.start();
  try {
    await waitFor(() => srv.conns[0]?.closed === true, "closed");
    assert.notEqual(c.connectionStatus, "connected");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("state and events are parsed and emitted", async () => {
  const srv = await goodServer();
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast });
  const states: ClinkState[] = [];
  const events: ClinkEvent[] = [];
  c.on("state", (s) => states.push(s));
  c.on("event", (e) => events.push(e));
  c.start();
  try {
    await connected(c);
    await waitFor(() => states.length === 1, "state");
    assert.equal(states[0]!.state, "recording");
    assert.equal(states[0]!.rec.s, 12);
    assert.equal(c.state?.bufferS, 30);
    const s = srv.conns[0]!;
    s.send({ t: "clipSaved", seconds: 30, future: true });
    s.send({ t: "clipNotSaved", reason: "paused" });
    s.send({ t: "shareProgress", p: 50 });
    s.send({ t: "shareDone", outcome: "linkCopied" });
    s.send({ t: "unknownEvent" });
    s.send({ ...STATE, state: "paused" });
    await waitFor(() => events.length === 4 && states.length === 2, "events");
    assert.deepEqual(events.map((e) => e.t), ["clipSaved", "clipNotSaved", "shareProgress", "shareDone"]);
    assert.equal(states[1]!.state, "paused");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("cmd: ack ok and ack error, ids increase", async () => {
  const srv = await goodServer((verb) => (verb === "nope" ? "unknownVerb" : undefined));
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast });
  c.start();
  try {
    await connected(c);
    assert.deepEqual(await c.send("clip"), { ok: true });
    assert.deepEqual(await c.send("nope"), { ok: false, error: "unknownVerb" });
    const cmds = srv.conns[0]!.received.filter((m) => m.t === "cmd");
    assert.deepEqual(cmds.map((m) => [m.id, m.verb]), [[1, "clip"], [2, "nope"]]);
  } finally {
    c.stop();
    await srv.close();
  }
});

test("cmd: no ack in time resolves with a timeout error", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.welcome(m);
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast, ackTimeoutMs: 80 });
  c.start();
  try {
    await connected(c);
    assert.deepEqual(await c.send("clip"), { ok: false, error: "timeout" });
  } finally {
    c.stop();
    await srv.close();
  }
});

test("send while not connected fails fast", async () => {
  const c = new ClinkClient({ scan: scanOf([]), ...fast });
  assert.deepEqual(await c.send("clip"), { ok: false, error: "notConnected" });
});

test("bye session: the next pipe is tried", async () => {
  const wrong = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.send({ t: "bye", reason: "session" });
  });
  const right = await goodServer();
  const c = new ClinkClient({ scan: scanOf([wrong.path, right.path]), ...fast });
  c.start();
  try {
    await connected(c);
    assert.equal(wrong.conns.length, 1);
    assert.equal(right.conns.length, 1);
  } finally {
    c.stop();
    await wrong.close();
    await right.close();
  }
});

test("bye proto: updatePlugin, then the normal backoff (not a long park)", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.send({ t: "bye", reason: "proto", max: 0 });
  });
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({ scan: scanOf([srv.path]), sleep: clock.fn, ...fast });
  c.start();
  try {
    await waitFor(() => c.connectionStatus === "updatePlugin" && clock.delays.length === 1, "updatePlugin");
    assert.deepEqual(clock.delays, [500]);
    assert.equal(srv.conns.length, 1);
    clock.release();
    await waitFor(() => srv.conns.length === 2 && clock.delays.length === 2, "second attempt");
    assert.deepEqual(clock.delays, [500, 1000]);
    assert.equal(c.connectionStatus, "updatePlugin");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("reconnect backoff: 0.5, 1, 2, 5, 10 s, then stays at 10 s", async () => {
  let attempts = 0;
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({
    scan: scanOf([keyedPipe()]),
    sleep: clock.fn,
    ...fast,
    connect: () => {
      attempts++;
      return failingSocket("ENOENT");
    },
  });
  c.start();
  try {
    for (let i = 1; i <= 7; i++) {
      await waitFor(() => clock.delays.length === i, `wait ${i}`);
      assert.equal(attempts, i);
      clock.release();
    }
    assert.deepEqual(clock.delays, [500, 1000, 2000, 5000, 10000, 10000, 10000]);
    assert.equal(c.connectionStatus, "off");
  } finally {
    c.stop();
  }
});

test("backoff restarts after a successful connection", async () => {
  const srv = await goodServer();
  let down = true;
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({
    scan: scanOf([srv.path]),
    sleep: clock.fn,
    connect: (p) => (down ? failingSocket("ENOENT") : net.connect(p)),
    ...fast,
  });
  c.start();
  try {
    await waitFor(() => clock.delays.length === 1, "wait 1");
    clock.release();
    await waitFor(() => clock.delays.length === 2, "wait 2");
    down = false;
    clock.release();
    await connected(c);
    srv.conns[0]!.socket.destroy();
    await waitFor(() => clock.delays.length === 3, "wait after drop");
    assert.deepEqual(clock.delays, [500, 1000, 500]);
  } finally {
    c.stop();
    await srv.close();
  }
});

test("rescan: 2 s, backing off to 10 s, while Clink is off", async () => {
  let scans = 0;
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({ scan: async () => (scans++, { remote: [], control: [] }), sleep: clock.fn });
  c.start();
  try {
    for (let i = 1; i <= 6; i++) {
      await waitFor(() => clock.delays.length === i, `wait ${i}`);
      assert.equal(scans, i);
      clock.release();
    }
    assert.deepEqual(clock.delays, [2000, 4000, 8000, 10000, 10000, 10000]);
    assert.equal(c.connectionStatus, "off");
  } finally {
    c.stop();
  }
});
test("busy (connect never completes) is told apart from off (nothing listed)", async () => {
  const busy = new ClinkClient({ scan: scanOf([keyedPipe()]), connect: () => new net.Socket(), ...fast, connectTimeoutMs: 40 });
  busy.start();
  try {
    await waitFor(() => busy.connectionStatus === "busy", "busy");
  } finally {
    busy.stop();
  }
  const busy2 = new ClinkClient({ scan: scanOf([keyedPipe()]), connect: () => failingSocket("EBUSY"), ...fast });
  busy2.start();
  try {
    await waitFor(() => busy2.connectionStatus === "busy", "busy via EBUSY");
  } finally {
    busy2.stop();
  }
  const off = new ClinkClient({ scan: scanOf([]) });
  off.start();
  try {
    await waitFor(() => off.connectionStatus === "off" && true, "off");
    assert.equal(off.connectionStatus, "off");
  } finally {
    off.stop();
  }
  const gone = new ClinkClient({ scan: scanOf([keyedPipe()]), connect: () => failingSocket("ENOENT"), ...fast });
  gone.start();
  try {
    await waitFor(() => gone.connectionStatus === "off" && true, "off after ENOENT");
  } finally {
    gone.stop();
  }
});

test("a line over the cap drops the connection", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => {
      if (m.t === "hello") {
        c.welcome(m);
        c.raw("x".repeat(MAX_LINE_BYTES + 10) + "\n");
      }
    };
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast });
  const seen = statuses(c);
  c.start();
  try {
    await waitFor(() => seen.includes("connected"), "connected");
    await waitFor(() => srv.conns[0]!.closed, "server saw close");
    await waitFor(() => c.connectionStatus !== "connected", "left connected");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("keepalive: pings go out; a link that stops answering is dropped", async () => {
  let answer = true;
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => {
      if (m.t === "hello") c.welcome(m);
      else if (m.t === "ping" && answer) c.send({ t: "pong" });
    };
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast, pingIntervalMs: 40, deadAfterMs: 120 });
  c.start();
  try {
    await connected(c);
    await waitFor(() => srv.conns[0]!.received.filter((m) => m.t === "ping").length >= 3, "3 pings");
    assert.equal(c.connectionStatus, "connected");
    answer = false;
    await waitFor(() => c.connectionStatus !== "connected", "dead link noticed");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("a dropped connection is re-established", async () => {
  const srv = await goodServer();
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast });
  c.start();
  try {
    await connected(c);
    srv.conns[0]!.socket.destroy();
    await waitFor(() => c.connectionStatus !== "connected", "left connected");
    await waitFor(() => srv.conns.length === 2 && c.connectionStatus === "connected", "reconnected", 4000);
  } finally {
    c.stop();
    await srv.close();
  }
});

test("pending commands fail with disconnected when the link drops", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && c.welcome(m);
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast, ackTimeoutMs: 5000 });
  c.start();
  try {
    await connected(c);
    const p = c.send("clip");
    await waitFor(() => srv.conns[0]!.received.some((m) => m.t === "cmd"), "cmd arrived");
    srv.conns[0]!.socket.destroy();
    assert.deepEqual(await p, { ok: false, error: "disconnected" });
  } finally {
    c.stop();
    await srv.close();
  }
});

test("connect errors: only EBUSY is busy; EPIPE and ETIMEDOUT are failed (off)", async () => {
  for (const code of ["EPIPE", "ETIMEDOUT", "ECONNREFUSED"]) {
    const c = new ClinkClient({ scan: scanOf([keyedPipe()]), connect: () => failingSocket(code), ...fast });
    const seen = statuses(c);
    c.start();
    try {
      await waitFor(() => seen.includes("connecting") && c.connectionStatus === "off", `off after ${code}`);
      assert.ok(!seen.includes("busy"), code);
    } finally {
      c.stop();
    }
  }
});

test("stop() destroys a socket that is still in its handshake", async () => {
  const srv = await fakeServer((c) => {
    c.onMessage = () => {}; // never welcomes
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast, helloTimeoutMs: 30_000 });
  c.start();
  try {
    await waitFor(() => srv.conns[0]?.received.some((m) => m.t === "hello") === true, "hello sent");
    c.stop();
    await waitFor(() => srv.conns[0]!.closed, "server saw the socket close");
    assert.equal(c.connectionStatus, "off");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("stop() then start(): one loop only, and the old session cannot report connected", async () => {
  // One loop: scans happen only for the live generation.
  let scans = 0;
  const clock = new FakeSleep(fast.keyRetryMs);
  const a = new ClinkClient({ scan: async () => (scans++, { remote: [], control: [] }), sleep: clock.fn });
  a.start();
  await waitFor(() => clock.delays.length === 1, "first loop asleep");
  a.stop();
  a.start();
  await waitFor(() => clock.delays.length === 2, "second loop asleep");
  clock.release();
  await waitFor(() => clock.delays.length === 3, "second loop ran again");
  await new Promise((r) => setTimeout(r, 30));
  a.stop();
  assert.equal(scans, 3, "two loops would have scanned more often");

  // A session that welcomes late (after stop+start) must not surface.
  const srv = await fakeServer((c) => {
    c.onMessage = (m) => m.t === "hello" && setTimeout(() => !c.socket.destroyed && c.welcome(m), 150);
  });
  const c = new ClinkClient({ scan: scanOf([srv.path]), ...fast, helloTimeoutMs: 5000 });
  const seen = statuses(c);
  let welcomes = 0;
  c.on("welcome", () => welcomes++);
  c.start();
  try {
    await waitFor(() => srv.conns.length === 1 && srv.conns[0]!.received.length === 1, "first hello");
    c.stop();
    c.start();
    await waitFor(() => srv.conns.length === 2, "second connection");
    await waitFor(() => c.connectionStatus === "connected", "connected via the new generation");
    assert.equal(welcomes, 1);
    assert.equal(seen.filter((s) => s === "connected").length, 1);
    assert.ok(srv.conns[0]!.closed, "old socket was destroyed");
  } finally {
    c.stop();
    await srv.close();
  }
});

test("updateClink needs two consecutive Control-only scans", async () => {
  const lists = [
    { remote: [], control: ["\\\\.\\pipe\\Clink.Control.s1"] },
    { remote: [], control: [] }, // Clink finished starting or went away: the count restarts
    { remote: [], control: ["\\\\.\\pipe\\Clink.Control.s1"] },
    { remote: [], control: ["\\\\.\\pipe\\Clink.Control.s1"] },
  ];
  let i = 0;
  const clock = new FakeSleep(fast.keyRetryMs);
  const c = new ClinkClient({ scan: async () => lists[Math.min(i++, lists.length - 1)]!, sleep: clock.fn });
  const seen = statuses(c);
  c.start();
  try {
    for (let n = 1; n <= 3; n++) {
      await waitFor(() => clock.delays.length === n, `scan ${n}`);
      assert.ok(!seen.includes("updateClink"), `not after scan ${n}`);
      clock.release();
    }
    await waitFor(() => c.connectionStatus === "updateClink", "updateClink after two in a row");
  } finally {
    c.stop();
  }
});
