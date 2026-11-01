// Key images as SVG strings (viewBox 0 0 144 144: crisp at 72 and 144 px). Pure functions, no I/O, no SDK.
// All text is drawn inside the SVG; the plugin never calls setTitle, so the user's own key title stays theirs.
// Only plain SVG attributes are used (no CSS, no pathLength, no filters) so any SVG renderer draws the same picture.
import type { ConnectionStatus } from "./pipe-client.ts";
import { type ClinkState, emptyState } from "./protocol.ts";

/** Clink's Dark theme tokens: Clink/UI/Palette.xaml and ThemeManager.DarkBase (Bg, Panel3, Fg, Muted, Accent, Accent2, Good, Warn, Danger). */
export const COLORS = {
  bg: "#1B1D24",
  panel3: "#3B3E4B",
  fg: "#E9EBF2",
  muted: "#9EA4B3",
  accent: "#6F9CC4",
  accent2: "#8EB1D2",
  /** The status-dot colours, StatusDotWindow.KeyFor: Recording = Good, Error = Danger, Paused = Muted, anything else = Warn. */
  good: "#5EDB8A",
  warn: "#F2C94C",
  danger: "#F26B6B",
} as const;

const FONT = "'Segoe UI', Arial, sans-serif";

export type ActionKind = "saveClip" | "record" | "pause" | "shareLast" | "screenshot";
/** A short confirmation shown in place of the normal image. */
export type Flash = "saved" | "linkCopied" | "opened";

export type KeyView = {
  kind: ActionKind;
  status: ConnectionStatus;
  state: ClinkState | null;
  flash?: Flash | null;
  /** Share last clip only: upload or share progress in percent, or null when nothing is going. */
  progress?: number | null;
};

export const toDataUrl = (svg: string): string => "data:image/svg+xml;base64," + Buffer.from(svg, "utf8").toString("base64");

const esc = (s: string): string => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
const r2 = (n: number): string => String(Math.round(n * 100) / 100);

const text = (s: string, x: number, y: number, size: number, fill: string): string =>
  `<text x="${x}" y="${y}" font-family="${FONT}" font-size="${size}" font-weight="600" fill="${fill}" text-anchor="middle">${esc(s)}</text>`;

const frame = (body: string): string =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144"><rect width="144" height="144" fill="${COLORS.bg}"/>${body}</svg>`;

const dimmed = (body: string, opacity: number): string => `<g opacity="${opacity}">${body}</g>`;

// ------------------------------------------------------------------ glyphs (24 x 24 design grid)

const STROKE = (c: string): string => `fill="none" stroke="${c}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"`;

const GLYPHS: Record<ActionKind | "play", (c: string) => string> = {
  saveClip: (c) => `<rect x="3" y="5" width="18" height="14" rx="3" ${STROKE(c)}/><path d="M10 9.5v5l4.5-2.5z" fill="${c}"/>`,
  record: (c) => `<circle cx="12" cy="12" r="7" fill="${c}"/>`,
  pause: (c) => `<rect x="6" y="5" width="4" height="14" rx="1" fill="${c}"/><rect x="14" y="5" width="4" height="14" rx="1" fill="${c}"/>`,
  play: (c) => `<path d="M8 5v14l11-7z" fill="${c}" stroke="${c}" stroke-width="1.5" stroke-linejoin="round"/>`,
  shareLast: (c) => `<path d="M12 15V4M7.5 8.5L12 4l4.5 4.5M5 13v6h14v-6" ${STROKE(c)}/>`,
  screenshot: (c) =>
    `<path d="M4 9V5a1 1 0 0 1 1-1h4M15 4h4a1 1 0 0 1 1 1v4M20 15v4a1 1 0 0 1-1 1h-4M9 20H5a1 1 0 0 1-1-1v-4" ${STROKE(c)}/><circle cx="12" cy="12" r="3.2" fill="${c}"/>`,
};

/** A glyph centred on (cx, cy), `size` units wide. */
const glyph = (name: ActionKind | "play", color: string, cx: number, cy: number, size: number): string =>
  `<g transform="translate(${r2(cx - size / 2)} ${r2(cy - size / 2)}) scale(${r2(size / 24)})">${GLYPHS[name](color)}</g>`;

// ------------------------------------------------------------------ formatting

