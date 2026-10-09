<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { QButton, QIcon, QIconButton } from '@qingtoolbox/module-ui'
import {
  clearActivities,
  clearDiagnostics,
  dismissPreview,
  emitMockActivity,
  getContext,
  getState,
  hideModuleWindow,
  previewIsland,
  readDiagnostics,
  refreshProviders,
  setSettings,
  timerCommand,
  hideTemporarily,
  restoreIsland,
} from './bridge'
import type {
  Anchor,
  Diagnostics,
  FullscreenPolicy,
  ModuleContext,
  ModuleState,
  MonitorStrategy,
  ProviderKind,
  RgbColor,
  SettingsPatch,
} from './types'
import IslandPreview from './components/IslandPreview.vue'
import ActivityRow from './components/ActivityRow.vue'
import ProviderCard from './components/ProviderCard.vue'
import DiagnosticsPanel from './components/DiagnosticsPanel.vue'
import { RefreshGate } from './refreshGate'
import MaterialGallery from './components/MaterialGallery.vue'
import { renderText, usesPlaceholder } from './textTemplate'
import TimerControls from './components/TimerControls.vue'

const context = ref<ModuleContext | null>(null)
const state = ref<ModuleState | null>(null)
const diagnostics = ref<Diagnostics | null>(null)
const busy = ref(false)
/** Set when a call fails, so the page can say what broke instead of freezing. */
const error = ref<string | null>(null)
/** Set while the settings page is showing its own standalone preview. */
const previewing = ref(false)
const customTextDraft = ref('')
const peekTextDraft = ref('')
const expandedTextDraft = ref('')
const textMode = ref<'customText' | 'peekText' | 'expandedText'>('customText')
const textDrafts = { customText: customTextDraft, peekText: peekTextDraft, expandedText: expandedTextDraft }
const textLabels = { customText: '常驻', peekText: '悬停', expandedText: '展开' }
const textHints = { customText: '胶囊里一直显示的一行', peekText: '指针停留时出现在第二行', expandedText: '点击展开后显示，第一行作为标题' }
const textInputId = computed(() => ({ customText: 'custom-text', peekText: 'peek-text', expandedText: 'expanded-text' }[textMode.value]))
const activeTextDraft = computed({ get: () => textDrafts[textMode.value].value, set: value => { textDrafts[textMode.value].value = value } })
const fallbackDraft = ref('暂无数据')
const DEFAULT_COLOR: RgbColor = { r: 21, g: 26, b: 37 }
const colorDraft = ref<RgbColor>({ ...DEFAULT_COLOR })
const geometryDraft = ref({ compactWidth: 232, offsetX: 0, offsetY: 0, scale: 1 })
const previewState = ref<'compact' | 'peek' | 'expanded'>('compact')
const colorHex = computed(() => '#' + ['r', 'g', 'b'].map(key => clampChannel(colorDraft.value[key as keyof RgbColor]).toString(16).padStart(2, '0')).join(''))
/** One-click tints; the first is the module default. */
const colorPresets: { name: string; color: RgbColor }[] = [
  { name: '墨夜', color: DEFAULT_COLOR },
  { name: '曜石', color: { r: 8, g: 8, b: 10 } },
  { name: '深海', color: { r: 14, g: 38, b: 72 } },
  { name: '松烟', color: { r: 22, g: 52, b: 44 } },
  { name: '绛紫', color: { r: 54, g: 26, b: 64 } },
  { name: '赭石', color: { r: 92, g: 44, b: 26 } },
  { name: '宣纸', color: { r: 240, g: 232, b: 215 } },
  { name: '雾白', color: { r: 244, g: 246, b: 250 } },
]

/** Polling is only used while the page is open; the module itself is event-driven. */
let poller: number | undefined
let disposed = false
let pointerHeld = false
// A read started before a user action/interaction must never overwrite it.
const refreshGate = new RefreshGate()

function refreshBlocked(): boolean {
  return disposed || document.hidden || busy.value || pointerHeld || editingInput()
}

function editingInput(): boolean {
  const element = document.activeElement
  return element instanceof HTMLTextAreaElement ||
    (element instanceof HTMLInputElement && ['text', 'search', 'number', 'color'].includes(element.type))
}

function pointerStarted(): void {
  pointerHeld = true
  refreshGate.invalidate()
}
function pointerEnded(): void { pointerHeld = false }
function focusChanged(): void { refreshGate.invalidate() }
function windowBlurred(): void {
  pointerHeld = false
  refreshGate.invalidate()
}

async function run<T>(action: () => Promise<T>, apply: (value: T) => void): Promise<void> {
  if (busy.value) return
  refreshGate.invalidate()
  busy.value = true
  error.value = null
  try {
    apply(await action())
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    busy.value = false
  }
}

