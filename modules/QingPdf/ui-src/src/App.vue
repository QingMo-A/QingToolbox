<script setup lang="ts">
import { QButton, QIcon, QIconButton, selectFiles, selectDirectory, selectSavePath } from '@qingtoolbox/module-ui'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { getCurrentWebview } from '@tauri-apps/api/webview'
import { getContext, getState, hideModuleWindow, invokeModule } from './bridge'
import { formatSize, messageOf, splitPreview } from './presentation'
import PdfIcon from './PdfIcon.vue'
import type { ModuleContext, PdfState } from './types'

type Tab = 'merge' | 'split' | 'extract' | 'rotate'
const tabs: { id: Tab; label: string }[] = [
  { id: 'merge', label: '拼接' }, { id: 'split', label: '均分' },
  { id: 'extract', label: '提取页面' }, { id: 'rotate', label: '旋转' },
]
const state = ref<PdfState>({ mergeFiles: [], source: null, busy: false, activeOperation: null, runtimeStatus: 'Unavailable', message: null, result: null })
const context = ref<ModuleContext | null>(null)
const tab = ref<Tab>('merge')
const parts = ref(2)
const extractPages = ref('1')
const rotatePages = ref('')
const degrees = ref(90)
const loading = ref(true)
const requestBusy = ref(false)
const dialogBusy = ref(false)
const pendingOperation = ref('')
const error = ref('')
const draggingId = ref<string | null>(null)
const dragTarget = ref<string | null>(null)
const externalDrag = ref(false)
let unlistenDrop: (() => void) | undefined
let disposed = false

const unavailable = computed(() => state.value.runtimeStatus === 'Unavailable')
const disabled = computed(() => loading.value || requestBusy.value || dialogBusy.value || state.value.busy || unavailable.value)
const working = computed(() => Boolean(pendingOperation.value) || state.value.busy)
const sourceReady = computed(() => Boolean(state.value.source))
const tabIndex = computed(() => tabs.findIndex(item => item.id === tab.value))
const totalMergePages = computed(() => state.value.mergeFiles.reduce((sum, file) => sum + file.pages, 0))
const ranges = computed(() => splitPreview(state.value.source?.pages ?? 0, parts.value))
const splitValid = computed(() => ranges.value.length > 0)
const runLabels: Record<string, string> = { runMerge: '正在拼接', runSplit: '正在均分', runExtract: '正在提取', runRotate: '正在旋转' }

async function call(method: string, payload: Record<string, unknown> = {}): Promise<boolean> {
  if (requestBusy.value) return false
  requestBusy.value = true
  pendingOperation.value = runLabels[method] ?? ''
  error.value = ''
  try {
    state.value = await invokeModule<PdfState>(method, payload)
    return true
  } catch (reason) {
    error.value = messageOf(reason)
    return false
  } finally {
    requestBusy.value = false
    pendingOperation.value = ''
  }
}

async function fileDialog<T>(pick: () => Promise<T>): Promise<T | null> {
  if (disabled.value) return null
  dialogBusy.value = true
  error.value = ''
  try { return await pick() }
  catch (reason) { error.value = messageOf(reason); return null }
  finally { dialogBusy.value = false }
}

async function addPaths(paths: string[]): Promise<void> {
  if (disabled.value || paths.length === 0) return
  if (paths.some(path => !/\.pdf$/i.test(path))) {
    error.value = '请选择 PDF 文件。'
    return
  }
  if (tab.value === 'merge') await call('addMergeFiles', { paths })
  else if (paths.length === 1) await call('setSource', { path: paths[0] })
  else error.value = '此操作一次处理一个 PDF；多个文件请使用“拼接”。'
}

async function chooseMergeFiles(): Promise<void> {
  const picked = await fileDialog(() => selectFiles({ title: '选择要拼接的 PDF', multiple: true, filters: [{ name: 'PDF 文件', extensions: ['pdf'] }] }))
  if (picked) await addPaths(picked)
}

async function chooseSource(): Promise<void> {
  const picked = await fileDialog(() => selectFiles({ title: '选择源 PDF', multiple: false, filters: [{ name: 'PDF 文件', extensions: ['pdf'] }] }))
  if (picked?.[0]) await addPaths(picked)
}

function chooseOutputFile(name: string): Promise<string | null> {
  return fileDialog(() => selectSavePath({ title: '保存 PDF', defaultPath: name, filters: [{ name: 'PDF 文件', extensions: ['pdf'] }] }))
}

