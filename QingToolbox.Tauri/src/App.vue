<script setup lang="ts">
import { invoke } from '@tauri-apps/api/core'
import { open } from '@tauri-apps/plugin-dialog'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  isProtocolEnvelope,
  type HostInfo,
  type ModuleListPayload,
  type ModuleRuntimeSnapshot,
  type ModuleSummary,
  type ProtocolEnvelope,
  type SettingsSnapshot,
  type SettingsUpdate,
} from './protocol'

const hostInfo = ref<HostInfo | null>(null)
const modules = ref<ModuleSummary[]>([])
const roots = ref<ModuleListPayload['roots']>([])
const runtimeById = ref<Record<string, ModuleRuntimeSnapshot>>({})
const settings = ref<SettingsSnapshot | null>(null)
const settingsExpanded = ref(false)
const settingsBusy = ref(false)
const importing = ref(false)
const loading = ref(true)
const refreshing = ref(false)
const error = ref<string | null>(null)
let refreshSequence = 0
let runtimePollTimer: number | undefined

const validModuleCount = computed(() => modules.value.filter((module) => module.valid).length)
const invalidModuleCount = computed(() => modules.value.length - validModuleCount.value)
const appearanceLabel = computed(() => {
  const value = settings.value?.appearancePresetId
  return {
    'qing-default': 'Qing 默认',
    'light': '浅色',
    'dark': '深色',
    'system': '跟随系统',
  }[value ?? ''] ?? '自定义预设'
})
const statusLabel = computed(() => {
  if (loading.value || refreshing.value) return '正在扫描模块…'
  if (error.value) return '扫描失败'
  return `${validModuleCount.value} 个可用模块`
})

function reasonMessage(reason: unknown): string {
  if (reason instanceof Error) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
    try { return JSON.stringify(reason) } catch { /* fall through */ }
  }
  return String(reason)
}

async function loadHostInfo(): Promise<void> {
  try {
    hostInfo.value = await invoke<HostInfo>('get_host_info')
  } catch (reason) {
    // `npm run dev` intentionally remains usable in a normal browser.
    if (import.meta.env.DEV) {
      hostInfo.value = {
        productName: 'QingToolbox',
        version: '0.1.0',
        backend: 'browser-preview',
        protocolVersion: '1',
      }
    } else {
      throw new Error(reasonMessage(reason))
    }
  }
}

function applyAppearance(value: string | undefined): void {
  if (typeof document === 'undefined') return
  document.documentElement.dataset.appearance = value || 'qing-default'
}

async function loadSettings(): Promise<void> {
  try {
    settings.value = await invoke<SettingsSnapshot>('get_settings')
    applyAppearance(settings.value.appearancePresetId)
  } catch (reason) {
    if (import.meta.env.DEV) {
      settings.value = {
        settingsSchemaVersion: 1,
        language: 'system',
        appearancePresetId: 'qing-default',
        closeBehavior: 'ask',
        startupPresentation: 'main',
        launchAtLogin: false,
        showLogsInSidebar: false,
        recentModuleIds: [],
      }
      applyAppearance(settings.value.appearancePresetId)
      return
    }
    throw new Error(reasonMessage(reason))
  }
}

async function saveSettings(update: SettingsUpdate): Promise<void> {
  if (settingsBusy.value) return
  settingsBusy.value = true
  error.value = null
  try {
    settings.value = await invoke<SettingsSnapshot>('update_settings', { update })
    applyAppearance(settings.value.appearancePresetId)
  } catch (reason) {
    error.value = reasonMessage(reason)
  } finally {
    settingsBusy.value = false
  }
}

async function importModule(): Promise<void> {
  if (importing.value || refreshing.value || hostInfo.value?.backend !== 'rust') return
  importing.value = true
  error.value = null
  try {
    const selected = await open({
      title: '导入 QingToolbox 模块',
      multiple: false,
      directory: false,
      filters: [{ name: 'QingToolbox module', extensions: ['qmod'] }],
    })
    if (!selected || Array.isArray(selected)) return
    await invoke('import_module', { sourcePath: selected })
    await refreshModules()
  } catch (reason) {
    error.value = reasonMessage(reason)
  } finally {
    importing.value = false
  }
}

function selectSetting(name: keyof SettingsUpdate, event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  void saveSettings({ [name]: value })
}

function toggleSetting(name: keyof SettingsUpdate, event: Event): void {
  const value = (event.target as HTMLInputElement).checked
  void saveSettings({ [name]: value })
}

