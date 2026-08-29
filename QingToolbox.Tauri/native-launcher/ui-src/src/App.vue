<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  getContext,
  hideModuleWindow,
  invokeModule,
  type LauncherItem,
  type LauncherState,
  type ModuleContext,
} from './bridge'

const emptyState: LauncherState = {
  sortMode: 'custom',
  items: [],
  folders: [],
  customOrder: [],
  recent: [],
  hotkey: { ctrl: true, alt: true, shift: false, win: false, virtualKey: 32, keyLabel: 'Space' },
  hotkeyStatus: 'Inactive',
  active: true,
}

const context = ref<ModuleContext | null>(null)
const state = ref<LauncherState>(structuredClone(emptyState))
const query = ref('')
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const draggedId = ref<string | null>(null)
const dropIndex = ref<number | null>(null)
const draggedClickGuard = ref(false)
let draggedClickGuardTimer: number | undefined

const filteredItems = computed(() => {
  const needle = query.value.trim().toLocaleLowerCase()
  return needle
    ? state.value.items.filter((item) => item.name.toLocaleLowerCase().includes(needle))
    : state.value.items
})

const modeLabel = computed(() => ({
  custom: '自定义',
  alphabetical: '首字母',
  desktop: '桌面',
}[state.value.sortMode]))

function messageOf(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
    try { return JSON.stringify(reason) } catch { /* fall through */ }
  }
  return String(reason)
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    context.value = await getContext()
    state.value = await invokeModule<LauncherState>('getState')
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    loading.value = false
  }
}

async function refresh(): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>('refreshDesktop')
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

async function setMode(mode: LauncherState['sortMode']): Promise<void> {
  if (busy.value || state.value.sortMode === mode) return
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>('setSortMode', { mode })
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

async function launch(item: LauncherItem): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>('launchItem', { id: item.id })
    await hideModuleWindow()
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

function dragStart(event: DragEvent, item: LauncherItem): void {
  if (state.value.sortMode === 'alphabetical' || query.value.trim()) {
    event.preventDefault()
    return
  }
  draggedId.value = item.id
  // `dropIndex` is a slot in the list after the dragged item is removed. This
  // keeps the insertion coordinate stable when moving an item from the front
  // towards the back (and makes the final slot a valid drop target).
  const sourceIndex = state.value.items.findIndex((value) => value.id === item.id)
  dropIndex.value = Math.max(0, sourceIndex)
  draggedClickGuard.value = false
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', item.id)
  }
}

function dragOver(event: DragEvent, index: number): void {
  if (!draggedId.value || state.value.sortMode === 'alphabetical' || query.value.trim()) return
  const item = state.value.items[index]
  if (!item) return
  if (item.id === draggedId.value) {
    event.preventDefault()
    event.stopPropagation()
    return
  }
  event.preventDefault()
  event.stopPropagation()
  const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect()
  const remainingIndex = remainingItems().findIndex((value) => value.id === item.id)
  if (remainingIndex < 0) return
  dropIndex.value = remainingIndex + (event.clientX > bounds.left + bounds.width / 2 ? 1 : 0)
}

function gridDragOver(event: DragEvent): void {
  if (!draggedId.value || state.value.sortMode === 'alphabetical' || query.value.trim()) return
  // Tile handlers stop propagation. Reaching the grid itself means the
  // pointer is over unused space, which is the explicit append position.
  event.preventDefault()
  dropIndex.value = remainingItems().length
}

function remainingItems(): LauncherItem[] {
  return state.value.items.filter((item) => item.id !== draggedId.value)
}

function remainingIndex(itemId: string): number {
  return remainingItems().findIndex((item) => item.id === itemId)
}

function showDropBefore(item: LauncherItem): boolean {
  return draggedId.value !== null && dropIndex.value === remainingIndex(item.id)
}

function showDropAfter(item: LauncherItem): boolean {
  const index = remainingIndex(item.id)
  return draggedId.value !== null && index >= 0 && index === remainingItems().length - 1 && dropIndex.value === index + 1
}

function guardClickAfterDrag(): void {
  draggedClickGuard.value = true
  if (draggedClickGuardTimer !== undefined) window.clearTimeout(draggedClickGuardTimer)
  draggedClickGuardTimer = window.setTimeout(() => {
    draggedClickGuard.value = false
    draggedClickGuardTimer = undefined
  }, 350)
}

function clickItem(item: LauncherItem): void {
  if (draggedClickGuard.value) {
    draggedClickGuard.value = false
    return
  }
  void launch(item)
}

