import { ClinkAction } from "./base.ts";

export class ShareLast extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.share-last";

  protected override verb(): string {
    return "share";
  }
}