function applyState(next: ModuleState): void {
  if (state.value?.settings.customText !== next.settings.customText) customTextDraft.value = next.settings.customText
  if (state.value?.settings.peekText !== next.settings.peekText) peekTextDraft.value = next.settings.peekText
  if (state.value?.settings.expandedText !== next.settings.expandedText) expandedTextDraft.value = next.settings.expandedText
  if (state.value?.settings.placeholderFallback !== next.settings.placeholderFallback) fallbackDraft.value = next.settings.placeholderFallback
  const previous = state.value?.settings.backgroundColor
  if (!previous || ['r', 'g', 'b'].some(key => previous[key as keyof RgbColor] !== next.settings.backgroundColor[key as keyof RgbColor])) {
    colorDraft.value = { ...next.settings.backgroundColor }
  }
  const geometryKeys = ['compactWidth', 'offsetX', 'offsetY', 'scale'] as const
  if (!state.value || geometryKeys.some(key => state.value!.settings[key] !== next.settings[key])) {
    geometryDraft.value = { compactWidth: next.settings.compactWidth, offsetX: next.settings.offsetX, offsetY: next.settings.offsetY, scale: next.settings.scale }
  }
  state.value = next
  previewing.value = next.island.previewActive
}

async function load(): Promise<void> {
  // Background reads are silent: never toggle `busy`/disabled, clear an action
  // error, or reset controls while a pointer or editable draft is active.
  const epoch = refreshGate.begin(refreshBlocked())
  if (epoch === null) return
  try {
    const next = await getState()
    if (refreshGate.accepts(epoch, refreshBlocked())) {
      if (!state.value) error.value = null
      applyState(next)
    }
  } catch (cause) {
    // Only an initial failure needs a page notice. Transient polling failures
    // must not repeatedly insert/remove a banner and shift the whole form.
    if (!state.value && refreshGate.accepts(epoch, refreshBlocked())) {
      error.value = cause instanceof Error ? cause.message : String(cause)
    }
  } finally {
    refreshGate.finish()
  }
}

async function patch(changes: SettingsPatch): Promise<void> {
  await run(() => setSettings(changes), applyState)
}

async function saveGeometry(): Promise<void> {
  const value = geometryDraft.value
  const number = (n: number, fallback: number, min: number, max: number) => Number.isFinite(Number(n)) ? Math.max(min, Math.min(max, Number(n))) : fallback
  geometryDraft.value = {
    compactWidth: Math.round(number(value.compactWidth, 232, 200, 480)),
    offsetX: Math.round(number(value.offsetX, 0, -4096, 4096)),
    offsetY: Math.round(number(value.offsetY, 0, -4096, 4096)),
    scale: number(value.scale, 1, 0.75, 1.5),
  }
  await patch({ ...geometryDraft.value })
}

function clampChannel(value: number): number {
  const number = Number(value)
  return Number.isFinite(number) ? Math.round(Math.max(0, Math.min(255, number))) : 0
}
async function saveColor(): Promise<void> {
  const color = colorDraft.value
  colorDraft.value = { r: clampChannel(color.r), g: clampChannel(color.g), b: clampChannel(color.b) }
  await patch({ backgroundColor: { ...colorDraft.value } })
}
async function pickColor(event: Event): Promise<void> {
  const hex = (event.target as HTMLInputElement).value.slice(1)
  colorDraft.value = { r: parseInt(hex.slice(0, 2), 16), g: parseInt(hex.slice(2, 4), 16), b: parseInt(hex.slice(4, 6), 16) }
  await saveColor()
}
async function applyColor(color: RgbColor): Promise<void> {
  colorDraft.value = { ...color }
  await saveColor()
}
function sameColor(a: RgbColor, b: RgbColor): boolean {
  return clampChannel(a.r) === b.r && clampChannel(a.g) === b.g && clampChannel(a.b) === b.b
}
/** Filled share of a range track, as a CSS length for `--fill`. */
function fill(value: number, min: number, max: number): string {
  return `${Math.max(0, Math.min(100, (Number(value) - min) / (max - min) * 100))}%`
}

async function togglePreview(): Promise<void> {
  if (previewing.value) {
    await run(dismissPreview, applyState)
    return
  }
  previewState.value = 'expanded'
  await run(() => previewIsland('progress'), applyState)
}

async function insertPlaceholder(key: string): Promise<void> {
  refreshGate.invalidate()
  const input = document.getElementById(textInputId.value) as HTMLInputElement | HTMLTextAreaElement | null
  const start = input?.selectionStart ?? activeTextDraft.value.length
  const end = input?.selectionEnd ?? start
  const token = `{${key}}`
  activeTextDraft.value = Array.from(activeTextDraft.value.slice(0, start) + token + activeTextDraft.value.slice(end)).slice(0, 256).join('')
  await nextTick()
  input?.focus()
  input?.setSelectionRange(Math.min(start + token.length, activeTextDraft.value.length), Math.min(start + token.length, activeTextDraft.value.length))
}

