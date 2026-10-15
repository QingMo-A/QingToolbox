import assert from 'node:assert/strict'
import { test } from 'node:test'
import { IconRevealMotion } from './revealMotion.ts'

function fixture(count) {
  const calls = [], animations = []
  const tiles = Array.from({ length: count }, () => ({
    querySelectorAll(selector) {
      assert.equal(selector, '.tile-image > .launcher-app-image, .folder-empty-mark')
      return [{ animate(frames, options) {
        calls.push({ frames, options }); const animation = { cancelled: false, cancel() { this.cancelled = true } }
        animations.push(animation); return animation
      } }]
    },
    animate() { assert.fail('Tile layout/white shell must never animate') },
  }))
  return { tiles, calls, animations }
}
test('every wake animates glyphs without moving the shell or tile layout', () => {
  const f = fixture(4), motion = new IconRevealMotion()
  motion.play(f.tiles, 2)
  assert.equal(f.calls.length, 4)
  assert.deepEqual(f.calls.map(c => c.options.delay), [40, 66, 66, 92])
  motion.play(f.tiles, 2) // immediate reopen is not suppressed for 900ms
  assert.equal(f.calls.length, 8)
  assert.ok(f.animations.slice(0, 4).every(a => a.cancelled))
  assert.ok(f.animations.slice(4).every(a => !a.cancelled))
  motion.cancel()
  assert.ok(f.animations.every(a => a.cancelled))
})
test('large grids only animate the bounded visible tile set', () => {
  const f = fixture(500), motion = new IconRevealMotion()
  motion.play(f.tiles, 7)
  assert.equal(f.calls.length, 32)
  motion.cancel()
})
