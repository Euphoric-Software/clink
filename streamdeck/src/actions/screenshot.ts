import { ClinkAction } from "./base.ts";

export class Screenshot extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.screenshot";

  protected override verb(): string {
    return "screenshot";
  }
}