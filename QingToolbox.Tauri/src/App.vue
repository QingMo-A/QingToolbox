<script setup lang="ts">
import { invoke } from '@tauri-apps/api/core'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  isProtocolEnvelope,
  type HostInfo,
  type ModuleListPayload,
  type ModuleRuntimeSnapshot,
  type ModuleSummary,
  type ProtocolEnvelope,
} from './protocol'

const hostInfo = ref<HostInfo | null>(null)
const modules = ref<ModuleSummary[]>([])
const roots = ref<ModuleListPayload['roots']>([])
const runtimeById = ref<Record<string, ModuleRuntimeSnapshot>>({})
const loading = ref(true)
const refreshing = ref(false)
const error = ref<string | null>(null)
let refreshSequence = 0
let runtimePollTimer: number | undefined

const validModuleCount = computed(() => modules.value.filter((module) => module.valid).length)
const invalidModuleCount = computed(() => modules.value.length - validModuleCount.value)
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
  try {
    await loadHostInfo()
  } catch (reason) {
    error.value = reasonMessage(reason)
  }
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

    <section class="modules-section" aria-labelledby="modules-title">
      <div class="section-heading">
        <div>
          <p class="eyebrow">只读发现</p>
          <h2 id="modules-title">模块</h2>
        </div>
        <span v-if="invalidModuleCount" class="warning-count">{{ invalidModuleCount }} 个清单需要修复</span>
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
