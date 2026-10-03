<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { LauncherItem, LauncherState } from './bridge'
import { insertionSlot, moveToSlot, previewSlot } from './gridOrder'
import LauncherAppIcon from './LauncherAppIcon.vue'
import { QIcon } from '@qingtoolbox/module-ui'

type Tile = LauncherItem | LauncherState['folders'][number]
const props = defineProps<{ tiles: Tile[]; enabled: boolean; busy: boolean; desktopMode: boolean; saveOrder: (ids: string[]) => Promise<void> }>()
const emit = defineEmits<{
  open: [tile: Tile]; rename: [folder: LauncherState['folders'][number]];
  removeFolder: [folder: LauncherState['folders'][number]]; removeItem: [itemId: string];
  moveInto: [itemId: string, folderId: string]; dragging: [id: string | null];
  customHover: [active: boolean]; dropToCustom: [itemId: string]
}>()
const viewport = ref<HTMLElement | null>(null)
const surface = ref<HTMLElement | null>(null)
const ordered = ref<Tile[]>([])
const width = ref(700)
const moving = ref<string | null>(null)
const gap = ref<number | null>(null)
const folderTarget = ref<string | null>(null)
let folderAnchor: { x: number; y: number } | null = null
const ghost = ref({ x: 0, y: 0 })
const rowHeight = 132
const columns = computed(() => Math.max(1, Math.floor(width.value / 120)))
const cellWidth = computed(() => width.value / columns.value)
const ids = computed(() => ordered.value.map(tile => tile.id))
const movingTile = computed(() => ordered.value.find(tile => tile.id === moving.value))
const height = computed(() => Math.max(2, Math.ceil(ordered.value.length / columns.value)) * rowHeight)
let pointer: { id: number; tile: string; x: number; y: number; offsetX: number; offsetY: number } | null = null
let lastPoint = { x: 0, y: 0 }
let candidate = ''
let candidateTimer: number | undefined
let frame = 0
let observer: ResizeObserver | undefined
let suppressClick = false
let suppressTimer: number | undefined
let customHover = false
watch(() => props.tiles, tiles => { if (!moving.value) ordered.value = [...tiles] }, { immediate: true })
watch(() => props.enabled, enabled => { if (!enabled) cancel() })
function isFolder(tile: Tile): tile is LauncherState['folders'][number] { return 'items' in tile }
function position(tile: Tile): Record<string, string | number> {
  const slot = previewSlot(ids.value, tile.id, moving.value, gap.value)
  return { width: `${cellWidth.value - 10}px`, transform: `translate3d(${slot % columns.value * cellWidth.value + 5}px, ${Math.floor(slot / columns.value) * rowHeight}px, 0)` }
}
function down(event: PointerEvent, tile: Tile): void {
  if (event.button !== 0 || !props.enabled || props.busy || (event.target as HTMLElement).closest('button')) return
  const icon = (event.currentTarget as HTMLElement).querySelector('.tile-image')!.getBoundingClientRect()
  pointer = { id: event.pointerId, tile: tile.id, x: event.clientX, y: event.clientY, offsetX: event.clientX - icon.left, offsetY: event.clientY - icon.top }
  lastPoint = { x: event.clientX, y: event.clientY }
}
function move(event: PointerEvent): void {
  if (!pointer || event.pointerId !== pointer.id) return
  lastPoint = { x: event.clientX, y: event.clientY }
  if (!moving.value && Math.hypot(event.clientX - pointer.x, event.clientY - pointer.y) < 7) return
  event.preventDefault()
  if (!moving.value) { moving.value = pointer.tile; surface.value?.setPointerCapture(event.pointerId); emit('dragging', moving.value); frame = requestAnimationFrame(tick) }
  ghost.value = { x: event.clientX - pointer.offsetX, y: event.clientY - pointer.offsetY }
  updateCustomHover(event.clientX, event.clientY)
  hitTest()
}
function overCustomTab(x: number, y: number): boolean {
  return props.desktopMode && Boolean(document.elementFromPoint(x, y)?.closest('[data-mode="custom"]'))
}
function updateCustomHover(x: number, y: number): void {
  const next = overCustomTab(x, y)
  if (next !== customHover) { customHover = next; emit('customHover', next) }
}
function queueTarget(key: string, apply: () => void, delay = 110): void {
  if (candidate === key) return
  candidate = key
  if (candidateTimer !== undefined) clearTimeout(candidateTimer)
  candidateTimer = window.setTimeout(() => { apply(); candidateTimer = undefined }, delay)
}
function folderUnderPointer(): LauncherState['folders'][number] | null {
  if (!pointer || !moving.value || !movingTile.value || isFolder(movingTile.value) || props.desktopMode || !surface.value || !viewport.value) return null
  const clip = viewport.value.getBoundingClientRect()
  if (lastPoint.x < clip.left || lastPoint.x > clip.right || lastPoint.y < clip.top || lastPoint.y > clip.bottom) return null
  const bounds = surface.value.getBoundingClientRect()
  const x = lastPoint.x - bounds.left, y = lastPoint.y - bounds.top
  return ordered.value.find(tile => {
    if (!isFolder(tile)) return false
    const slot = previewSlot(ids.value, tile.id, moving.value, gap.value)
    const centerX = (slot % columns.value + .5) * cellWidth.value
    const centerY = Math.floor(slot / columns.value) * rowHeight + 44
    return Math.abs(x - centerX) <= 37 && Math.abs(y - centerY) <= 39
  }) as LauncherState['folders'][number] | undefined ?? null
}
function activeFolderTarget(): string | null {
  const clip = viewport.value?.getBoundingClientRect()
  if (!clip || lastPoint.x < clip.left || lastPoint.x > clip.right || lastPoint.y < clip.top || lastPoint.y > clip.bottom) return null
  if (folderTarget.value && folderAnchor && Math.hypot(lastPoint.x - folderAnchor.x, lastPoint.y - folderAnchor.y) <= 52) return folderTarget.value
  return folderUnderPointer()?.id ?? null
}
function hitTest(): void {
  if (!pointer || !moving.value || !surface.value || !viewport.value) return
  const bounds = surface.value.getBoundingClientRect()
  const clip = viewport.value.getBoundingClientRect()
  const x = lastPoint.x - bounds.left, y = lastPoint.y - bounds.top
  if (lastPoint.x < clip.left || lastPoint.x > clip.right || lastPoint.y < clip.top || lastPoint.y > clip.bottom) {
    queueTarget('outside', () => { gap.value = null; folderTarget.value = null; folderAnchor = null })
    return
  }
  if (folderTarget.value && folderAnchor && Math.hypot(lastPoint.x - folderAnchor.x, lastPoint.y - folderAnchor.y) <= 52) return
  const folder = folderUnderPointer()
  if (folder) {
    queueTarget(`folder:${folder.id}`, () => {
      folderTarget.value = folder.id
      folderAnchor = { ...lastPoint }
      gap.value = null
    }, 180)
    return
  }
  // Once opened, the entire empty cell plus a small margin belongs to the
  // insertion target. Neighbour animations cannot steal its hit area.
  if (gap.value !== null) {
    const left = gap.value % columns.value * cellWidth.value
    const top = Math.floor(gap.value / columns.value) * rowHeight
    if (x >= left - 8 && x <= left + cellWidth.value + 8 && y >= top - 8 && y <= top + rowHeight + 8) {
      queueTarget(`slot:${gap.value}`, () => {})
      return
    }
  }
  // Allow the initial compacting animation before opening another gap.
  if (Math.hypot(lastPoint.x - pointer.x, lastPoint.y - pointer.y) < 38 && gap.value === null) return
  const slot = insertionSlot(x, y, cellWidth.value, rowHeight, columns.value, ordered.value.length - 1)
  queueTarget(`slot:${slot}`, () => { gap.value = slot; folderTarget.value = null; folderAnchor = null })
}
function tick(): void {
  if (!moving.value || !viewport.value) return
  const rect = viewport.value.getBoundingClientRect()
  const delta = lastPoint.y < rect.top + 28 ? -6 : lastPoint.y > rect.bottom - 28 ? 6 : 0
  if (delta) { viewport.value.scrollTop += delta; hitTest() }
  frame = requestAnimationFrame(tick)
}
function cleanup(): void {
  if (candidateTimer !== undefined) clearTimeout(candidateTimer)
  candidateTimer = undefined; candidate = ''; cancelAnimationFrame(frame)
  if (pointer && surface.value?.hasPointerCapture(pointer.id)) surface.value.releasePointerCapture(pointer.id)
  pointer = null; moving.value = null; gap.value = null; folderTarget.value = null; folderAnchor = null; emit('dragging', null)
  if (customHover) { customHover = false; emit('customHover', false) }
}
function guardClick(): void {
  suppressClick = true
  clearTimeout(suppressTimer)
  suppressTimer = window.setTimeout(() => { suppressClick = false }, 400)
}
async function up(event: PointerEvent): Promise<void> {
  if (!pointer || event.pointerId !== pointer.id) return
  const id = moving.value, slot = gap.value, folder = activeFolderTarget()
  if (!id) { cleanup(); return }
  guardClick()
  if (overCustomTab(event.clientX, event.clientY)) { cleanup(); emit('dropToCustom', id); return }
  const before = [...ordered.value]
  if (slot !== null) {
    const order = moveToSlot(ids.value, id, slot)
    ordered.value = order.map(value => before.find(tile => tile.id === value)!)
  }
  cleanup()
  if (folder) { emit('moveInto', id, folder); return }
  if (slot !== null && ordered.value.some((tile, index) => tile.id !== before[index]?.id)) {
    try { await props.saveOrder(ordered.value.map(tile => tile.id)) }
    catch { ordered.value = [...props.tiles] }
  }
}
function cancel(): void { if (moving.value) guardClick(); cleanup(); ordered.value = [...props.tiles] }
function keydown(event: KeyboardEvent): void { if (event.key === 'Escape' && pointer) { event.preventDefault(); event.stopImmediatePropagation(); cancel() } }
// Where the dragged icon will land, drawn in the same slot geometry the
// hit test uses, so the outline and the drop can never disagree.
const slotStyle = computed(() => gap.value === null ? null : {
  width: `${cellWidth.value - 10}px`,
  transform: `translate3d(${gap.value % columns.value * cellWidth.value + 5}px, ${Math.floor(gap.value / columns.value) * rowHeight}px, 0)`,
})
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)')
// A short squash on the icon acknowledges the launch while the backend opens
// the app. It is added on top of the hover transform, never replacing it.
function pulse(target: EventTarget | null): void {
  if (reducedMotion.matches) return
  ;(target as HTMLElement | null)?.querySelector('.tile-image')?.animate(
    [{ transform: 'scale(1)' }, { transform: 'scale(.88)', offset: .35 }, { transform: 'scale(1.06)', offset: .7 }, { transform: 'scale(1)' }],
    { duration: 320, easing: 'ease-out', composite: 'add' },
  )
}
function click(tile: Tile, event?: Event): void {
  if (suppressClick || props.busy) return
  pulse(event?.currentTarget ?? null)
  emit('open', tile)
}
onMounted(() => {
  observer = new ResizeObserver(() => { if (surface.value) width.value = surface.value.clientWidth })
  if (surface.value) observer.observe(surface.value)
  window.addEventListener('pointermove', move, { passive: false })
  window.addEventListener('pointerup', up)
  window.addEventListener('pointercancel', cancel)
  window.addEventListener('blur', cancel)
  window.addEventListener('keydown', keydown, true)
})
onBeforeUnmount(() => {
  cleanup(); observer?.disconnect(); clearTimeout(suppressTimer)
  window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up)
  window.removeEventListener('pointercancel', cancel); window.removeEventListener('blur', cancel)
  window.removeEventListener('keydown', keydown, true)
})
</script>

