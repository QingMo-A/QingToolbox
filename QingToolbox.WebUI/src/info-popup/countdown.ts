/** One monotonic countdown per card. Pausing preserves, rather than resets, time. */
export class PopupCountdown {
  private timer: ReturnType<typeof setTimeout> | null = null
  private remaining = 0
  private startedAt = 0
  private active = false

  constructor(private readonly expire: () => void, private readonly now: () => number = () => performance.now()) {}

  start(milliseconds: number, paused = false) {
    this.stop()
    this.remaining = Math.max(0, milliseconds)
    this.active = true
    if (!paused) this.resume()
  }

  pause() {
    if (this.timer === null) return
    clearTimeout(this.timer)
    this.timer = null
    this.remaining = Math.max(0, this.remaining - Math.max(0, this.now() - this.startedAt))
  }

  resume() {
    if (this.timer !== null || !this.active) return
    this.startedAt = this.now()
    this.timer = setTimeout(() => {
      this.timer = null
      this.remaining = 0
      this.active = false
      this.expire()
    }, this.remaining)
  }

  stop() {
    if (this.timer !== null) clearTimeout(this.timer)
    this.timer = null
    this.remaining = 0
    this.active = false
  }
}
