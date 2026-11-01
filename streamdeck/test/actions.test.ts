import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { test } from "node:test";
import { type ActionEnv } from "../src/actions/base.ts";
import { Pause } from "../src/actions/pause.ts";
import { RecordKey } from "../src/actions/record.ts";
import { SaveClip } from "../src/actions/save-clip.ts";
import { Screenshot } from "../src/actions/screenshot.ts";
import { ShareLast } from "../src/actions/share-last.ts";
import type { ClinkClient, ConnectionStatus } from "../src/pipe-client.ts";
import type { ClinkState } from "../src/protocol.ts";
import { KeyUpdateQueue } from "../src/throttle.ts";
import { STATE } from "./helpers.ts";

const { t: _t, ...BASE } = STATE;
const stateOf = (state: ClinkState["state"], over: Partial<ClinkState> = {}): ClinkState => ({ ...BASE, state, rec: { ...BASE.rec, on: false }, ...over }) as ClinkState;

class FakeClient extends EventEmitter {
  state: ClinkState | null = stateOf("recording");
  connectionStatus: ConnectionStatus = "connected";
  ok = true;
  readonly sent: string[] = [];
  async send(verb: string): Promise<{ ok: boolean; error?: string }> {
    this.sent.push(verb);
    return this.ok ? { ok: true } : { ok: false, error: "notConnected" };
  }
}

class FakeKey {
  readonly calls: string[] = [];
  reject = false;
  readonly id: string;
  constructor(id = "k") {
    this.id = id;
  }
  isKey(): boolean {
    return true;
  }
  private rec(call: string): Promise<void> {
    this.calls.push(call);
    return this.reject ? Promise.reject(new Error("boom")) : Promise.resolve();
  }
  setState = (i: number) => this.rec(`state${i}`);
  setImage = (url: string) => this.rec("img:" + Buffer.from(url.split(",")[1]!, "base64").toString("utf8"));
  showOk = () => this.rec("ok");
  showAlert = () => this.rec("alert");
  setTitle = () => this.rec("TITLE");
  /** The SVG of the last setImage call. */
  get lastSvg(): string {
    return [...this.calls].reverse().find((c) => c.startsWith("img:"))?.slice(4) ?? "";
  }
}

const settle = async (): Promise<void> => {
  for (let i = 0; i < 4; i++) await new Promise((r) => setImmediate(r));
};
const texts = (svg: string): string[] => [...svg.matchAll(/<text [^>]*>([^<]*)<\/text>/g)].map((m) => m[1]!);

/** Fake timers: fire(ms) runs the timers that fall due. */
function fakeEnv() {
  const timers: { id: number; at: number; fn: () => void }[] = [];
  let now = 0;
  let next = 1;
  const env: ActionEnv = {
    queue: new KeyUpdateQueue({ now: () => 0, sleep: () => new Promise(() => {}), perSecond: 1000 }),
    setTimer: (fn, ms) => (timers.push({ id: next, at: now + ms, fn }), next++),
    clearTimer: (h) => void timers.splice(0, timers.length, ...timers.filter((t) => t.id !== h)),
  };
  return {
    env,
    get timers() {
      return timers.length;
    },
    async advance(ms: number) {
      now += ms;
      for (const t of timers.filter((x) => x.at <= now).sort((a, b) => a.at - b.at)) {
        timers.splice(timers.indexOf(t), 1);
        t.fn();
      }
      await settle();
    },
  };
}

function make<T extends object>(Ctor: new (c: ClinkClient, e: ActionEnv) => T, keys: FakeKey[] = []) {
  const client = new FakeClient();
  const fe = fakeEnv();
  const action = new Ctor(client as unknown as ClinkClient, fe.env);
  Object.defineProperty(action, "actions", { get: () => keys });
  const press = (k: FakeKey) => (action as any).onKeyDown({ action: k });
  const appear = (k: FakeKey) => (action as any).onWillAppear({ action: k });
  const vanish = (k: FakeKey) => (action as any).onWillDisappear({ action: k });
  const ev = (e: object) => client.emit("event", e);
  return { client, action, press, appear, vanish, ev, fe };
}

test("each action sends its verb", async () => {
  const cases: [new (c: ClinkClient, e: ActionEnv) => object, string][] = [
    [SaveClip, "clip"],
    [RecordKey, "record"],
    [ShareLast, "share"],
    [Screenshot, "screenshot"],
    [Pause, "pause"],
  ];
  for (const [Ctor, verb] of cases) {
    const { client, press } = make(Ctor);
    const key = new FakeKey();
    await press(key);
    assert.deepEqual(client.sent, [verb], Ctor.name);
    assert.deepEqual(key.calls, ["ok"]);
  }
});

