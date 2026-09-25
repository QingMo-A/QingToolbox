<script setup lang="ts">
import LauncherIcon from './LauncherIcon.vue'
import LauncherAppIcon from './LauncherAppIcon.vue'
import { OverlayInteraction } from './overlayInteraction'
import LauncherGrid from './LauncherGrid.vue'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  getContext,
  hideModuleWindow,
  invokeModule,
  parseSearchMode,
  setModuleHotkey,
  setHotkeyRecording,
  type EverythingResult,
  type EverythingSearchMode,
  type EverythingSearchResponse,
  type LauncherItem,
  type LauncherState,
  type ModuleContext,
} from './bridge'

const panel = ref<HTMLElement | null>(null)
const searchInput = ref<HTMLInputElement | null>(null)
const overlay = ref(new OverlayInteraction())
const recentContainer = ref<HTMLElement | null>(null)
const recentCount = ref(5)
let recentObserver: ResizeObserver | undefined
const overlayListeners: UnlistenFn[] = []
let disposed = false

function isOutsidePanel(event: PointerEvent): boolean {
  return !(event.target as HTMLElement).closest('.launcher-shell')
}
function beginBlankClick(event: PointerEvent): void {
  if (event.button !== 0) return
  overlay.value.begin(event.pointerId, event.clientX, event.clientY, isOutsidePanel(event))
}
function endBlankClick(event: PointerEvent): void {
  const dismiss = overlay.value.end(event.pointerId, event.clientX, event.clientY, isOutsidePanel(event))
  if (dismiss && !busy.value && !recordingHotkey.value) void hideModuleWindow()
}
function internalDragChanged(id: string | null): void {
  draggedId.value = id
  if (!id) customDropHover.value = false
  overlay.value.internalDrag(Boolean(id))
}
function reveal(focusSearch = true): void {
  overlay.value.cancel()
  if (!window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    panel.value?.animate([{ opacity: 0, transform: 'translateY(10px) scale(.985)' }, { opacity: 1, transform: 'none' }], { duration: 200, easing: 'cubic-bezier(.2,.8,.2,1)' })
  }
  if (focusSearch && !recordingHotkey.value && !overlay.value.external) searchInput.value?.focus({ preventScroll: true })
}
function addOverlayListener<T>(event: string, callback: (payload: T) => void): void {
  void listen<T>(event, message => callback(message.payload)).then(unlisten => {
    if (disposed) unlisten()
    else overlayListeners.push(unlisten)
  }).catch(() => {})
}

const DEFAULT_HOTKEY = 'Ctrl+Alt+L'

const emptyState: LauncherState = {
  sortMode: 'custom',
  items: [],
  folders: [],
  customOrder: [],
  recent: [],
  hotkey: { ctrl: true, alt: true, shift: false, win: false, virtualKey: 76, keyLabel: 'L' },
  hotkeyStatus: 'HostManaged',
  active: true,
}

const context = ref<ModuleContext | null>(null)
const state = ref<LauncherState>(structuredClone(emptyState))
// Keep the initial value independent from the mapping helpers below. In a
// production module bundle, setup code runs before later const declarations;
// calling formatHotkey here would read namedHotkeys while it is still in the
// temporal dead zone and leave Vue with an empty root.
const hotkeyDraft = ref(DEFAULT_HOTKEY)
const query = ref('')
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const everythingResults = ref<EverythingResult[]>([])
const everythingStatus = ref<EverythingSearchResponse['status']>('idle')
const everythingError = ref('')
const selectedEverythingIndex = ref(-1)
const resultMenu = ref<{ result: EverythingResult; x: number; y: number } | null>(null)
const draggedId = ref<string | null>(null)
const customDropHover = ref(false)
const openFolderId = ref<string | null>(null)
const hotkeyPanelOpen = ref(false)
const recordingHotkey = ref(false)
let recordingSession: number | null = null
let recordingEpoch = 0
let recordingStop: Promise<unknown> = Promise.resolve()
let recordingKeyQuietUntil = 0
const hotkeySaving = ref(false)
const hotkeyError = ref('')
const hotkeyInput = ref<HTMLInputElement | null>(null)
let everythingDebounceTimer: number | undefined
let everythingRequestSerial = 0
let unlistenStateChanged: UnlistenFn | undefined

