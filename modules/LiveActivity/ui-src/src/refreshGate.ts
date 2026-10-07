/** Single-flight reads; a user action invalidates any older response. */
export class RefreshGate {
  private epoch = 0
  private reading = false
  private stopped = false

  begin(blocked: boolean): number | null {
    if (blocked || this.reading || this.stopped) return null
    this.reading = true
    return this.epoch
  }
  accepts(epoch: number, blocked: boolean): boolean {
    return !this.stopped && !blocked && this.epoch === epoch
  }
  finish(): void { this.reading = false }
  invalidate(): void { this.epoch++ }
  stop(): void {
    this.stopped = true
    this.invalidate()
  }
}
