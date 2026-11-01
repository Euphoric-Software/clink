import { readdir } from "node:fs/promises";
import streamDeck from "@elgato/streamdeck";
import { realEnv } from "./actions/base.ts";
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

// One update queue for all keys (at most 10 key updates per second in total).
const env = realEnv();
streamDeck.actions.registerAction(new SaveClip(client, env));
streamDeck.actions.registerAction(new RecordKey(client, env));
streamDeck.actions.registerAction(new Pause(client, env));
streamDeck.actions.registerAction(new ShareLast(client, env));
streamDeck.actions.registerAction(new Screenshot(client, env));

await streamDeck.connect();
// The build marker (clink-build:release / clink-build:dev) goes to the log, which also keeps it in the bundle for the pack guard.
streamDeck.logger.info(`Clink plugin ${PLUGIN_VERSION} ${__CLINK_BUILD__}`);
const globals = await streamDeck.settings.getGlobalSettings();
applyGlobals(globals);
client.start();