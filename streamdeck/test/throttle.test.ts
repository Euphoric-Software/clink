import assert from "node:assert/strict";
import { test } from "node:test";
import { KeyUpdateQueue } from "../src/throttle.ts";

/** Fake time: sleep() waits until advance() has moved the clock far enough. */
class Clock {
  t = 0;
  private waits: { at: number; resolve: () => void }[] = [];
  readonly now = (): number => this.t;
  readonly sleep = (ms: number): Promise<void> => new Promise((resolve) => this.waits.push({ at: this.t + ms, resolve }));
  async advance(ms: number): Promise<void> {
    const end = this.t + ms;
    for (;;) {
      await settle();
      const due = this.waits.filter((w) => w.at <= end).sort((a, b) => a.at - b.at)[0];
      if (!due) break;
      this.t = Math.max(this.t, due.at);
      this.waits.splice(this.waits.indexOf(due), 1);
      due.resolve();
    }
    this.t = end;
    await settle();
  }
}
const settle = async (): Promise<void> => {
  for (let i = 0; i < 5; i++) await new Promise((r) => setImmediate(r));
};

class Key {
  readonly calls: string[] = [];
  fail = false;
  readonly id: string;
  constructor(id: string) {
    this.id = id;
  }
  private rec(c: string): Promise<void> {
    this.calls.push(c);
    return this.fail ? Promise.reject(new Error("boom")) : Promise.resolve();
  }
  setImage = (img: string) => this.rec("img:" + Buffer.from(img.split(",")[1]!, "base64").toString());
  setState = (i: number) => this.rec("state" + i);
}

const make = () => {
  const clock = new Clock();
  return { clock, q: new KeyUpdateQueue({ now: clock.now, sleep: clock.sleep }) };
};

test("sends the picture as a base64 SVG data URL", async () => {
  const { q } = make();
  const k = new Key("a");
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.deepEqual(k.calls, ["img:<svg>1</svg>"]);
});

test("at most 10 calls per second across all keys", async () => {
  const { q, clock } = make();
  const keys = Array.from({ length: 25 }, (_, i) => new Key("k" + i));
  keys.forEach((k, i) => q.submit(k, { svg: `<svg>${i}</svg>` }));
  await settle();
  const total = (): number => keys.reduce((n, k) => n + k.calls.length, 0);
  assert.equal(total(), 10, "first second: 10");
  await clock.advance(999);
  assert.equal(total(), 10, "still 10 just before the window ends");
  await clock.advance(1);
  assert.equal(total(), 20);
  await clock.advance(1000);
  assert.equal(total(), 25);
  assert.equal(q.pending, 0);
});

test("a State change and a picture are two calls (State first), both counted", async () => {
  const { q, clock } = make();
  const k = new Key("a");
  for (let i = 0; i < 6; i++) q.submit(new Key("x" + i), { svg: `<svg>${i}</svg>` }); // 6 calls
  q.submit(k, { svg: "<svg>a</svg>", state: 1 }); // 2 more
  await settle();
  assert.deepEqual(k.calls, ["state1", "img:<svg>a</svg>"]);
  q.submit(new Key("y"), { svg: "<svg>y</svg>" });
  q.submit(new Key("z"), { svg: "<svg>z</svg>" }); // the 9th and 10th... one more than allowed waits
  q.submit(new Key("w"), { svg: "<svg>w</svg>" });
  await settle();
  assert.equal(q.pending, 1);
  await clock.advance(1000);
  assert.equal(q.pending, 0);
});

test("coalesces to the latest picture per key while waiting", async () => {
  const { q, clock } = make();
  const fillers = Array.from({ length: 10 }, (_, i) => new Key("f" + i));
  fillers.forEach((k) => q.submit(k, { svg: "<svg>f</svg>" }));
  const k = new Key("a");
  q.submit(k, { svg: "<svg>1</svg>" });
  q.submit(k, { svg: "<svg>2</svg>" });
  q.submit(k, { svg: "<svg>3</svg>" });
  await settle();
  assert.deepEqual(k.calls, []);
  await clock.advance(1000);
  assert.deepEqual(k.calls, ["img:<svg>3</svg>"], "only the newest one is sent");
});

test("an unchanged picture is not sent again; a changed one is", async () => {
  const { q } = make();
  const k = new Key("a");
  q.submit(k, { svg: "<svg>1</svg>", state: 0 });
  await settle();
  q.submit(k, { svg: "<svg>1</svg>", state: 0 });
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.deepEqual(k.calls, ["state0", "img:<svg>1</svg>"]);
  q.submit(k, { svg: "<svg>2</svg>", state: 0 });
  await settle();
  assert.deepEqual(k.calls, ["state0", "img:<svg>1</svg>", "img:<svg>2</svg>"], "same State: no setState");
  q.submit(k, { svg: "<svg>2</svg>", state: 1 });
  await settle();
  assert.deepEqual(k.calls.slice(3), ["state1", "img:<svg>2</svg>"], "a State change re-sends the picture after it");
});

test("a key that disappeared is dropped from the queue and forgotten", async () => {
  const { q, clock } = make();
  Array.from({ length: 10 }, (_, i) => new Key("f" + i)).forEach((k) => q.submit(k, { svg: "<svg>f</svg>" }));
  const k = new Key("gone");
  q.submit(k, { svg: "<svg>1</svg>" });
  assert.equal(q.pending, 1);
  q.drop("gone");
  assert.equal(q.pending, 0);
  await clock.advance(2000);
  assert.deepEqual(k.calls, []);
  // Back again later: treated as new, so the same picture is sent.
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.deepEqual(k.calls, ["img:<svg>1</svg>"]);
});

test("a failed call clears the cache so the next submit sends again; no endless retry by itself", async () => {
  const { q, clock } = make();
  const k = new Key("a");
  k.fail = true;
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.equal(k.calls.length, 1);
  await clock.advance(5000);
  assert.equal(k.calls.length, 1, "it does not hammer a failing key");
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.equal(k.calls.length, 2, "same picture again after the failure");
  k.fail = false;
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.equal(k.calls.length, 3);
  q.submit(k, { svg: "<svg>1</svg>" });
  await settle();
  assert.equal(k.calls.length, 3, "and now it is deduped");
});

test("a failed setState is retried (with the picture) on the next submit", async () => {
  const { q } = make();
  const k = new Key("a");
  k.fail = true;
  q.submit(k, { svg: "<svg>1</svg>", state: 1 });
  await settle();
  k.fail = false;
  q.submit(k, { svg: "<svg>1</svg>", state: 1 });
  await settle();
  assert.deepEqual(k.calls.slice(-2), ["state1", "img:<svg>1</svg>"]);
});

test("a throwing or rejecting SDK call never escapes", async () => {
  const { q } = make();
  const bad = {
    id: "b",
    setImage: (): Promise<void> => {
      throw new Error("sync");
    },
    setState: (): Promise<void> => Promise.reject(new Error("async")),
  };
  q.submit(bad, { svg: "<svg>1</svg>", state: 1 });
  await settle();
  q.submit(bad, { svg: "<svg>2</svg>", state: 1 });
  await settle();
  assert.equal(q.pending, 0);
});