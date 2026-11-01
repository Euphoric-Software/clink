import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { test } from "node:test";
import { Pause } from "../src/actions/pause.ts";
import { RecordKey } from "../src/actions/record.ts";
import { SaveClip } from "../src/actions/save-clip.ts";
import { Screenshot } from "../src/actions/screenshot.ts";
import { ShareLast } from "../src/actions/share-last.ts";
import type { ClinkClient } from "../src/pipe-client.ts";
import type { ClinkState } from "../src/protocol.ts";
import { STATE } from "./helpers.ts";

const { t: _t, ...BASE } = STATE;
const stateOf = (state: ClinkState["state"], recOn = false): ClinkState => ({ ...BASE, state, rec: { ...BASE.rec, on: recOn } }) as ClinkState;

class FakeClient extends EventEmitter {
  state: ClinkState | null = null;
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
  constructor(id: string) {
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
  showOk = () => this.rec("ok");
  showAlert = () => this.rec("alert");
}

function make<T extends object>(Ctor: new (c: ClinkClient) => T, keys: FakeKey[] = []) {
  const client = new FakeClient();
  const action = new Ctor(client as unknown as ClinkClient);
  Object.defineProperty(action, "actions", { get: () => keys });
  const press = (k: FakeKey) => (action as any).onKeyDown({ action: k });
  const appear = (k: FakeKey) => (action as any).onWillAppear({ action: k });
  return { client, action, press, appear };
}

test("each action sends its verb", async () => {
  const cases: [new (c: ClinkClient) => object, string][] = [
    [SaveClip, "clip"],
    [RecordKey, "record"],
    [ShareLast, "share"],
    [Screenshot, "screenshot"],
    [Pause, "pause"],
  ];
  for (const [Ctor, verb] of cases) {
    const { client, press } = make(Ctor);
    const key = new FakeKey("k");
    await press(key);
    assert.deepEqual(client.sent, [verb], Ctor.name);
    assert.deepEqual(key.calls, ["ok"]);
  }
});

test("pause sends resume while Clink is paused", async () => {
  const { client, press } = make(Pause);
  client.state = stateOf("paused");
  await press(new FakeKey("k"));
  assert.deepEqual(client.sent, ["resume"]);
});

test("a failed send (Clink off) shows the alert", async () => {
  const { client, press } = make(RecordKey);
  client.ok = false;
  const key = new FakeKey("k");
  await press(key);
  assert.deepEqual(key.calls, ["alert"]);
});

test("a rejected showOk/showAlert/setState does not throw or leave a rejection", async () => {
  const key = new FakeKey("k");
  key.reject = true;
  const { client, press, appear } = make(Pause, [key]);
  await press(key);
  client.ok = false;
  await press(key);
  appear(key);
  client.emit("state");
  await new Promise((r) => setImmediate(r));
  assert.ok(key.calls.length >= 3);
});

test("a rejected setState is retried on the next state line", async () => {
  const k = new FakeKey("k");
  k.reject = true;
  const rec = make(RecordKey, [k]);
  rec.appear(k);
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(k.calls, ["state0"]);
  rec.client.emit("state");
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(k.calls, ["state0", "state0"], "tried again after the failure");
  k.reject = false;
  rec.client.emit("state");
  await new Promise((r) => setImmediate(r));
  rec.client.emit("state");
  assert.deepEqual(k.calls, ["state0", "state0", "state0"], "and stops once it succeeds");
});

test("record and pause follow Clink's state through the State index, once per change", async () => {
  const k = new FakeKey("k");
  const rec = make(RecordKey, [k]);
  rec.appear(k);
  assert.deepEqual(k.calls, ["state0"]);
  rec.client.state = stateOf("recording", true);
  rec.client.emit("state");
  rec.client.emit("state");
  rec.client.emit("status");
  assert.deepEqual(k.calls, ["state0", "state1"], "no repeat setState for the same index");
  rec.client.state = null;
  rec.client.emit("status");
  assert.deepEqual(k.calls, ["state0", "state1", "state0"]);

  const p = new FakeKey("p");
  const pause = make(Pause, [p]);
  pause.client.state = stateOf("paused");
  pause.client.emit("state");
  assert.deepEqual(p.calls, ["state1"]);
  pause.client.state = stateOf("idle");
  pause.client.emit("state");
  assert.deepEqual(p.calls, ["state1", "state0"]);
});