// Mount and native page-load/show can refer to the same reveal. Never restart
// an entrance midway (especially by resetting a visible panel's opacity to 0).
export class RevealGate {
  private last = -Infinity
  accept(now: number): boolean {
    if (now - this.last < 900) return false
    this.last = now
    return true
  }
}
