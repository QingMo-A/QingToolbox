<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { invoke, onDropResult, onPresentationChanged, onStateChanged, waitForPresentation } from './bridge'
import { alphabeticalItems, beginPointerGesture, canStartPointerGesture, cancelPointerGesture, completePointerGesture, movePointerGesture, pointerPreview, recentColumnCapacity, recentItems, searchLauncherItems, targetPointerInsertion, shortcutFromKeyboard, visibleRecentItems } from './launcher'
import type { PointerGesture } from './launcher'
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
const dragPreview = ref<{ itemId: string; x: number; y: number } | null>(null)
const pointerOverId = ref<string | null>(null)
const pointerGesture = ref<PointerGesture | null>(null)
const gridRef = ref<HTMLElement | null>(null)
let endingPointerId: number | undefined
const suppressClick = ref(false)
let suppressClickTimer: number | undefined
let pointerCaptureTarget: HTMLElement | null = null
let pointerSessionCleanup: (() => void) | undefined
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

function apply(next: State) {
  state.sortMode = next.sortMode
  state.items = next.items
  state.recent = next.recent
  state.hotkey = next.hotkey
  state.hotkeyStatus = next.hotkeyStatus
  state.active = next.active
  void loadIcons(next.items)
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
function switchSort(mode: 'custom' | 'alphabetical') { if (state.sortMode !== mode) void run('setSortMode', { mode }) }
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

function itemFromPoint(x: number, y: number) {
  const element = document.elementFromPoint(x, y)
  const itemId = element?.closest<HTMLElement>('[data-launcher-item-id]')?.dataset.launcherItemId
  return itemId && state.items.some(item => item.id === itemId) ? itemId : null
}

function insertionIndexAtPoint(x: number, y: number, movingId: string) {
  const grid = gridRef.value
  if (!grid) return 0
  const tiles = Array.from(grid.querySelectorAll<HTMLElement>('[data-launcher-item-id]'))
    .filter(tile => tile.dataset.launcherItemId !== movingId)
  for (let index = 0; index < tiles.length; index += 1) {
    const rect = tiles[index].getBoundingClientRect()
    const midpointY = rect.top + rect.height / 2
    const midpointX = rect.left + rect.width / 2
    if (y < midpointY || (y <= rect.bottom && x < midpointX)) return index
  }
  return tiles.length
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
    dragPreview.value = pointerPreview(moved, event.clientX, event.clientY)
    markDragClickSuppressed()
  }
  if (moved.active) dragPreview.value = pointerPreview(moved, event.clientX, event.clientY)
  const overId = itemFromPoint(event.clientX, event.clientY)
  const insertionIndex = insertionIndexAtPoint(event.clientX, event.clientY, moved.movingId)
  const result = targetPointerInsertion(moved, state.items.map(item => item.id), insertionIndex)
  pointerGesture.value = result.gesture
  if (result.ids.join('\u0000') !== state.items.map(item => item.id).join('\u0000')) {
    state.items = result.ids.map(id => state.items.find(item => item.id === id)!).filter(Boolean)
  }
  pointerOverId.value = overId && overId !== moved.movingId ? overId : null
}

