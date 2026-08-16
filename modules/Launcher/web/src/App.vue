<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { invoke, onDropResult, onPresentationChanged, onStateChanged, waitForPresentation } from './bridge'
import { alphabeticalItems, beginPointerGesture, canStartPointerGesture, cancelPointerGesture, completePointerGesture, gridInsertionCandidate, movePointerGesture, recentColumnCapacity, recentItems, reorderVisibleToIndex, searchLauncherItems, shortcutFromKeyboard, stabilizeInsertionCandidate, visibleRecentItems } from './launcher'
import type { FrozenGridMetrics, PointerGesture } from './launcher'
import type { DropResult, Item, Presentation, State } from './types'

const fallback: State = {
  sortMode: 'custom', items: [], recent: [],
  hotkey: { ctrl: true, alt: true, shift: false, win: false, virtualKey: 32, keyLabel: 'Space' },
  hotkeyStatus: 'Inactive', active: false,
}
const state = reactive<State>(structuredClone(fallback))
const presentation = reactive<Presentation>({ appearancePresetId: 'qing-default', languageCode: 'en-US' })
const resources = ref<Record<string, string>>({})
const icons = reactive<Record<string, string>>({})
const view = ref<'main' | 'settings'>('main')
const busy = ref(false)
const recording = ref(false)
const draggingId = ref<string | null>(null)
const dragPreview = ref<{ itemId: string; x: number; y: number; offsetX: number; offsetY: number } | null>(null)
const pointerOverId = ref<string | null>(null)
const pointerGesture = ref<PointerGesture | null>(null)
type FrozenDragGrid = FrozenGridMetrics & { left: number; top: number; paddingLeft: number; paddingTop: number; scrollTop: number }
type DragProjection = { sourceId: string; originIds: string[]; visibleIds: string[]; baseRemainingIds: string[]; sourceVisibleIndex: number; metrics: FrozenDragGrid | null; candidateSlot: number | null; pendingSlot: number | null; pendingSince: number; grabOffsetX: number; grabOffsetY: number }
const dragProjection = ref<DragProjection | null>(null)
const committingReorder = ref(false)
const gridRef = ref<HTMLElement | null>(null)
let commitStartFrame: number | undefined
let commitReleaseFrame: number | undefined
let endingPointerId: number | undefined
const suppressClick = ref(false)
let suppressClickTimer: number | undefined
let pointerCaptureTarget: HTMLElement | null = null
let pointerSessionCleanup: (() => void) | undefined
let pointerGrabOffset = { x: 32, y: 32 }
const notice = ref('')
const searchQuery = ref('')
const recentContainer = ref<HTMLElement | null>(null)
const recentCapacity = ref(1)
let recentResizeObserver: ResizeObserver | undefined
const locale = computed(() => presentation.languageCode === 'zh-CN' ? 'zh-CN' : 'en-US')
const t = (key: string, fallbackText = key) => resources.value[key] ?? fallbackText
const sortedItems = computed(() => state.sortMode === 'alphabetical' ? alphabeticalItems(state.items) : state.items)
const visibleItems = computed(() => searchLauncherItems(sortedItems.value, searchQuery.value))
const recent = computed(() => visibleRecentItems(recentItems(state.recent.length ? state.recent : state.items), recentCapacity.value, searchQuery.value))
const dragPreviewItem = computed(() => dragPreview.value ? state.items.find(item => item.id === dragPreview.value?.itemId) ?? null : null)
const gridEntries = computed(() => {
  const projection = dragProjection.value
  if (!projection) return visibleItems.value.map(item => ({ key: item.id, item }))
  return projection.visibleIds
    .map(id => state.items.find(item => item.id === id))
    .filter((item): item is Item => Boolean(item))
    .map(item => ({ key: item.id, item }))
})