test("pause sends resume while Clink is paused", async () => {
  const { client, press } = make(Pause);
  client.state = stateOf("paused");
  await press(new FakeKey());
  assert.deepEqual(client.sent, ["resume"]);
});

test("a failed send shows the alert", async () => {
  const { client, press } = make(RecordKey);
  client.ok = false;
  const key = new FakeKey();
  await press(key);
  assert.deepEqual(key.calls, ["alert"]);
});

test("a press while off, busy, connecting or in need of an update only alerts and sends nothing", async () => {
  for (const status of ["off", "busy", "connecting", "updateClink", "updatePlugin"] as const) {
    const { client, press } = make(SaveClip);
    client.connectionStatus = status;
    const key = new FakeKey();
    await press(key);
    assert.deepEqual(client.sent, [], status);
    assert.deepEqual(key.calls, ["alert"], status);
  }
});

test("a rejected showOk/showAlert/setState/setImage does not throw or leave a rejection", async () => {
  const key = new FakeKey();
  key.reject = true;
  const { client, press, appear } = make(Pause, [key]);
  await press(key);
  client.ok = false;
  await press(key);
  appear(key);
  client.emit("state");
  await settle();
  assert.ok(key.calls.length >= 4);
});

test("a key's first picture comes on appear; the State is set before the picture, and setTitle is never called", async () => {
  const k = new FakeKey();
  const rec = make(RecordKey, [k]);
  rec.appear(k);
  await settle();
  assert.equal(k.calls[0], "state0");
  assert.deepEqual(texts(k.lastSvg), ["Record"]);
  rec.client.state = stateOf("recording", { rec: { on: true, s: 5, resuming: false, queued: false, finishing: false } });
  rec.client.emit("state");
  await settle();
  assert.ok(k.calls.includes("state1"));
  assert.deepEqual(texts(k.lastSvg), ["00:05"]);
  assert.ok(!k.calls.includes("TITLE"));
});

test("the same state again causes no call; a changed timer sends one picture", async () => {
  const k = new FakeKey();
  const rec = make(RecordKey, [k]);
  const on = (s: number) => stateOf("recording", { rec: { on: true, s, resuming: false, queued: false, finishing: false } });
  rec.client.state = on(1);
  rec.appear(k);
  await settle();
  const n = k.calls.length;
  rec.client.emit("state");
  rec.client.emit("status");
  await settle();
  assert.equal(k.calls.length, n);
  rec.client.state = on(2);
  rec.client.emit("state");
  await settle();
  assert.equal(k.calls.length, n + 1);
});

test("record and pause follow Clink's state through the State index", async () => {
  const k = new FakeKey();
  const rec = make(RecordKey, [k]);
  rec.appear(k);
  await settle();
  rec.client.state = stateOf("recording", { rec: { on: true, s: 0, resuming: false, queued: false, finishing: false } });
  rec.client.emit("state");
  await settle();
  rec.client.state = null;
  rec.client.connectionStatus = "off";
  rec.client.emit("status");
  await settle();
  assert.deepEqual(k.calls.filter((c) => c.startsWith("state")), ["state0", "state1", "state0"]);
  assert.deepEqual(texts(k.lastSvg), ["Clink off"]);

  const p = new FakeKey();
  const pause = make(Pause, [p]);
  pause.client.state = stateOf("paused");
  pause.client.emit("state");
  await settle();
  assert.deepEqual(p.calls.filter((c) => c.startsWith("state")), ["state1"]);
  assert.deepEqual(texts(p.lastSvg), ["Resume"]);
  pause.client.state = stateOf("recording");
  pause.client.emit("state");
  await settle();
  assert.deepEqual(p.calls.filter((c) => c.startsWith("state")), ["state1", "state0"]);
  assert.deepEqual(texts(p.lastSvg), ["Pause"]);
});

test("every connection status draws its overlay on every key", async () => {
  const want: [ConnectionStatus, string[]][] = [
    ["off", ["Clink off"]],
    ["updateClink", ["Update", "Clink"]],
    ["updatePlugin", ["Update", "plugin"]],
    ["busy", ["Busy"]],
  ];
  for (const [status, expect] of want) {
    const k = new FakeKey();
    const a = make(Screenshot, [k]);
    a.client.connectionStatus = status;
    a.client.state = null;
    a.client.emit("status");
    await settle();
    assert.deepEqual(texts(k.lastSvg), expect, status);
  }
});

