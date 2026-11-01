// Contact sheet of every key image, one 144 px tile each with a label, as one PNG: dist/preview/keys.png (dist/ is gitignored).
// The tiles are drawn by the same render.ts the plugin uses. Run: npm run preview
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { type ActionKind, type Flash, renderDefault, renderKey } from "../src/render.ts";
import type { ConnectionStatus } from "../src/pipe-client.ts";
import { type ClinkState, emptyState } from "../src/protocol.ts";
import { screenshotHtml } from "./edge.ts";

export type Tile = { label: string; svg: string };

const st = (over: Partial<ClinkState> = {}): ClinkState => ({ ...emptyState(), state: "recording", bufferS: 60, screenshots: true, cloud: true, ...over });
const rec = (over: Partial<ClinkState["rec"]>): ClinkState["rec"] => ({ ...emptyState().rec, ...over });

export function tiles(): Tile[] {
  const out: Tile[] = [];
  const key = (label: string, kind: ActionKind, state: ClinkState | null, o: { status?: ConnectionStatus; flash?: Flash; progress?: number } = {}): void => {
    out.push({ label, svg: renderKey({ kind, status: o.status ?? "connected", state, flash: o.flash ?? null, progress: o.progress ?? null }) });
  };
  key("Save clip: recording", "saveClip", st());
  key("Save clip: 2:30 buffer", "saveClip", st({ bufferS: 150 }));
  key("Save clip: paused", "saveClip", st({ state: "paused" }));
  key("Save clip: clips off", "saveClip", st({ state: "clipsOff" }));
  key("Save clip: waiting", "saveClip", st({ state: "waitingForGame" }));
  key("Save clip: starting", "saveClip", st({ state: "starting" }));
  key("Save clip: error", "saveClip", st({ state: "error" }));
  key("Save clip: Saved flash", "saveClip", st(), { flash: "saved" });
  key("Record: idle", "record", st());
  key("Record: recording", "record", st({ rec: rec({ on: true, s: 83 }) }));
  key("Record: over 1 h", "record", st({ rec: rec({ on: true, s: 3723 }) }));
  key("Record: resuming", "record", st({ rec: rec({ on: true, s: 83, resuming: true }) }));
  key("Record: queued", "record", st({ rec: rec({ queued: true }) }));
  key("Record: saving", "record", st({ rec: rec({ finishing: true }) }));
  key("Pause: running", "pause", st());
  key("Pause: paused", "pause", st({ state: "paused" }));
  key("Share: idle", "shareLast", st());
  key("Share: cloud off", "shareLast", st({ cloud: false }));
  key("Share: 25 %", "shareLast", st(), { progress: 25 });
  key("Share: 75 %", "shareLast", st(), { progress: 75 });
  key("Share: upload 60 %", "shareLast", st({ upload: { on: true, p: 60 } }));
  key("Share: Link copied", "shareLast", st(), { flash: "linkCopied" });
  key("Share: Opened", "shareLast", st(), { flash: "opened" });
  key("Screenshot: on", "screenshot", st());
  key("Screenshot: off", "screenshot", st({ screenshots: false }));
  const kinds: [ActionKind, string][] = [["saveClip", "Save clip"], ["record", "Record"], ["pause", "Pause"], ["shareLast", "Share"], ["screenshot", "Screenshot"]];
  for (const status of ["off", "updateClink", "updatePlugin", "busy", "connecting"] as const)
    for (const [kind, name] of kinds) key(`${name}: ${status}`, kind, null, { status });
  for (const [kind, name] of kinds) out.push({ label: `${name}: manifest default`, svg: renderDefault(kind, 0) });
  out.push({ label: "Record: manifest default (on)", svg: renderDefault("record", 1) });
  out.push({ label: "Pause: manifest default (on)", svg: renderDefault("pause", 1) });
  return out;
}

const COLS = 8;
const CELL_W = 144 + 24;
const CELL_H = 144 + 40;

export function sheetHtml(list: Tile[]): { html: string; width: number; height: number } {
  const width = COLS * CELL_W + 24;
  const height = Math.ceil(list.length / COLS) * CELL_H + 24;
  const cells = list
    .map((t) => `<div class="c">${t.svg}<div class="l">${t.label.replace(/&/g, "&amp;").replace(/</g, "&lt;")}</div></div>`)
    .join("");
  const html =
    `<!doctype html><meta charset="utf-8"><style>` +
    `body{margin:0;padding:12px;background:#0E0F13;font:12px 'Segoe UI',Arial,sans-serif;color:#C9CDD8}` +
    `.g{display:grid;grid-template-columns:repeat(${COLS},${CELL_W}px)}` +
    `.c{width:${CELL_W}px;height:${CELL_H}px;box-sizing:border-box;padding:0 12px}` +
    `.c svg{display:block;width:144px;height:144px;outline:1px solid #3B3E4B}` +
    `.l{margin-top:6px;line-height:15px}</style><div class="g">${cells}</div>`;
  return { html, width, height };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const dir = path.join(import.meta.dirname, "..", "dist", "preview");
  mkdirSync(dir, { recursive: true });
  const list = tiles();
  const { html, width, height } = sheetHtml(list);
  writeFileSync(path.join(dir, "keys.html"), html);
  const png = path.join(dir, "keys.png");
  screenshotHtml(html, png, width, height, false);
  console.log(`${list.length} tiles -> ${png}`);
}