function gridEntryShift(index: number, itemId: string) {
  const projection = dragProjection.value
  const metrics = projection?.metrics
  if (!projection || !metrics || itemId === projection.sourceId) return undefined
  const remainingIndex = projection.baseRemainingIds.indexOf(itemId)
  if (remainingIndex < 0) return undefined
  const slot = projection.candidateSlot
  const desiredIndex = remainingIndex + (slot !== null && remainingIndex >= slot ? 1 : 0)
  const columns = Math.max(1, metrics.columns)
  const strideX = metrics.cellWidth + metrics.gapX
  const strideY = metrics.cellHeight + metrics.gapY
  const deltaColumn = desiredIndex % columns - index % columns
  const deltaRow = Math.floor(desiredIndex / columns) - Math.floor(index / columns)
  if (deltaColumn === 0 && deltaRow === 0) return undefined
  return { translate: `${deltaColumn * strideX}px ${deltaRow * strideY}px` }
}

function apply(next: State) {
  state.sortMode = next.sortMode
  state.items = next.items
  state.recent = next.recent
  state.hotkey = next.hotkey
  state.hotkeyStatus = next.hotkeyStatus
  state.active = next.active
  void loadIcons([...next.items, ...next.recent])
}

async function run(method: string, payload?: unknown) {
  busy.value = true
  try {
    const next = await invoke<State>(method, payload)
    if (next && 'items' in next) apply(next)
  } catch (error) {
    notice.value = error instanceof Error ? error.message : t('errors.request', 'The request could not be completed.')
  } finally {
    busy.value = false
  }
}

async function loadResources() {
  try { resources.value = await fetch(`../i18n/${locale.value}.json`).then(response => response.json()) }
  catch { resources.value = {} }
}

async function initialLoad() {
  const ready = await waitForPresentation()
  Object.assign(presentation, ready)
  await loadResources()
  const next = await invoke<State>('getState')
  if (next) apply(next)
}

async function loadIcons(items: readonly Item[]) {
  await Promise.all(items.filter(item => item.iconKey && !icons[item.id]).map(async item => {
    try {
      const response = await invoke<{ dataUrl: string | null }>('getIcon', { id: item.id })
      if (response?.dataUrl) icons[item.id] = response.dataUrl
    } catch { /* fallback icon remains */ }
  }))
}

function iconFor(item: Item) { return icons[item.id] ?? '' }
function hideLauncher() { void run('hideWindow') }
function switchSort(mode: 'custom' | 'alphabetical' | 'desktop') { if (state.sortMode !== mode) void run('setSortMode', { mode }) }
function clearSearch() { searchQuery.value = '' }
function measureRecentCapacity() {
  recentCapacity.value = recentColumnCapacity(recentContainer.value?.clientWidth ?? 0)
}
function remove(item: Item) { void run('removeItem', { id: item.id }) }
function launch(item: Item) { void run('launchItem', { id: item.id }) }
function markDragClickSuppressed() {
  suppressClick.value = true
  if (suppressClickTimer !== undefined) window.clearTimeout(suppressClickTimer)
  suppressClickTimer = window.setTimeout(() => {
    suppressClick.value = false
    suppressClickTimer = undefined
  }, 250)
}
function activate(item: Item) {
  if (suppressClick.value) {
    suppressClick.value = false
    if (suppressClickTimer !== undefined) window.clearTimeout(suppressClickTimer)
    suppressClickTimer = undefined
    return
  }
  launch(item)
}

function resolveGridElement(): HTMLElement | null {
  const value = gridRef.value as unknown as (HTMLElement & { $el?: HTMLElement }) | null
  if (!value) return null
  return typeof value.querySelectorAll === 'function' ? value : value.$el ?? null
}

function captureFrozenGrid(): FrozenDragGrid | null {
  const grid = resolveGridElement()
  if (!grid) return null
  const tiles = Array.from(grid.querySelectorAll<HTMLElement>('[data-launcher-item-id]'))
  const firstRect = tiles[0]?.getBoundingClientRect()
  const style = getComputedStyle(grid)
  const columns = Math.max(1, style.gridTemplateColumns.split(/\s+/).filter(Boolean).length)
  const gapX = Number.parseFloat(style.columnGap) || 0
  const gapY = Number.parseFloat(style.rowGap) || gapX
  const paddingLeft = Number.parseFloat(style.paddingLeft) || 0
  const paddingTop = Number.parseFloat(style.paddingTop) || 0
  const width = firstRect?.width || Number.parseFloat(style.gridAutoColumns) || 112
  const height = firstRect?.height || Number.parseFloat(style.gridAutoRows) || 132
  const bounds = grid.getBoundingClientRect()
  return { left: bounds.left, top: bounds.top, paddingLeft, paddingTop, scrollTop: grid.scrollTop, cellWidth: width, cellHeight: height, columns, gapX, gapY }
}

