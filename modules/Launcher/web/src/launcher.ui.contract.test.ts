import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const appSource = readFileSync(resolve(__dirname, 'App.vue'), 'utf8')
const stylesSource = readFileSync(resolve(__dirname, 'styles.css'), 'utf8')

describe('launcher pointer and clear affordance contracts', () => {
  it('keeps one custom clear affordance and hides native search cancellation', () => {
    expect((appSource.match(/class="search-clear"/g) ?? []).length).toBe(1)
    expect(stylesSource).toContain('.search-input::-webkit-search-cancel-button')
    expect(stylesSource).toContain('.search-input::-webkit-search-decoration')
  })

  it('keeps remove controls out of launch and drag gestures', () => {
    expect(appSource).toContain('@pointerdown.stop.prevent')
    expect(appSource).toContain('@click.stop.prevent="remove(item)"')
    expect(stylesSource).toContain('.app-tile:hover .remove-button, .app-tile:focus-within .remove-button')
    expect(stylesSource).toContain('.app-tile.dragging .app-name, .app-tile.dragging .remove-button')
  })

  it('pads the scroll viewport so the first-row hover border is visible', () => {
    expect(stylesSource).toContain('padding: 4px; scrollbar-color:')
  })
})
