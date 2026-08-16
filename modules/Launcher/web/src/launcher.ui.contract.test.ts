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

  it('keeps insertion motion on the independent translate property', () => {
    expect(appSource).toContain(':style="gridEntryShift(index, entry.item.id)"')
    expect(appSource).not.toContain('class="drag-placeholder"')
    expect(stylesSource).toContain('translate .32s cubic-bezier(.22,1.18,.32,1)')
    expect(stylesSource).toContain('will-change: transform, translate')
  })

  it('hands a dropped insertion to final layout without replaying translate', () => {
    expect(appSource).toContain("'committing-reorder': committingReorder")
    expect(appSource).toContain('committingReorder.value = true')
    expect(stylesSource).toContain('.launcher-grid.committing-reorder .app-tile { transition: background .17s ease, border-color .17s ease, transform .17s ease !important; }')
  })

  it('pads the scroll viewport so the first-row hover border is visible', () => {
    expect(stylesSource).toContain('padding: 4px; scrollbar-color:')
  })

  it('keeps real icons unbacked without rendering a placeholder surface', () => {
    expect(stylesSource).toContain('.app-icon { align-items: center; background: transparent; border: 0;')
    expect(stylesSource).toContain('.fallback-icon {')
    expect(appSource).not.toContain('class="drag-placeholder"')
    expect(stylesSource).not.toContain('.drag-placeholder {')
  })

  it('allows recording to stop manually and captures Alt accelerators before the host menu', () => {
    expect(appSource).toContain('@click="toggleRecording"')
    expect(appSource).toContain(':disabled="busy"')
    expect(appSource).toContain("window.addEventListener('keydown', onKeyDown, true)")
    expect(appSource).toContain("window.addEventListener('keyup', onKeyUp, true)")
    expect(appSource).toContain('event.stopPropagation()')
  })

  it('replaces the native WebView menu with resultId-scoped Everything actions', () => {
    expect(appSource).toContain("window.addEventListener('contextmenu', suppressNativeContextMenu)")
    expect(appSource).toContain('@contextmenu="showEverythingContextMenu($event, result, index)"')
    expect(appSource).toContain("invoke('openEverythingResultFolder', { resultId: result.id })")
    expect(appSource).toContain("invoke('copyEverythingResultPath', { resultId: result.id })")
    expect(stylesSource).toContain('.everything-context-menu {')
  })
})