function candidateSlotAtPoint(x: number, y: number, projection: DragProjection) {
  const metrics = projection.metrics
  if (!metrics) return null
  const localX = x - metrics.left - metrics.paddingLeft
  const localY = y - metrics.top - metrics.paddingTop + metrics.scrollTop
  return gridInsertionCandidate(localX, localY, metrics, projection.baseRemainingIds.length, projection.candidateSlot)
}

function stopPointerSession() {
  pointerSessionCleanup?.()
  pointerSessionCleanup = undefined
}

function startPointerSession(pointerId: number) {
  stopPointerSession()
  const onMove = (event: PointerEvent) => { if (event.pointerId === pointerId) movePointer(event) }
  const onUp = (event: PointerEvent) => { if (event.pointerId === pointerId) endPointer(event) }
  const onCancel = (event: PointerEvent) => { if (event.pointerId === pointerId) endPointer(event, true) }
  const onBlur = () => { cancelCurrentPointer() }
  window.addEventListener('pointermove', onMove)
  window.addEventListener('pointerup', onUp)
  window.addEventListener('pointercancel', onCancel)
  window.addEventListener('blur', onBlur)
  pointerSessionCleanup = () => {
    window.removeEventListener('pointermove', onMove)
    window.removeEventListener('pointerup', onUp)
    window.removeEventListener('pointercancel', onCancel)
    window.removeEventListener('blur', onBlur)
  }
}

function beginPointer(item: Item, event: PointerEvent) {
  if (!canStartPointerGesture(state.sortMode, event.button, Boolean((event.target as Element | null)?.closest('button,[data-no-drag]')))) return
  const tile = event.currentTarget as HTMLElement
  const rect = tile.getBoundingClientRect()
  pointerGrabOffset = { x: Math.max(0, Math.min(rect.width, event.clientX - rect.left)), y: Math.max(0, Math.min(rect.height, event.clientY - rect.top)) }
  pointerGesture.value = beginPointerGesture(event.pointerId, item.id, event.clientX, event.clientY, state.items.map(value => value.id), visibleItems.value.map(value => value.id))
  dragPreview.value = null
  pointerOverId.value = null
  pointerCaptureTarget = tile
  startPointerSession(event.pointerId)
  try { tile.setPointerCapture(event.pointerId) } catch { /* capture is best effort on older WebViews */ }
}

function movePointer(event: PointerEvent) {
  const current = pointerGesture.value
  if (!current || current.pointerId !== event.pointerId) return
  const moved = movePointerGesture(current, event.clientX, event.clientY)
  if (!moved.active) {
    pointerGesture.value = moved
    return
  }
  if (!current.active) {
    event.preventDefault()
    draggingId.value = moved.movingId
    const visibleIds = [...moved.scopeIds]
    dragProjection.value = {
      sourceId: moved.movingId,
      originIds: [...moved.originIds],
      visibleIds,
      baseRemainingIds: visibleIds.filter(id => id !== moved.movingId),
      sourceVisibleIndex: visibleIds.indexOf(moved.movingId),
      metrics: captureFrozenGrid(),
      candidateSlot: null,
      pendingSlot: null,
      pendingSince: event.timeStamp,
      grabOffsetX: pointerGrabOffset.x,
      grabOffsetY: pointerGrabOffset.y,
    }
    dragPreview.value = { itemId: moved.movingId, x: event.clientX, y: event.clientY, offsetX: pointerGrabOffset.x, offsetY: pointerGrabOffset.y }
    markDragClickSuppressed()
  }
  if (moved.active && dragPreview.value) dragPreview.value = { ...dragPreview.value, x: event.clientX, y: event.clientY }
  if (dragProjection.value) {
    const projection = dragProjection.value
    const detectedSlot = candidateSlotAtPoint(event.clientX, event.clientY, projection)
    const stable = stabilizeInsertionCandidate(
      { slot: projection.candidateSlot, pendingSlot: projection.pendingSlot, pendingSince: projection.pendingSince },
      detectedSlot,
      event.timeStamp,
    )
    dragProjection.value = { ...projection, candidateSlot: stable.slot, pendingSlot: stable.pendingSlot, pendingSince: stable.pendingSince }
  }
  pointerGesture.value = moved
  pointerOverId.value = null
}

