import { ClinkAction, SHARE_FLASH_MS } from "./base.ts";
import type { ClinkEvent } from "../protocol.ts";

export class ShareLast extends ClinkAction {
  override readonly manifestId = "software.euphoric.clink.share-last";
  protected override readonly kind = "shareLast";
  /** Percent of the share going on now, from shareProgress events; null when none. */
  private sharing: number | null = null;

  protected override verb(): string {
    return "share";
  }

  protected override progress(): number | null {
    return this.sharing;
  }

  protected override onDisconnected(): void {
    this.sharing = null;
  }

  protected override onClinkEvent(ev: ClinkEvent): void {
    if (ev.t === "shareProgress") {
      this.sharing = ev.p;
      this.refresh();
    } else if (ev.t === "shareDone") {
      this.sharing = null;
      switch (ev.outcome) {
        case "linkCopied":
        case "uploaded":
          this.showFlash("linkCopied", SHARE_FLASH_MS);
          break;
        case "fileShareOpened":
          this.showFlash("opened", SHARE_FLASH_MS);
          break;
        case "failed":
        case "refused":
        case "noClip":
          this.alertAll();
          this.refresh();
          break;
        default:
          this.refresh(); // declined: the user said no, nothing to report
      }
    }
  }
}