async function drop(event: DragEvent): Promise<void> {
  event.preventDefault()
  event.stopPropagation()
  const movingId = draggedId.value
  const targetIndex = dropIndex.value
  draggedId.value = null
  dropIndex.value = null
  if (!movingId || targetIndex === null || busy.value) return
  const ids = state.value.items.map((item) => item.id).filter((id) => id !== movingId)
  ids.splice(Math.max(0, Math.min(targetIndex, ids.length)), 0, movingId)
  if (ids.every((id, index) => id === state.value.items[index]?.id)) {
    guardClickAfterDrag()
    return
  }
  guardClickAfterDrag()
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>('setCustomOrder', { ids })
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

function cancelDrag(): void {
  draggedId.value = null
  dropIndex.value = null
}

onMounted(() => { void load() })
onBeforeUnmount(() => {
  if (draggedClickGuardTimer !== undefined) window.clearTimeout(draggedClickGuardTimer)
})
</script>

<template>
  <main class="launcher-shell">
    <header class="topbar">
      <div class="brand">
        <div class="brand-mark" aria-hidden="true">
          <img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" />
          <span v-else>▦</span>
        </div>
        <div>
          <p class="eyebrow">QING TOOLBOX</p>
          <h1>启动台</h1>
        </div>
      </div>
      <div class="top-actions">
        <span class="version">Rust module · v{{ context?.version ?? '—' }}</span>
        <button class="refresh" type="button" :disabled="busy || loading" @click="refresh">↻</button>
      </div>
    </header>

    <section class="search-row" aria-label="搜索应用">
      <span class="search-icon" aria-hidden="true">⌕</span>
      <input v-model="query" type="search" placeholder="搜索应用…" />
      <button v-if="query" class="clear" type="button" aria-label="清空搜索" @click="query = ''">×</button>
    </section>

    <nav class="mode-tabs" aria-label="排序方式">
      <button v-for="mode in (['custom', 'alphabetical', 'desktop'] as const)" :key="mode" type="button" :class="{ active: state.sortMode === mode }" :disabled="busy" @click="setMode(mode)">
        {{ { custom: '自定义', alphabetical: '首字母', desktop: '桌面' }[mode] }}
      </button>
      <span class="mode-hint">{{ modeLabel }} · {{ state.items.length }} 个应用</span>
    </nav>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <section v-if="loading" class="loading-card" aria-live="polite">
      <span class="spinner" aria-hidden="true" />
      <div><strong>正在准备启动台</strong><small>读取 Rust 模块状态…</small></div>
    </section>
    <section v-else class="content">
      <div v-if="!filteredItems.length" class="empty">
        <span class="empty-icon" aria-hidden="true">⌁</span>
        <strong>{{ query ? '没有匹配的应用' : '桌面上还没有可用项目' }}</strong>
        <small>{{ query ? '换个关键词试试' : '将 .exe、.lnk 或 .url 放到桌面后刷新' }}</small>
      </div>
      <div v-else class="app-grid" :class="{ dragging: draggedId }" @dragover="gridDragOver" @dragend="cancelDrag" @drop="drop">
        <article
          v-for="(item, index) in filteredItems"
          :key="item.id"
          class="app-tile"
          :class="{ dragging: draggedId === item.id, 'drop-before': showDropBefore(item), 'drop-after': showDropAfter(item) }"
          :draggable="state.sortMode !== 'alphabetical' && !query.trim()"
          @dragstart="dragStart($event, item)"
          @dragover="dragOver($event, index)"
          @click="clickItem(item)"
        >
          <div class="app-icon" aria-hidden="true"><span>{{ item.name.slice(0, 1).toUpperCase() }}</span></div>
          <strong :title="item.name">{{ item.name }}</strong>
          <small>{{ item.source === 'desktop' ? '桌面' : '自定义' }}</small>
        </article>
      </div>

      <section v-if="state.recent.length" class="recent">
        <div class="section-title"><span>最近启动</span><small>最近 10 项</small></div>
        <div class="recent-list">
          <button v-for="item in state.recent" :key="item.id" type="button" class="recent-item" @click="launch(item)">
            <span class="recent-icon">{{ item.name.slice(0, 1).toUpperCase() }}</span><span>{{ item.name }}</span>
          </button>
        </div>
      </section>
    </section>

    <footer><span>拖动图标可调整顺序</span><span v-if="state.sortMode === 'alphabetical'">首字母模式下排序已锁定</span><span v-else>位置由 Rust 持久化</span></footer>
  </main>
</template>