function endPointer(event: PointerEvent, canceled = false) {
  const current = pointerGesture.value
  if (!current || current.pointerId !== event.pointerId || endingPointerId === event.pointerId) return
  endingPointerId = event.pointerId
  const tile = pointerCaptureTarget
  try { if (tile?.hasPointerCapture(event.pointerId)) tile.releasePointerCapture(event.pointerId) } catch { /* already released */ }
  const projection = dragProjection.value
  if (canceled) {
    cancelPointerGesture(current)
  } else {
    const result = completePointerGesture(current, current.originIds)
    if (result.persist && projection && projection.candidateSlot !== null) {
      const ids = reorderVisibleToIndex(projection.originIds, projection.visibleIds, projection.sourceId, projection.candidateSlot)
      committingReorder.value = true
      state.items = ids.map(id => state.items.find(item => item.id === id)!).filter(Boolean)
      markDragClickSuppressed()
      void run('setCustomOrder', { ids })
      if (commitStartFrame !== undefined) window.cancelAnimationFrame(commitStartFrame)
      if (commitReleaseFrame !== undefined) window.cancelAnimationFrame(commitReleaseFrame)
      commitStartFrame = window.requestAnimationFrame(() => {
        commitStartFrame = undefined
        commitReleaseFrame = window.requestAnimationFrame(() => {
          committingReorder.value = false
          commitReleaseFrame = undefined
        })
      })
    }
  }
  if (current.active || canceled) markDragClickSuppressed()
  pointerGesture.value = null
  draggingId.value = null
  dragPreview.value = null
  pointerOverId.value = null
  dragProjection.value = null
  pointerCaptureTarget = null
  pointerGrabOffset = { x: 32, y: 32 }
  stopPointerSession()
  endingPointerId = undefined
}

function cancelCurrentPointer() {
  const current = pointerGesture.value
  if (!current) return false
  state.items = current.originIds.map(id => state.items.find(item => item.id === id)).filter((item): item is Item => Boolean(item))
  pointerGesture.value = null
  const tile = pointerCaptureTarget
  try { if (tile?.hasPointerCapture(current.pointerId)) tile.releasePointerCapture(current.pointerId) } catch { /* already released */ }
  draggingId.value = null
  dragPreview.value = null
  pointerOverId.value = null
  dragProjection.value = null
  pointerCaptureTarget = null
  pointerGrabOffset = { x: 32, y: 32 }
  stopPointerSession()
  markDragClickSuppressed()
  return true
}

function handleDropResult(result: DropResult) {
  const parts: string[] = []
  if (result.added.length) parts.push(`${t('drop.added', 'Added')}: ${result.added.join(', ')}`)
  if (result.skipped.length) parts.push(`${t('drop.skipped', 'Skipped')}: ${result.skipped.length}`)
  notice.value = parts.join(' · ')
}

function stopRecording() { recording.value = false; notice.value = '' }
function toggleRecording() {
  if (recording.value) { stopRecording(); return }
  recording.value = true
  notice.value = t('status.waiting', 'Waiting for shortcut...')
}
function onKeyDown(event: KeyboardEvent) {
  if (recording.value) {
    event.preventDefault()
    event.stopPropagation()
    if (event.key === 'Escape') { stopRecording(); return }
    const hotkey = shortcutFromKeyboard(event)
    if (!hotkey) return
    recording.value = false; void run('setHotkey', hotkey); return
  }
  if (event.key === 'Escape') {
    if (cancelCurrentPointer()) { event.preventDefault(); return }
    hideLauncher()
  }
}
function onKeyUp(event: KeyboardEvent) {
  if (!recording.value) return
  event.preventDefault()
  event.stopPropagation()
}

