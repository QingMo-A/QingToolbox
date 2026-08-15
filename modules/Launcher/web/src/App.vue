<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { invoke, onDropResult, onPresentationChanged, onStateChanged, waitForPresentation } from './bridge'
import { alphabeticalItems, recentItems, reorderIds, shortcutFromKeyboard } from './launcher'
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
const suppressClick = ref(false)
let suppressClickTimer: number | undefined
const notice = ref('')
const locale = computed(() => presentation.languageCode === 'zh-CN' ? 'zh-CN' : 'en-US')
const t = (key: string, fallbackText = key) => resources.value[key] ?? fallbackText
const visibleItems = computed(() => state.sortMode === 'alphabetical' ? alphabeticalItems(state.items) : state.items)
const recent = computed(() => recentItems(state.recent.length ? state.recent : state.items))

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
function beginDrag(item: Item, event: DragEvent) {
  if (state.sortMode !== 'custom') return
  draggingId.value = item.id
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', item.id)
  }
}
function moveDrag(over: Item, event: DragEvent) {
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
  if (state.sortMode !== 'custom' || !draggingId.value || draggingId.value === over.id) return
  const ids = reorderIds(state.items.map(item => item.id), draggingId.value, over.id)
  state.items = ids.map(id => state.items.find(item => item.id === id)!).filter(Boolean)
  markDragClickSuppressed()
}
function finishDrag() {
  if (draggingId.value) {
    markDragClickSuppressed()
    void run('setCustomOrder', { ids: state.items.map(item => item.id) })
  }
  draggingId.value = null
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
  if (event.key === 'Escape') hideLauncher()
}

onMounted(() => {
  const offState = onStateChanged(apply)
  const offDrop = onDropResult(handleDropResult)
  const offPresentation = onPresentationChanged(next => { Object.assign(presentation, next); void loadResources() })
  window.addEventListener('keydown', onKeyDown)
  void initialLoad()
  onUnmounted(() => {
    offState(); offDrop(); offPresentation(); window.removeEventListener('keydown', onKeyDown)
    if (suppressClickTimer !== undefined) window.clearTimeout(suppressClickTimer)
  })
})
</script>

<template>
  <main class="launcher-viewport" :class="{ recording }" @pointerdown.self="hideLauncher">
    <section class="launcher-panel" @pointerdown.stop>
      <template v-if="view === 'main'">
        <header class="panel-header">
          <div class="brand"><span class="brand-mark">Q</span><h1>{{ t('view.launcher', 'Launcher') }}</h1></div>
          <div class="top-actions">
            <div class="segmented" role="group" :aria-label="t('sort.custom', 'Sort')">
              <button :class="{ active: state.sortMode === 'custom' }" @click="switchSort('custom')">{{ t('sort.custom', 'Custom') }}</button>
              <button :class="{ active: state.sortMode === 'alphabetical' }" @click="switchSort('alphabetical')">{{ t('sort.alphabetical', 'A-Z') }}</button>
            </div>
            <button class="icon-button" :aria-label="t('actions.settings', 'Settings')" :title="t('actions.settings', 'Settings')" @click="view = 'settings'">&#9881;</button>
          </div>
        </header>
        <section class="drop-area">
          <div v-if="visibleItems.length" class="launcher-grid">
            <article v-for="item in visibleItems" :key="item.id" class="app-tile" :class="{ dragging: draggingId === item.id }" :draggable="state.sortMode === 'custom'" tabindex="0" @dragstart="beginDrag(item, $event)" @dragover.prevent="moveDrag(item, $event)" @drop.prevent="finishDrag" @dragend="finishDrag" @click="activate(item)" @keydown.enter="launch(item)">
              <div class="app-icon"><img v-if="iconFor(item)" :src="iconFor(item)" :alt="item.name" /><span v-else>{{ item.name.slice(0, 1).toUpperCase() }}</span></div>
              <div class="app-name" :title="item.name">{{ item.name }}</div>
              <button class="remove-button" :aria-label="`${t('actions.remove', 'Remove')} ${item.name}`" @click.stop="remove(item)">&#215;</button>
            </article>
          </div>
          <div v-else class="empty-drop"><div class="empty-grid">&#43;</div><strong>{{ t('view.overlayEmpty', 'Drag an app or shortcut here') }}</strong></div>
        </section>
        <section class="recent-section">
          <div class="section-heading"><span class="section-label">{{ t('view.recent', 'Recently used') }}</span></div>
          <div v-if="recent.length" class="recent-row"><button v-for="item in recent" :key="item.id" class="recent-tile" @click="launch(item)"><span class="recent-icon"><img v-if="iconFor(item)" :src="iconFor(item)" :alt="item.name" /><span v-else>{{ item.name.slice(0, 1).toUpperCase() }}</span></span><span>{{ item.name }}</span></button></div>
          <p v-else class="muted">{{ t('view.noRecent', 'Nothing launched yet.') }}</p>
        </section>
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