async function runMerge(): Promise<void> {
  if (disabled.value || state.value.mergeFiles.length < 2) return
  const outputPath = await chooseOutputFile('merged.pdf')
  if (outputPath) await call('runMerge', { outputPath })
}

async function runSplit(): Promise<void> {
  if (disabled.value || !sourceReady.value || !splitValid.value) return
  const directory = await fileDialog(() => selectDirectory({ title: '选择输出文件夹' }))
  if (typeof directory === 'string' && directory) await call('runSplit', { parts: parts.value, outputDirectory: directory })
}

async function runExtract(): Promise<void> {
  if (disabled.value || !state.value.source || !extractPages.value.trim()) return
  const outputPath = await chooseOutputFile(`${state.value.source.name.replace(/\.pdf$/i, '')}_pages.pdf`)
  if (outputPath) await call('runExtract', { pages: extractPages.value, outputPath })
}

async function runRotate(): Promise<void> {
  if (disabled.value || !state.value.source) return
  const outputPath = await chooseOutputFile(`${state.value.source.name.replace(/\.pdf$/i, '')}_rotated.pdf`)
  if (outputPath) await call('runRotate', { degrees: degrees.value, pages: rotatePages.value, outputPath })
}

async function reorder(fromId: string, toId: string): Promise<void> {
  if (fromId === toId || disabled.value) return
  const before = [...state.value.mergeFiles]
  const from = before.findIndex(file => file.id === fromId)
  const to = before.findIndex(file => file.id === toId)
  if (from < 0 || to < 0) return
  const next = [...before]
  next.splice(to, 0, next.splice(from, 1)[0])
  state.value.mergeFiles = next
  if (!await call('setMergeOrder', { ids: next.map(file => file.id) })) state.value.mergeFiles = before
}

function startDrag(event: DragEvent, id: string): void {
  if (disabled.value) { event.preventDefault(); return }
  draggingId.value = id
  if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', id) }
}
function endDrag(): void { draggingId.value = null; dragTarget.value = null }
function dropFile(id: string): void {
  const from = draggingId.value
  endDrag()
  if (from) void reorder(from, id)
}
function move(id: string, offset: number): void {
  const index = state.value.mergeFiles.findIndex(file => file.id === id)
  const target = state.value.mergeFiles[index + offset]
  if (target) void reorder(id, target.id)
}
function changeTab(event: KeyboardEvent, index: number): void {
  let next: number
  if (event.key === 'ArrowRight') next = (index + 1) % tabs.length
  else if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length
  else if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = tabs.length - 1
  else return
  event.preventDefault()
  tab.value = tabs[next].id
  document.getElementById(`tab-${tab.value}`)?.focus()
}

function onWindowKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && !requestBusy.value && !dialogBusy.value) {
    event.preventDefault()
    if (error.value) error.value = ''
    else void hideModuleWindow().catch(reason => { error.value = messageOf(reason) })
  }
}

onMounted(async () => {
  window.addEventListener('keydown', onWindowKeydown)
  try { context.value = await getContext(); state.value = await getState() }
  catch (reason) { error.value = messageOf(reason) }
  finally { loading.value = false }
  try {
    const stop = await getCurrentWebview().onDragDropEvent(event => {
      if (disposed) return
      const payload = event.payload
      if (payload.type === 'enter' || payload.type === 'over') externalDrag.value = !disabled.value && !draggingId.value
      else if (payload.type === 'leave') externalDrag.value = false
      else if (payload.type === 'drop') { externalDrag.value = false; void addPaths(payload.paths) }
    })
    if (disposed) stop()
    else unlistenDrop = stop
  } catch { /* Older hosts can still use the native file picker. */ }
})
onUnmounted(() => { disposed = true; unlistenDrop?.(); window.removeEventListener('keydown', onWindowKeydown) })
</script>