onMounted(() => {
  const offState = onStateChanged(apply)
  const offDrop = onDropResult(handleDropResult)
  const offPresentation = onPresentationChanged(next => { Object.assign(presentation, next); void loadResources() })
  window.addEventListener('keydown', onKeyDown, true)
  window.addEventListener('keyup', onKeyUp, true)
  recentResizeObserver = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(measureRecentCapacity)
  if (recentContainer.value) recentResizeObserver?.observe(recentContainer.value)
  measureRecentCapacity()
  void initialLoad()
onUnmounted(() => {
    offState(); offDrop(); offPresentation(); window.removeEventListener('keydown', onKeyDown, true); window.removeEventListener('keyup', onKeyUp, true)
    if (commitStartFrame !== undefined) window.cancelAnimationFrame(commitStartFrame)
    if (commitReleaseFrame !== undefined) window.cancelAnimationFrame(commitReleaseFrame)
    if (suppressClickTimer !== undefined) window.clearTimeout(suppressClickTimer)
    stopPointerSession()
    pointerCaptureTarget = null
    recentResizeObserver?.disconnect()
  })
})
</script>

<template>
  <main class="launcher-viewport" :class="{ recording }" @pointerdown.self="hideLauncher">
    <section class="launcher-panel" @pointerdown.stop>
      <template v-if="view === 'main'">
        <header class="panel-header">
          <div class="brand"><span class="brand-mark" aria-hidden="true"><svg class="brand-mark-icon" viewBox="0 0 24 24" focusable="false"><path d="M5 5h5v5H5zM14 5h5v5h-5zM5 14h5v5H5zM14 14h5v5h-5z" /></svg></span><h1>{{ t('view.launcher', 'Launcher') }}</h1></div>
          <div class="top-actions">
            <div class="segmented" role="group" :aria-label="t('sort.custom', 'Sort')">
              <button :class="{ active: state.sortMode === 'custom' }" @click="switchSort('custom')">{{ t('sort.custom', 'Custom') }}</button>
              <button :class="{ active: state.sortMode === 'alphabetical' }" @click="switchSort('alphabetical')">{{ t('sort.alphabetical', 'A-Z') }}</button>
              <button :class="{ active: state.sortMode === 'desktop' }" @click="switchSort('desktop')">{{ t('sort.desktop', 'Desktop') }}</button>
            </div>
            <button class="icon-button" :aria-label="t('actions.settings', 'Settings')" :title="t('actions.settings', 'Settings')" @click="view = 'settings'">&#9881;</button>
          </div>
        </header>
        <div class="search-row">
          <span class="search-icon" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false"><circle cx="10.8" cy="10.8" r="6.8" /><path d="m16 16 4.2 4.2" /></svg></span>
          <input v-model="searchQuery" class="search-input" type="search" :placeholder="t('search.placeholder', 'Search apps')" :aria-label="t('search.placeholder', 'Search apps')" @keydown.escape.stop.prevent="clearSearch" />
          <button v-if="searchQuery" class="search-clear" type="button" :aria-label="t('search.clear', 'Clear search')" @click="clearSearch">&#215;</button>
        </div>
        <section class="drop-area">
          <TransitionGroup v-if="gridEntries.length" ref="gridRef" name="launcher-grid" tag="div" class="launcher-grid" :class="{ 'pointer-reordering': Boolean(dragProjection), 'committing-reorder': committingReorder }">
            <template v-for="(entry, index) in gridEntries" :key="entry.key">
              <article class="app-tile" :class="{ dragging: draggingId === entry.item.id, 'drag-over': pointerOverId === entry.item.id && draggingId !== entry.item.id }" :style="gridEntryShift(index, entry.item.id)" :data-launcher-item-id="entry.item.id" :draggable="false" tabindex="0" @pointerdown="beginPointer(entry.item, $event)" @click="activate(entry.item)" @keydown.enter="launch(entry.item)">
                <div class="app-icon"><img v-if="iconFor(entry.item)" :src="iconFor(entry.item)" :alt="entry.item.name" /><span v-else class="fallback-icon">{{ entry.item.name.slice(0, 1).toUpperCase() }}</span></div>
                <div class="app-name" :title="entry.item.name">{{ entry.item.name }}</div>
                <button v-if="entry.item.source !== 'desktop'" class="remove-button" data-no-drag :aria-label="`${t('actions.remove', 'Remove')} ${entry.item.name}`" @pointerdown.stop.prevent @click.stop.prevent="remove(entry.item)">&#215;</button>
              </article>
            </template>
          </TransitionGroup>
          <div v-else class="empty-drop"><div class="empty-grid">{{ searchQuery ? '?' : state.sortMode === 'desktop' ? '⌂' : '+' }}</div><strong>{{ searchQuery ? t('search.noResults', 'No matching apps') : state.sortMode === 'desktop' ? t('view.desktopEmpty', 'No desktop applications found') : t('view.overlayEmpty', 'Drag an app or shortcut here') }}</strong></div>
        </section>
        <section ref="recentContainer" class="recent-section">
          <div class="section-heading"><span class="section-label">{{ t('view.recent', 'Recently used') }}</span></div>
          <div v-if="recent.length" class="recent-row"><button v-for="item in recent" :key="item.id" class="recent-tile" @click="launch(item)"><span class="recent-icon"><img v-if="iconFor(item)" :src="iconFor(item)" :alt="item.name" /><span v-else>{{ item.name.slice(0, 1).toUpperCase() }}</span></span><span class="recent-name">{{ item.name }}</span></button></div>
          <p v-else class="muted">{{ searchQuery ? t('search.noResults', 'No matching apps') : t('view.noRecent', 'Nothing launched yet.') }}</p>
        </section>
        <div v-if="dragPreview && dragPreviewItem" class="drag-preview" :style="{ left: `${dragPreview.x - dragPreview.offsetX}px`, top: `${dragPreview.y - dragPreview.offsetY}px` }" aria-hidden="true">
          <img v-if="iconFor(dragPreviewItem)" :src="iconFor(dragPreviewItem)" alt="" />
          <span v-else class="fallback-icon">{{ dragPreviewItem.name.slice(0, 1).toUpperCase() }}</span>
        </div>
        <div v-if="notice" class="notice" role="status"><span>{{ notice }}</span><button :aria-label="t('actions.close', 'Close')" @click="notice = ''">&#215;</button></div>
      </template>
      <template v-else>
        <section class="settings-view">
          <header class="settings-header"><button class="back-button" @click="view = 'main'">&#8592; {{ t('actions.back', 'Back') }}</button><h1>{{ t('view.settings', 'Launcher settings') }}</h1></header>
          <article class="settings-card"><span class="section-label">{{ t('settings.hotkey', 'Global shortcut') }}</span><p>{{ t('settings.hotkeyHint', 'Toggle the Launcher window from anywhere.') }}</p><div class="hotkey-row"><kbd>{{ [state.hotkey.ctrl && 'Ctrl', state.hotkey.alt && 'Alt', state.hotkey.shift && 'Shift', state.hotkey.win && 'Meta', state.hotkey.keyLabel].filter(Boolean).join(' + ') }}</kbd><button class="primary" :disabled="busy" @click="toggleRecording">{{ recording ? t('actions.stopRecording', 'Stop recording') : t('actions.record', 'Record new shortcut') }}</button></div><small>{{ state.hotkeyStatus === 'Registered' ? t('status.registered', 'Registered') : state.hotkeyStatus === 'Conflict' ? t('status.conflict', 'Conflict') : t('status.inactive', 'Inactive') }} · {{ t('settings.hotkeyHelp', 'Ctrl, Alt, Shift, or Meta plus one key.') }}</small></article>
          <div v-if="notice" class="notice" role="status">{{ notice }}</div>
        </section>
      </template>
    </section>
  </main>
</template>
