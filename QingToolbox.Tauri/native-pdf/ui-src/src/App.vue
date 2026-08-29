<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { open, save } from '@tauri-apps/plugin-dialog'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import type { ModuleContext, PdfFile, PdfState } from './types'

type Tab = 'merge' | 'split' | 'extract' | 'rotate'

const emptyState = (): PdfState => ({
  mergeFiles: [],
  source: null,
  busy: false,
  activeOperation: null,
  runtimeStatus: 'Unavailable',
  message: null,
  result: null,
})

const state = ref<PdfState>(emptyState())
const context = ref<ModuleContext | null>(null)
const tab = ref<Tab>('merge')
const parts = ref(2)
const extractPages = ref('1')
const rotatePages = ref('')
const degrees = ref(90)
const loading = ref(true)
const requestBusy = ref(false)
const error = ref('')
const notice = ref('')
const draggingId = ref<string | null>(null)

const unavailable = computed(() => state.value.runtimeStatus === 'Unavailable')
const disabled = computed(() => loading.value || requestBusy.value || state.value.busy || unavailable.value)
const sourceReady = computed(() => Boolean(state.value.source))
const totalMergePages = computed(() => state.value.mergeFiles.reduce((sum, file) => sum + file.pages, 0))
const splitRanges = computed(() => {
  const total = state.value.source?.pages ?? 0
  const count = Math.max(2, Math.min(parts.value, total || 2))
  if (!total || count > total) return []
  const base = Math.floor(total / count)
  const remainder = total % count
  let start = 1
  return Array.from({ length: count }, (_, index) => {
    const size = base + (index < remainder ? 1 : 0)
    const range = `${start}–${start + size - 1}`
    start += size
    return range
  })
})

function apply(next: PdfState | null | undefined): void {
  if (next) state.value = next
}

function messageOf(reason: unknown): string {
  if (reason instanceof Error && reason.message) return reason.message
  if (typeof reason === 'object' && reason !== null) {
    const value = reason as { message?: unknown; error?: unknown }
    if (typeof value.message === 'string' && value.message) return value.message
    if (typeof value.error === 'string' && value.error) return value.error
  }
  return 'PDF 操作未能完成。'
}

async function call(method: string, payload: Record<string, unknown> = {}): Promise<PdfState | null> {
  requestBusy.value = true
  error.value = ''
  try {
    const next = await invokeModule<PdfState>(method, payload)
    apply(next)
    return next
  } catch (reason) {
    error.value = messageOf(reason)
    return null
  } finally {
    requestBusy.value = false
  }
}

async function chooseMergeFiles(): Promise<void> {
  if (disabled.value) return
  try {
    const picked = await open({
      title: '选择要拼接的 PDF',
      multiple: true,
      directory: false,
      filters: [{ name: 'PDF 文件', extensions: ['pdf'] }],
    })
    if (!picked) return
    const paths = Array.isArray(picked) ? picked : [picked]
    await call('addMergeFiles', { paths })
  } catch (reason) {
    error.value = messageOf(reason)
  }
}

async function chooseSource(): Promise<void> {
  if (disabled.value) return
  try {
    const picked = await open({
      title: '选择源 PDF',
      multiple: false,
      directory: false,
      filters: [{ name: 'PDF 文件', extensions: ['pdf'] }],
    })
    if (typeof picked === 'string' && picked) await call('setSource', { path: picked })
  } catch (reason) {
    error.value = messageOf(reason)
  }
}

async function chooseOutputFile(suggestedName: string): Promise<string | null> {
  try {
    const picked = await save({
      title: '选择输出 PDF',
      defaultPath: suggestedName,
      filters: [{ name: 'PDF 文件', extensions: ['pdf'] }],
    })
    return picked
  } catch (reason) {
    error.value = messageOf(reason)
    return null
  }
}

async function chooseOutputDirectory(): Promise<string | null> {
  try {
    const picked = await open({ title: '选择输出文件夹', directory: true, multiple: false })
    return typeof picked === 'string' ? picked : null
  } catch (reason) {
    error.value = messageOf(reason)
    return null
  }
}

async function runMerge(): Promise<void> {
  if (disabled.value || state.value.mergeFiles.length < 2) return
  const outputPath = await chooseOutputFile('merged.pdf')
  if (outputPath) await call('runMerge', { outputPath })
}

async function runSplit(): Promise<void> {
  if (disabled.value || !state.value.source) return
  const outputDirectory = await chooseOutputDirectory()
  if (outputDirectory) await call('runSplit', { parts: parts.value, outputDirectory })
}

