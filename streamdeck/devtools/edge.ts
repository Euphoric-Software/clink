// Headless Microsoft Edge screenshots (no window is shown). Dev tooling only: never part of the plugin.
import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

const EDGE_PATHS = [
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
];

export function findEdge(): string {
  const found = EDGE_PATHS.find((p) => existsSync(p));
  if (!found) throw new Error("Microsoft Edge not found");
  return found;
}

/** Renders `html` in headless Edge and writes a width x height PNG. `transparent`: no page background (keep the body background unset). */
export function screenshotHtml(html: string, png: string, width: number, height: number, transparent: boolean): void {
  const tmp = mkdtempSync(path.join(tmpdir(), "clink-sd-edge-"));
  try {
    const page = path.join(tmp, "page.html");
    writeFileSync(page, html);
    execFileSync(
      findEdge(),
      [
        "--headless",
        "--disable-gpu",
        "--hide-scrollbars",
        "--no-first-run",
        "--force-device-scale-factor=1",
        `--user-data-dir=${path.join(tmp, "profile")}`,
        ...(transparent ? ["--default-background-color=00000000"] : []),
        `--window-size=${width},${height}`,
        `--screenshot=${png}`,
        pathToFileURL(page).href,
      ],
      { stdio: "ignore", timeout: 60_000, windowsHide: true },
    );
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}