const namedHotkeys: Record<string, { token: string; label: string; virtualKey: number }> = {
  Space: { token: 'Space', label: 'Space', virtualKey: 0x20 },
  Enter: { token: 'Enter', label: 'Enter', virtualKey: 0x0d },
  Escape: { token: 'Escape', label: 'Esc', virtualKey: 0x1b },
  Tab: { token: 'Tab', label: 'Tab', virtualKey: 0x09 },
  Backspace: { token: 'Backspace', label: 'Backspace', virtualKey: 0x08 },
  Delete: { token: 'Delete', label: 'Delete', virtualKey: 0x2e },
  Insert: { token: 'Insert', label: 'Insert', virtualKey: 0x2d },
  Home: { token: 'Home', label: 'Home', virtualKey: 0x24 },
  End: { token: 'End', label: 'End', virtualKey: 0x23 },
  PageUp: { token: 'PageUp', label: 'Page Up', virtualKey: 0x21 },
  PageDown: { token: 'PageDown', label: 'Page Down', virtualKey: 0x22 },
  ArrowUp: { token: 'ArrowUp', label: '↑', virtualKey: 0x26 },
  ArrowDown: { token: 'ArrowDown', label: '↓', virtualKey: 0x28 },
  ArrowLeft: { token: 'ArrowLeft', label: '←', virtualKey: 0x25 },
  ArrowRight: { token: 'ArrowRight', label: '→', virtualKey: 0x27 },
  Backquote: { token: 'Backquote', label: '`', virtualKey: 0xc0 },
  Minus: { token: 'Minus', label: '-', virtualKey: 0xbd },
  Equal: { token: 'Equal', label: '=', virtualKey: 0xbb },
  BracketLeft: { token: 'BracketLeft', label: '[', virtualKey: 0xdb },
  BracketRight: { token: 'BracketRight', label: ']', virtualKey: 0xdd },
  Backslash: { token: 'Backslash', label: '\\', virtualKey: 0xdc },
  Semicolon: { token: 'Semicolon', label: ';', virtualKey: 0xba },
  Quote: { token: 'Quote', label: "'", virtualKey: 0xde },
  Comma: { token: 'Comma', label: ',', virtualKey: 0xbc },
  Period: { token: 'Period', label: '.', virtualKey: 0xbe },
  Slash: { token: 'Slash', label: '/', virtualKey: 0xbf },
}

const namedHotkeyAliases: Record<string, string> = {
  esc: 'Escape',
  escape: 'Escape',
  'page up': 'PageUp',
  'page down': 'PageDown',
  up: 'ArrowUp',
  down: 'ArrowDown',
  left: 'ArrowLeft',
  right: 'ArrowRight',
  super: 'Super',
  win: 'Super',
}

const search = computed(() => parseSearchMode(query.value))
const everythingActive = computed(() => search.value.mode !== 'normal')
const everythingBadge = computed(() => ({
  'everything-all': 'Everything',
  'everything-file': 'Everything · 文件',
  'everything-directory': 'Everything · 文件夹',
}[search.value.mode as Exclude<EverythingSearchMode, 'normal'>] ?? 'Everything'))

function displayKeyLabel(value: string): string {
  if (value.startsWith('Key') && value.length === 4) return value.slice(3)
  if (value.startsWith('Digit') && value.length === 6) return value.slice(5)
  return namedHotkeys[value]?.label ?? value
}

function keyDescriptor(value: string): { token: string; label: string; virtualKey: number } | null {
  const raw = value.trim()
  const trimmed = raw.length === 1 ? raw.toUpperCase() : raw
  if (/^Key[A-Z]$/.test(trimmed)) {
    return { token: trimmed, label: trimmed.slice(3), virtualKey: trimmed.charCodeAt(3) }
  }
  if (/^Digit[0-9]$/.test(trimmed)) {
    return { token: trimmed, label: trimmed.slice(5), virtualKey: trimmed.charCodeAt(5) }
  }
  if (/^F(?:[1-9]|1[0-2])$/.test(trimmed)) {
    return { token: trimmed, label: trimmed, virtualKey: 0x6f + Number(trimmed.slice(1)) }
  }
  const canonical = namedHotkeys[trimmed]
    ? trimmed
    : namedHotkeyAliases[trimmed.toLowerCase()]
  if (canonical && namedHotkeys[canonical]) return namedHotkeys[canonical]
  if (/^[A-Z]$/.test(trimmed)) {
    return { token: `Key${trimmed}`, label: trimmed, virtualKey: trimmed.charCodeAt(0) }
  }
  if (/^[0-9]$/.test(trimmed)) {
    return { token: `Digit${trimmed}`, label: trimmed, virtualKey: trimmed.charCodeAt(0) }
  }
  return null
}

function formatHotkey(value: LauncherState['hotkey']): string {
  const parts: string[] = []
  if (value.ctrl) parts.push('Ctrl')
  if (value.alt) parts.push('Alt')
  if (value.shift) parts.push('Shift')
  if (value.win) parts.push('Win')
  parts.push(displayKeyLabel(value.keyLabel))
  return parts.join('+')
}

