// Key-file handshake: proves the other end of a pipe is the Clink of this user (it can read the key file only the user can read).
// proof = HMAC-SHA256(key, "clink-remote-proof-v1" 0x00 pipeName 0x00 nonce). Never logs or returns the key or the proof.
import { createHmac, timingSafeEqual } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { uptime } from "node:os";
import path from "node:path";
import { PIPE_DIR, isRemotePipeName } from "./discovery.ts";

export const NONCE_BYTES = 32;
export const KEY_BYTES = 32;
const CONTEXT = "clink-remote-proof-v1";
const MAX_B64_CHARS = 256;
const B64 = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;

/** Reason codes only; these are what gets logged. */
export type HandshakeFailure = "badPipeName" | "keyMissing" | "keyStale" | "keyBad" | "noProof" | "badProof" | "proofMismatch";

/** %LOCALAPPDATA%\Clink\remote */
export function defaultKeyDir(): string {
  const base = process.env.LOCALAPPDATA;
  return path.join(base && base.length > 0 ? base : path.join(process.env.USERPROFILE ?? "", "AppData", "Local"), "Clink", "remote");
}

/** The pipe name (no \\.\pipe\ prefix) when the path is a Clink.Remote pipe, else null. Only such names ever reach a file path. */
export function pipeNameOf(pipePath: string): string | null {
  if (!pipePath.startsWith(PIPE_DIR)) return null;
  const name = pipePath.slice(PIPE_DIR.length);
  return isRemotePipeName(name) ? name : null;
}

/** Strict base64 (standard alphabet, padded). Null when it is not. */
export function decodeBase64(text: string): Buffer | null {
  if (text.length === 0 || text.length > MAX_B64_CHARS || !B64.test(text)) return null;
  return Buffer.from(text, "base64");
}

export function expectedProof(key: Buffer, pipeName: string, nonce: Buffer): Buffer {
  const data = Buffer.concat([Buffer.from(CONTEXT, "utf8"), Buffer.from([0]), Buffer.from(pipeName, "utf8"), Buffer.from([0]), nonce]);
  return createHmac("sha256", key).update(data).digest();
}

/** A key file is never legitimately larger than this (base64 of 32 bytes is 44 chars). */
export const MAX_KEY_FILE_BYTES = 1024;
/** A key written before the machine booted is a leftover from an earlier run. The slack covers clock rounding and small clock steps. */
export const BOOT_SLACK_MS = 60_000;

/**
 * A key file whose mtime is older than this is stale. This is one half of a contract: the Clink side (RemoteServer key upkeep)
 * refreshes the file's mtime every 60 s and on resume, so a live Clink's key is always far younger than this.
 */
export const KEY_MAX_AGE_MS = 300_000;

/** A key file dated further ahead than this is not from a live Clink (a clock far in the past on this PC, or a planted file). */
export const FUTURE_SLACK_MS = 60_000;

export type KeyClock = { now: number; uptimeS: number };

/** Checks the key file without reading it: it must exist, be a small regular file, not predate this boot, not be older than KEY_MAX_AGE_MS and not be dated more than FUTURE_SLACK_MS ahead. Null when fine. */
export async function checkKeyFile(keyDir: string, pipeName: string, clock?: KeyClock): Promise<HandshakeFailure | null> {
  let st;
  try {
    st = await stat(path.join(keyDir, pipeName + ".key"));
  } catch {
    return "keyMissing";
  }
  if (!st.isFile()) return "keyBad";
  if (st.size > MAX_KEY_FILE_BYTES) return "keyBad";
  const now = clock?.now ?? Date.now();
  const bootMs = now - (clock?.uptimeS ?? uptime()) * 1000;
  if (st.mtimeMs < bootMs - BOOT_SLACK_MS) return "keyStale";
  if (now - st.mtimeMs > KEY_MAX_AGE_MS) return "keyStale";
  if (st.mtimeMs - now > FUTURE_SLACK_MS) return "keyStale";
  return null;
}

/** Reads <keyDir>\<pipeName>.key fresh (size and age checked first). pipeName must already be validated by pipeNameOf. */
export async function readKey(keyDir: string, pipeName: string, clock?: KeyClock): Promise<{ key: Buffer } | { fail: HandshakeFailure }> {
  const bad = await checkKeyFile(keyDir, pipeName, clock);
  if (bad) return { fail: bad };
  let text: string;
  try {
    text = await readFile(path.join(keyDir, pipeName + ".key"), "utf8");
  } catch {
    return { fail: "keyMissing" };
  }
  if (text.length > MAX_KEY_FILE_BYTES) return { fail: "keyBad" };
  const key = decodeBase64(text.trim());
  if (!key || key.length !== KEY_BYTES) return { fail: "keyBad" };
  return { key };
}
/** Checks welcome.proof against the expected value. Null when it matches. */
export function checkProof(key: Buffer, pipeName: string, nonce: Buffer, proof: string | undefined): HandshakeFailure | null {
  if (proof === undefined) return "noProof";
  const got = decodeBase64(proof);
  if (!got) return "badProof";
  const want = expectedProof(key, pipeName, nonce);
  if (got.length !== want.length) return "badProof";
  return timingSafeEqual(got, want) ? null : "proofMismatch";
}