/** mm:ss, or h:mm:ss from one hour on. */
export function fmtTimer(totalSeconds: number): string {
  const t = Math.max(0, Math.floor(Number.isFinite(totalSeconds) ? totalSeconds : 0));
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const p2 = (n: number): string => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${p2(m)}:${p2(s)}` : `${p2(m)}:${p2(s)}`;
}

/** Replay length: "60s", "2m", "2:30"; empty when unknown. */
export function fmtBuffer(seconds: number): string {
  const s = Math.floor(seconds);
  if (!Number.isFinite(s) || s <= 0) return "";
  if (s < 120) return `${s}s`;
  return s % 60 === 0 ? `${s / 60}m` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/** The status dot colour of the Clink state (StatusDotWindow.KeyFor: recording Good, error Danger, paused and clips off Muted, else Warn). */
export function stateColor(state: string): string {
  switch (state) {
    case "recording":
      return COLORS.good;
    case "error":
      return COLORS.danger;
    case "paused":
    case "clipsOff":
    case "":
      return COLORS.muted;
    default:
      return COLORS.warn; // starting, waitingForGame, anything unknown
  }
}

const STATE_WORD: Record<string, string> = { clipsOff: "Off", paused: "Paused", waitingForGame: "Waiting", starting: "Starting", error: "Error" };

/** Progress ring arc from 12 o'clock clockwise. 100 %: a full circle; 0 %: nothing. */
export function ringArc(cx: number, cy: number, r: number, percent: number, color: string, width: number): string {
  const p = Math.max(0, Math.min(100, Math.round(percent)));
  if (p === 0) return "";
  if (p === 100) return `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="${color}" stroke-width="${width}"/>`;
  const a = (p / 100) * 2 * Math.PI;
  const x = cx + r * Math.sin(a);
  const y = cy - r * Math.cos(a);
  return `<path d="M${cx} ${cy - r}A${r} ${r} 0 ${p > 50 ? 1 : 0} 1 ${r2(x)} ${r2(y)}" fill="none" stroke="${color}" stroke-width="${width}" stroke-linecap="round"/>`;
}

// ------------------------------------------------------------------ pieces

const caption = (label: string, color: string = COLORS.fg): string => text(label, 72, 124, label.length > 9 ? 22 : 26, color);

function flashImage(flash: Flash): string {
  const lines = flash === "saved" ? ["Saved"] : flash === "opened" ? ["Opened"] : ["Link", "copied"];
  const check = `<path d="M46 44l17 17 35-36" fill="none" stroke="${COLORS.bg}" stroke-width="11" stroke-linecap="round" stroke-linejoin="round"/>`;
  const label = lines.length === 1 ? text(lines[0]!, 72, 120, 34, COLORS.bg) : text(lines[0]!, 72, 98, 28, COLORS.bg) + text(lines[1]!, 72, 128, 28, COLORS.bg);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144"><rect width="144" height="144" fill="${COLORS.good}"/>${check}${label}</svg>`;
}

/** Connection overlays: the action's glyph, dimmed, and a short message. */
function overlay(kind: ActionKind, status: ConnectionStatus): string {
  switch (status) {
    case "off":
      return frame(dimmed(glyph(kind, COLORS.muted, 72, 52, 60), 0.4) + text("Clink off", 72, 116, 26, COLORS.muted));
    case "busy":
      return frame(dimmed(glyph(kind, COLORS.muted, 72, 52, 60), 0.4) + text("Busy", 72, 116, 30, COLORS.warn));
    case "updateClink":
      return frame(dimmed(glyph(kind, COLORS.muted, 72, 40, 48), 0.4) + text("Update", 72, 100, 26, COLORS.warn) + text("Clink", 72, 130, 26, COLORS.warn));
    case "updatePlugin":
      return frame(dimmed(glyph(kind, COLORS.muted, 72, 40, 48), 0.4) + text("Update", 72, 100, 26, COLORS.warn) + text("plugin", 72, 130, 26, COLORS.warn));
    default:
      return frame("");
  }
}

function saveClipBody(s: ClinkState): string {
  const color = stateColor(s.state);
  const word = s.state === "recording" ? "" : (STATE_WORD[s.state] ?? "");
  const dot = `<circle cx="72" cy="44" r="22" fill="${color}"/>`;
  const sub = word ? text(word, 72, 90, 22, COLORS.muted) : "";
  const len = fmtBuffer(s.bufferS);
  return dot + sub + (len ? text(len, 72, 126, 40, COLORS.fg) : "");
}

function recordBody(s: ClinkState): string {
  const r = s.rec;
  if (r.finishing) {
    // A three-quarter arc stands for "working".
    return `<path d="M72 28A22 22 0 1 1 50 50" fill="none" stroke="${COLORS.accent}" stroke-width="7" stroke-linecap="round"/>` + caption("Saving");
  }
  if (r.on || r.resuming || r.queued) {
    const amber = r.resuming || r.queued;
    const color = amber ? COLORS.warn : COLORS.danger;
    const t = r.on ? fmtTimer(r.s) : "Queued";
    const long = t.length > 5;
    return `<circle cx="72" cy="44" r="22" fill="${color}"/>` + text(t, 72, 122, long ? 32 : 38, COLORS.fg);
  }
  return `<circle cx="72" cy="50" r="22" fill="none" stroke="${COLORS.danger}" stroke-width="7"/>` + caption("Record");
}

