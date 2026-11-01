// Regenerates the committed art in software.euphoric.clink.sdPlugin/imgs from src/render.ts:
// action icons (20/40 white SVG), category icon (28/56 SVG), default key images (72/144 SVG) and the plugin icon PNGs (256 and 512).
// The PNGs come from headless Edge. Run: npm run assets   (the build itself never writes into the .sdPlugin folder)
import { writeFileSync } from "node:fs";
import path from "node:path";
import { type ActionKind, categoryIconSvg, iconSvg, pluginIconSvg, renderDefault } from "../src/render.ts";
import { screenshotHtml } from "./edge.ts";

const imgs = path.join(import.meta.dirname, "..", "software.euphoric.clink.sdPlugin", "imgs");
const put = (name: string, text: string): void => writeFileSync(path.join(imgs, name), text);

const ACTIONS: [ActionKind, string][] = [
  ["saveClip", "save-clip"],
  ["record", "record"],
  ["pause", "pause"],
  ["shareLast", "share-last"],
  ["screenshot", "screenshot"],
];

for (const [kind, file] of ACTIONS) {
  put(`${file}.svg`, iconSvg(kind));
  put(`${file}-key.svg`, renderDefault(kind, 0) + "\n");
}
put("record-key-on.svg", renderDefault("record", 1) + "\n");
put("pause-key-on.svg", renderDefault("pause", 1) + "\n");
put("category-icon.svg", categoryIconSvg());

for (const [size, name] of [[256, "plugin-icon.png"], [512, "plugin-icon@2x.png"]] as const) {
  const html = `<!doctype html><meta charset="utf-8"><style>html,body{margin:0;padding:0;background:transparent}svg{display:block;width:${size}px;height:${size}px}</style>${pluginIconSvg()}`;
  screenshotHtml(html, path.join(imgs, name), size, size, true);
}
console.log("assets written to " + imgs);