async function runExtract(): Promise<void> {
  if (disabled.value || !state.value.source || !extractPages.value.trim()) return
  const stem = state.value.source.name.replace(/\.pdf$/i, '')
  const outputPath = await chooseOutputFile(`${stem}_pages.pdf`)
  if (outputPath) await call('runExtract', { pages: extractPages.value, outputPath })
}

async function runRotate(): Promise<void> {
  if (disabled.value || !state.value.source) return
  const stem = state.value.source.name.replace(/\.pdf$/i, '')
  const outputPath = await chooseOutputFile(`${stem}_rotated.pdf`)
  if (outputPath) await call('runRotate', { degrees: degrees.value, pages: rotatePages.value, outputPath })
}

function reorder(fromId: string, toId: string): void {
  if (fromId === toId || disabled.value) return
  const ids = state.value.mergeFiles.map(file => file.id)
  const from = ids.indexOf(fromId)
  const to = ids.indexOf(toId)
  if (from < 0 || to < 0) return
  ids.splice(to, 0, ids.splice(from, 1)[0])
  state.value.mergeFiles = ids
    .map(id => state.value.mergeFiles.find(file => file.id === id))
    .filter((file): file is PdfFile => Boolean(file))
  void call('setMergeOrder', { ids })
}

function dropFile(file: PdfFile): void {
  const fromId = draggingId.value
  draggingId.value = null
  if (fromId) reorder(fromId, file.id)
}

function move(file: PdfFile, offset: number): void {
  const index = state.value.mergeFiles.findIndex(candidate => candidate.id === file.id)
  const target = state.value.mergeFiles[index + offset]
  if (target) reorder(file.id, target.id)
}

function formatSize(value: number): string {
  if (value < 1024 * 1024) return `${Math.max(1, Math.round(value / 1024))} KB`
  return `${(value / 1024 / 1024).toFixed(value < 10 * 1024 * 1024 ? 1 : 0)} MB`
}

function clearNotice(): void { notice.value = '' }

function onWindowKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !requestBusy.value) {
    event.preventDefault()
    if (error.value) error.value = ''
    else void hideModuleWindow()
  }
}

onMounted(async () => {
  window.addEventListener('keydown', onWindowKeydown)
  try {
    context.value = await getContext()
    apply(await getState())
  } catch (reason) {
    error.value = messageOf(reason)
  } finally {
    loading.value = false
  }
})

onUnmounted(() => window.removeEventListener('keydown', onWindowKeydown))
</script>

