// Finds Clink's pipes by listing \\.\pipe\ (Windows lists named pipes as directory entries).

export const PIPE_DIR = "\\\\.\\pipe\\";

export type ReadDir = (dir: string) => Promise<string[]>;

const REMOTE = /^Clink\.Remote\.s\d+$/;
const REMOTE_DEV = /^Clink\.Remote\.dev\.[0-9a-f]{8}\.s\d+$/;
const CONTROL = /^Clink\.Control\.s\d+$/;
const CONTROL_DEV = /^Clink\.Control(\.dev\.[0-9a-f]{8})?\.s\d+$/;

export type Scan = {
  /** Full paths of the duplex status pipes (session pipes of this user are tried by the server's own session check). */
  remote: string[];
  /** Full paths of the old one-way command pipes (older Clink). Only used to tell "update Clink" from "off"; never connected to. */
  control: string[];
};

export const EMPTY_SCAN: Scan = { remote: [], control: [] };

const bySession = (a: string, b: string): number => Number(/\d+$/.exec(a)?.[0] ?? 0) - Number(/\d+$/.exec(b)?.[0] ?? 0) || a.localeCompare(b);

/** True for a Clink.Remote pipe name (release or dev form), without the \\.\pipe\ prefix. */
export const isRemotePipeName = (name: string): boolean => REMOTE.test(name) || REMOTE_DEV.test(name);

export function matchRemote(names: readonly string[], devInstances: boolean): string[] {
  return names.filter((n) => REMOTE.test(n) || (devInstances && REMOTE_DEV.test(n))).sort(bySession);
}

export function matchControl(names: readonly string[], devInstances = false): string[] {
  const re = devInstances ? CONTROL_DEV : CONTROL;
  return names.filter((n) => re.test(n)).sort(bySession);
}

/** Lists the pipe directory. A failure to list (for example while Windows is busy) counts as nothing found. */
export async function scanPipes(readdir: ReadDir, devInstances: boolean): Promise<Scan> {
  let names: string[];
  try {
    names = await readdir(PIPE_DIR);
  } catch {
    return EMPTY_SCAN;
  }
  return {
    remote: matchRemote(names, devInstances).map((n) => PIPE_DIR + n),
    control: matchControl(names, devInstances).map((n) => PIPE_DIR + n),
  };
}