/** Only explicit reveals are cached. No persistence, and no secret is included in list state. */
export class RevealCache {
  private values = new Map<string, string>()
  private timers = new Map<string, ReturnType<typeof setTimeout>>()
  constructor(private changed: () => void, private duration = 30_000) {}
  get(id: string) { return this.values.get(id) }
  set(id: string, value: string) {
    this.hide(id)
    this.values.set(id, value)
    this.timers.set(id, setTimeout(() => this.hide(id), this.duration))
    this.changed()
  }
  hide(id: string) {
    clearTimeout(this.timers.get(id)); this.timers.delete(id); this.values.delete(id); this.changed()
  }
  clear() { for (const timer of this.timers.values()) clearTimeout(timer); this.timers.clear(); this.values.clear(); this.changed() }
}