<template>
  <main class="pdf-viewport" :class="{ 'has-external-drag': externalDrag }" @contextmenu.prevent>
    <section class="pdf-workspace">
      <header class="pdf-header">
        <div class="pdf-brand"><span class="brand-mark"><PdfIcon name="document" /></span><h1>{{ context?.name || 'Qing PDF' }}</h1></div>
        <div class="header-actions">
          <span class="runtime" :class="{ unavailable: unavailable && !loading, preparing: loading || working }" :title="state.runtimeStatus" role="status"><i />{{ loading ? '正在准备' : working ? '处理中' : unavailable ? '引擎不可用' : '已就绪' }}</span>
          <QIconButton label="刷新状态" :disabled="disabled" :loading="loading" @click="call('getState')"><QIcon name="refresh" /></QIconButton>
        </div>
      </header>

      <nav class="pdf-tabs" role="tablist" aria-label="PDF 工具" :style="{ '--tab-index': tabIndex }">
        <span class="tab-thumb" aria-hidden="true" />
        <button v-for="(item, index) in tabs" :id="`tab-${item.id}`" :key="item.id" role="tab" :aria-selected="tab === item.id" :aria-controls="`panel-${item.id}`" :tabindex="tab === item.id ? 0 : -1" :class="{ active: tab === item.id }" @click="tab = item.id" @keydown="changeTab($event, index)"><PdfIcon :name="item.id" /><span>{{ item.label }}</span></button>
      </nav>

      <Transition name="notice"><div v-if="error || (unavailable && !loading)" class="error-banner" role="alert"><QIcon name="statusWarning" /><span>{{ error || '内置 PDF 引擎不可用，请重新安装完整模块包。' }}</span><QIconButton v-if="error" label="关闭提示" @click="error = ''"><QIcon name="close" /></QIconButton></div></Transition>

      <Transition name="panel" mode="out-in">
        <section :id="`panel-${tab}`" :key="tab" class="tool-card" role="tabpanel" :aria-labelledby="`tab-${tab}`" :aria-busy="working">
          <template v-if="tab === 'merge'">
            <div class="tool-heading"><div><h2>拼接 PDF</h2><span class="subtle">{{ state.mergeFiles.length }} 个文件 · {{ totalMergePages }} 页</span></div><div class="heading-actions"><QButton :disabled="disabled" :loading="dialogBusy" @click="chooseMergeFiles"><PdfIcon name="add" />添加 PDF</QButton><QIconButton label="清空列表" :disabled="disabled || !state.mergeFiles.length" @click="call('clearMergeFiles')"><QIcon name="remove" /></QIconButton></div></div>
            <TransitionGroup v-if="state.mergeFiles.length" name="files" tag="div" class="file-list">
              <article v-for="(file, index) in state.mergeFiles" :key="file.id" class="file-row" :class="{ dragging: draggingId === file.id, 'drop-target': dragTarget === file.id }" :draggable="!disabled" @dragstart="startDrag($event, file.id)" @dragend="endDrag" @dragover.prevent="dragTarget = file.id" @dragleave="dragTarget = null" @drop.prevent="dropFile(file.id)">
                <span class="drag-handle"><PdfIcon name="grip" /></span><span class="file-order">{{ index + 1 }}</span><span class="file-icon"><PdfIcon name="document" /></span>
                <div class="file-copy"><strong :title="file.name">{{ file.name }}</strong><small :title="file.directory">{{ file.directory }}</small><span class="mobile-meta">{{ file.pages }} 页 · {{ formatSize(file.size) }}</span></div>
                <span class="file-meta">{{ file.pages }} 页<span>{{ formatSize(file.size) }}</span></span>
                <div class="row-actions"><QIconButton label="上移" :disabled="disabled || index === 0" @click="move(file.id, -1)"><PdfIcon name="up" /></QIconButton><QIconButton label="下移" :disabled="disabled || index === state.mergeFiles.length - 1" @click="move(file.id, 1)"><PdfIcon name="down" /></QIconButton><QIconButton label="移除文件" :disabled="disabled" @click="call('removeMergeFile', { id: file.id })"><QIcon name="close" /></QIconButton></div>
              </article>
            </TransitionGroup>
            <button v-else class="file-drop-zone" :disabled="disabled" @click="chooseMergeFiles"><span class="drop-mark"><PdfIcon name="add" /></span><strong>添加 PDF</strong><span>拖入文件，或点击选择</span></button>
            <footer class="tool-footer"><span class="subtle">{{ state.mergeFiles.length ? '拖动文件调整拼接顺序' : '至少添加两个 PDF' }}</span><QButton variant="primary" :disabled="disabled || state.mergeFiles.length < 2" :loading="pendingOperation === '正在拼接'" @click="runMerge"><PdfIcon name="merge" />{{ pendingOperation === '正在拼接' ? '正在拼接' : '开始拼接' }}</QButton></footer>
          </template>
          <template v-else>
            <div class="tool-heading"><h2>{{ tab === 'split' ? '均分 PDF' : tab === 'extract' ? '提取页面' : '旋转 PDF' }}</h2><QButton :disabled="disabled" :loading="dialogBusy" @click="chooseSource"><QIcon name="folder" />{{ state.source ? '更换 PDF' : '选择 PDF' }}</QButton></div>
            <div v-if="state.source" class="source-card"><span class="file-icon"><PdfIcon name="document" /></span><div class="file-copy"><strong :title="state.source.name">{{ state.source.name }}</strong><small :title="state.source.directory">{{ state.source.directory }}</small></div><span class="file-meta">{{ state.source.pages }} 页<span>{{ formatSize(state.source.size) }}</span></span></div>
            <button v-else class="source-empty" :disabled="disabled" @click="chooseSource"><PdfIcon name="document" /><span>拖入或选择一个 PDF</span></button>
            <div class="operation-body">
              <template v-if="tab === 'split'">
                <label class="field split-field"><span>均分份数</span><div class="split-input"><input v-model.number="parts" type="number" min="2" :max="state.source?.pages || 2" :disabled="!sourceReady || disabled" /><span>份</span></div></label>
                <div class="distribution"><span class="field-label">页面分配</span><div v-if="ranges.length" class="range-list"><span v-for="(range, index) in ranges" :key="index" class="range-chip"><b>{{ index + 1 }}</b>{{ range }} 页</span><span v-if="parts > ranges.length" class="subtle">另 {{ parts - ranges.length }} 份</span></div><span v-else class="subtle">{{ sourceReady ? '份数需为 2 至总页数之间的整数' : '选择文件后显示' }}</span></div>
              </template>
              <label v-else-if="tab === 'extract'" class="field"><span>页码范围</span><input v-model="extractPages" type="text" placeholder="例如：1-3, 5, 8-10" :disabled="!sourceReady || disabled" /><small>用逗号分隔，按输入顺序提取。</small></label>
              <template v-else>
                <div class="field"><span>顺时针旋转</span><div class="angle-options" role="group" aria-label="旋转角度"><button v-for="angle in [90, 180, 270]" :key="angle" :aria-pressed="degrees === angle" :class="{ selected: degrees === angle }" :disabled="!sourceReady || disabled" @click="degrees = angle"><PdfIcon name="rotate" />{{ angle }}°</button></div></div>
                <label class="field"><span>页码范围</span><input v-model="rotatePages" type="text" placeholder="留空表示全部页面" :disabled="!sourceReady || disabled" /></label>
              </template>
            </div>
            <footer class="tool-footer"><span class="subtle">{{ sourceReady ? `${state.source?.pages} 页` : '尚未选择源文件' }}</span><QButton v-if="tab === 'split'" variant="primary" :disabled="disabled || !sourceReady || !splitValid" :loading="pendingOperation === '正在均分'" @click="runSplit"><PdfIcon name="split" />生成分份文件</QButton><QButton v-else-if="tab === 'extract'" variant="primary" :disabled="disabled || !sourceReady || !extractPages.trim()" :loading="pendingOperation === '正在提取'" @click="runExtract"><PdfIcon name="extract" />提取页面</QButton><QButton v-else variant="primary" :disabled="disabled || !sourceReady" :loading="pendingOperation === '正在旋转'" @click="runRotate"><PdfIcon name="rotate" />旋转 PDF</QButton></footer>
          </template>
        </section>
      </Transition>

      <Transition name="notice"><aside v-if="working" class="job-card" role="status"><span class="job-spinner" /><strong>{{ pendingOperation || '正在处理 PDF' }}…</strong></aside><aside v-else-if="state.result" class="result-card" role="status"><span class="result-mark"><QIcon name="statusSuccess" /></span><div class="result-copy"><strong>处理完成</strong><span :title="state.result.files.join('\n')">{{ state.result.files.length }} 个文件 · {{ state.result.outputDirectory }}</span></div><QButton :disabled="disabled" @click="call('openResult', { resultId: state.result.id })"><QIcon name="folder" />查看输出</QButton><QIconButton label="关闭结果" :disabled="disabled" @click="call('clearResult')"><QIcon name="close" /></QIconButton></aside></Transition>
    </section>
    <Transition name="notice"><div v-if="externalDrag" class="external-drop" aria-live="polite"><span><PdfIcon name="document" /><strong>{{ tab === 'merge' ? '松开以添加 PDF' : '松开以选择 PDF' }}</strong></span></div></Transition>
  </main>
</template>