function selectTextMode(mode: typeof textMode.value): void {
  textMode.value = mode
  previewState.value = mode === 'customText' ? 'compact' : mode === 'peekText' ? 'peek' : 'expanded'
}
async function saveText(): Promise<void> {
  await patch({ [textMode.value]: activeTextDraft.value })
}

async function loadDiagnostics(): Promise<void> {
  await run(readDiagnostics, (value) => {
    diagnostics.value = value
  })
}

async function wipeDiagnostics(): Promise<void> {
  await run(clearDiagnostics, (value) => {
    diagnostics.value = value
  })
}

async function showMock(scenario: 'working' | 'waiting' | 'success' | 'failed' | 'progress' | 'all'): Promise<void> {
  await run(() => emitMockActivity(scenario), applyState)
}

const settings = computed(() => state.value?.settings ?? null)
const island = computed(() => state.value?.island ?? null)
const previewText = computed(() => {
  let text = renderText(customTextDraft.value, state.value?.templateValues ?? {}, fallbackDraft.value)
  if (state.value) for (const [key, label, show] of [['stopwatch', '计时', state.value.settings.showStopwatch], ['countdown', '倒计时', state.value.settings.showCountdown]] as const) {
    const timer = state.value.timers[key]
    if (show && timer.started && (timer.finished || !usesPlaceholder(customTextDraft.value, key))) {
      text += (text ? ' · ' : '') + (timer.finished ? '倒计时结束' : `${label} ${timer.text}${timer.running ? '' : '（已暂停）'}`)
    }
  }
  return text
})
const previewPeekText = computed(() => renderText(peekTextDraft.value, state.value?.templateValues ?? {}, fallbackDraft.value) || null)
const previewExpandedText = computed(() => renderText(expandedTextDraft.value, state.value?.templateValues ?? {}, fallbackDraft.value) || null)
/** What the draft in the editor resolves to right now. */
const renderedDraft = computed(() => textMode.value === 'customText' ? previewText.value : textMode.value === 'peekText' ? previewPeekText.value : previewExpandedText.value)
const unsupported = computed(() => state.value?.platform === 'unsupported')
const canSimulate = computed(() => Boolean(state.value?.active && settings.value?.enabled))
const previewClock = computed(() => {
  if (!settings.value?.showClock) return null
  if (island.value?.ambient?.clock) return island.value.ambient.clock
  const date = new Date()
  const hour = settings.value.clock24Hour ? String(date.getHours()).padStart(2, '0') : String(date.getHours() % 12 || 12)
  return `${hour}:${String(date.getMinutes()).padStart(2, '0')}${settings.value.showSeconds ? `:${String(date.getSeconds()).padStart(2, '0')}` : ''}${settings.value.clock24Hour ? '' : date.getHours() < 12 ? ' AM' : ' PM'}`
})
/** One honest line about the real island window, for the hero. */
const desktopStatus = computed(() => {
  if (!state.value) return { on: false, text: '正在读取…' }
  if (state.value.hiddenSeconds) return { on: false, text: `已临时隐藏 · ${state.value.hiddenSeconds}s` }
  if (state.value.overlay.visible) return { on: true, text: state.value.island.previewActive ? '桌面预览中' : '桌面上显示中' }
  return { on: false, text: state.value.settings.enabled ? '桌面上暂未显示' : '桌面上未显示' }
})

const anchorOptions: { value: Anchor; label: string }[] = [
  { value: 'topLeft', label: '左上' },
  { value: 'topCenter', label: '顶部居中' },
  { value: 'topRight', label: '右上' },
  { value: 'bottomLeft', label: '左下' },
  { value: 'bottomCenter', label: '底部居中' },
  { value: 'bottomRight', label: '右下' },
]
const monitorOptions: { value: MonitorStrategy; key: string }[] = [
  { value: 'primary', key: 'fields.monitorStrategies.primary' },
  { value: 'active', key: 'fields.monitorStrategies.active' },
]
const fullscreenOptions: { value: FullscreenPolicy; key: string }[] = [
  { value: 'always', key: 'fields.fullscreenPolicies.always' },
  { value: 'hide', key: 'fields.fullscreenPolicies.hide' },
  { value: 'important', key: 'fields.fullscreenPolicies.important' },
]

/** Only implemented providers are listed; future adapters are not fake controls. */
const providerRows: { kind: ProviderKind; implemented: boolean }[] = [
  { kind: 'mock', implemented: true },
  { kind: 'codex', implemented: true },
]

function statusFor(kind: ProviderKind) {
  return state.value?.providers.find((provider) => provider.kind === kind) ?? null
}