async function refreshModules(): Promise<void> {
  const sequence = ++refreshSequence
  refreshing.value = true
  error.value = null

  try {
    const response = await invoke<ProtocolEnvelope<ModuleListPayload>>('list_modules')
    if (!isProtocolEnvelope<ModuleListPayload>(response, 'modules.list')) {
      throw new Error('模块协议版本或消息类型不受支持。')
    }
    if (sequence !== refreshSequence) return
    modules.value = response.payload.modules
    roots.value = response.payload.roots
    await refreshRuntimeSnapshots()
  } catch (reason) {
    if (sequence !== refreshSequence) return
    if (import.meta.env.DEV) {
      // Browser preview has no Rust process or filesystem access.
      modules.value = []
      roots.value = []
      error.value = '浏览器预览模式未连接 Rust 模块扫描器。'
    } else {
      error.value = reasonMessage(reason)
    }
  } finally {
    if (sequence === refreshSequence) {
      refreshing.value = false
      loading.value = false
    }
  }
}

async function refreshRuntimeSnapshots(): Promise<void> {
  try {
    const snapshots = await invoke<ModuleRuntimeSnapshot[]>('get_all_module_runtime')
    runtimeById.value = Object.fromEntries(snapshots.map((snapshot) => [snapshot.moduleId, snapshot]))
  } catch {
    // Runtime status is supplementary; discovery remains useful by itself.
    // Discovery and the rest of the shell remain usable if a process status
    // probe temporarily fails.
  }
}

function runtimeLabel(module: ModuleSummary): string {
  const state = runtimeById.value[module.id]?.state ?? 'notStarted'
  return {
    notStarted: '未启动',
    starting: '启动中',
    running: '运行中',
    stopped: '已停止',
    failed: '启动失败',
  }[state]
}

function isRunning(module: ModuleSummary): boolean {
  const state = runtimeById.value[module.id]?.state
  return state === 'starting' || state === 'running'
}

