import assert from "node:assert/strict";
import { test } from "node:test";
import { tiles } from "../devtools/preview.ts";
import type { ConnectionStatus } from "../src/pipe-client.ts";
import { type ClinkState, emptyState } from "../src/protocol.ts";
import { type ActionKind, COLORS, type Flash, fmtBuffer, fmtTimer, renderDefault, renderKey, ringArc, stateColor, toDataUrl } from "../src/render.ts";

const st = (over: Partial<ClinkState> = {}): ClinkState => ({ ...emptyState(), state: "recording", bufferS: 60, screenshots: true, cloud: true, ...over });
const rec = (over: Partial<ClinkState["rec"]>): ClinkState["rec"] => ({ ...emptyState().rec, ...over });
const draw = (kind: ActionKind, state: ClinkState | null, o: { status?: ConnectionStatus; flash?: Flash; progress?: number } = {}): string =>
  renderKey({ kind, status: o.status ?? "connected", state, flash: o.flash ?? null, progress: o.progress ?? null });
/** All text drawn in the SVG, in order. */
const texts = (svg: string): string[] => [...svg.matchAll(/<text [^>]*>([^<]*)<\/text>/g)].map((m) => m[1]!);
const dotFill = (svg: string): string | undefined => /<circle cx="72" cy="44" r="22" fill="(#[0-9A-F]{6})"/.exec(svg)?.[1];

test("every image is a 144 x 144 SVG with a dark background, text inside, and no title call needed", () => {
  for (const t of tiles()) {
    assert.match(t.svg, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="144" height="144" viewBox="0 0 144 144">/, t.label);
    assert.ok(t.svg.endsWith("</svg>"), t.label);
    assert.ok(!/<script|<style|href=/.test(t.svg), t.label + ": nothing active in the SVG");
    assert.ok(t.svg.includes("Segoe UI") || texts(t.svg).length === 0, t.label + ": generic font stack");
    assert.ok(t.svg.includes(`fill="${COLORS.bg}"`) || t.svg.includes(`fill="${COLORS.good}"`), t.label);
  }
});

test("the palette is Clink's dark theme and the status-dot colours", () => {
  assert.deepEqual([COLORS.good, COLORS.danger, COLORS.warn, COLORS.muted], ["#5EDB8A", "#F26B6B", "#F2C94C", "#9EA4B3"]);
  assert.equal(COLORS.bg, "#1B1D24");
});

test("data URL: base64 SVG that decodes to the same text", () => {
  const svg = draw("record", st());
  const url = toDataUrl(svg);
  assert.ok(url.startsWith("data:image/svg+xml;base64,"));
  assert.equal(Buffer.from(url.slice("data:image/svg+xml;base64,".length), "base64").toString("utf8"), svg);
});

test("save clip: dot colour per state and the buffer length", () => {
  const cases: [string, string][] = [
    ["recording", COLORS.good],
    ["paused", COLORS.muted],
    ["clipsOff", COLORS.muted],
    ["waitingForGame", COLORS.warn],
    ["starting", COLORS.warn],
    ["error", COLORS.danger],
  ];
  for (const [state, color] of cases) {
    assert.equal(dotFill(draw("saveClip", st({ state }))), color, state);
    assert.equal(stateColor(state), color, state);
  }
  assert.deepEqual(texts(draw("saveClip", st())), ["60s"]);
  assert.deepEqual(texts(draw("saveClip", st({ state: "paused", bufferS: 90 }))), ["Paused", "90s"]);
  assert.deepEqual(texts(draw("saveClip", st({ state: "recording", bufferS: 0 }))), [], "unknown length: no label");
});

test("buffer and timer formats", () => {
  assert.deepEqual([fmtBuffer(60), fmtBuffer(119), fmtBuffer(120), fmtBuffer(150), fmtBuffer(0), fmtBuffer(Number.NaN)], ["60s", "119s", "2m", "2:30", "", ""]);
  assert.deepEqual([fmtTimer(0), fmtTimer(83), fmtTimer(3599), fmtTimer(3600), fmtTimer(3723), fmtTimer(-5)], ["00:00", "01:23", "59:59", "1:00:00", "1:02:03", "00:00"]);
});

test("saved flash: green tile with Saved", () => {
  const svg = draw("saveClip", st(), { flash: "saved" });
  assert.ok(svg.includes(`<rect width="144" height="144" fill="${COLORS.good}"/>`));
  assert.deepEqual(texts(svg), ["Saved"]);
});

test("record: idle ring, red dot with timer, h:mm:ss, amber when resuming or queued, Saving while finishing", () => {
  const idle = draw("record", st());
  assert.deepEqual(texts(idle), ["Record"]);
  assert.ok(idle.includes(`fill="none" stroke="${COLORS.danger}"`) && !dotFill(idle));

  const on = draw("record", st({ rec: rec({ on: true, s: 83 }) }));
  assert.equal(dotFill(on), COLORS.danger);
  assert.deepEqual(texts(on), ["01:23"]);

  assert.deepEqual(texts(draw("record", st({ rec: rec({ on: true, s: 3723 }) }))), ["1:02:03"]);

  const resuming = draw("record", st({ rec: rec({ on: true, s: 83, resuming: true }) }));
  assert.equal(dotFill(resuming), COLORS.warn);
  assert.deepEqual(texts(resuming), ["01:23"]);
  const queued = draw("record", st({ rec: rec({ queued: true }) }));
  assert.equal(dotFill(queued), COLORS.warn);
  assert.deepEqual(texts(queued), ["Queued"]);

  const fin = draw("record", st({ rec: rec({ on: true, s: 9, finishing: true }) }));
  assert.deepEqual(texts(fin), ["Saving"]);
  assert.ok(!dotFill(fin));
});

