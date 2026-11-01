// One global queue for key updates. Elgato asks plugins for at most 10 programmatic key updates per second; this keeps to that
// across ALL keys: latest wanted picture per key (older ones are dropped), nothing sent when the key already shows it,
// and a failed SDK call forgets what the key shows so the next submit sends it again.
import { toDataUrl } from "./render.ts";

/** What the queue needs of a key (the SDK's KeyAction has these). */
export interface KeyTarget {
  readonly id: string;
  setImage(image: string): Promise<void> | void;
  setState(state: number): Promise<void> | void;
}

export type Want = {
  /** The SVG text. Encoded as a data URL only when it is sent. */
  svg: string;
  /** Manifest State to select first (two-state actions); undefined: leave the State alone. */
  state?: number;
};

export type QueueOptions = {
  perSecond?: number;
  windowMs?: number;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
};

type Shown = { svg?: string; state?: number };
type Entry = { key: KeyTarget; want: Want };

export const MAX_UPDATES_PER_SECOND = 10;

export class KeyUpdateQueue {
  private readonly perWindow: number;
  private readonly windowMs: number;
  private readonly now: () => number;
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly entries = new Map<string, Entry>();
  private readonly shown = new Map<string, Shown>();
  /** Keys with work to do, oldest first. */
  private readonly order = new Set<string>();
  /** Times (ms) of the calls made in the last window. */
  private readonly sent: number[] = [];
  private running = false;
  private done: Promise<void> = Promise.resolve();

  constructor(o: QueueOptions = {}) {
    this.perWindow = o.perSecond ?? MAX_UPDATES_PER_SECOND;
    this.windowMs = o.windowMs ?? 1000;
    this.now = o.now ?? (() => Date.now());
    this.sleep = o.sleep ?? ((ms) => new Promise((r) => setTimeout(r, ms)));
  }

  /** Keys waiting to be updated. */
  get pending(): number {
    return this.order.size;
  }

  /** Resolves when the queue is empty (tests await it). */
  idle(): Promise<void> {
    return this.done;
  }

  /** Wants this picture (and State) on the key. Replaces an older want of the same key that is still waiting. */
  submit(key: KeyTarget, want: Want): void {
    this.entries.set(key.id, { key, want });
    if (this.needs(key.id)) {
      this.order.add(key.id);
      if (!this.running) {
        this.running = true;
        this.done = this.pump();
      }
    }
  }

  /** The key went away (onWillDisappear): forget everything about it. */
  drop(id: string): void {
    this.entries.delete(id);
    this.shown.delete(id);
    this.order.delete(id);
  }

  private needs(id: string): boolean {
    const e = this.entries.get(id);
    if (!e) return false;
    const s = this.shown.get(id);
    return (e.want.state !== undefined && s?.state !== e.want.state) || s?.svg !== e.want.svg;
  }

  /** Time to wait before the next call is allowed (0: now). */
  private waitMs(): number {
    const t = this.now();
    while (this.sent.length > 0 && t - this.sent[0]! >= this.windowMs) this.sent.shift();
    return this.sent.length < this.perWindow ? 0 : this.sent[0]! + this.windowMs - t;
  }

  private async pump(): Promise<void> {
    try {
      for (;;) {
        for (const id of this.order) if (!this.needs(id)) this.order.delete(id); // done keys leave before any waiting
        if (this.order.size === 0) break;
        const wait = this.waitMs();
        if (wait > 0) {
          await this.sleep(wait);
          continue;
        }
        const id = this.order.values().next().value as string;
        this.sent.push(this.now());
        this.sendOne(this.entries.get(id)!);
      }
    } finally {
      this.running = false;
    }
  }

  /** One SDK call for the key (the State first, then the picture). The key stays queued until nothing is left to send. */
  private sendOne({ key, want }: Entry): void {
    const id = key.id;
    let s = this.shown.get(id);
    if (!s) this.shown.set(id, (s = {}));
    const fail = (field: "state" | "svg", value: unknown): void => {
      const cur = this.shown.get(id);
      if (cur && cur[field] === value) delete cur[field];
      this.order.delete(id); // no endless retry of a failing key: the next submit sends it again
    };
    if (want.state !== undefined && s.state !== want.state) {
      const state = want.state;
      s.state = state;
      delete s.svg; // a State change brings back that State's own image, so the picture is sent again after it
      this.call(() => key.setState(state), () => fail("state", state));
      return;
    }
    const svg = want.svg;
    s.svg = svg;
    this.call(() => key.setImage(toDataUrl(svg)), () => fail("svg", svg));
  }

  private call(fn: () => Promise<void> | void, onFail: () => void): void {
    try {
      void Promise.resolve(fn()).catch(onFail);
    } catch {
      onFail();
    }
  }
}