<template>
  <main class="viewport">
    <section class="workspace">
      <header class="hero">
        <div class="brand-mark" aria-hidden="true">
          <img v-if="context?.iconDataUrl" :src="context.iconDataUrl" alt="" />
          <span v-else>PDF</span>
        </div>
        <div class="hero-copy">
          <span class="eyebrow">LOCAL PDF WORKSPACE</span>
          <h1>{{ context?.name || 'Qing PDF' }}</h1>
          <p>快速处理 PDF 文档结构，文件始终留在本机。</p>
        </div>
        <div class="runtime" :class="{ unavailable }"><span class="status-dot" />{{ loading ? '正在准备…' : state.runtimeStatus }}</div>
      </header>

      <nav class="tabs" aria-label="PDF 工具">
        <button :class="{ active: tab === 'merge' }" @click="tab = 'merge'">拼接</button>
        <button :class="{ active: tab === 'split' }" @click="tab = 'split'">均分</button>
        <button :class="{ active: tab === 'extract' }" @click="tab = 'extract'">提取页面</button>
        <button :class="{ active: tab === 'rotate' }" @click="tab = 'rotate'">旋转</button>
      </nav>

      <div v-if="unavailable && !loading" class="alert error-alert">内置 qpdf 运行时不可用，请重新安装完整模块包。</div>
      <div v-if="error" class="alert error-alert"><span>{{ error }}</span><button aria-label="关闭" @click="error = ''">×</button></div>
      <div v-if="notice" class="alert"><span>{{ notice }}</span><button aria-label="关闭" @click="clearNotice">×</button></div>

      <section v-if="tab === 'merge'" class="tool-card">
        <div class="tool-heading">
          <div><h2>拼接 PDF</h2><p>按最终文档需要的顺序排列文件。</p></div>
          <div class="heading-actions"><button class="secondary" :disabled="disabled" @click="chooseMergeFiles">＋ 添加 PDF</button><button class="ghost" :disabled="disabled || !state.mergeFiles.length" @click="call('clearMergeFiles')">清空</button></div>
        </div>
        <div v-if="state.mergeFiles.length" class="file-list">
          <article v-for="(file, index) in state.mergeFiles" :key="file.id" class="file-row" :class="{ dragging: draggingId === file.id }" draggable="true" @dragstart="draggingId = file.id" @dragend="draggingId = null" @dragover.prevent @drop.prevent="dropFile(file)">
            <span class="drag-handle" aria-hidden="true">⠿</span><span class="order">{{ index + 1 }}</span><span class="pdf-mini">PDF</span>
            <span class="file-copy"><strong>{{ file.name }}</strong><small>{{ file.directory }}</small></span><span class="meta">{{ file.pages }} 页 · {{ formatSize(file.size) }}</span>
            <span class="row-actions"><button :disabled="disabled || index === 0" aria-label="上移" @click="move(file, -1)">↑</button><button :disabled="disabled || index === state.mergeFiles.length - 1" aria-label="下移" @click="move(file, 1)">↓</button><button :disabled="disabled" aria-label="移除" @click="call('removeMergeFile', { id: file.id })">×</button></span>
          </article>
        </div>
        <button v-else class="drop-zone" :disabled="disabled" @click="chooseMergeFiles"><span class="drop-icon">＋</span><strong>拼接列表中还没有 PDF</strong><small>点击选择文件；拖放支持将在宿主统一接入。</small></button>
        <footer class="tool-footer"><span>{{ totalMergePages }} 页</span><button class="primary" :disabled="disabled || state.mergeFiles.length < 2" @click="runMerge">开始拼接</button></footer>
      </section>

      <section v-else class="tool-card">
        <div class="source-card" :class="{ empty: !state.source }">
          <template v-if="state.source"><span class="pdf-large">PDF</span><span class="file-copy"><small>源 PDF</small><strong>{{ state.source.name }}</strong><span>{{ state.source.directory }}</span></span><span class="meta">{{ state.source.pages }} 页 · {{ formatSize(state.source.size) }}</span></template>
          <template v-else><span class="source-empty-copy"><strong>选择一个 PDF 后即可开始</strong><small>页面范围会在本机校验。</small></span></template>
          <button class="secondary" :disabled="disabled" @click="chooseSource">{{ state.source ? '更换 PDF' : '选择 PDF' }}</button>
        </div>

        <div v-if="tab === 'split'" class="operation-body"><div class="field-row"><label><span>均分份数</span><input v-model.number="parts" type="number" min="2" :max="state.source?.pages || 2" :disabled="!sourceReady || disabled" /></label></div><div class="distribution"><span>页面分配</span><div><b v-for="(range, index) in splitRanges" :key="range">{{ index + 1 }}<small>{{ range }}</small></b></div></div><button class="primary operation-button" :disabled="disabled || !sourceReady || parts < 2 || parts > (state.source?.pages || 0)" @click="runSplit">生成分份文件</button></div>
        <div v-else-if="tab === 'extract'" class="operation-body"><label class="field"><span>页码范围</span><input v-model="extractPages" type="text" placeholder="例如：1-3,5,8-10" :disabled="!sourceReady || disabled" /><small>支持逗号分隔的连续页码范围。</small></label><button class="primary operation-button" :disabled="disabled || !sourceReady || !extractPages.trim()" @click="runExtract">提取页面</button></div>
        <div v-else class="operation-body"><label class="field"><span>旋转角度</span><select v-model.number="degrees" :disabled="!sourceReady || disabled"><option :value="90">90°</option><option :value="180">180°</option><option :value="270">270°</option></select></label><label class="field"><span>页码范围（可选）</span><input v-model="rotatePages" type="text" placeholder="留空表示全部页面" :disabled="!sourceReady || disabled" /></label><button class="primary operation-button" :disabled="disabled || !sourceReady" @click="runRotate">旋转 PDF</button></div>
      </section>

      <aside v-if="state.busy" class="job-card"><span class="spinner" /><span><strong>正在处理 PDF…</strong><small>{{ state.activeOperation }}</small></span><button class="secondary" @click="call('cancel')">取消</button></aside>
      <aside v-else-if="state.result" class="result-card"><span class="result-check">✓</span><span><strong>处理结果已生成</strong><small>{{ state.result.files.join(' · ') }}</small></span><button class="secondary" @click="call('openResult', { resultId: state.result?.id })">查看输出</button><button class="ghost" @click="call('clearResult')">关闭</button></aside>
      <aside v-else-if="state.message === 'canceled'" class="alert">操作已取消。</aside>

      <footer class="privacy"><span>● 所有处理均在本机完成</span><p>页面内容会被保留；复杂目录、表单或数字签名请在处理后再次检查。</p></footer>
    </section>
  </main>
</template>
