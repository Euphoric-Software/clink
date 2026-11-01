import { createHmac, randomBytes } from "node:crypto";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import net from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { type ClinkClient, type ClientOptions } from "../src/pipe-client.ts";
import { LineSplitter } from "../src/protocol.ts";

/** Unique fake pipe in the dev name form, so it passes the plugin's pipe name check. */
export const pipePath = (): string => `\\\\.\\pipe\\Clink.Remote.dev.${randomBytes(4).toString("hex")}.s1`;
export const nameOf = (p: string): string => p.slice("\\\\.\\pipe\\".length);

/** Key folder shared by the tests of one file. */
export const KEYDIR = mkdtempSync(path.join(tmpdir(), "clink-sd-keys-"));
mkdirSync(KEYDIR, { recursive: true });
process.on("exit", () => rmSync(KEYDIR, { recursive: true, force: true }));

/** Independent copy of the proof formula (the tests must not reuse the code under test). */
export function proofOf(key: Buffer, pipeName: string, nonce: Buffer): string {
  return createHmac("sha256", key)
    .update(Buffer.concat([Buffer.from("clink-remote-proof-v1"), Buffer.from([0]), Buffer.from(pipeName), Buffer.from([0]), nonce]))
    .digest("base64");
}

export const WELCOME = { t: "welcome", proto: 1, app: "Clink", version: "9.9.9", dev: true, elevated: false, verbs: ["clip", "record"] };
export const STATE = {
  t: "state",
  state: "recording",
  rec: { on: true, s: 12, resuming: false, queued: false, finishing: false },
  bufferS: 30,
  saving: false,
  screenshots: true,
  cloud: false,
  signedIn: false,
  upload: { on: false, p: 0 },
  updatePending: false,
  elevated: false,
};

export class Conn {
  readonly received: Record<string, unknown>[] = [];
  closed = false;
  private readonly splitter = new LineSplitter();
  onMessage: (m: Record<string, unknown>) => void = () => {};
  readonly socket: net.Socket;
  /** Set by fakeServer: the pipe name and key this server proves with. */
  pipeName = "";
  key: Buffer = Buffer.alloc(32);
  constructor(socket: net.Socket) {
    this.socket = socket;
    socket.on("data", (d) => {
      for (const l of this.splitter.push(d)) {
        let m: Record<string, unknown>;
        try {
          m = JSON.parse(l) as Record<string, unknown>;
        } catch {
          m = { raw: l }; // a legacy verb line, not JSON
        }
        this.received.push(m);
        this.onMessage(m);
      }
    });
    socket.on("close", () => (this.closed = true));
    socket.on("error", () => {});
  }
  /** Sends a welcome with a correct proof for this hello's nonce (fields in `over` win; `over.proof` undefined removes it). */
  welcome(hello: Record<string, unknown>, over: Record<string, unknown> = {}): void {
    const nonce = Buffer.from(String(hello.nonce ?? ""), "base64");
    this.send({ ...WELCOME, proof: proofOf(this.key, this.pipeName, nonce), ...over });
  }
  send(m: unknown): void {
    this.socket.write(JSON.stringify(m) + "\n");
  }
  raw(s: string): void {
    this.socket.write(s);
  }
}

export type FakeServer = { path: string; name: string; key: Buffer; keyFile: string; conns: Conn[]; close: () => Promise<void> };

/** A fake Clink on a unique pipe. `onConn` decides how it behaves. */
export async function fakeServer(onConn: (c: Conn) => void, opts: { writeKey?: boolean } = {}): Promise<FakeServer> {
  const pipe = pipePath();
  const name = nameOf(pipe);
  const key = randomBytes(32);
  const keyFile = path.join(KEYDIR, name + ".key");
  if (opts.writeKey !== false) writeFileSync(keyFile, key.toString("base64"));
  const conns: Conn[] = [];
  const sockets = new Set<net.Socket>();
  const server = net.createServer((s) => {
    sockets.add(s);
    s.on("close", () => sockets.delete(s));
    const c = new Conn(s);
    c.pipeName = name;
    c.key = key;
    conns.push(c);
    onConn(c);
  });
  await new Promise<void>((res) => server.listen(pipe, res));
  return {
    path: pipe,
    name,
    key,
    keyFile,
    conns,
    close: () =>
      new Promise<void>((res) => {
        for (const s of sockets) s.destroy();
        server.close(() => res());
      }),
  };
}

/** Well-behaved server: welcome + state after hello, pong, ack ok (or the error `ackError` returns for a verb). */
export function goodServer(ackError: (verb: string) => string | undefined = () => undefined): Promise<FakeServer> {
  return fakeServer((c) => {
    c.onMessage = (m) => {
      if (m.t === "hello") {
        c.welcome(m);
        c.send(STATE);
      } else if (m.t === "ping") c.send({ t: "pong" });
      else if (m.t === "cmd") {
        const error = ackError(String(m.verb));
        c.send(error ? { t: "ack", id: m.id, ok: false, error } : { t: "ack", id: m.id, ok: true });
      }
    };
  });
}

export async function waitFor(cond: () => boolean, what = "condition", ms = 3000): Promise<void> {
  const end = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > end) throw new Error("timed out waiting for " + what);
    await new Promise((r) => setImmediate(r));
  }
}

export const scanOf = (remote: string[], control: string[] = []): ClientOptions["scan"] => async () => ({ remote, control });

/** A fresh pipe path in the dev name form whose key file already exists (for tests that fake the socket instead of listening). */
export function keyedPipe(): string {
  const p = pipePath();
  writeFileSync(path.join(KEYDIR, nameOf(p) + ".key"), randomBytes(32).toString("base64"));
  return p;
}

/** A socket whose connect fails right away with the given errno code. */
export function failingSocket(code: string): net.Socket {
  const s = new net.Socket();
  process.nextTick(() => s.destroy(Object.assign(new Error(code), { code })));
  return s;
}

export const statuses = (c: ClinkClient): string[] => {
  const list: string[] = [];
  c.on("status", (s) => list.push(s));
  return list;
};
/**
 * Fake clock for the gaps between scans and reconnects: records each requested delay; `release()` ends the current wait.
 * Waits of exactly `retryMs` (the silent key retry) are kept apart in `retries` and pass at once, or wait for `releaseRetry()` when `holdRetry` is set.
 */
export class FakeSleep {
  readonly delays: number[] = [];
  readonly retries: number[] = [];
  holdRetry = false;
  private current: (() => void) | null = null;
  private heldRetry: (() => void) | null = null;
  private readonly retryMs: number;
  constructor(retryMs = -1) {
    this.retryMs = retryMs;
  }
  releaseRetry(): void {
    const r = this.heldRetry;
    this.heldRetry = null;
    r?.();
  }
  get retryHeld(): boolean {
    return this.heldRetry !== null;
  }
  readonly fn = (ms: number, signal: AbortSignal): Promise<void> => {
    if (ms === this.retryMs) {
      this.retries.push(ms);
      if (!this.holdRetry) return Promise.resolve();
      return new Promise((resolve) => {
        this.heldRetry = resolve;
        signal.addEventListener("abort", () => resolve(), { once: true });
      });
    }
    this.delays.push(ms);
    return new Promise((resolve) => {
      this.current = resolve;
      signal.addEventListener("abort", () => resolve(), { once: true });
    });
  };
  release(): void {
    const r = this.current;
    this.current = null;
    r?.();
  }
}