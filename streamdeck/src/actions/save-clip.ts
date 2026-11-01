import { ClinkAction } from "./base.ts";

export class SaveClip extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.save-clip";

  protected override verb(): string {
    return "clip";
  }
}