function pauseBody(s: ClinkState): string {
  return s.state === "paused" ? glyph("play", COLORS.warn, 72, 52, 64) + caption("Resume", COLORS.warn) : glyph("pause", COLORS.fg, 72, 52, 64) + caption("Pause");
}

function shareBody(s: ClinkState, progress: number | null | undefined): string {
  const p = progress ?? (s.upload.on ? s.upload.p : null);
  if (p !== null && p !== undefined) {
    const pct = Math.max(0, Math.min(100, Math.round(p)));
    const track = `<circle cx="72" cy="58" r="38" fill="none" stroke="${COLORS.panel3}" stroke-width="9"/>`;
    return track + ringArc(72, 58, 38, pct, COLORS.accent, 9) + text(`${pct}%`, 72, 69, 30, COLORS.fg) + text("Sharing", 72, 132, 22, COLORS.muted);
  }
  const body = glyph("shareLast", COLORS.accent2, 72, 52, 64) + caption("Share");
  return s.cloud ? body : dimmed(body, 0.35);
}

function screenshotBody(s: ClinkState): string {
  const body = glyph("screenshot", COLORS.accent2, 72, 52, 64) + caption("Screenshot");
  return s.screenshots ? body : dimmed(body, 0.35);
}

/** The key image for one key: (action, connection, Clink state, transient flash) in, SVG out. */
export function renderKey(v: KeyView): string {
  if (v.status !== "connected" && v.status !== "connecting") return overlay(v.kind, v.status);
  const waiting = v.status === "connecting" || v.state === null;
  if (!waiting && v.flash) return flashImage(v.flash);
  // No state yet: a dim, wordless picture (state "" has no caption and the muted colour).
  const s = v.state ?? { ...emptyState(), state: "" };
  let body: string;
  switch (v.kind) {
    case "saveClip":
      body = saveClipBody(s);
      break;
    case "record":
      body = recordBody(s);
      break;
    case "pause":
      body = pauseBody(s);
      break;
    case "shareLast":
      body = shareBody(s, v.progress);
      break;
    case "screenshot":
      body = screenshotBody(s);
      break;
  }
  return frame(waiting ? dimmed(body, 0.4) : body);
}

// ------------------------------------------------------------------ static art (manifest defaults, icons)

const DEFAULTS: Record<ActionKind, { color: string; labels: string[] }> = {
  saveClip: { color: COLORS.accent, labels: ["Save clip"] },
  record: { color: COLORS.danger, labels: ["Record", "Stop"] },
  pause: { color: COLORS.fg, labels: ["Pause", "Resume"] },
  shareLast: { color: COLORS.accent2, labels: ["Share"] },
  screenshot: { color: COLORS.accent2, labels: ["Screenshot"] },
};

/** The picture a key has in the manifest (before the plugin has drawn anything). `index`: the manifest State. */
export function renderDefault(kind: ActionKind, index = 0): string {
  const d = DEFAULTS[kind];
  const label = d.labels[index] ?? d.labels[0]!;
  if (kind === "pause" && index === 1) return frame(glyph("play", COLORS.warn, 72, 52, 64) + caption(label, COLORS.warn));
  if (kind === "record" && index === 1) return frame(`<circle cx="72" cy="50" r="22" fill="${COLORS.danger}"/>` + caption(label));
  return frame(glyph(kind, d.color, 72, 52, 64) + caption(label));
}

/** Action list icon: 20 x 20 (shown at 20 and 40), white on transparent. */
export function iconSvg(kind: ActionKind): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24">${GLYPHS[kind]("#fff")}</svg>\n`;
}

/** Category icon: 28 x 28 (shown at 28 and 56), white on transparent: a ring around a play mark. */
export function categoryIconSvg(): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28"><circle cx="14" cy="14" r="11" fill="none" stroke="#fff" stroke-width="2.5"/><path d="M11.5 9.5v9l7.5-4.5z" fill="#fff"/></svg>\n`;
}

/** Plugin icon source (rendered to 256 and 512 px PNG): a Clink-blue ring with a play mark and a status dot on the dark tile. */
export function pluginIconSvg(): string {
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">` +
    `<rect width="256" height="256" rx="48" fill="${COLORS.bg}"/>` +
    `<circle cx="128" cy="128" r="78" fill="none" stroke="${COLORS.accent}" stroke-width="22"/>` +
    `<path d="M108 92v72l60-36z" fill="${COLORS.fg}"/>` +
    `<circle cx="196" cy="60" r="20" fill="${COLORS.good}"/>` +
    `</svg>\n`
  );
}
