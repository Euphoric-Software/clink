import { ClinkAction, SAVED_FLASH_MS } from "./base.ts";
import type { ClinkEvent } from "../protocol.ts";

export class SaveClip extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.save-clip";
  protected override readonly kind = "saveClip";

  protected override readonly ackShowsOk = false; // the result comes as clipSaved / clipNotSaved

  protected override verb(): string {
    return "clip";
  }

  protected override onClinkEvent(ev: ClinkEvent): void {
    if (ev.t === "clipSaved") this.showFlash("saved", SAVED_FLASH_MS);
    else if (ev.t === "clipNotSaved") this.alertAll();
  }
}