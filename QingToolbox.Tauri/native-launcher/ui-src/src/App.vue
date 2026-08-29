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
const openFolderId = ref<string | null>(null)
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

function topLevelOrder(): string[] {
  if (state.value.customOrder.length) return [...state.value.customOrder]
  return [
    ...state.value.items.map((item) => item.id),
    ...state.value.folders.map((folder) => folder.id),
  ]
}

async function invokeState(method: string, payload: Record<string, unknown> = {}): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>(method, payload)
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    busy.value = false
  }
}

async function createFolder(): Promise<void> {
  const name = window.prompt('请输入文件夹名称', '新文件夹')
  if (!name?.trim()) return
  await invokeState('createFolder', { name: name.trim() })
}

async function renameFolder(folder: LauncherState['folders'][number]): Promise<void> {
  const name = window.prompt('重命名文件夹', folder.name)
  if (!name?.trim() || name.trim() === folder.name) return
  await invokeState('renameFolder', { id: folder.id, name: name.trim() })
}

async function deleteFolder(folder: LauncherState['folders'][number]): Promise<void> {
  if (!window.confirm(`删除“${folder.name}”？其中的应用会回到启动台。`)) return
  if (openFolderId.value === folder.id) openFolderId.value = null
  await invokeState('deleteFolder', { id: folder.id })
}

async function dropIntoFolder(event: DragEvent, folder: LauncherState['folders'][number]): Promise<void> {
  event.preventDefault()
  event.stopPropagation()
  const movingId = draggedId.value
  draggedId.value = null
  dropIndex.value = null
  if (!movingId || state.value.sortMode !== 'custom' || busy.value) return
  await invokeState('moveItemToFolder', { itemId: movingId, folderId: folder.id })
  guardClickAfterDrag()
}

function allowFolderDrop(event: DragEvent): void {
  if (!draggedId.value || state.value.sortMode !== 'custom' || query.value.trim()) return
  event.preventDefault()
  event.stopPropagation()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
}

async function moveOutOfFolder(item: LauncherItem, folder: LauncherState['folders'][number]): Promise<void> {
  await invokeState('moveItemOutOfFolder', { itemId: item.id, folderId: folder.id })
}

async function moveFolderItem(folder: LauncherState['folders'][number], index: number, offset: number): Promise<void> {
  const target = index + offset
  if (target < 0 || target >= folder.items.length) return
  const ids = folder.items.map((item) => item.id)
  ;[ids[index], ids[target]] = [ids[target], ids[index]]
  await invokeState('setFolderOrder', { folderId: folder.id, ids })
}

async function drop(event: DragEvent): Promise<void> {
  event.preventDefault()
  event.stopPropagation()
  const movingId = draggedId.value
  const targetIndex = dropIndex.value
  const visibleRemaining = remainingItems()
  const currentOrder = topLevelOrder()
  draggedId.value = null
  dropIndex.value = null
  if (!movingId || targetIndex === null || busy.value) return
  const ids = currentOrder.filter((id) => id !== movingId)
  const targetItem = visibleRemaining[targetIndex]
  let insertion = targetItem ? ids.indexOf(targetItem.id) : -1
  if (insertion < 0) {
    const lastVisible = visibleRemaining.at(-1)
    insertion = lastVisible ? ids.indexOf(lastVisible.id) + 1 : ids.length
  }
  ids.splice(Math.max(0, Math.min(insertion, ids.length)), 0, movingId)
  if (ids.every((id, index) => id === currentOrder[index])) {
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
      <button v-if="state.sortMode === 'custom' && !query.trim()" class="folder-add" type="button" :disabled="busy" @click="createFolder">
        ＋ 文件夹
      </button>
    </nav>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <section v-if="loading" class="loading-card" aria-live="polite">
      <span class="spinner" aria-hidden="true" />
      <div><strong>正在准备启动台</strong><small>读取 Rust 模块状态…</small></div>
    </section>
    <section v-else class="content">
      <div v-if="!filteredItems.length && !(state.sortMode === 'custom' && !query.trim() && state.folders.length)" class="empty">
        <span class="empty-icon" aria-hidden="true">⌁</span>
        <strong>{{ query ? '没有匹配的应用' : '桌面上还没有可用项目' }}</strong>
        <small>{{ query ? '换个关键词试试' : '将 .exe、.lnk 或 .url 放到桌面后刷新' }}</small>
      </div>
      <section v-if="state.sortMode === 'custom' && !query.trim() && state.folders.length" class="folder-grid" aria-label="文件夹">
        <article
          v-for="folder in state.folders"
          :key="folder.id"
          class="folder-tile"
          :class="{ 'folder-drop-target': draggedId }"
          @click="openFolderId = folder.id"
          @dragover="allowFolderDrop"
          @drop="dropIntoFolder($event, folder)"
        >
          <div class="folder-preview" aria-hidden="true">
            <span v-for="item in folder.items.slice(0, 4)" :key="item.id">{{ item.name.slice(0, 1).toUpperCase() }}</span>
            <span v-if="!folder.items.length" class="folder-empty-mark">＋</span>
          </div>
          <strong :title="folder.name">{{ folder.name }}</strong>
          <small>{{ folder.items.length }} 个项目</small>
          <div class="folder-actions" @click.stop>
            <button type="button" aria-label="重命名文件夹" @click="renameFolder(folder)">✎</button>
            <button type="button" aria-label="删除文件夹" @click="deleteFolder(folder)">×</button>
          </div>
        </article>
      </section>
      <div v-if="filteredItems.length" class="app-grid" :class="{ dragging: draggedId }" @dragover="gridDragOver" @dragend="cancelDrag" @drop="drop">
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

      <section v-if="openFolderId && state.folders.find((folder) => folder.id === openFolderId)" class="folder-panel" role="dialog" aria-modal="true">
        <div class="folder-panel-card">
          <header>
            <div>
              <p class="eyebrow">自定义文件夹</p>
              <h2>{{ state.folders.find((folder) => folder.id === openFolderId)?.name }}</h2>
            </div>
            <button type="button" class="folder-close" aria-label="关闭文件夹" @click="openFolderId = null">×</button>
          </header>
          <div class="folder-items">
            <div v-for="(item, index) in state.folders.find((folder) => folder.id === openFolderId)?.items" :key="item.id" class="folder-item">
              <button type="button" class="folder-item-main" @click="clickItem(item)">
                <span class="recent-icon">{{ item.name.slice(0, 1).toUpperCase() }}</span>
                <span>{{ item.name }}</span>
              </button>
              <div class="folder-item-actions">
                <button type="button" :disabled="index === 0 || busy" @click="moveFolderItem(state.folders.find((folder) => folder.id === openFolderId)!, index, -1)">↑</button>
                <button type="button" :disabled="index === (state.folders.find((folder) => folder.id === openFolderId)?.items.length ?? 1) - 1 || busy" @click="moveFolderItem(state.folders.find((folder) => folder.id === openFolderId)!, index, 1)">↓</button>
                <button type="button" :disabled="busy" @click="moveOutOfFolder(item, state.folders.find((folder) => folder.id === openFolderId)!)">移出</button>
              </div>
            </div>
            <p v-if="!(state.folders.find((folder) => folder.id === openFolderId)?.items.length)" class="folder-panel-empty">把应用拖到这里的文件夹卡片即可归类。</p>
          </div>
        </div>
      </section>

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
