import { readdir } from "node:fs/promises";
import streamDeck from "@elgato/streamdeck";
import { Pause } from "./actions/pause.ts";
import { RecordKey } from "./actions/record.ts";
import { SaveClip } from "./actions/save-clip.ts";
import { Screenshot } from "./actions/screenshot.ts";
import { ShareLast } from "./actions/share-last.ts";
import { scanPipes } from "./discovery.ts";
import { ClinkClient } from "./pipe-client.ts";
import { PLUGIN_VERSION } from "./version.ts";

// Dev instances: always on in the `build:dev` bundle (compile-time flag), otherwise only via the hidden global setting
// (no UI yet; edit the global settings JSON).
let devInstances = __CLINK_DEV_INSTANCES__;
const applyGlobals = (s: { devInstances?: unknown }): void => {
  devInstances = __CLINK_DEV_INSTANCES__ || s.devInstances === true;
};
streamDeck.settings.onDidReceiveGlobalSettings((ev) => applyGlobals(ev.settings));

const client = new ClinkClient({
  scan: () => scanPipes((dir) => readdir(dir), devInstances),
  clientVersion: PLUGIN_VERSION,
  log: (reason) => streamDeck.logger.warn(`handshake failed: ${reason}`),
});

streamDeck.actions.registerAction(new SaveClip(client));
streamDeck.actions.registerAction(new RecordKey(client));
streamDeck.actions.registerAction(new Pause(client));
streamDeck.actions.registerAction(new ShareLast(client));
streamDeck.actions.registerAction(new Screenshot(client));

await streamDeck.connect();
const globals = await streamDeck.settings.getGlobalSettings();
applyGlobals(globals);
client.start();