test("pause: running shows Pause bars, paused shows Resume in amber", () => {
  const run = draw("pause", st());
  assert.deepEqual(texts(run), ["Pause"]);
  assert.equal((run.match(/<rect x="[614]+" y="5"/g) ?? []).length, 2, "two bars");
  const paused = draw("pause", st({ state: "paused" }));
  assert.deepEqual(texts(paused), ["Resume"]);
  assert.ok(paused.includes(`fill="${COLORS.warn}"`) && paused.includes('d="M8 5v14l11-7z"'), "play triangle");
});

test("share: idle, dimmed without cloud, progress ring from p and from upload.p", () => {
  const idle = draw("shareLast", st());
  assert.deepEqual(texts(idle), ["Share"]);
  assert.ok(!idle.includes("opacity"));
  const off = draw("shareLast", st({ cloud: false }));
  assert.ok(off.includes('<g opacity="0.35">'));

  const p25 = draw("shareLast", st(), { progress: 25 });
  assert.deepEqual(texts(p25), ["25%", "Sharing"]);
  assert.match(p25, /d="M72 20A38 38 0 0 1 110 58"/, "quarter arc, small-arc flag, ends at 3 o'clock");
  const p75 = draw("shareLast", st(), { progress: 75 });
  assert.match(p75, /d="M72 20A38 38 0 1 1 34 58"/, "three-quarter arc, large-arc flag, ends at 9 o'clock");
  const full = draw("shareLast", st(), { progress: 100 });
  assert.ok(full.includes('r="38" fill="none" stroke="#6F9CC4"'), "100 %: full circle");
  const zero = draw("shareLast", st(), { progress: 0 });
  assert.deepEqual(texts(zero), ["0%", "Sharing"]);
  assert.ok(!/<path d="M72 20A/.test(zero));
  const viaUpload = draw("shareLast", st({ upload: { on: true, p: 60 } }));
  assert.deepEqual(texts(viaUpload), ["60%", "Sharing"]);
  assert.deepEqual(texts(draw("shareLast", st({ upload: { on: false, p: 60 } }))), ["Share"], "upload not on: no ring");
  assert.equal(ringArc(10, 10, 5, 0, "#fff", 2), "");
});

test("share flashes", () => {
  assert.deepEqual(texts(draw("shareLast", st(), { flash: "linkCopied" })), ["Link", "copied"]);
  assert.deepEqual(texts(draw("shareLast", st(), { flash: "opened" })), ["Opened"]);
});

test("screenshot: dimmed when screenshots are off", () => {
  assert.ok(!draw("screenshot", st()).includes("opacity"));
  assert.ok(draw("screenshot", st({ screenshots: false })).includes('<g opacity="0.35">'));
  assert.deepEqual(texts(draw("screenshot", st())), ["Screenshot"]);
});

test("connection overlays for every action", () => {
  const kinds: ActionKind[] = ["saveClip", "record", "pause", "shareLast", "screenshot"];
  for (const kind of kinds) {
    assert.deepEqual(texts(draw(kind, null, { status: "off" })), ["Clink off"], kind);
    assert.deepEqual(texts(draw(kind, null, { status: "updateClink" })), ["Update", "Clink"], kind);
    assert.deepEqual(texts(draw(kind, null, { status: "updatePlugin" })), ["Update", "plugin"], kind);
    assert.deepEqual(texts(draw(kind, null, { status: "busy" })), ["Busy"], kind);
    const connecting = draw(kind, null, { status: "connecting" });
    assert.ok(connecting.includes('<g opacity="0.4">'), kind + ": subtle while connecting");
    assert.ok(!texts(connecting).some((x) => /Clink|Update|Busy/.test(x)), kind);
    // A stale state or flash never shows through an overlay.
    assert.deepEqual(texts(draw(kind, st(), { status: "off", flash: "saved" })), ["Clink off"], kind);
  }
  assert.ok(draw("record", null, { status: "off" }).includes(COLORS.muted));
  assert.ok(draw("record", null, { status: "updateClink" }).includes(COLORS.warn));
});

test("connected but no state yet looks like connecting", () => {
  assert.ok(draw("saveClip", null).includes('<g opacity="0.4">'));
});

test("defaults and icons", () => {
  assert.deepEqual(texts(renderDefault("record", 0)), ["Record"]);
  assert.deepEqual(texts(renderDefault("record", 1)), ["Stop"]);
  assert.deepEqual(texts(renderDefault("pause", 1)), ["Resume"]);
});

test("text is escaped", () => {
  // No user text reaches the renderer today; the escape is a guard.
  assert.ok(!tiles().some((t) => /<text[^>]*>[^<]*&(?!#)/.test(t.svg)));
});