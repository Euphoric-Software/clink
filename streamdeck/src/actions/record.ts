import { ClinkAction } from "./base.ts";
import type { ClinkState } from "../protocol.ts";

export class RecordKey extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.record";

  protected override verb(): string {
    return "record";
  }

  /** State 1: a manual recording is going. */
  protected override stateIndex(state: ClinkState | null): number {
    return state?.rec.on ? 1 : 0;
  }
}