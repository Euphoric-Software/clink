import { SingletonAction, type KeyDownEvent, type WillAppearEvent, type WillDisappearEvent } from "@elgato/streamdeck";
import type { ClinkClient } from "../pipe-client.ts";
import type { ClinkEvent, ClinkState } from "../protocol.ts";
import { type ActionKind, type Flash, type KeyView, renderKey } from "../render.ts";
import { type KeyTarget, KeyUpdateQueue } from "../throttle.ts";

const ignore = (): void => {};

/** How long a confirmation ("Saved", "Link copied", "Opened") replaces the normal image. */
export const SAVED_FLASH_MS = 1500;
export const SHARE_FLASH_MS = 2000;

/** Timers and the shared update queue: the one global queue for all keys, and timer functions tests can replace. */
export type ActionEnv = {
  queue: KeyUpdateQueue;
  setTimer: (fn: () => void, ms: number) => unknown;
  clearTimer: (handle: unknown) => void;
};

export const realEnv = (queue: KeyUpdateQueue = new KeyUpdateQueue()): ActionEnv => ({
  queue,
  setTimer: (fn, ms) => setTimeout(fn, ms),
  clearTimer: (h) => clearTimeout(h as NodeJS.Timeout),
});

/**
 * Shared behaviour: a key press sends one verb and shows the result on the key. Every key's image is drawn by render.ts from
 * (action, connection status, Clink state, flash) and goes through the shared throttled queue. Keys with two manifest States
 * follow Clink's state through setState (the manifest sets DisableAutomaticStates, so only our setState changes the State).
 * setTitle is never called: the user's title stays theirs.
 */
export abstract class ClinkAction extends SingletonAction {
  protected readonly client: ClinkClient;
  protected readonly env: ActionEnv;
  protected abstract readonly kind: ActionKind;
  private flash: Flash | null = null;
  private flashTimer: unknown = null;

  constructor(client: ClinkClient, env: ActionEnv = realEnv()) {
    super();
    this.client = client;
    this.env = env;
    client.on("state", () => this.refresh());
    client.on("status", (status) => {
      if (status !== "connected") {
        this.clearFlash(); // a pending confirmation must not reappear on reconnect
        this.onDisconnected();
      }
      this.refresh();
    });
    client.on("event", (ev) => this.onClinkEvent(ev));
  }

  /** The verb to send for a press, or null when this press has nothing to send. */
  protected abstract verb(state: ClinkState | null): string | null;

  /** Manifest State (0 or 1) for two-state actions; undefined: this action has one State. */
  protected stateIndex(_state: ClinkState | null): number | undefined {
    return undefined;
  }

  /** Share progress in percent for the picture, or null. Only Share last clip has one. */
  protected progress(): number | null {
    return null;
  }

  /** The link to Clink is gone or not yet there: forget anything that only made sense on the old connection. */
  protected onDisconnected(): void {}

  /**
   * True when a successful ack already means the verb worked (record, pause, screenshot): the key shows the SDK check mark.
   * False when the outcome arrives later as an event (Save clip: clipSaved/clipNotSaved, Share: shareDone): no early check mark.
   */
  protected readonly ackShowsOk: boolean = true;

  /** A Clink event (clipSaved, shareDone, ...). Actions override what concerns them. */
  protected onClinkEvent(_ev: ClinkEvent): void {}

  /** Shows a confirmation image for `ms`, then the normal one. A newer flash replaces an older one. */
  protected showFlash(flash: Flash, ms: number): void {
    if (this.flashTimer !== null) this.env.clearTimer(this.flashTimer);
    this.flash = flash;
    this.flashTimer = this.env.setTimer(() => {
      this.flash = null;
      this.flashTimer = null;
      this.refresh();
    }, ms);
    this.refresh();
  }

  private clearFlash(): void {
    if (this.flashTimer !== null) this.env.clearTimer(this.flashTimer);
    this.flashTimer = null;
    this.flash = null;
  }

  /** Alert on every visible key of this action (a failure the press itself did not see). */
  protected alertAll(): void {
    for (const a of this.actions) if (a.isKey()) Promise.resolve(a.showAlert()).catch(ignore);
  }

  protected view(): KeyView {
    return { kind: this.kind, status: this.client.connectionStatus, state: this.client.state, flash: this.flash, progress: this.progress() };
  }

  private submit(key: KeyTarget): void {
    const v = this.view();
    this.env.queue.submit(key, { svg: renderKey(v), state: this.stateIndex(v.state) });
  }

  /** Keys inside a multi-action get no picture from us; their presses still send the verb. */
  protected refresh(): void {
    for (const a of this.actions) if (a.isKey() && !a.isInMultiAction()) this.submit(a);
  }

  override onWillAppear(ev: WillAppearEvent): void {
    if (ev.action.isKey() && !ev.action.isInMultiAction()) this.submit(ev.action);
  }

  override onWillDisappear(ev: WillDisappearEvent): void {
    this.env.queue.drop(ev.action.id);
  }

  override async onKeyDown(ev: KeyDownEvent): Promise<void> {
    // Off, busy, update needed: nothing to send. (Connecting also lands here: no link yet.)
    const verb = this.client.connectionStatus === "connected" ? this.verb(this.client.state) : null;
    if (verb === null) return void Promise.resolve(ev.action.showAlert()).catch(ignore);
    const result = await this.client.send(verb);
    if (result.ok && !this.ackShowsOk) return; // the result key shows comes with the event
    await Promise.resolve(result.ok ? ev.action.showOk() : ev.action.showAlert()).catch(ignore);
  }
}
