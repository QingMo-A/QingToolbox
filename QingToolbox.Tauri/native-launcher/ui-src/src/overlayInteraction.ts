/** A blank click is a complete gesture, not a mouse-up left behind by OLE
 * file dropping or internal icon reordering. No focus-loss auto-dismiss. */
export class OverlayInteraction {
  external = false
  internal = false
  blockedUntil = 0
  private origin: { x: number; y: number; id: number } | null = null

  externalDrag(active: boolean, now = performance.now()): void {
    this.external = active
    this.origin = null
    this.blockedUntil = now + 650
  }
  internalDrag(active: boolean, now = performance.now()): void {
    this.internal = active
    this.origin = null
    if (!active) this.blockedUntil = now + 450
  }
  begin(id: number, x: number, y: number, blank: boolean, now = performance.now()): void {
    this.origin = blank && !this.blocked(now) ? { id, x, y } : null
  }
  end(id: number, x: number, y: number, blank: boolean, now = performance.now()): boolean {
    const start = this.origin
    this.origin = null
    return Boolean(start && start.id === id && blank && !this.blocked(now) && Math.hypot(x - start.x, y - start.y) < 6)
  }
  cancel(): void { this.origin = null }
  blocked(now = performance.now()): boolean { return this.external || this.internal || now < this.blockedUntil }
}