onMounted(async () => {
  document.addEventListener('pointerdown', pointerStarted, true)
  document.addEventListener('focusin', focusChanged, true)
  window.addEventListener('pointerup', pointerEnded, true)
  window.addEventListener('pointercancel', pointerEnded, true)
  window.addEventListener('blur', windowBlurred)
  document.addEventListener('visibilitychange', visibilityChanged)
  context.value = await getContext().catch(() => null)
  if (disposed) return
  await load()
  if (disposed) return
  poller = window.setInterval(() => { if (!document.hidden) void load() }, 1500)
})

onBeforeUnmount(() => {
  disposed = true
  refreshGate.stop()
  document.removeEventListener('pointerdown', pointerStarted, true)
  document.removeEventListener('focusin', focusChanged, true)
  window.removeEventListener('pointerup', pointerEnded, true)
  window.removeEventListener('pointercancel', pointerEnded, true)
  window.removeEventListener('blur', windowBlurred)
  document.removeEventListener('visibilitychange', visibilityChanged)
  if (poller !== undefined) window.clearInterval(poller)
  // Leaving the page must not leave a preview island on screen.
  if (previewing.value) void dismissPreview()
})

function visibilityChanged(): void {
  refreshGate.invalidate()
  if (document.hidden) pointerHeld = false
  if (document.hidden && previewing.value) void run(dismissPreview, applyState)
  else if (!document.hidden) void load()
}

async function close(): Promise<void> {
  if (previewing.value) await run(dismissPreview, applyState)
  await hideModuleWindow()
}
</script>