test("save clip: Saved flash for 1.5 s on clipSaved, then back to normal", async () => {
  const k = new FakeKey();
  const a = make(SaveClip, [k]);
  a.appear(k);
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["30s"]);
  a.ev({ t: "clipSaved", seconds: 30 });
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["Saved"]);
  await a.fe.advance(1499);
  assert.deepEqual(texts(k.lastSvg), ["Saved"], "still shown just before 1.5 s");
  await a.fe.advance(1);
  assert.notDeepEqual(texts(k.lastSvg), ["Saved"]);
  assert.deepEqual(texts(k.lastSvg), ["30s"]);
  assert.equal(a.fe.timers, 0);
});

test("save clip: a second clipSaved restarts the flash timer; clipNotSaved alerts and does not flash", async () => {
  const k = new FakeKey();
  const a = make(SaveClip, [k]);
  a.appear(k);
  a.ev({ t: "clipSaved", seconds: 30 });
  await a.fe.advance(1000);
  a.ev({ t: "clipSaved", seconds: 30 });
  await a.fe.advance(1000);
  assert.deepEqual(texts(k.lastSvg), ["Saved"], "restarted: 2 s after the first, 1 s after the second");
  await a.fe.advance(500);
  assert.notDeepEqual(texts(k.lastSvg), ["Saved"]);
  const before = k.calls.length;
  a.ev({ t: "clipNotSaved", reason: "paused" });
  await settle();
  assert.deepEqual(k.calls.slice(before), ["alert"]);
});

test("share: progress ring from shareProgress, cleared on shareDone", async () => {
  const k = new FakeKey();
  const a = make(ShareLast, [k]);
  a.appear(k);
  a.ev({ t: "shareProgress", p: 40 });
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["40%", "Sharing"]);
  a.ev({ t: "shareProgress", p: 80 });
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["80%", "Sharing"]);
  a.ev({ t: "shareDone", outcome: "declined" });
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["Share"]);
  assert.ok(!k.calls.includes("alert"));
});

test("share: Link copied and Opened for 2 s; failed, refused and noClip alert", async () => {
  for (const [outcome, label] of [["linkCopied", ["Link", "copied"]], ["uploaded", ["Link", "copied"]], ["fileShareOpened", ["Opened"]]] as const) {
    const k = new FakeKey();
    const a = make(ShareLast, [k]);
    a.appear(k);
    a.ev({ t: "shareDone", outcome });
    await settle();
    assert.deepEqual(texts(k.lastSvg), [...label], outcome);
    await a.fe.advance(1999);
    assert.deepEqual(texts(k.lastSvg), [...label], outcome + " 1999 ms");
    await a.fe.advance(1);
    assert.deepEqual(texts(k.lastSvg), ["Share"], outcome + " done");
  }
  for (const outcome of ["failed", "refused", "noClip"]) {
    const k = new FakeKey();
    const a = make(ShareLast, [k]);
    a.appear(k);
    await settle();
    a.ev({ t: "shareDone", outcome });
    await settle();
    assert.ok(k.calls.includes("alert"), outcome);
    assert.deepEqual(texts(k.lastSvg), ["Share"], outcome);
  }
});

test("share: a lost connection forgets the progress", async () => {
  const k = new FakeKey();
  const a = make(ShareLast, [k]);
  a.appear(k);
  a.ev({ t: "shareProgress", p: 50 });
  a.client.connectionStatus = "off";
  a.client.emit("status");
  a.client.connectionStatus = "connected";
  a.client.emit("status");
  await settle();
  assert.deepEqual(texts(k.lastSvg), ["Share"]);
});

test("share and screenshot are dimmed from the state flags", async () => {
  const k = new FakeKey();
  const a = make(ShareLast, [k]);
  a.client.state = stateOf("recording", { cloud: false });
  a.appear(k);
  await settle();
  assert.ok(k.lastSvg.includes('<g opacity="0.35">'));
  const s = new FakeKey();
  const b = make(Screenshot, [s]);
  b.client.state = stateOf("recording", { screenshots: false });
  b.appear(s);
  await settle();
  assert.ok(s.lastSvg.includes('<g opacity="0.35">'));
});

test("a key that disappears is dropped from the queue", async () => {
  const k = new FakeKey();
  const a = make(SaveClip, [k]);
  a.appear(k);
  a.vanish(k);
  a.appear(k);
  await settle();
  assert.equal(k.calls.filter((c) => c.startsWith("img:")).length, 2, "after the drop the same picture is sent again");
});

test("a rejected setImage is retried on the next state line", async () => {
  const k = new FakeKey();
  k.reject = true;
  const a = make(SaveClip, [k]);
  a.appear(k);
  await settle();
  assert.equal(k.calls.length, 1);
  a.client.emit("state");
  await settle();
  assert.equal(k.calls.length, 2, "tried again after the failure");
  k.reject = false;
  a.client.emit("state");
  await settle();
  a.client.emit("state");
  await settle();
  assert.equal(k.calls.length, 3, "and stops once it succeeds");
});