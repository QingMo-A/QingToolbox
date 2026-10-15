// Move only the glyphs, not the shell, tile positions, hover targets or actions.
// Every real wake gets a wave, including rapid close/reopen; old waves are
// cancelled instead of using a time gate which randomly drops later wakes.
export class IconRevealMotion {
  private animations = new Set<Animation>()
  cancel(): void {
    for (const animation of this.animations) animation.cancel()
    this.animations.clear()
  }
  play(tiles: HTMLElement[], columns: number): void {
    this.cancel()
    tiles.slice(0, 32).forEach((tile, index) => {
      const delay = (index % Math.max(1, columns) + Math.floor(index / Math.max(1, columns))) * 26
      for (const glyph of tile.querySelectorAll<HTMLElement>('.tile-image > .launcher-app-image, .folder-empty-mark')) {
        const animation = glyph.animate([
          { opacity: .7, transform: 'translateY(14px) scale(.9)' },
          { opacity: 1, transform: 'none' },
        ], { duration: 340, delay: 40 + delay, easing: 'cubic-bezier(.34,1.3,.64,1)', fill: 'backwards' })
        this.animations.add(animation)
        animation.onfinish = () => this.animations.delete(animation)
      }
    })
  }
}
