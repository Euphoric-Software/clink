// Clink remote protocol v1: UTF-8 JSON lines. Mirrors Clink/Services/RemoteProtocol.cs.
// Unknown fields are ignored; unknown message types parse to null. Pure: no I/O.

export const PROTO = 1;
/** Longest incoming line (bytes, without the line break); a longer one drops the connection. */
export const MAX_LINE_BYTES = 64 * 1024;

export type RecState = { on: boolean; s: number; resuming: boolean; queued: boolean; finishing: boolean };

export type ClinkState = {
  /** "clipsOff" | "starting" | "recording" | "waitingForGame" | "paused" | "error" (unknown values are kept as sent). */
  state: string;
  rec: RecState;
  bufferS: number;
  saving: boolean;
  screenshots: boolean;
  cloud: boolean;
  signedIn: boolean;
  upload: { on: boolean; p: number };
  updatePending: boolean;
  elevated: boolean;
};

export type Welcome = { t: "welcome"; proto: number; app: string; version: string; dev: boolean; elevated: boolean; verbs: string[]; proof?: string };
export type StateMsg = { t: "state" } & ClinkState;
export type Ack = { t: "ack"; id: number; ok: boolean; error?: string };
export type Bye = { t: "bye"; reason: string; max?: number };
export type ClipSaved = { t: "clipSaved"; seconds: number };
export type ClipNotSaved = { t: "clipNotSaved"; reason: string };
export type ShareProgress = { t: "shareProgress"; p: number };
export type ShareDone = { t: "shareDone"; outcome: string };

export type ClinkEvent = ClipSaved | ClipNotSaved | ShareProgress | ShareDone;
export type ServerMessage = Welcome | StateMsg | Ack | Bye | { t: "pong" } | ClinkEvent;

export type ClientMessage =
  | { t: "hello"; proto: number; client: string; v: string; nonce: string }
  | { t: "cmd"; id: number; verb: string }
  | { t: "ping" };

const isObj = (x: unknown): x is Record<string, unknown> => typeof x === "object" && x !== null && !Array.isArray(x);
const str = (x: unknown, d = ""): string => (typeof x === "string" ? x : d);
const bool = (x: unknown, d = false): boolean => (typeof x === "boolean" ? x : d);
const num = (x: unknown, d = 0): number => (typeof x === "number" && Number.isFinite(x) ? x : d);

/** A state with every field defaulted, so a missing field never breaks a renderer. */
export function emptyState(): ClinkState {
  return {
    state: "clipsOff",
    rec: { on: false, s: 0, resuming: false, queued: false, finishing: false },
    bufferS: 0,
    saving: false,
    screenshots: false,
    cloud: false,
    signedIn: false,
    upload: { on: false, p: 0 },
    updatePending: false,
    elevated: false,
  };
}

/** Parses one line from Clink. Null: not JSON, not an object, no string "t", or a type this client does not know. */
export function parseLine(line: string): ServerMessage | null {
  let v: unknown;
  try {
    v = JSON.parse(line);
  } catch {
    return null;
  }
  if (!isObj(v) || typeof v.t !== "string") return null;
  switch (v.t) {
    case "welcome": {
      const w: Welcome = {
        t: "welcome",
        proto: num(v.proto, -1),
        app: str(v.app),
        version: str(v.version),
        dev: bool(v.dev),
        elevated: bool(v.elevated),
        verbs: Array.isArray(v.verbs) ? v.verbs.filter((x): x is string => typeof x === "string") : [],
      };
      if (typeof v.proof === "string") w.proof = v.proof;
      return w;
    }
    case "state": {
      const rec = isObj(v.rec) ? v.rec : {};
      const up = isObj(v.upload) ? v.upload : {};
      return {
        t: "state",
        state: str(v.state, "clipsOff"),
        rec: { on: bool(rec.on), s: num(rec.s), resuming: bool(rec.resuming), queued: bool(rec.queued), finishing: bool(rec.finishing) },
        bufferS: num(v.bufferS),
        saving: bool(v.saving),
        screenshots: bool(v.screenshots),
        cloud: bool(v.cloud),
        signedIn: bool(v.signedIn),
        upload: { on: bool(up.on), p: num(up.p) },
        updatePending: bool(v.updatePending),
        elevated: bool(v.elevated),
      };
    }
    case "ack": {
      if (typeof v.id !== "number" || !Number.isSafeInteger(v.id)) return null;
      const ack: Ack = { t: "ack", id: v.id, ok: bool(v.ok) };
      if (typeof v.error === "string") ack.error = v.error;
      return ack;
    }
    case "pong":
      return { t: "pong" };
    case "bye": {
      const bye: Bye = { t: "bye", reason: str(v.reason) };
      if (typeof v.max === "number") bye.max = v.max;
      return bye;
    }
    case "clipSaved":
      return { t: "clipSaved", seconds: num(v.seconds) };
    case "clipNotSaved":
      return { t: "clipNotSaved", reason: str(v.reason, "failed") };
    case "shareProgress":
      return { t: "shareProgress", p: num(v.p) };
    case "shareDone":
      return { t: "shareDone", outcome: str(v.outcome, "failed") };
    default:
      return null;
  }
}

/** One line to send, with its trailing line feed. */
export function serialize(msg: ClientMessage): string {
  return JSON.stringify(msg) + "\n";
}

/** nonce: base64 of 32 fresh random bytes. */
export const hello = (client: string, v: string, nonce: string): string => serialize({ t: "hello", proto: PROTO, client, v, nonce });
export const cmd = (id: number, verb: string): string => serialize({ t: "cmd", id, verb });
export const ping = (): string => serialize({ t: "ping" });

export class LineTooLongError extends Error {
  constructor() {
    super("line too long");
    this.name = "LineTooLongError";
  }
}

/** Splits a byte stream into lines (LF, optional CR). Throws LineTooLongError as soon as a line passes the cap, before buffering it all. */
export class LineSplitter {
  private pending: Buffer = Buffer.alloc(0);
  private readonly maxBytes: number;
  constructor(maxBytes = MAX_LINE_BYTES) {
    this.maxBytes = maxBytes;
  }

  push(chunk: Buffer): string[] {
    const lines: string[] = [];
    let data = this.pending.length ? Buffer.concat([this.pending, chunk]) : chunk;
    let start = 0;
    for (;;) {
      const nl = data.indexOf(0x0a, start);
      if (nl < 0) break;
      let end = nl;
      if (end > start && data[end - 1] === 0x0d) end--;
      if (end - start > this.maxBytes) throw new LineTooLongError();
      lines.push(data.toString("utf8", start, end));
      start = nl + 1;
    }
    const rest = data.subarray(start);
    if (rest.length > this.maxBytes + 1) throw new LineTooLongError(); // +1: the CR of a CR LF
    this.pending = Buffer.from(rest);
    return lines;
  }
}