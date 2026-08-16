import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const appSource = readFileSync(resolve(__dirname, 'App.vue'), 'utf8')
const stylesSource = readFileSync(resolve(__dirname, 'styles.css'), 'utf8')

describe('launcher pointer and clear affordance contracts', () => {
  it('exposes independent custom, alphabetical, and desktop views', () => {
    expect(appSource).toContain("switchSort('custom')")
    expect(appSource).toContain("switchSort('alphabetical')")
    expect(appSource).toContain("switchSort('desktop')")
    expect(appSource).toContain("entry.item.source !== 'desktop'")
  })

  it('keeps one custom clear affordance and hides native search cancellation', () => {
    expect((appSource.match(/class="search-clear"/g) ?? []).length).toBe(1)
    expect(stylesSource).toContain('.search-input::-webkit-search-cancel-button')
    expect(stylesSource).toContain('.search-input::-webkit-search-decoration')
  })

  it('keeps remove controls out of launch and drag gestures', () => {
    expect(appSource).toContain('@pointerdown.stop.prevent')
    expect(appSource).toContain('@click.stop.prevent="remove(entry.item)"')
    expect(stylesSource).toContain('.app-tile:hover .remove-button, .app-tile:focus-within .remove-button')
    expect(stylesSource).toContain('.app-tile.dragging .app-name, .app-tile.dragging .remove-button')
  })

  it('hides the dragged source immediately while remaining tiles use a spring move', () => {
    expect(appSource).toContain("'pointer-reordering': Boolean(dragProjection)")
    expect(stylesSource).toContain('.launcher-grid.pointer-reordering .launcher-grid-leave-active { opacity: 0; transition: none; }')
    expect(stylesSource).toContain('.launcher-grid-move { transition: transform .32s cubic-bezier(.22,1.18,.32,1); }')
  })

  it('pads the scroll viewport so the first-row hover border is visible', () => {
    expect(stylesSource).toContain('padding: 4px; scrollbar-color:')
  })

  it('keeps real icons unbacked and the drag slot visually transparent', () => {
    expect(stylesSource).toContain('.app-icon { align-items: center; background: transparent; border: 0;')
    expect(stylesSource).toContain('.fallback-icon {')
    expect(stylesSource).toContain('.drag-placeholder { background: transparent !important; border: 0 !important; box-shadow: none !important;')
    expect(stylesSource).toContain('pointer-events: none; visibility: hidden;')
  })
})