<template>
  <div class="shell">
    <svg class="sprite" width="0" height="0" aria-hidden="true"><defs>
      <symbol id="i-eye" viewBox="0 0 24 24"><path d="M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12Z" /><circle cx="12" cy="12" r="3" /></symbol>
      <symbol id="i-palette" viewBox="0 0 24 24"><path d="M12 3.5a8.5 8.5 0 1 0 0 17c1.2 0 1.8-.9 1.4-2l-.3-.8c-.4-1.1.4-2.2 1.6-2.2h2.1a3.7 3.7 0 0 0 3.7-3.7C20.5 7 16.7 3.5 12 3.5Z" /><circle cx="7.8" cy="11" r="1.1" /><circle cx="10.5" cy="7.4" r="1.1" /><circle cx="15" cy="7.6" r="1.1" /></symbol>
      <symbol id="i-move" viewBox="0 0 24 24"><rect x="3.5" y="4.5" width="17" height="12" rx="2" /><path d="M9 20h6M12 16.5V20" /><rect x="9" y="6.8" width="6" height="2.4" rx="1.2" /></symbol>
      <symbol id="i-text" viewBox="0 0 24 24"><path d="M5 6.5V5h14v1.5M12 5v14M9.5 19h5" /></symbol>
      <symbol id="i-timer" viewBox="0 0 24 24"><circle cx="12" cy="13.5" r="7" /><path d="M12 13.5V10M10 3.5h4M18.5 7l1.2-1.2" /></symbol>
      <symbol id="i-sliders" viewBox="0 0 24 24"><path d="M4 7h9M17 7h3M4 17h3M11 17h9" /><circle cx="15" cy="7" r="2" /><circle cx="9" cy="17" r="2" /></symbol>
      <symbol id="i-plug" viewBox="0 0 24 24"><path d="M9 3.5v4M15 3.5v4M7 7.5h10v3.5a5 5 0 0 1-10 0V7.5ZM12 16v4.5" /></symbol>
      <symbol id="i-pulse" viewBox="0 0 24 24"><path d="M3 12h4l2.5-6 5 12 2.5-6h4" /></symbol>
    </defs></svg>

    <header class="hero">
      <div class="brand">
        <div class="brand-mark" aria-hidden="true">
          <img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" />
          <span v-else class="brand-pill"><i /></span>
        </div>
        <div class="brand-text">
          <p class="eyebrow">QING ISLAND</p>
          <h1>实时活动</h1>
          <div class="hero-chips">
            <span class="state-chip" :class="{ on: state?.active }"><i />{{ state?.active ? '模块已启用' : '模块已加载 · 未启用' }}</span>
            <span class="state-chip" :class="{ on: desktopStatus.on }"><i />{{ desktopStatus.text }}</span>
          </div>
        </div>
      </div>
      <div class="hero-actions">
        <label v-if="settings" class="master-switch" :class="{ on: settings.enabled }">
          <span>
            <strong>启用灵动岛</strong>
            <small>{{ settings.enabled ? '常驻在桌面最上层' : '关闭时不创建任何窗口' }}</small>
          </span>
          <input
            type="checkbox"
            role="switch"
            class="toggle"
            :checked="settings.enabled"
            :disabled="busy"
            @change="patch({ enabled: ($event.target as HTMLInputElement).checked })"
          />
        </label>
        <QIconButton label="关闭" @click="close()"><QIcon name="close" :size="16" /></QIconButton>
      </div>
    </header>

    <p v-if="unsupported" class="notice warn">
      实时活动面板仅支持 Windows。当前平台不会创建任何窗口。
    </p>
    <p v-if="error" class="notice danger">
      {{ error }}
      <button type="button" aria-label="关闭提示" @click="error = null">✕</button>
    </p>
    <p v-if="state?.overlay.failure" class="notice danger">
      面板窗口创建失败：{{ state.overlay.failure }}
    </p>

    <div v-if="settings" class="grid">
      <!-- Stage ---------------------------------------------------------- -->
      <section class="card wide card-preview">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-eye" /></svg></span>
          <div class="card-title"><h2>实时预览</h2><p>设置页里的示意画面，不会打扰桌面上正在运行的灵动岛</p></div>
          <div class="card-tools">
            <div class="segmented preview-states">
              <button v-for="(label, mode) in { compact: '胶囊', peek: '悬停', expanded: '展开' }" :key="mode" type="button" :class="{ active: previewState === mode }" :aria-pressed="previewState === mode" @click="previewState = mode">{{ label }}</button>
            </div>
            <QButton size="small" :variant="previewing ? 'secondary' : 'primary'" :disabled="busy || unsupported" title="在真实桌面上展示一次带进度的示例" @click="togglePreview()">
              {{ previewing ? '结束预览' : '预览面板' }}
            </QButton>
          </div>
        </div>
        <div class="preview-row">
          <!--
            This preview is the settings page's own rendering of the same
            geometry. It deliberately does not read the live island state: the
            point is to show the user what a state looks like without waiting for
            a real task to reach it, and without disturbing the real one.
          -->
          <IslandPreview
            :anchor="settings.anchor"
            :scale="geometryDraft.scale"
            :compact-width="geometryDraft.compactWidth"
            :offset-x="geometryDraft.offsetX"
            :offset-y="geometryDraft.offsetY"
            :state="previewState"
            :surface-style="state?.overlay.materialFallback && settings.surfaceStyle === 'frosted' ? 'translucent' : settings.surfaceStyle"
            :opacity="settings.backgroundOpacity"
            :background-color="colorDraft"
            :clock="previewClock"
            :date="island?.ambient?.date ?? null"
            :custom-text="previewText"
            :peek-text="previewPeekText"
            :expanded-text="previewExpandedText"
            :live="previewing"
          />
          <aside class="preview-notes">
            <div class="notes-block">
              <span class="notes-label">模拟活动</span>
              <div class="mock-actions">
                <QButton size="small" :disabled="busy || !canSimulate" @click="showMock('working')">运行中</QButton>
                <QButton size="small" :disabled="busy || !canSimulate" @click="showMock('waiting')">等待处理</QButton>
                <QButton size="small" :disabled="busy || !canSimulate" @click="showMock('progress')">带进度</QButton>
                <QButton size="small" :disabled="busy || !canSimulate" @click="showMock('failed')">失败</QButton>
                <QButton size="small" :disabled="busy || !canSimulate" @click="showMock('all')">全部状态</QButton>
                <QButton size="small" :disabled="busy" @click="run(clearActivities, applyState)">
                  清空
                </QButton>
              </div>
              <small v-if="!canSimulate" class="hint">启用模块并打开「启用灵动岛」后可以模拟。</small>
            </div>
            <div v-if="island" class="live">
              <div class="live-head">
                <span class="notes-label">桌面上的活动</span>
                <span class="state-chip mono">{{ island.state }}</span>
                <span class="muted">
                  {{ state?.counts.activities ?? 0 }} 个活动 · 已丢弃
                  {{ state?.counts.dropped ?? 0 }}
                </span>
              </div>
              <ul v-if="island.stack.length" class="stack">
                <ActivityRow
                  v-for="activity in island.stack.slice(0, 5)"
                  :key="activity.id"
                  :activity="activity"
                />
              </ul>
              <p v-else class="live-empty">当前没有活动。模拟一个，看看灵动岛如何响应。</p>
            </div>
          </aside>
        </div>
      </section>

      <!-- Material and colour -------------------------------------------- -->
      <section class="card card-gallery card-appearance">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-palette" /></svg></span>
          <div class="card-title"><h2>材质与颜色</h2><p>决定灵动岛如何融入你的桌面</p></div>
        </div>
        <MaterialGallery :selected="settings.surfaceStyle" :background-color="colorDraft" :disabled="busy" @select="patch({ surfaceStyle: $event })" />
        <small v-if="state?.overlay.materialFallback" class="hint">{{ state.overlay.materialFallback }}</small>
        <div class="field">
          <div class="field-heading">
            <label class="field-label" for="background-opacity">{{ settings.surfaceStyle === 'frosted' ? '磨砂着色强度' : '背景不透明度' }}<b class="value-chip">{{ settings.surfaceStyle === 'solid' ? 100 : Math.round(settings.backgroundOpacity * 100) }}%</b></label>
            <QButton v-if="settings.surfaceStyle !== 'solid'" size="small" :disabled="busy" title="设为 60%，让背景效果更明显" @click="patch({ backgroundOpacity: 0.6 })">推荐强度</QButton>
          </div>
          <input
            id="background-opacity"
            class="range opacity-range"
            type="range"
            min="35"
            max="100"
            step="5"
            :style="{ '--fill': fill(settings.backgroundOpacity * 100, 35, 100), '--swatch': colorHex }"
            :value="Math.round(settings.backgroundOpacity * 100)"
            :disabled="busy || settings.surfaceStyle === 'solid'"
            @change="patch({ backgroundOpacity: Number(($event.target as HTMLInputElement).value) / 100 })"
          />
        </div>
        <div class="field">
          <div class="field-heading">
            <span class="field-label">背景颜色</span>
            <QButton size="small" :disabled="busy" @click="applyColor(DEFAULT_COLOR)">恢复默认</QButton>
          </div>
          <div class="swatches" role="group" aria-label="常用背景色">
            <label class="color-well" :style="{ '--c': colorHex }" title="自定义颜色">
              <input id="background-color" class="color-picker" type="color" aria-label="选择背景颜色" :value="colorHex" :disabled="busy" @change="pickColor" />
            </label>
            <button
              v-for="preset in colorPresets"
              :key="preset.name"
              type="button"
              class="swatch"
              :class="{ active: sameColor(colorDraft, preset.color) }"
              :style="{ '--c': `rgb(${preset.color.r} ${preset.color.g} ${preset.color.b})` }"
              :title="preset.name"
              :aria-label="`使用${preset.name}`"
              :disabled="busy"
              @click="applyColor(preset.color)"
            />
          </div>
          <div class="color-editor">
            <label v-for="channel in (['r', 'g', 'b'] as const)" :key="channel" class="color-channel" :for="`color-${channel}`">
              <span :data-channel="channel">{{ channel.toUpperCase() }}</span>
              <input :id="`color-${channel}`" v-model.number="colorDraft[channel]" class="text-input" type="number" min="0" max="255" step="1" :disabled="busy" @change="saveColor" @keydown.enter.prevent="saveColor" />
            </label>
            <span class="hex-chip" :title="`当前颜色 ${colorHex.toUpperCase()}`"><i :style="{ background: colorHex }" />{{ colorHex.toUpperCase() }}</span>
          </div>
        </div>
      </section>

      <!-- Placement ------------------------------------------------------- -->
      <section class="card card-position">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-move" /></svg></span>
          <div class="card-title"><h2>位置和尺寸</h2><p>停靠在屏幕哪里、有多大</p></div>
          <div class="card-tools">
            <QButton size="small" :disabled="busy" @click="patch({ anchor: 'topCenter', compactWidth: 232, offsetX: 0, offsetY: 0, scale: 1 })">复位</QButton>
          </div>
        </div>
        <div class="field">
          <span class="field-label">停靠位置</span>
          <div class="monitor">
            <div class="anchor-grid">
              <button
                v-for="option in anchorOptions"
                :key="option.value"
                type="button"
                :class="{ active: settings.anchor === option.value }"
                :aria-pressed="settings.anchor === option.value"
                :data-anchor="option.value"
                :disabled="busy"
                @click="patch({ anchor: option.value })"
              >
                <i class="anchor-mark" :data-anchor="option.value" aria-hidden="true" />{{ option.label }}
              </button>
            </div>
          </div>
        </div>
        <div class="offset-fields">
          <label class="field" for="offset-x"><span class="field-label">横向偏移</span><span class="unit-input"><input id="offset-x" v-model.number="geometryDraft.offsetX" class="text-input" type="number" min="-4096" max="4096" step="4" title="正数向右，负数向左；逻辑像素" :disabled="busy" @change="saveGeometry" @keydown.enter.prevent="saveGeometry" /><em>px</em></span></label>
          <label class="field" for="offset-y"><span class="field-label">纵向偏移</span><span class="unit-input"><input id="offset-y" v-model.number="geometryDraft.offsetY" class="text-input" type="number" min="-4096" max="4096" step="4" title="正数向下，负数向上；逻辑像素" :disabled="busy" @change="saveGeometry" @keydown.enter.prevent="saveGeometry" /><em>px</em></span></label>
        </div>
        <div class="field">
          <label class="field-label" for="compact-width">胶囊宽度<b class="value-chip">{{ geometryDraft.compactWidth }} px</b></label>
          <input id="compact-width" v-model.number="geometryDraft.compactWidth" class="range" type="range" min="200" max="480" step="4" :style="{ '--fill': fill(geometryDraft.compactWidth, 200, 480) }" :disabled="busy" @change="saveGeometry" />
        </div>
        <div class="field">
          <label class="field-label" for="island-scale">整体大小<b class="value-chip">{{ Math.round(geometryDraft.scale * 100) }}%</b></label>
          <input
            id="island-scale"
            v-model.number="geometryDraft.scale"
            class="range"
            type="range"
            min="0.75"
            max="1.5"
            step="0.05"
            :style="{ '--fill': fill(geometryDraft.scale, 0.75, 1.5) }"
            :disabled="busy"
            @change="saveGeometry"
          />
        </div>
      </section>

      <!-- Content --------------------------------------------------------- -->
      <section class="card wide card-content">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-text" /></svg></span>
          <div class="card-title"><h2>显示内容</h2><p>时间，以及胶囊、悬停、展开三种状态各自的文本</p></div>
        </div>
        <div class="content-layout">
          <div class="content-main">
            <div class="clock-row">
              <div class="clock-options">
                <label class="switch chip-switch">
                  <span><strong>显示时间</strong></span>
                  <input type="checkbox" role="switch" class="toggle small" :checked="settings.showClock" :disabled="busy" @change="patch({ showClock: ($event.target as HTMLInputElement).checked })" />
                </label>
                <label class="switch chip-switch">
                  <span><strong>显示秒数</strong></span>
                  <input type="checkbox" role="switch" class="toggle small" :checked="settings.showSeconds" :disabled="busy || !settings.showClock" @change="patch({ showSeconds: ($event.target as HTMLInputElement).checked })" />
                </label>
              </div>
              <div class="segmented clock-format">
                <button type="button" :class="{ active: settings.clock24Hour }" :disabled="busy || !settings.showClock" @click="patch({ clock24Hour: true })">24 小时制</button>
                <button type="button" :class="{ active: !settings.clock24Hour }" :disabled="busy || !settings.showClock" @click="patch({ clock24Hour: false })">12 小时制</button>
              </div>
            </div>
            <div class="field custom-text-field">
              <div class="segmented text-modes">
                <button v-for="(label, mode) in textLabels" :key="mode" type="button" :class="{ active: textMode === mode }" :aria-pressed="textMode === mode" @click="selectTextMode(mode)">{{ label }}</button>
              </div>
              <label class="field-label" :for="textInputId">{{ textLabels[textMode] }}文本<small>{{ textHints[textMode] }}</small></label>
              <div class="text-editor">
                <textarea v-if="textMode === 'expandedText'" :id="textInputId" v-model="activeTextDraft" class="text-input expanded-text-input" rows="3" maxlength="256" placeholder="支持换行与占位符，留空不显示" :disabled="busy" @keydown.ctrl.enter.prevent="saveText" />
                <input v-else :id="textInputId" v-model="activeTextDraft" class="text-input" type="text" maxlength="256" :placeholder="textMode === 'peekText' ? '文字或占位符，留空不显示' : '文字或 {codex.remaining}，留空取消'" :disabled="busy" @keydown.enter.prevent="saveText" />
                <QButton size="small" :disabled="busy || activeTextDraft === settings[textMode]" @click="saveText">保存</QButton>
              </div>
              <p class="render-line" :class="{ dirty: activeTextDraft !== settings[textMode] }">
                <span class="render-tag">{{ activeTextDraft !== settings[textMode] ? '未保存' : '效果' }}</span>
                <span class="render-text">{{ renderedDraft || '（这一状态不显示文本）' }}</span>
              </p>
            </div>
          </div>
          <div class="content-side">
            <div class="field">
              <span class="field-label">插入占位符<small>插入到当前光标位置</small></span>
              <div class="placeholder-chips">
                <button v-for="item in state?.placeholders ?? []" :key="item.key" type="button" class="token" :disabled="busy" :title="`插入 {${item.key}}`" @click="insertPlaceholder(item.key)">{{ item.label }}</button>
              </div>
              <small class="hint">单独缺省：<code>{codex.remaining|暂无额度}</code></small>
            </div>
            <div class="field">
              <label class="field-label" for="placeholder-fallback">占位符无数据时显示</label>
              <div class="text-editor">
                <input id="placeholder-fallback" v-model="fallbackDraft" class="text-input" type="text" maxlength="48" placeholder="留空则隐藏缺失数据" :disabled="busy" @keydown.enter.prevent="patch({ placeholderFallback: fallbackDraft })" />
                <QButton size="small" :disabled="busy || fallbackDraft === settings.placeholderFallback" @click="patch({ placeholderFallback: fallbackDraft })">保存</QButton>
              </div>
            </div>
          </div>
        </div>
      </section>

      <TimerControls v-if="state && settings" :settings="settings" :timers="state.timers" :busy="busy" :can-run="canSimulate" @patch="patch" @command="(kind, action) => run(() => timerCommand(kind, action), applyState)" />

      <!-- Behaviour ------------------------------------------------------- -->
      <section class="card card-general">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-sliders" /></svg></span>
          <div class="card-title"><h2>行为</h2><p>悬停、遮挡、显示器与全屏</p></div>
        </div>
        <div class="setting-list">
          <label class="switch row">
            <span><strong>悬停时展开文本</strong><small>指针停留在胶囊上时显示第二行</small></span>
            <input type="checkbox" role="switch" class="toggle" :checked="settings.peekOnHover" :disabled="busy" @change="patch({ peekOnHover: ($event.target as HTMLInputElement).checked })" />
          </label>
          <label class="switch row">
            <span><strong>鼠标穿透</strong><small>点击胶囊位置时操作下方窗口；在这里关闭穿透。</small></span>
            <input type="checkbox" role="switch" class="toggle" :checked="settings.clickThrough" :disabled="busy" @change="patch({ clickThrough: ($event.target as HTMLInputElement).checked })" />
          </label>
        </div>
        <div class="obstruction-actions">
          <QButton size="small" :disabled="busy || !canSimulate" @click="run(hideTemporarily, applyState)">隐藏 30 秒</QButton>
          <QButton size="small" :disabled="busy || !state?.hiddenSeconds" @click="run(restoreIsland, applyState)">恢复显示{{ state?.hiddenSeconds ? ` · ${state.hiddenSeconds}s` : '' }}</QButton>
        </div>
        <div class="field">
          <span class="field-label">显示在哪块屏幕</span>
          <div class="segmented">
            <button
              v-for="option in monitorOptions"
              :key="option.value"
              type="button"
              :class="{ active: settings.monitorStrategy === option.value }"
              :disabled="busy"
              @click="patch({ monitorStrategy: option.value })"
            >
              {{ option.value === 'primary' ? '主显示器' : '鼠标所在屏' }}
            </button>
          </div>
        </div>
        <div class="field">
          <span class="field-label">有应用全屏时</span>
          <div class="segmented">
            <button
              v-for="option in fullscreenOptions"
              :key="option.value"
              type="button"
              :class="{ active: settings.fullscreenPolicy === option.value }"
              :disabled="busy"
              @click="patch({ fullscreenPolicy: option.value })"
            >
              {{
                option.value === 'always'
                  ? '始终显示'
                  : option.value === 'hide'
                    ? '隐藏'
                    : '仅提醒'
              }}
            </button>
          </div>
          <small class="hint">「仅提醒」只放行需要你处理或已经失败的任务。</small>
        </div>
      </section>

      <!-- Providers ------------------------------------------------------- -->
      <section class="card wide card-providers">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-plug" /></svg></span>
          <div class="card-title"><h2>数据来源</h2><p>灵动岛只显示它真正观察到的数据</p></div>
          <div class="card-tools">
            <QButton size="small" :disabled="busy" @click="run(refreshProviders, applyState)">刷新</QButton>
          </div>
        </div>
        <div class="providers">
          <ProviderCard
            v-for="row in providerRows"
            :key="row.kind"
            :kind="row.kind"
            :implemented="row.implemented"
            :status="statusFor(row.kind)"
            :account="row.kind === 'codex' ? state?.codexAccount ?? null : null"
          />
        </div>
        <label class="switch row">
          <span>
            <strong>在文本中显示 Codex 数据</strong>
            <small>{{ state?.codexProgram.error ?? (state?.codexProgram.running ? '已检测到 Codex · 自动接入' : '等待 Codex 启动') }}</small>
          </span>
          <input type="checkbox" role="switch" class="toggle" :checked="settings.showCodexData" :disabled="busy" @change="patch({ showCodexData: ($event.target as HTMLInputElement).checked })" />
        </label>
      </section>

      <!-- Diagnostics ----------------------------------------------------- -->
      <section class="card wide card-diagnostics">
        <div class="card-head">
          <span class="card-icon" aria-hidden="true"><svg><use href="#i-pulse" /></svg></span>
          <div class="card-title"><h2>诊断</h2><p>只记录生命周期与错误</p></div>
          <div class="card-tools head-actions">
            <QButton size="small" :disabled="busy" @click="loadDiagnostics()">读取日志</QButton>
            <QButton size="small" :disabled="busy" @click="wipeDiagnostics()">清空</QButton>
          </div>
        </div>
        <DiagnosticsPanel :diagnostics="diagnostics" />
      </section>
    </div>

    <footer class="footer">
      <span class="muted">
        已运行 {{ state?.uptimeSeconds ?? 0 }} 秒 ·
        协议 v{{ context?.protocolVersion ?? 1 }}
      </span>
      <span v-if="state?.overlay.visible" class="muted telemetry">
        原生渲染 {{ state.overlay.renderCount }} 帧<template v-if="state.overlay.glassSampleMicros"> · 磨砂采样 {{ (state.overlay.glassSampleMicros / 1000).toFixed(1) }} ms</template>
      </span>
    </footer>
  </div>
</template>