function endPointer(event: PointerEvent, canceled = false) {
  const current = pointerGesture.value
  if (!current || current.pointerId !== event.pointerId || endingPointerId === event.pointerId) return
  endingPointerId = event.pointerId
  const tile = pointerCaptureTarget
  try { if (tile?.hasPointerCapture(event.pointerId)) tile.releasePointerCapture(event.pointerId) } catch { /* already released */ }
  if (canceled) {
    const result = cancelPointerGesture(current)
    if (result.dragged) state.items = result.ids.map(id => state.items.find(item => item.id === id)!).filter(Boolean)
  } else {
    const result = completePointerGesture(current, state.items.map(item => item.id))
    if (result.persist) {
      markDragClickSuppressed()
      void run('setCustomOrder', { ids: result.ids })
    }
  }
  if (current.active || canceled) markDragClickSuppressed()
  pointerGesture.value = null
  draggingId.value = null
  dragPreview.value = null
  pointerOverId.value = null
  pointerCaptureTarget = null
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
  pointerCaptureTarget = null
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

function startRecording() { recording.value = true; notice.value = t('status.waiting', 'Waiting for shortcut...') }
function onKeyDown(event: KeyboardEvent) {
  if (recording.value) {
    if (event.key === 'Escape') { recording.value = false; notice.value = ''; return }
    const hotkey = shortcutFromKeyboard(event)
    if (!hotkey) return
    event.preventDefault(); recording.value = false; void run('setHotkey', hotkey); return
  }
  if (event.key === 'Escape') {
    if (cancelCurrentPointer()) { event.preventDefault(); return }
    hideLauncher()
  }
}

onMounted(() => {
  const offState = onStateChanged(apply)
  const offDrop = onDropResult(handleDropResult)
  const offPresentation = onPresentationChanged(next => { Object.assign(presentation, next); void loadResources() })
  window.addEventListener('keydown', onKeyDown)
  recentResizeObserver = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(measureRecentCapacity)
  if (recentContainer.value) recentResizeObserver?.observe(recentContainer.value)
  measureRecentCapacity()
  void initialLoad()
  onUnmounted(() => {
    offState(); offDrop(); offPresentation(); window.removeEventListener('keydown', onKeyDown)
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
          <TransitionGroup v-if="visibleItems.length" ref="gridRef" name="launcher-grid" tag="div" class="launcher-grid">
            <article v-for="item in visibleItems" :key="item.id" class="app-tile" :class="{ dragging: draggingId === item.id, 'drag-over': pointerOverId === item.id && draggingId !== item.id }" :data-launcher-item-id="item.id" :draggable="false" tabindex="0" @pointerdown="beginPointer(item, $event)" @click="activate(item)" @keydown.enter="launch(item)">
              <div class="app-icon"><img v-if="iconFor(item)" :src="iconFor(item)" :alt="item.name" /><span v-else>{{ item.name.slice(0, 1).toUpperCase() }}</span></div>
              <div class="app-name" :title="item.name">{{ item.name }}</div>
              <button class="remove-button" data-no-drag :aria-label="`${t('actions.remove', 'Remove')} ${item.name}`" @pointerdown.stop @click.stop="remove(item)">&#215;</button>
            </article>
          </TransitionGroup>
          <div v-else class="empty-drop"><div class="empty-grid">{{ searchQuery ? '?' : '+' }}</div><strong>{{ searchQuery ? t('search.noResults', 'No matching apps') : t('view.overlayEmpty', 'Drag an app or shortcut here') }}</strong></div>
        </section>
        <section ref="recentContainer" class="recent-section">
          <div class="section-heading"><span class="section-label">{{ t('view.recent', 'Recently used') }}</span></div>
          <div v-if="recent.length" class="recent-row"><button v-for="item in recent" :key="item.id" class="recent-tile" @click="launch(item)"><span class="recent-icon"><img v-if="iconFor(item)" :src="iconFor(item)" :alt="item.name" /><span v-else>{{ item.name.slice(0, 1).toUpperCase() }}</span></span><span class="recent-name">{{ item.name }}</span></button></div>
          <p v-else class="muted">{{ searchQuery ? t('search.noResults', 'No matching apps') : t('view.noRecent', 'Nothing launched yet.') }}</p>
        </section>
        <div v-if="dragPreview && dragPreviewItem" class="drag-preview" :style="{ left: `${dragPreview.x}px`, top: `${dragPreview.y}px` }" aria-hidden="true">
          <img v-if="iconFor(dragPreviewItem)" :src="iconFor(dragPreviewItem)" alt="" />
          <span v-else>{{ dragPreviewItem.name.slice(0, 1).toUpperCase() }}</span>
        </div>
        <div v-if="notice" class="notice" role="status"><span>{{ notice }}</span><button :aria-label="t('actions.close', 'Close')" @click="notice = ''">&#215;</button></div>
      </template>
      <template v-else>
        <section class="settings-view">
          <header class="settings-header"><button class="back-button" @click="view = 'main'">&#8592; {{ t('actions.back', 'Back') }}</button><h1>{{ t('view.settings', 'Launcher settings') }}</h1></header>
          <article class="settings-card"><span class="section-label">{{ t('settings.hotkey', 'Global shortcut') }}</span><p>{{ t('settings.hotkeyHint', 'Toggle the Launcher window from anywhere.') }}</p><div class="hotkey-row"><kbd>{{ [state.hotkey.ctrl && 'Ctrl', state.hotkey.alt && 'Alt', state.hotkey.shift && 'Shift', state.hotkey.win && 'Meta', state.hotkey.keyLabel].filter(Boolean).join(' + ') }}</kbd><button class="primary" :disabled="recording || busy" @click="startRecording">{{ recording ? t('status.waiting', 'Waiting for shortcut...') : t('actions.record', 'Record new shortcut') }}</button></div><small>{{ state.hotkeyStatus === 'Registered' ? t('status.registered', 'Registered') : state.hotkeyStatus === 'Conflict' ? t('status.conflict', 'Conflict') : t('status.inactive', 'Inactive') }} · {{ t('settings.hotkeyHelp', 'Ctrl, Alt, Shift, or Meta plus one key.') }}</small></article>
          <div v-if="notice" class="notice" role="status">{{ notice }}</div>
        </section>
      </template>
    </section>
  </main>
</template>
