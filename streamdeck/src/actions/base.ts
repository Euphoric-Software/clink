import { SingletonAction, type KeyDownEvent, type WillAppearEvent, type WillDisappearEvent } from "@elgato/streamdeck";
import type { ClinkClient } from "../pipe-client.ts";
import type { ClinkState } from "../protocol.ts";

const ignore = (): void => {};

/**
 * Shared behaviour: a key press sends one verb and shows the result on the key; keys with two manifest States
 * follow Clink's state (the manifest sets DisableAutomaticStates, so only our setState changes the State).
 * No custom rendering yet (later slice): images come from the manifest.
 */
export abstract class ClinkAction extends SingletonAction {
  protected readonly client: ClinkClient;
  /** Last State index sent per action instance (key), so unchanged state events cause no setState call. */
  private readonly shown = new Map<string, number>();

  constructor(client: ClinkClient) {
    super();
    this.client = client;
    client.on("state", () => this.refresh());
    client.on("status", () => this.refresh());
  }

  /** The verb to send for a press, or null when this press has nothing to send. */
  protected abstract verb(state: ClinkState | null): string | null;

  /** Manifest State (0 or 1) for two-state actions; undefined: this action has one State. */
  protected stateIndex(_state: ClinkState | null): number | undefined {
    return undefined;
  }

  protected refresh(): void {
    const index = this.stateIndex(this.client.state);
    if (index === undefined) return;
    for (const a of this.actions) {
      if (!a.isKey() || this.shown.get(a.id) === index) continue;
      this.shown.set(a.id, index);
      const id = a.id;
      void Promise.resolve(a.setState(index)).catch(() => this.forget(id, index));
    }
  }

  override onWillAppear(ev: WillAppearEvent): void {
    const index = this.stateIndex(this.client.state);
    if (index === undefined || !ev.action.isKey()) return;
    this.shown.set(ev.action.id, index);
    const id = ev.action.id;
    void Promise.resolve(ev.action.setState(index)).catch(() => this.forget(id, index));
  }

  /** A failed setState: drop the record so the next state line tries again. */
  private forget(id: string, index: number): void {
    if (this.shown.get(id) === index) this.shown.delete(id);
  }

  override onWillDisappear(ev: WillDisappearEvent): void {
    this.shown.delete(ev.action.id);
  }

  override async onKeyDown(ev: KeyDownEvent): Promise<void> {
    const verb = this.verb(this.client.state);
    if (verb === null) return void Promise.resolve(ev.action.showAlert()).catch(ignore);
    const result = await this.client.send(verb);
    await Promise.resolve(result.ok ? ev.action.showOk() : ev.action.showAlert()).catch(ignore);
  }
}
