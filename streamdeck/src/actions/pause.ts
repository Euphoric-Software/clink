import { ClinkAction } from "./base.ts";
import type { ClinkState } from "../protocol.ts";

export class Pause extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.pause";
  protected override readonly kind = "pause";

  /** Sends pause, or resume while Clink is paused. */
  protected override verb(state: ClinkState | null): string {
    return state?.state === "paused" ? "resume" : "pause";
  }

  /** State 1: paused. */
  protected override stateIndex(state: ClinkState | null): number {
    return state?.state === "paused" ? 1 : 0;
  }
}