function hotkeyTokenFromEvent(event: KeyboardEvent): { token: string; label: string; virtualKey: number } | null {
  const code = event.code
  if (/^Key[A-Z]$/.test(code)) return { token: code, label: code.slice(3), virtualKey: code.charCodeAt(3) }
  if (/^Digit[0-9]$/.test(code)) return { token: code, label: code.slice(5), virtualKey: code.charCodeAt(5) }
  if (/^F(?:[1-9]|1[0-2])$/.test(code)) {
    const number = Number(code.slice(1))
    return { token: code, label: code, virtualKey: 0x6f + number }
  }
  return namedHotkeys[code] ?? namedHotkeys[event.key] ?? null
}

function hotkeySpecFromText(value: string): LauncherState['hotkey'] {
  const tokens = value.split('+').map((token) => token.trim()).filter(Boolean)
  const key = keyDescriptor(tokens.pop() ?? '')
  if (!key) throw new Error('请选择一个有效的主按键。')
  const modifiers = new Set<string>()
  for (const token of tokens) {
    const normalized = token.toLowerCase()
    if (['ctrl', 'control'].includes(normalized)) modifiers.add('ctrl')
    else if (['alt', 'option'].includes(normalized)) modifiers.add('alt')
    else if (normalized === 'shift') modifiers.add('shift')
    else if (['super', 'win', 'command', 'cmd'].includes(normalized)) modifiers.add('win')
    else throw new Error(`不支持的修饰键：${token}`)
  }
  if (!modifiers.size) {
    throw new Error('快捷键至少需要 Ctrl、Alt、Shift 或 Win 中的一个修饰键。')
  }
  return {
    ctrl: modifiers.has('ctrl'),
    alt: modifiers.has('alt'),
    shift: modifiers.has('shift'),
    win: modifiers.has('win'),
    virtualKey: key.virtualKey,
    keyLabel: key.token,
  }
}

function hotkeyText(value: LauncherState['hotkey']): string {
  return [
    value.ctrl ? 'Ctrl' : '',
    value.alt ? 'Alt' : '',
    value.shift ? 'Shift' : '',
    value.win ? 'Super' : '',
    value.keyLabel,
  ].filter(Boolean).join('+')
}

const displayedHotkeyDraft = computed(() => {
  try { return formatHotkey(hotkeySpecFromText(hotkeyDraft.value)) }
  catch { return hotkeyDraft.value }
})

function stopHotkeyRecording(): void {
  if (recordingHotkey.value) recordingKeyQuietUntil = performance.now() + 300
  recordingEpoch++
  recordingHotkey.value = false
  window.removeEventListener('keydown', captureHotkey, true)
  if (recordingSession !== null) {
    recordingSession = null
    recordingStop = setHotkeyRecording(false).catch(reason => { hotkeyError.value = messageOf(reason) })
  }
}

function nativeRecordedKey(value: { session: number; virtualKey: number; ctrl: boolean; alt: boolean; shift: boolean; win: boolean }): void {
  if (!recordingHotkey.value || value.session !== recordingSession) return
  const vk = value.virtualKey
  const key = vk >= 65 && vk <= 90 ? `Key${String.fromCharCode(vk)}`
    : vk >= 48 && vk <= 57 ? `Digit${String.fromCharCode(vk)}`
    : vk >= 112 && vk <= 123 ? `F${vk - 111}`
    : Object.values(namedHotkeys).find(key => key.virtualKey === vk)?.token
  captureHotkey(new KeyboardEvent('keydown', { code: key ?? '', key: vk === 27 ? 'Escape' : key ?? '',
    ctrlKey: value.ctrl, altKey: value.alt, shiftKey: value.shift, metaKey: value.win }))
}

function captureHotkey(event: KeyboardEvent): void {
  if (!recordingHotkey.value) return
  if (event.key === 'Escape' && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
    event.preventDefault()
    event.stopImmediatePropagation()
    stopHotkeyRecording()
    return
  }
  // Native WebView accelerator handling intercepts Alt/Ctrl combinations;
  // this DOM path also covers ordinary Shift+letter keys and browser tests.
  event.preventDefault()
  event.stopImmediatePropagation()
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(event.key)) return
  const key = hotkeyTokenFromEvent(event)
  if (!key) {
    hotkeyError.value = '这个按键暂不支持，请换一个主按键。'
    return
  }
  const modifiers = [
    event.ctrlKey ? 'Ctrl' : '',
    event.altKey ? 'Alt' : '',
    event.shiftKey ? 'Shift' : '',
    event.metaKey ? 'Super' : '',
  ].filter(Boolean)
  if (!modifiers.length) {
    hotkeyError.value = '快捷键至少需要一个修饰键。'
    return
  }
  hotkeyDraft.value = `${modifiers.join('+')}+${key.token}`
  hotkeyError.value = ''
  stopHotkeyRecording()
}