<template>
  <div ref="viewport" class="grid-scroll">
    <div ref="surface" class="launcher-grid" :class="{ 'is-dragging': moving }" :style="{ height: `${height}px` }">
      <div v-if="moving && slotStyle" class="drop-slot" :style="slotStyle" aria-hidden="true"></div>
      <article v-for="tile in ordered" :key="tile.id" class="launcher-tile" :data-id="tile.id"
        :class="{ lifted: moving === tile.id, 'folder-target': folderTarget === tile.id }" :style="position(tile)"
        role="button" tabindex="0" :aria-label="tile.name" @pointerdown="down($event, tile)" @dragstart.prevent
        @click="click(tile, $event)" @keydown.enter="click(tile, $event)">
        <div v-if="isFolder(tile)" class="tile-image folder-preview">
          <span v-for="item in tile.items.slice(0, 4)" :key="item.id" class="folder-preview-icon"><LauncherAppIcon :icon-key="item.iconKey" /></span>
          <span v-if="!tile.items.length" class="folder-empty-mark">＋</span>
          <div class="tile-icon-actions" @pointerdown.stop @click.stop>
            <button type="button" class="tile-icon-action" aria-label="重命名文件夹" title="重命名文件夹" :disabled="busy" @click="emit('rename', tile)"><QIcon name="edit" :size="12" /></button>
            <button type="button" class="tile-icon-action delete" aria-label="删除文件夹" title="删除文件夹" :disabled="busy" @click="emit('removeFolder', tile)">×</button>
          </div>
        </div>
        <div v-else class="tile-image app-icon">
          <LauncherAppIcon :icon-key="tile.iconKey" />
          <div v-if="!desktopMode" class="tile-icon-actions" @pointerdown.stop @click.stop>
            <button type="button" class="tile-icon-action delete app-remove" :aria-label="`删除 ${tile.name}`" :title="`从启动台删除 ${tile.name}`"
              :disabled="busy" @click="emit('removeItem', tile.id)">×</button>
          </div>
        </div>
        <span class="tile-name" :title="tile.name">{{ tile.name }}</span>
      </article>
    </div>
  </div>
  <Teleport to="body">
    <div v-if="movingTile" class="drag-ghost" :style="{ transform: `translate3d(${ghost.x}px, ${ghost.y}px, 0)` }">
      <div class="drag-ghost-inner">
        <div v-if="isFolder(movingTile)" class="folder-preview"><span v-for="item in movingTile.items.slice(0, 4)" :key="item.id" class="folder-preview-icon"><LauncherAppIcon :icon-key="item.iconKey" /></span></div>
        <LauncherAppIcon v-else :icon-key="movingTile.iconKey" />
      </div>
    </div>
  </Teleport>
</template>