async function toggleModule(module: ModuleSummary): Promise<void> {
  if (!module.valid) return
  error.value = null
  try {
    if (module.uiKind === 'Web') {
      await invoke('open_module', { moduleId: module.id })
    } else {
      const command = isRunning(module) ? 'stop_module' : 'start_module'
      const snapshot = await invoke<ModuleRuntimeSnapshot>(command, { moduleId: module.id })
      runtimeById.value = { ...runtimeById.value, [module.id]: snapshot }
      if (snapshot.lastError) error.value = snapshot.lastError
    }
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
}

async function initialize(): Promise<void> {
  const hostInfoResult = loadHostInfo().catch((reason) => {
    error.value = reasonMessage(reason)
  })
  const settingsResult = loadSettings().catch((reason) => {
    error.value = reasonMessage(reason)
  })
  await Promise.all([hostInfoResult, settingsResult])
  await refreshModules()
  loading.value = false
  if (hostInfo.value?.backend === 'rust' && runtimePollTimer === undefined) {
    runtimePollTimer = window.setInterval(() => {
      void refreshRuntimeSnapshots()
    }, 500)
  }
}

async function hideToTray(): Promise<void> {
  try {
    await invoke('hide_to_tray')
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
}

onMounted(() => {
  void initialize()
})

onBeforeUnmount(() => {
  if (runtimePollTimer !== undefined) {
    window.clearInterval(runtimePollTimer)
    runtimePollTimer = undefined
  }
})
</script>

<template>
  <main class="shell">
    <header class="brand">
      <div class="brand-mark" aria-hidden="true">Q</div>
      <div>
        <p class="eyebrow">QING TOOLBOX</p>
        <h1>工具箱</h1>
      </div>
      <div class="brand-actions">
        <span class="migration-badge">Tauri 迁移预览</span>
        <button class="quiet-button" type="button" :aria-expanded="settingsExpanded" @click="settingsExpanded = !settingsExpanded">
          {{ settingsExpanded ? '收起设置' : '设置' }}
        </button>
        <button class="quiet-button" type="button" @click="hideToTray">隐藏到托盘</button>
      </div>
    </header>

    <section class="hero" aria-labelledby="hero-title">
      <div>
        <p class="eyebrow">轻量化桌面宿主</p>
        <h2 id="hero-title">Rust 后端 · Tauri 框架 · Vue 前端</h2>
        <p class="hero-copy">
          新宿主只读取可信模块清单，不加载代码。模块生命周期和权限将通过版本化协议逐步接入。
        </p>
      </div>
      <div class="status-orb" aria-hidden="true"><span /></div>
    </section>

    <section class="status-card" aria-live="polite">
      <div class="status-heading">
        <span class="status-dot" :class="{ ready: !loading && !error }" />
        <h3>宿主状态</h3>
        <span class="status-label">{{ statusLabel }}</span>
        <button class="refresh-button" type="button" :disabled="refreshing" @click="refreshModules">
          {{ refreshing ? '扫描中…' : '刷新模块' }}
        </button>
      </div>

      <p v-if="error" class="error">{{ error }}</p>
      <dl v-else-if="hostInfo" class="info-grid">
        <div>
          <dt>后端</dt>
          <dd>{{ hostInfo.backend === 'rust' ? 'Rust' : '浏览器预览' }}</dd>
        </div>
        <div>
          <dt>版本</dt>
          <dd>{{ hostInfo.version }}</dd>
        </div>
        <div>
          <dt>模块协议</dt>
          <dd>v{{ hostInfo.protocolVersion }}</dd>
        </div>
        <div>
          <dt>扫描根</dt>
          <dd>{{ roots.length }} 个</dd>
        </div>
      </dl>
    </section>

    <section v-if="settingsExpanded && settings" class="settings-card" aria-labelledby="settings-title">
      <div class="section-heading">
        <div>
          <p class="eyebrow">Rust 持久化</p>
          <h2 id="settings-title">宿主设置</h2>
        </div>
        <span class="settings-summary">{{ appearanceLabel }}</span>
      </div>
      <div class="settings-grid">
        <label class="setting-field">
          <span>界面语言</span>
          <select :value="settings.language" :disabled="settingsBusy" @change="selectSetting('language', $event)">
            <option value="system">跟随系统</option>
            <option value="zh-CN">简体中文</option>
            <option value="en-US">English</option>
          </select>
        </label>
        <label class="setting-field">
          <span>外观预设</span>
          <select :value="settings.appearancePresetId" :disabled="settingsBusy" @change="selectSetting('appearancePresetId', $event)">
            <option value="qing-default">Qing 默认</option>
            <option value="light">浅色</option>
            <option value="dark">深色</option>
            <option value="system">跟随系统</option>
            <option value="neon-circuit">Neon Circuit</option>
            <option value="greenline">Greenline</option>
            <option value="aurora-flow">Aurora Flow</option>
            <option value="qing-nova">Qing Nova</option>
          </select>
        </label>
        <label class="setting-field">
          <span>关闭窗口时</span>
          <select :value="settings.closeBehavior" :disabled="settingsBusy" @change="selectSetting('closeBehavior', $event)">
            <option value="ask">每次询问</option>
            <option value="tray">最小化到托盘</option>
            <option value="exit">退出工具箱</option>
          </select>
        </label>
        <label class="setting-field">
          <span>启动时显示</span>
          <select :value="settings.startupPresentation" :disabled="settingsBusy" @change="selectSetting('startupPresentation', $event)">
            <option value="main">主窗口</option>
            <option value="minimized">最小化窗口</option>
            <option value="tray">托盘</option>
          </select>
        </label>
      </div>
      <div class="settings-toggles">
        <label class="setting-toggle">
          <input type="checkbox" :checked="settings.launchAtLogin" :disabled="settingsBusy" @change="toggleSetting('launchAtLogin', $event)" />
          <span><strong>登录时启动</strong><small>保存偏好；启动注册接入将在后续迁移阶段启用。</small></span>
        </label>
        <label class="setting-toggle">
          <input type="checkbox" :checked="settings.showLogsInSidebar" :disabled="settingsBusy" @change="toggleSetting('showLogsInSidebar', $event)" />
          <span><strong>在侧栏显示日志</strong><small>为后续诊断面板保留的宿主偏好。</small></span>
        </label>
      </div>
    </section>

    <section class="modules-section" aria-labelledby="modules-title">
      <div class="section-heading">
        <div>
          <p class="eyebrow">只读发现</p>
          <h2 id="modules-title">模块</h2>
        </div>
        <div class="section-actions">
          <span v-if="invalidModuleCount" class="warning-count">{{ invalidModuleCount }} 个清单需要修复</span>
          <button class="import-button" type="button" :disabled="importing || refreshing || hostInfo?.backend !== 'rust'" @click="importModule">
            {{ importing ? '导入中…' : '导入 .qmod' }}
          </button>
        </div>
      </div>

      <p v-if="loading" class="empty-state">正在读取固定模块根…</p>
      <p v-else-if="!modules.length && !error" class="empty-state">尚未发现模块。</p>
      <div v-else class="module-grid">
        <article v-for="module in modules" :key="`${module.source}:${module.id}`" class="module-card">
          <div class="module-icon" aria-hidden="true">
            <img v-if="module.iconDataUrl" :src="module.iconDataUrl" alt="" />
            <span v-else>{{ module.name.slice(0, 1) }}</span>
          </div>
          <div class="module-content">
            <div class="module-title-row">
              <h3>{{ module.name }}</h3>
              <span class="module-source">{{ module.source === 'bundled' ? '内置' : '用户' }}</span>
            </div>
            <p class="module-id">{{ module.id }} · v{{ module.version }}</p>
            <p v-if="module.description" class="module-description">{{ module.description }}</p>
            <div v-if="module.valid" class="module-runtime-row">
              <p class="module-ready">清单有效 · {{ runtimeLabel(module) }}</p>
              <button class="module-action" type="button" @click="toggleModule(module)">
                {{ module.uiKind === 'Web' ? '打开' : isRunning(module) ? '停止' : '启动' }}
              </button>
            </div>
            <p v-else class="module-invalid">{{ module.issues[0]?.message ?? '清单无效' }}</p>
          </div>
        </article>
      </div>
    </section>

    <footer>
      <span>QingToolbox.Tauri</span>
      <span>新架构基线 · {{ new Date().getFullYear() }}</span>
    </footer>
  </main>
</template>