async function startHotkeyRecording(): Promise<void> {
  hotkeyError.value = ''
  if (recordingHotkey.value) {
    stopHotkeyRecording()
    return
  }
  recordingHotkey.value = true
  const epoch = ++recordingEpoch
  try {
    await recordingStop
    const session = await setHotkeyRecording(true)
    if (epoch !== recordingEpoch || disposed) { await setHotkeyRecording(false); return }
    recordingSession = session
    window.addEventListener('keydown', captureHotkey, true)
    await nextTick()
    hotkeyInput.value?.focus()
  } catch (reason) {
    if (epoch === recordingEpoch) { recordingHotkey.value = false; hotkeyError.value = messageOf(reason) }
  }
}

async function saveHotkey(): Promise<void> {
  if (hotkeySaving.value || !hotkeyDraft.value) return
  stopHotkeyRecording()
  await recordingStop
  let next: LauncherState['hotkey']
  try {
    next = hotkeySpecFromText(hotkeyDraft.value)
  } catch (reason) {
    hotkeyError.value = messageOf(reason)
    return
  }
  const previousText = state.value.hotkey ? hotkeyText(state.value.hotkey) : DEFAULT_HOTKEY
  hotkeySaving.value = true
  hotkeyError.value = ''
  try {
    // Register first so an unavailable OS shortcut cannot leave the module
    // claiming a binding that the host could not actually own.
    await setModuleHotkey(hotkeyText(next))
    state.value = await invokeModule<LauncherState>('setHotkey', next)
    hotkeyDraft.value = hotkeyText(next)
  } catch (reason) {
    try { await setModuleHotkey(previousText) } catch { /* keep the diagnostic below */ }
    hotkeyError.value = messageOf(reason)
  } finally {
    hotkeySaving.value = false
  }
}

async function resetHotkey(): Promise<void> {
  hotkeyDraft.value = DEFAULT_HOTKEY
  await saveHotkey()
}

