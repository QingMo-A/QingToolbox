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
const textInputId = computed(() => ({ customText: 'custom-text', peekText: 'peek-text', expandedText: 'expanded-text' }[textMode.value]))
const activeTextDraft = computed({ get: () => textDrafts[textMode.value].value, set: value => { textDrafts[textMode.value].value = value } })
const fallbackDraft = ref('暂无数据')
const colorDraft = ref<RgbColor>({ r: 21, g: 26, b: 37 })
const geometryDraft = ref({ compactWidth: 232, offsetX: 0, offsetY: 0, scale: 1 })
const previewState = ref<'compact' | 'peek' | 'expanded'>('compact')
const colorHex = computed(() => '#' + ['r', 'g', 'b'].map(key => clampChannel(colorDraft.value[key as keyof RgbColor]).toString(16).padStart(2, '0')).join(''))
const materialTitles = { solid: '完全遮住背景', translucent: '透出清晰的背景', frosted: '模糊背景后着色；此模式下灵动岛不进入系统截屏' }

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
const previewPeekText = computed(() => peekTextDraft.value.trim() ? renderText(peekTextDraft.value, state.value?.templateValues ?? {}, fallbackDraft.value) : null)
const previewExpandedText = computed(() => expandedTextDraft.value.trim() ? renderText(expandedTextDraft.value, state.value?.templateValues ?? {}, fallbackDraft.value) : null)
const unsupported = computed(() => state.value?.platform === 'unsupported')
const canSimulate = computed(() => Boolean(state.value?.active && settings.value?.enabled))
const previewClock = computed(() => {
  if (!settings.value?.showClock) return null
  if (island.value?.ambient?.clock) return island.value.ambient.clock
  const date = new Date()
  const hour = settings.value.clock24Hour ? String(date.getHours()).padStart(2, '0') : String(date.getHours() % 12 || 12)
  return `${hour}:${String(date.getMinutes()).padStart(2, '0')}${settings.value.showSeconds ? `:${String(date.getSeconds()).padStart(2, '0')}` : ''}${settings.value.clock24Hour ? '' : date.getHours() < 12 ? ' AM' : ' PM'}`
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
    <header class="hero">
      <div class="brand">
        <div class="brand-mark">
          <img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" />
          <span v-else>◍</span>
        </div>
        <div class="brand-text">
          <p class="eyebrow">QING ISLAND</p>
          <h1>实时活动</h1>
          <span class="state-chip">{{ state?.active ? '模块已启用' : '模块已加载 · 未启用' }}</span>
        </div>
      </div>
      <QIconButton label="关闭" @click="close()"><QIcon name="close" :size="16" /></QIconButton>
    </header>

    <p v-if="unsupported" class="notice warn">
      实时活动面板仅支持 Windows。当前平台不会创建任何窗口。
    </p>
    <p v-if="error" class="notice danger">
      {{ error }}
      <button type="button" @click="error = null">✕</button>
    </p>
    <p v-if="state?.overlay.failure" class="notice danger">
      面板窗口创建失败：{{ state.overlay.failure }}
    </p>

    <div v-if="settings" class="grid">
      <section class="card card-gallery">
        <h2>材质样式 <span class="sample-label">透明材质为 60% 示例</span></h2>
        <MaterialGallery :selected="settings.surfaceStyle" :background-color="colorDraft" :custom-text="previewText" :disabled="busy" @select="patch({ surfaceStyle: $event })" />
      </section>
      <!-- General ------------------------------------------------------- -->
      <section class="card card-general">
        <h2>常规</h2>
        <label class="switch">
          <input
            type="checkbox"
            :checked="settings.enabled"
            :disabled="busy"
            @change="patch({ enabled: ($event.target as HTMLInputElement).checked })"
          />
          <span>
            <strong>启用灵动岛</strong>
          </span>
        </label>
        <label class="switch">
          <input
            type="checkbox"
            :checked="settings.peekOnHover"
            :disabled="busy"
            @change="patch({ peekOnHover: ($event.target as HTMLInputElement).checked })"
          />
          <span>
            <strong>悬停时展开一行上下文</strong>
            <small>鼠标移到胶囊上时，多显示一行进度或账号信息。</small>
          </span>
        </label>
      </section>

      <section class="card card-content">
        <h2>常驻内容</h2>
        <div class="clock-options">
          <label class="switch">
            <input type="checkbox" :checked="settings.showClock" :disabled="busy" @change="patch({ showClock: ($event.target as HTMLInputElement).checked })" />
            <strong>显示时间</strong>
          </label>
          <label class="switch">
            <input type="checkbox" :checked="settings.showSeconds" :disabled="busy || !settings.showClock" @change="patch({ showSeconds: ($event.target as HTMLInputElement).checked })" />
            <strong>显示秒数</strong>
          </label>
        </div>
        <div class="segmented">
          <button type="button" :class="{ active: settings.clock24Hour }" :disabled="busy || !settings.showClock" @click="patch({ clock24Hour: true })">24 小时制</button>
          <button type="button" :class="{ active: !settings.clock24Hour }" :disabled="busy || !settings.showClock" @click="patch({ clock24Hour: false })">12 小时制</button>
        </div>
        <div class="field custom-text-field">
          <div class="segmented text-modes">
            <button v-for="(label, mode) in textLabels" :key="mode" type="button" :class="{ active: textMode === mode }" :aria-pressed="textMode === mode" @click="selectTextMode(mode)">{{ label }}</button>
          </div>
          <label class="field-label" :for="textInputId">{{ textLabels[textMode] }}文本</label>
          <div class="text-editor">
            <textarea v-if="textMode === 'expandedText'" :id="textInputId" v-model="activeTextDraft" class="text-input expanded-text-input" rows="3" maxlength="256" placeholder="支持换行与占位符，留空使用默认内容" :disabled="busy" @keydown.ctrl.enter.prevent="saveText" />
            <input v-else :id="textInputId" v-model="activeTextDraft" class="text-input" type="text" maxlength="256" :placeholder="textMode === 'peekText' ? '留空使用默认悬停内容' : '文字或 {codex.remaining}，留空取消'" :disabled="busy" @keydown.enter.prevent="saveText" />
            <QButton size="small" :disabled="busy || activeTextDraft === settings[textMode]" @click="saveText">保存</QButton>
          </div>
          <div class="placeholder-chips">
            <QButton v-for="item in state?.placeholders ?? []" :key="item.key" size="small" :disabled="busy" :title="`插入 {${item.key}}`" @click="insertPlaceholder(item.key)">{{ item.label }}</QButton>
          </div>
          <small class="hint">单独缺省：{codex.remaining|暂无额度}</small>
        </div>
        <div class="field">
          <label class="field-label" for="placeholder-fallback">占位符无数据时显示</label>
          <div class="text-editor">
            <input id="placeholder-fallback" v-model="fallbackDraft" class="text-input" type="text" maxlength="48" placeholder="留空则隐藏缺失数据" :disabled="busy" @keydown.enter.prevent="patch({ placeholderFallback: fallbackDraft })" />
            <QButton size="small" :disabled="busy || fallbackDraft === settings.placeholderFallback" @click="patch({ placeholderFallback: fallbackDraft })">保存</QButton>
          </div>
        </div>
      </section>

      <TimerControls v-if="state && settings" :settings="settings" :timers="state.timers" :busy="busy" :can-run="canSimulate" @patch="patch" @command="(kind, action) => run(() => timerCommand(kind, action), applyState)" />

      <section class="card card-obstruction">
        <h2>遮挡处理</h2>
        <label class="switch">
          <input type="checkbox" :checked="settings.clickThrough" :disabled="busy" @change="patch({ clickThrough: ($event.target as HTMLInputElement).checked })" />
          <span><strong>鼠标穿透</strong><small>点击胶囊位置时操作下方窗口；在这里关闭穿透。</small></span>
        </label>
        <div class="obstruction-actions">
          <QButton size="small" :disabled="busy || !canSimulate" @click="run(hideTemporarily, applyState)">隐藏 30 秒</QButton>
          <QButton size="small" :disabled="busy || !state?.hiddenSeconds" @click="run(restoreIsland, applyState)">恢复显示{{ state?.hiddenSeconds ? ` · ${state.hiddenSeconds}s` : '' }}</QButton>
        </div>
      </section>

      <!-- Appearance ---------------------------------------------------- -->
      <section class="card card-appearance">
        <h2>外观</h2>
        <div class="field">
          <span class="field-label">面板材质</span>
          <div class="segmented">
            <button v-for="style in (['solid', 'translucent', 'frosted'] as const)" :key="style" type="button" :class="{ active: settings.surfaceStyle === style }" :disabled="busy" :title="materialTitles[style]" @click="patch({ surfaceStyle: style })">{{ { solid: '实色', translucent: '半透明', frosted: '磨砂玻璃' }[style] }}</button>
          </div>
          <small v-if="state?.overlay.materialFallback" class="hint">{{ state.overlay.materialFallback }}</small>
        </div>
        <div class="field">
          <div class="field-heading">
            <label class="field-label" for="background-opacity">{{ settings.surfaceStyle === 'frosted' ? '磨砂着色强度' : '背景不透明度' }} · {{ settings.surfaceStyle === 'solid' ? 100 : Math.round(settings.backgroundOpacity * 100) }}%</label>
            <QButton v-if="settings.surfaceStyle !== 'solid'" size="small" :disabled="busy" title="设为 60%，让背景效果更明显" @click="patch({ backgroundOpacity: 0.6 })">推荐强度</QButton>
          </div>
          <input id="background-opacity" type="range" min="35" max="100" step="5" :value="Math.round(settings.backgroundOpacity * 100)" :disabled="busy || settings.surfaceStyle === 'solid'" @change="patch({ backgroundOpacity: Number(($event.target as HTMLInputElement).value) / 100 })" />
        </div>
        <div class="field">
          <div class="field-heading">
            <span class="field-label">背景颜色 · RGB</span>
            <QButton size="small" :disabled="busy" @click="colorDraft = { r: 21, g: 26, b: 37 }; saveColor()">恢复默认</QButton>
          </div>
          <div class="color-editor">
            <input id="background-color" class="color-picker" type="color" aria-label="选择背景颜色" :value="colorHex" :disabled="busy" @change="pickColor" />
            <label v-for="channel in (['r', 'g', 'b'] as const)" :key="channel" class="color-channel" :for="`color-${channel}`">
              <span>{{ channel.toUpperCase() }}</span>
              <input :id="`color-${channel}`" v-model.number="colorDraft[channel]" class="text-input" type="number" min="0" max="255" step="1" :disabled="busy" @change="saveColor" @keydown.enter.prevent="saveColor" />
            </label>
          </div>
        </div>
      </section>

      <section class="card card-position">
        <h2>位置和尺寸 <QButton size="small" :disabled="busy" @click="patch({ anchor: 'topCenter', compactWidth: 232, offsetX: 0, offsetY: 0, scale: 1 })">复位</QButton></h2>
        <div class="field">
          <span class="field-label">停靠位置</span>
          <div class="anchor-grid">
            <button
              v-for="option in anchorOptions"
              :key="option.value"
              type="button"
              :class="{ active: settings.anchor === option.value }"
              :aria-pressed="settings.anchor === option.value"
              :disabled="busy"
              @click="patch({ anchor: option.value })"
            >
              <i class="anchor-mark" :data-anchor="option.value" aria-hidden="true" />{{ option.label }}
            </button>
          </div>
        </div>
        <div class="offset-fields">
          <label class="field" for="offset-x"><span class="field-label">横向偏移</span><input id="offset-x" v-model.number="geometryDraft.offsetX" class="text-input" type="number" min="-4096" max="4096" step="4" title="正数向右，负数向左；逻辑像素" :disabled="busy" @change="saveGeometry" @keydown.enter.prevent="saveGeometry" /></label>
          <label class="field" for="offset-y"><span class="field-label">纵向偏移</span><input id="offset-y" v-model.number="geometryDraft.offsetY" class="text-input" type="number" min="-4096" max="4096" step="4" title="正数向下，负数向上；逻辑像素" :disabled="busy" @change="saveGeometry" @keydown.enter.prevent="saveGeometry" /></label>
        </div>
        <div class="field">
          <label class="field-label" for="compact-width">胶囊宽度 · {{ geometryDraft.compactWidth }} px</label>
          <input id="compact-width" v-model.number="geometryDraft.compactWidth" type="range" min="200" max="480" step="4" :disabled="busy" @change="saveGeometry" />
        </div>
        <div class="field">
          <label class="field-label" for="island-scale">整体大小 · {{ Math.round(geometryDraft.scale * 100) }}%</label>
          <input
            id="island-scale"
            type="range"
            min="0.75"
            max="1.5"
            step="0.05"
            v-model.number="geometryDraft.scale"
            :disabled="busy"
            @change="saveGeometry"
          />
        </div>
      </section>

      <!-- Display ------------------------------------------------------- -->
      <section class="card card-display">
        <h2>显示器</h2>
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
      </section>

      <!-- Fullscreen ---------------------------------------------------- -->
      <section class="card card-fullscreen">
        <h2>全屏</h2>
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

      <!-- Providers ----------------------------------------------------- -->
      <section class="card wide card-providers">
        <h2>
          数据来源
          <QButton size="small" :disabled="busy" @click="run(refreshProviders, applyState)">
            刷新
          </QButton>
        </h2>
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
        <label class="switch">
          <input
            type="checkbox"
            :checked="settings.showCodexData"
            :disabled="busy"
            @change="patch({ showCodexData: ($event.target as HTMLInputElement).checked })"
          />
          <span>
            <strong>显示 Codex 数据</strong>
            <small>{{ state?.codexProgram.error ?? (state?.codexProgram.running ? '已检测到 Codex · 自动接入' : '等待 Codex 启动') }}</small>
          </span>
        </label>
        <div class="field">
          <span class="field-label">Codex 数据位置</span>
          <div class="segmented data-position">
            <button type="button" :class="{ active: settings.codexDataPosition === 'header' }" :disabled="busy || !settings.showCodexData" @click="patch({ codexDataPosition: 'header' })">日期/时间区域</button>
            <button type="button" :class="{ active: settings.codexDataPosition === 'expanded' }" :disabled="busy || !settings.showCodexData" @click="patch({ codexDataPosition: 'expanded' })">展开面板底部</button>
            <button type="button" :class="{ active: settings.codexDataPosition === 'customText' }" :disabled="busy || !settings.showCodexData" @click="patch({ codexDataPosition: 'customText' })">仅自定义文本</button>
          </div>
        </div>
      </section>

      <!-- Preview ------------------------------------------------------- -->
      <section class="card card-preview">
        <h2>
          实时预览
          <QButton size="small" :disabled="busy || unsupported" @click="togglePreview()">
            {{ previewing ? '结束预览' : '预览面板' }}
          </QButton>
        </h2>
        <div class="segmented preview-states">
          <button v-for="(label, mode) in { compact: '胶囊', peek: '悬停', expanded: '展开' }" :key="mode" type="button" :class="{ active: previewState === mode }" :aria-pressed="previewState === mode" @click="previewState = mode">{{ label }}</button>
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
            :account="island?.account ?? null"
            :account-header="island?.accountHeader ?? null"
          />
          <div class="preview-notes">
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
          </div>
        </div>

        <div v-if="island" class="live">
          <div class="live-head">
            <span class="state-chip">{{ island.state }}</span>
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
          <p v-else class="muted">当前没有活动。</p>
        </div>
      </section>

      <!-- Diagnostics --------------------------------------------------- -->
      <section class="card wide card-diagnostics">
        <h2>
          诊断
          <span class="head-actions">
            <QButton size="small" :disabled="busy" @click="loadDiagnostics()">读取日志</QButton>
            <QButton size="small" :disabled="busy" @click="wipeDiagnostics()">清空</QButton>
          </span>
        </h2>
        <DiagnosticsPanel :diagnostics="diagnostics" />
      </section>
    </div>

    <footer class="footer">
      <span class="muted">
        已运行 {{ state?.uptimeSeconds ?? 0 }} 秒 ·
        协议 v{{ context?.protocolVersion ?? 1 }}
      </span>
    </footer>
  </div>
</template>
