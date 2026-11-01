// Connection to Clink: finds the status pipe, proves the server with the key-file handshake, keeps it alive and reconnects.
import { randomBytes } from "node:crypto";
import { EventEmitter } from "node:events";
import net from "node:net";
import { uptime } from "node:os";
import { type Scan, EMPTY_SCAN } from "./discovery.ts";
import { type HandshakeFailure, NONCE_BYTES, checkProof, defaultKeyDir, pipeNameOf, readKey } from "./handshake.ts";
import {
  type Ack,
  type ClinkEvent,
  type ClinkState,
  type ServerMessage,
  type Welcome,
  LineSplitter,
  LineTooLongError,
  PROTO,
  cmd,
  hello,
  parseLine,
  ping,
} from "./protocol.ts";

export type ConnectionStatus = "off" | "connecting" | "connected" | "busy" | "updateClink" | "updatePlugin";

export type AckResult = { ok: boolean; error?: string };

export type ServerInfo = Pick<Welcome, "version" | "dev" | "elevated" | "verbs">;

export const BACKOFF_MS = [500, 1000, 2000, 5000, 10000];
export const RESCAN_START_MS = 2000;
export const RESCAN_MAX_MS = 10000;

export type ClientOptions = {
  /** Lists the pipes (see discovery.ts). */
  scan: () => Promise<Scan>;
  /** Opens a pipe (tests use real fake servers or a socket that never connects). */
  connect?: (path: string) => net.Socket;
  clientName?: string;
  clientVersion?: string;
  /** libuv would wait up to about 30 s on a busy pipe; this is ours. */
  connectTimeoutMs?: number;
  helloTimeoutMs?: number;
  ackTimeoutMs?: number;
  pingIntervalMs?: number;
  /** How long after a ping without any line from Clink the link counts as dead. */
  deadAfterMs?: number;
  /** One silent retry of a missing or stale key file after this long (Clink may still be starting). */
  keyRetryMs?: number;
  /** Where Clink writes <pipeName>.key. Default %LOCALAPPDATA%\Clink\remote. Tests inject a temp folder. */
  keyDir?: string;
  /** Clock for the key age check (ms since epoch). Tests inject one. */
  now?: () => number;
  /** Receives a handshake failure reason code (never the key or the proof). */
  log?: (reason: string) => void;
  /** Waits ms (the gaps between scans and reconnects); resolves early when the signal aborts. Tests bring a fake clock. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
};

const realSleep = (ms: number, signal: AbortSignal): Promise<void> =>
  new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => (clearTimeout(timer), resolve()), { once: true });
  });

type Outcome = "ended" | "session" | "proto" | "busy" | "failed";

/** Only this connect error means "the pipe exists but is occupied". Our own connect timeout is the other busy case. */
const BUSY_CODES = new Set(["EBUSY"]);
/** Consecutive scans listing only the old Control pipe before we say "update Clink" (one scan can catch a Clink that is starting). */
export const CONTROL_ONLY_SCANS = 2;

export interface ClinkClient {
  on(event: "status", l: (status: ConnectionStatus) => void): this;
  on(event: "state", l: (state: ClinkState) => void): this;
  on(event: "event", l: (ev: ClinkEvent) => void): this;
  on(event: "welcome", l: (info: ServerInfo) => void): this;
}

export class ClinkClient extends EventEmitter {
  private readonly o: Required<Omit<ClientOptions, "connect" | "sleep">> & Pick<ClientOptions, "connect" | "sleep">;
  private running = false;
  private status: ConnectionStatus = "off";
  private live: net.Socket | null = null;
  /** The socket of the session in progress, including one still in its handshake. */
  private current: net.Socket | null = null;
  /** Bumped by start() and stop(): a loop or session of an older generation does nothing more. */
  private gen = 0;
  private nextId = 0;
  private pending = new Map<number, { resolve: (r: AckResult) => void; timer: NodeJS.Timeout }>();
  private sleepAbort: AbortController | null = null;
  private lastState: ClinkState | null = null;
  private info: ServerInfo | null = null;
  /** Last logged key/pipe-name failure per pipe, so an unchanged reason is logged once. Cleared when the key is usable. */
  private readonly lastLogged = new Map<string, string>();

  constructor(options: ClientOptions) {
    super();
    this.o = {
      clientName: "clink-streamdeck",
      clientVersion: "0.1.0",
      connectTimeoutMs: 1500,
      helloTimeoutMs: 5000,
      ackTimeoutMs: 3000,
      pingIntervalMs: 15000,
      deadAfterMs: 5000,
      keyRetryMs: 150,
      keyDir: defaultKeyDir(),
      now: () => Date.now(),
      log: () => {},
      ...options,
    };
  }

  get connectionStatus(): ConnectionStatus {
    return this.status;
  }
  /** Last state received on the current connection (null when not connected). */
  get state(): ClinkState | null {
    return this.lastState;
  }
  get serverInfo(): ServerInfo | null {
    return this.info;
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    void this.loop(++this.gen);
  }

  stop(): void {
    this.running = false;
    this.gen++;
    this.wakeSleep();
    this.current?.destroy();
    this.live?.destroy();
    this.current = null;
    this.live = null;
    this.lastState = null;
    this.info = null;
    this.failPending("stopped");
    this.setStatus("off");
  }

  /** Sends a command. Resolves with the server's ack, or an error: notConnected, timeout, disconnected. */
  async send(verb: string): Promise<AckResult> {
    const sock = this.live;
    if (sock) {
      const id = ++this.nextId;
      return new Promise<AckResult>((resolve) => {
        const timer = setTimeout(() => {
          this.pending.delete(id);
          resolve({ ok: false, error: "timeout" });
        }, this.o.ackTimeoutMs);
        this.pending.set(id, { resolve, timer });
        try {
          sock.write(cmd(id, verb));
        } catch {
          clearTimeout(timer);
          this.pending.delete(id);
          resolve({ ok: false, error: "notConnected" });
        }
      });
    }
    return { ok: false, error: "notConnected" };
  }

  // ---------------------------------------------------------------- state

  private setStatus(s: ConnectionStatus): void {
    if (this.status === s) return;
    this.status = s;
    this.emit("status", s);
  }

  private failPending(error: string): void {
    for (const p of this.pending.values()) {
      clearTimeout(p.timer);
      p.resolve({ ok: false, error });
    }
    this.pending.clear();
  }

  private sleep(ms: number): Promise<void> {
    const ac = new AbortController();
    this.sleepAbort = ac;
    return (this.o.sleep ?? realSleep)(ms, ac.signal);
  }

  private wakeSleep(): void {
    this.sleepAbort?.abort();
    this.sleepAbort = null;
  }
  // ---------------------------------------------------------------- main loop

  private async loop(gen: number): Promise<void> {
    const alive = (): boolean => this.running && gen === this.gen;
    let controlOnly = 0;
    let reconnect = 0;
    let rescan = RESCAN_START_MS;
    const nextRescan = (): number => {
      const d = rescan;
      rescan = Math.min(rescan * 2, RESCAN_MAX_MS);
      return d;
    };
    while (alive()) {
      let scan: Scan;
      try {
        scan = await this.o.scan();
      } catch {
        scan = EMPTY_SCAN;
      }
      if (!alive()) break;
      let delay: number;
      controlOnly = scan.remote.length === 0 && scan.control.length > 0 ? controlOnly + 1 : 0;
      if (scan.remote.length > 0) {
        let welcomed = false;
        let sawProto = false;
        let sawBusy = false;
        for (const path of scan.remote) {
          if (!alive()) break;
          const r = await this.runSession(path, gen);
          if (r.welcomed) {
            welcomed = true;
            break;
          }
          if (r.kind === "proto") sawProto = true;
          else if (r.kind === "busy") sawBusy = true;
          // "session": that pipe belongs to another Windows session, try the next one.
        }
        if (!alive()) break;
        if (welcomed) {
          reconnect = 0;
          rescan = RESCAN_START_MS;
          delay = BACKOFF_MS[0]!;
          this.setStatus("connecting");
        } else if (sawProto) {
          // Normal backoff: a pipe squatter answering "bye proto" must not be able to park the plugin.
          this.setStatus("updatePlugin");
          delay = BACKOFF_MS[Math.min(reconnect++, BACKOFF_MS.length - 1)]!;
        } else {
          this.setStatus(sawBusy ? "busy" : "off");
          delay = BACKOFF_MS[Math.min(reconnect++, BACKOFF_MS.length - 1)]!;
        }
      } else if (controlOnly >= CONTROL_ONLY_SCANS) {
        // Older Clink: only the one-way command pipe exists, on repeated scans. Name detection only; it is never connected to.
        this.setStatus("updateClink");
        delay = nextRescan();
      } else {
        this.setStatus("off");
        delay = nextRescan();
      }
      if (!alive()) break;
      await this.sleep(delay);
    }
  }

  /** Reads the key; a missing or stale file gets one silent retry (no log) in case Clink is still starting. */
  private async loadKey(pipeName: string): Promise<{ key: Buffer } | { fail: HandshakeFailure }> {
    const read = (): ReturnType<typeof readKey> => readKey(this.o.keyDir, pipeName, { now: this.o.now(), uptimeS: uptime() });
    let r = await read();
    if ("fail" in r && (r.fail === "keyMissing" || r.fail === "keyStale") && this.running) {
      await this.sleep(this.o.keyRetryMs);
      if (this.running) r = await read();
    }
    return r;
  }

  private logOnce(id: string, reason: string): void {
    if (this.lastLogged.get(id) === reason) return;
    this.lastLogged.set(id, reason);
    this.o.log(reason);
  }

  // ---------------------------------------------------------------- one connection

  private async runSession(path: string, gen: number): Promise<{ kind: Outcome; welcomed: boolean }> {
    const pipeName = pipeNameOf(path);
    if (pipeName === null) {
      // Not a Clink.Remote pipe name: never build a key path from it, never open it.
      this.logOnce(path, "badPipeName");
      return { kind: "failed", welcomed: false };
    }
    // Key first, before any connection: a pipe without a usable (present, small, fresh) key is skipped unopened,
    // and nothing is ever written to a pipe before the key is in hand.
    const loaded = await this.loadKey(pipeName);
    if (gen !== this.gen) return { kind: "ended", welcomed: false };
    if ("fail" in loaded) {
      this.logOnce(pipeName, loaded.fail);
      return { kind: "failed", welcomed: false };
    }
    this.lastLogged.delete(pipeName);
    const key = loaded.key;
    this.setStatus("connecting"); // only once a key is in hand; a pipe without one leaves the status alone
    return new Promise((resolve) => {
      let done = false;
      let welcomed = false;
      const nonce = randomBytes(NONCE_BYTES);
      const sock = (this.o.connect ?? ((p: string) => net.connect(p)))(path);
      this.current = sock;
      const reject = (reason: HandshakeFailure): void => {
        this.o.log(reason);
        finish("failed");
      };
      const splitter = new LineSplitter();
      let connectTimer: NodeJS.Timeout | null = setTimeout(() => finish("busy"), this.o.connectTimeoutMs);
      let helloTimer: NodeJS.Timeout | null = null;
      let pingTimer: NodeJS.Timeout | null = null;
      let deadTimer: NodeJS.Timeout | null = null;

      const finish = (kind: Outcome): void => {
        if (done) return;
        done = true;
        for (const t of [connectTimer, helloTimer, pingTimer, deadTimer]) if (t) clearTimeout(t);
        sock.destroy();
        if (this.current === sock) this.current = null;
        if (this.live === sock) {
          this.live = null;
          this.lastState = null;
          this.info = null;
        }
        if (gen === this.gen) this.failPending("disconnected");
        resolve({ kind, welcomed });
      };

      const onMessage = (m: ServerMessage): void => {
        if (gen !== this.gen) return finish("ended");
        if (!welcomed) {
          if (m.t === "welcome") {
            const bad = checkProof(key, pipeName, nonce, m.proof);
            if (bad) return reject(bad);
            if (m.proto !== PROTO || m.app !== "Clink") return finish("failed");
            welcomed = true;
            if (helloTimer) clearTimeout(helloTimer);
            helloTimer = null;
            this.live = sock;
            this.info = { version: m.version, dev: m.dev, elevated: m.elevated, verbs: m.verbs };
            this.setStatus("connected");
            this.emit("welcome", this.info);
            pingTimer = setInterval(() => {
              try {
                sock.write(ping());
              } catch {
                return finish("ended");
              }
              deadTimer ??= setTimeout(() => finish("ended"), this.o.deadAfterMs);
            }, this.o.pingIntervalMs);
          } else if (m.t === "bye") {
            finish(m.reason === "session" ? "session" : m.reason === "proto" ? "proto" : "failed");
          }
          return;
        }
        switch (m.t) {
          case "state": {
            const { t: _t, ...state } = m;
            this.lastState = state;
            this.emit("state", state);
            break;
          }
          case "ack": {
            const p = this.pending.get(m.id);
            if (p) {
              clearTimeout(p.timer);
              this.pending.delete(m.id);
              p.resolve(ackResult(m));
            }
            break;
          }
          case "bye":
            finish("ended");
            break;
          case "clipSaved":
          case "clipNotSaved":
          case "shareProgress":
          case "shareDone":
            this.emit("event", m);
            break;
          default:
            break; // pong, a second welcome
        }
      };

      sock.on("error", (e: NodeJS.ErrnoException) => finish(welcomed ? "ended" : BUSY_CODES.has(e.code ?? "") ? "busy" : "failed"));
      sock.on("close", () => finish(welcomed ? "ended" : "failed"));
      sock.on("data", (chunk: Buffer) => {
        if (done) return;
        if (deadTimer) {
          clearTimeout(deadTimer);
          deadTimer = null;
        }
        let lines: string[];
        try {
          lines = splitter.push(chunk);
        } catch (e) {
          return finish(e instanceof LineTooLongError && welcomed ? "ended" : "failed");
        }
        for (const line of lines) {
          if (done) return;
          const m = parseLine(line);
          if (m) onMessage(m);
        }
      });
      sock.on("connect", () => {
        if (connectTimer) clearTimeout(connectTimer);
        connectTimer = null;
        if (done) return;
        helloTimer = setTimeout(() => finish("failed"), this.o.helloTimeoutMs);
        try {
          sock.write(hello(this.o.clientName, this.o.clientVersion, nonce.toString("base64")));
        } catch {
          finish("failed");
        }
      });
    });
  }
}

function ackResult(a: Ack): AckResult {
  return a.ok ? { ok: true } : { ok: false, error: a.error ?? "error" };
}