const filteredItems = computed(() => {
  if (everythingActive.value) return []
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

function everythingStatusLabel(): string {
  if (everythingStatus.value === 'searching') return '正在搜索 Everything…'
  if (everythingStatus.value === 'indexing') return 'Everything 正在建立索引，请稍后重试'
  if (everythingStatus.value === 'unavailable') return '内置 Everything 暂不可用'
  if (everythingStatus.value === 'error') return 'Everything 搜索暂时不可用'
  if (!search.value.query) return '输入关键词开始搜索'
  return `${everythingResults.value.length} 个结果`
}

function scheduleEverythingSearch(): void {
  if (everythingDebounceTimer !== undefined) window.clearTimeout(everythingDebounceTimer)
  const serial = ++everythingRequestSerial
  everythingResults.value = []
  selectedEverythingIndex.value = -1
  resultMenu.value = null
  everythingError.value = ''
  if (!everythingActive.value) {
    everythingStatus.value = 'idle'
    return
  }
  everythingStatus.value = 'searching'
  everythingDebounceTimer = window.setTimeout(() => {
    everythingDebounceTimer = undefined
    void runEverythingSearch(serial, search.value.mode, search.value.query)
  }, 100)
}

async function runEverythingSearch(serial: number, mode: EverythingSearchMode, value: string): Promise<void> {
  if (mode === 'normal') return
  try {
    const response = await invokeModule<EverythingSearchResponse>('searchEverything', {
      mode,
      query: value,
      requestId: `ui-${serial}`,
    })
    // A slow IPC response must never overwrite a newer query or a return to
    // normal Launcher mode.
    if (serial !== everythingRequestSerial || search.value.mode !== mode) return
    everythingStatus.value = response.status
    everythingResults.value = Array.isArray(response.results) ? response.results : []
    everythingError.value = response.error ?? ''
    selectedEverythingIndex.value = everythingResults.value.length ? 0 : -1
  } catch (reason) {
    if (serial !== everythingRequestSerial || search.value.mode !== mode) return
    everythingStatus.value = 'error'
    everythingResults.value = []
    everythingError.value = messageOf(reason)
  }
}

function resultTypeLabel(result: EverythingResult): string {
  return result.isDirectory || result.resultType === 'directory' ? '文件夹' : '文件'
}

function resultIcon(result: EverythingResult): string {
  return result.isDirectory || result.resultType === 'directory' ? '▱' : '□'
}

function selectEverythingResult(index: number): void {
  if (index < 0 || index >= everythingResults.value.length) return
  selectedEverythingIndex.value = index
}

function showEverythingMenu(event: MouseEvent, result: EverythingResult): void {
  event.preventDefault()
  const width = 188
  const height = 132
  resultMenu.value = {
    result,
    x: Math.min(event.clientX, Math.max(8, window.innerWidth - width - 8)),
    y: Math.min(event.clientY, Math.max(8, window.innerHeight - height - 8)),
  }
  selectEverythingResult(everythingResults.value.indexOf(result))
}

function closeEverythingMenu(): void {
  resultMenu.value = null
}

async function openEverythingResult(result: EverythingResult): Promise<void> {
  if (busy.value) return
  busy.value = true
  everythingError.value = ''
  try {
    await invokeModule('openEverythingResult', { resultId: result.id })
    await hideModuleWindow()
  } catch (reason) {
    everythingError.value = messageOf(reason)
    everythingStatus.value = 'error'
  } finally {
    busy.value = false
    closeEverythingMenu()
  }
}

async function openEverythingResultFolder(result: EverythingResult): Promise<void> {
  if (busy.value) return
  busy.value = true
  everythingError.value = ''
  try {
    await invokeModule('openEverythingResultFolder', { resultId: result.id })
    await hideModuleWindow()
  } catch (reason) {
    everythingError.value = messageOf(reason)
    everythingStatus.value = 'error'
  } finally {
    busy.value = false
    closeEverythingMenu()
  }
}

async function copyEverythingResultPath(result: EverythingResult): Promise<void> {
  if (busy.value) return
  busy.value = true
  everythingError.value = ''
  try {
    await invokeModule('copyEverythingResultPath', { resultId: result.id })
    everythingStatus.value = 'ready'
  } catch (reason) {
    everythingError.value = messageOf(reason)
    everythingStatus.value = 'error'
  } finally {
    busy.value = false
    closeEverythingMenu()
  }
}

function onWindowKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && performance.now() < recordingKeyQuietUntil) { event.preventDefault(); return }
  if (event.key === 'Escape' && hotkeyPanelOpen.value && !recordingHotkey.value) {
    event.preventDefault(); hotkeyPanelOpen.value = false; return
  }
  if (event.key === 'Escape' && openFolderId.value) { event.preventDefault(); openFolderId.value = null; return }
  if (overlay.value.external) return
  if (resultMenu.value && event.key === 'Escape') {
    event.preventDefault()
    closeEverythingMenu()
    return
  }
  if (everythingActive.value) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (!everythingResults.value.length) return
      event.preventDefault()
      const delta = event.key === 'ArrowDown' ? 1 : -1
      const count = everythingResults.value.length
      selectedEverythingIndex.value = (selectedEverythingIndex.value + delta + count) % count
      return
    }
    if (event.key === 'Enter') {
      const selected = everythingResults.value[selectedEverythingIndex.value]
      if (selected) {
        event.preventDefault()
        void openEverythingResult(selected)
      }
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      closeEverythingMenu()
      // `/e` by itself is a mode selector, not a search. Preserve the
      // launcher's established Escape-to-hide behavior until the user has
      // entered an actual Everything query; once a query exists, Escape
      // clears it and immediately returns to the normal Launcher surface.
      if (search.value.query.trim()) query.value = ''
      else void hideModuleWindow()
    }
    return
  }
  if (event.key === 'Escape' && !query.value) {
    event.preventDefault()
    void hideModuleWindow()
  }
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    context.value = await getContext()
    state.value = await invokeModule<LauncherState>('getState')
    hotkeyDraft.value = formatHotkey(state.value.hotkey)
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    loading.value = false
    if (everythingActive.value) scheduleEverythingSearch()
  }
}

async function reloadStateAfterHostEvent(): Promise<void> {
  // The event carries no filesystem data. Ask the module for its normal
  // backend-owned projection so dropped paths never become a WebView input.
  if (loading.value || busy.value || everythingActive.value || draggedId.value) return
  try {
    state.value = await invokeModule<LauncherState>('getState')
    if (!recordingHotkey.value && !hotkeySaving.value) hotkeyDraft.value = formatHotkey(state.value.hotkey)
  } catch (reason) {
    error.value = messageOf(reason)
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

async function addDesktopItemToCustom(id: string): Promise<void> {
  if (busy.value || state.value.sortMode !== 'desktop') return
  busy.value = true
  error.value = ''
  try {
    state.value = await invokeModule<LauncherState>('addDesktopItemToCustom', { id })
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

type LauncherTile = LauncherItem | LauncherState['folders'][number]
const gridTiles = computed<LauncherTile[]>(() => {
  if (state.value.sortMode !== 'custom' || query.value.trim()) return filteredItems.value
  const all: LauncherTile[] = [...state.value.items, ...state.value.folders]
  const map = new Map(all.map(tile => [tile.id, tile]))
  return [...new Set([...state.value.customOrder, ...all.map(tile => tile.id)])]
    .map(id => map.get(id)).filter((tile): tile is LauncherTile => Boolean(tile))
})
function clickItem(item: LauncherItem): void { void launch(item) }
function openTile(tile: LauncherTile): void {
  if ('items' in tile) openFolderId.value = tile.id
  else void launch(tile)
}
async function saveGridOrder(ids: string[]): Promise<void> {
  busy.value = true
  error.value = ''
  try { state.value = await invokeModule<LauncherState>('setCustomOrder', { ids }) }
  catch (reason) { error.value = messageOf(reason); throw reason }
  finally { busy.value = false }
}
async function moveIntoFolder(itemId: string, folderId: string): Promise<void> {
  await invokeState('moveItemToFolder', { itemId, folderId })
}

async function removeItem(id: string): Promise<void> {
  if (state.value.sortMode === 'desktop') return
  await invokeState('removeItem', { id })
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

watch(query, () => scheduleEverythingSearch())
watch(hotkeyPanelOpen, open => { if (!open) stopHotkeyRecording() })
watch(recentContainer, element => {
  recentObserver?.disconnect()
  if (element) recentObserver?.observe(element)
}, { flush: 'post' })
watch(() => state.value.hotkey, (value) => {
  if (!recordingHotkey.value && !hotkeySaving.value) hotkeyDraft.value = formatHotkey(value)
}, { deep: true })

onMounted(() => {
  addOverlayListener<Parameters<typeof nativeRecordedKey>[0]>('launcher:recorded-key', nativeRecordedKey)
  addOverlayListener<number>('launcher:recording-stopped', session => {
    if (session !== recordingSession) return
    recordingSession = null
    stopHotkeyRecording()
  })
  addOverlayListener<{ active: boolean }>('launcher:external-drag', payload => overlay.value.externalDrag(payload.active))
  addOverlayListener<{ focusSearch: boolean }>('launcher:shown', payload => reveal(payload.focusSearch))
  recentObserver = new ResizeObserver(() => { recentCount.value = Math.max(1, Math.floor((recentContainer.value?.clientWidth ?? 740) / 148)) })
  if (recentContainer.value) recentObserver.observe(recentContainer.value)
  reveal(document.hasFocus())
  window.addEventListener('keydown', onWindowKeydown)
  void load()
  void listen<{ reason?: string }>('qmod:module-state-changed', async ({ payload }) => {
    if (payload.reason === 'externalDrop') {
      query.value = ''
      hotkeyPanelOpen.value = false
      openFolderId.value = null
      if (state.value.sortMode !== 'custom') await setMode('custom')
    }
    void reloadStateAfterHostEvent()
  }).then((unlisten) => {
    unlistenStateChanged = unlisten
  }).catch(() => {
    // Browser preview and older hosts do not expose the optional event bridge;
    // the rest of the launcher remains fully usable there.
  })
})
onBeforeUnmount(() => {
  disposed = true
  overlayListeners.splice(0).forEach(unlisten => unlisten())
  recentObserver?.disconnect()
  stopHotkeyRecording()
  if (everythingDebounceTimer !== undefined) window.clearTimeout(everythingDebounceTimer)
  unlistenStateChanged?.()
  unlistenStateChanged = undefined
  window.removeEventListener('keydown', onWindowKeydown)
})
</script>

<template>
  <main class="launcher-viewport" @pointerdown="beginBlankClick" @pointerup="endBlankClick" @pointercancel="overlay.cancel()" @contextmenu.prevent>
  <section ref="panel" class="launcher-shell" :class="{ 'external-drag': overlay.external }">
    <header class="topbar">
      <div class="brand">
        <div class="brand-mark" aria-hidden="true">
          <LauncherIcon name="grid" />
        </div>
        <div>
          <h1>启动台</h1>
        </div>
      </div>
      <div class="top-actions">
        <nav class="mode-tabs" aria-label="排序方式">
          <button v-for="mode in (['custom', 'alphabetical', 'desktop'] as const)" :key="mode" type="button" :data-mode="mode" :class="{ active: state.sortMode === mode, 'desktop-drop-target': mode === 'custom' && state.sortMode === 'desktop' && !!draggedId, 'desktop-drop-hover': mode === 'custom' && customDropHover }" :disabled="busy || everythingActive || (!!draggedId && !(state.sortMode === 'desktop' && mode === 'custom'))" @click="!draggedId && setMode(mode)">
            {{ { custom: '自定义', alphabetical: '首字母', desktop: '桌面' }[mode] }}
          </button>
        </nav>
        <button v-if="state.sortMode === 'custom' && !everythingActive" class="folder-add icon-button" type="button" aria-label="新建文件夹" title="新建文件夹" :disabled="busy" @click="createFolder"><LauncherIcon name="folderAdd" /></button>
        <button class="hotkey-toggle" type="button" aria-label="设置" title="设置" :aria-expanded="hotkeyPanelOpen" @click="hotkeyPanelOpen = !hotkeyPanelOpen">
          <LauncherIcon name="settings" />
        </button>
        <button class="refresh" type="button" aria-label="刷新应用" title="刷新应用" :disabled="busy || loading" @click="refresh"><LauncherIcon name="refresh" /></button>
      </div>
    </header>

    <section class="search-row" aria-label="搜索应用">
      <LauncherIcon class="search-icon" name="search" />
      <span v-if="everythingActive" class="everything-badge" :title="everythingBadge">{{ everythingBadge }}</span>
      <input ref="searchInput" v-model="query" type="search" aria-label="搜索应用" placeholder="搜索应用…" autocomplete="off" spellcheck="false" />
      <button v-if="query" class="clear" type="button" aria-label="清空搜索" @click="query = ''"><LauncherIcon name="close" /></button>
    </section>



    <section v-if="hotkeyPanelOpen" class="hotkey-panel" aria-labelledby="hotkey-title">
      <div>
        <h2 id="hotkey-title">快捷键</h2>
      </div>
      <div class="hotkey-controls">
        <input
          ref="hotkeyInput"
          class="hotkey-input"
          :value="recordingHotkey ? '请按下组合键…' : (displayedHotkeyDraft || formatHotkey(state.hotkey))"
          readonly
          aria-label="启动台快捷键"
          @click="startHotkeyRecording"
          @keydown="captureHotkey"
        />
        <button type="button" class="hotkey-action" :disabled="hotkeySaving" @click="startHotkeyRecording">
          {{ recordingHotkey ? '取消录入' : '重新录入' }}
        </button>
        <button type="button" class="hotkey-action secondary" :disabled="hotkeySaving || !hotkeyDraft" @click="saveHotkey">保存</button>
        <button type="button" class="hotkey-action secondary" :disabled="hotkeySaving" @click="resetHotkey">恢复默认</button>
      </div>
      <p v-if="hotkeyError" class="hotkey-error" role="alert">{{ hotkeyError }}</p>
      <p class="hotkey-current">当前：{{ formatHotkey(state.hotkey) }}</p>
      <button class="settings-close icon-button" aria-label="关闭设置" @click="stopHotkeyRecording(); hotkeyPanelOpen = false"><LauncherIcon name="close" /></button>
    </section>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-if="everythingActive && everythingError && everythingStatus !== 'indexing'" class="error everything-error" role="alert">{{ everythingError }}</p>
    <section v-if="loading" class="loading-card" aria-live="polite">
      <span class="spinner" aria-hidden="true" />
      <div><strong>正在准备启动台</strong></div>
    </section>
    <section v-else class="content">
      <section v-if="everythingActive" class="everything-panel" aria-live="polite" @contextmenu.prevent>
        <div v-if="everythingStatus === 'searching'" class="everything-loading">
          <span class="spinner" aria-hidden="true" />
          <span>正在查询内置 Everything…</span>
        </div>
        <div v-else-if="!everythingResults.length" class="empty everything-empty">
          <LauncherIcon class="empty-icon" name="search" />
          <strong>{{ everythingStatus === 'indexing' ? 'Everything 索引尚未就绪' : everythingError || (search.query ? '没有找到匹配结果' : '输入关键词开始搜索') }}</strong>
          <small v-if="everythingStatus === 'indexing'">{{ everythingStatusLabel() }}；普通启动台搜索不受影响。</small>
          <small v-else>{{ everythingError ? '普通启动台搜索仍可正常使用' : '支持 *.exe、file:、folder: 等 Everything 查询语法' }}</small>
          <button v-if="everythingStatus === 'indexing' || everythingStatus === 'unavailable' || everythingStatus === 'error'" type="button" class="everything-retry" @click="scheduleEverythingSearch">重试</button>
        </div>
        <div v-else class="everything-list" role="listbox" aria-label="Everything 搜索结果" :aria-activedescendant="selectedEverythingIndex >= 0 ? `everything-result-${everythingResults[selectedEverythingIndex]?.id}` : undefined">
          <article
            v-for="(result, index) in everythingResults"
            :id="`everything-result-${result.id}`"
            :key="result.id"
            class="everything-result"
            :class="{ selected: selectedEverythingIndex === index }"
            role="option"
            :aria-selected="selectedEverythingIndex === index"
            @mouseenter="selectEverythingResult(index)"
            @click="openEverythingResult(result)"
            @contextmenu="showEverythingMenu($event, result)"
          >
            <span class="everything-result-icon"><LauncherIcon :name="result.isDirectory ? 'folder' : 'file'" /></span>
            <span class="everything-result-copy">
              <strong :title="result.name">{{ result.name }}</strong>
              <small :title="result.parentPath">{{ result.parentPath || '—' }}</small>
            </span>
            <span class="everything-result-type">{{ resultTypeLabel(result) }}</span>
          </article>
        </div>
      </section>
      <template v-else>
      <div v-if="!filteredItems.length && !(state.sortMode === 'custom' && !query.trim() && state.folders.length)" class="empty">
          <LauncherIcon class="empty-icon" :name="query ? 'search' : 'grid'" />
          <strong>{{ query ? '没有匹配的应用' : state.sortMode === 'desktop' ? '桌面暂无应用' : '拖入应用或快捷方式' }}</strong>
        </div>
      <LauncherGrid v-if="gridTiles.length" :tiles="gridTiles" :enabled="state.sortMode !== 'alphabetical' && !query.trim()" :desktop-mode="state.sortMode === 'desktop'"
        :busy="busy" :save-order="saveGridOrder" @open="openTile" @rename="renameFolder" @remove-folder="deleteFolder"
        @move-into="moveIntoFolder" @remove-item="removeItem" @dragging="internalDragChanged" @custom-hover="customDropHover = $event" @drop-to-custom="addDesktopItemToCustom" />

      <Transition name="folder-open">
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
                <span class="recent-icon">
                  <LauncherAppIcon :icon-key="item.iconKey" />
                </span>
                <span>{{ item.name }}</span>
              </button>
              <div class="folder-item-actions">
                <button type="button" :disabled="index === 0 || busy" @click="moveFolderItem(state.folders.find((folder) => folder.id === openFolderId)!, index, -1)">↑</button>
                <button type="button" :disabled="index === (state.folders.find((folder) => folder.id === openFolderId)?.items.length ?? 1) - 1 || busy" @click="moveFolderItem(state.folders.find((folder) => folder.id === openFolderId)!, index, 1)">↓</button>
                <button type="button" :disabled="busy" @click="moveOutOfFolder(item, state.folders.find((folder) => folder.id === openFolderId)!)">移出</button>
              </div>
            </div>
            <p v-if="!(state.folders.find((folder) => folder.id === openFolderId)?.items.length)" class="folder-panel-empty">文件夹为空</p>
          </div>
        </div>
      </section>
      </Transition>

      <section class="recent">
        <div class="section-title"><LauncherIcon name="clock" /><span>最近启动</span></div>
        <div ref="recentContainer" class="recent-list">
          <button v-for="item in state.recent.slice(0, recentCount)" :key="item.id" type="button" class="recent-item" @click="launch(item)">
            <span class="recent-icon">
              <LauncherAppIcon :icon-key="item.iconKey" />
            </span><span>{{ item.name }}</span>
          </button>
          <span v-if="!state.recent.length" class="recent-empty">—</span>
        </div>
      </section>
      </template>
    </section>

    <div v-if="resultMenu" class="result-menu-backdrop" @click="closeEverythingMenu" @contextmenu.prevent="closeEverythingMenu">
      <div class="result-menu" :style="{ left: `${resultMenu.x}px`, top: `${resultMenu.y}px` }" role="menu" @click.stop>
        <strong class="result-menu-title" :title="resultMenu.result.name">{{ resultMenu.result.name }}</strong>
        <button type="button" role="menuitem" @click="openEverythingResult(resultMenu.result)">打开</button>
        <button type="button" role="menuitem" @click="openEverythingResultFolder(resultMenu.result)">打开所在目录</button>
        <button type="button" role="menuitem" @click="copyEverythingResultPath(resultMenu.result)">复制文件路径</button>
      </div>
    </div>


    <Transition name="drop-hint"><div v-if="overlay.external" class="external-drop-hint"><LauncherIcon name="upload" /><strong>松开添加到自定义</strong></div></Transition>
  </section>
  </main>
</template>
