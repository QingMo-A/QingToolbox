<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { getCurrentWebview } from '@tauri-apps/api/webview'
import { QButton, QIconButton, QIcon, QModal, selectFiles, selectDirectory } from '@qingtoolbox/module-ui'
import ConvertIcon from './ConvertIcon.vue'
import { getState, invokeModule } from './bridge'
import { batchSummary, formatSize, messageOf } from './presentation'
import type { Settings, State, Target } from './types'

type Category = 'image' | 'audio' | 'documents'
const tabs: { id: Category; title: string; icon: 'image' | 'audio' | 'document' }[] = [{id:'image',title:'图片',icon:'image'},{id:'audio',title:'音频',icon:'audio'},{id:'documents',title:'Word / PDF',icon:'document'}]
const initial: Settings = { ffmpegPath:'',libreOfficePath:'',outputDirectory:'',wordEngine:'auto' }
const state = ref<State>({files:[],busy:false,cancelling:false,settings:{...initial},engines:{wordAvailable:false,ffmpegPath:null,libreOfficePath:null},message:''})
const category = ref<Category>('image'), target = ref<Target>('png')
const quality = ref(90), maxWidth = ref(0), background = ref('#ffffff'), audioBitrate = ref(192), outputDirectory = ref('')
const error = ref(''), pending = ref(false), dragOver = ref(false), settingsOpen = ref(false)
const draft = ref<Settings>({...initial})
let epoch = 0, timer: ReturnType<typeof setInterval> | undefined, unlisten: (() => void) | undefined, disposed = false, polling = false
const tabIndex = computed(() => tabs.findIndex(v => v.id === category.value))
const summary = computed(() => batchSummary(state.value.files))
const officeAvailable = computed(() => state.value.settings.wordEngine === 'word' ? state.value.engines.wordAvailable : state.value.settings.wordEngine === 'libreoffice' ? Boolean(state.value.engines.libreOfficePath) : state.value.engines.wordAvailable || Boolean(state.value.engines.libreOfficePath))
const engineMissing = computed(() => category.value === 'audio' ? !state.value.engines.ffmpegPath : target.value === 'word-pdf' && !officeAvailable.value)
const disabled = computed(() => pending.value || state.value.busy)
const targetFormats = computed(() => category.value === 'image' ? ['png','jpg','webp','bmp'] : ['mp3','wav','flac','m4a'])
const statuses = { queued:'待转换',running:'正在转换',succeeded:'已完成',failed:'失败',cancelled:'已取消' }
const extensions = computed(() => category.value === 'image' ? ['png','jpg','jpeg','webp','bmp'] : category.value === 'audio' ? ['mp3','wav','flac','m4a','aac','ogg','opus','wma','mp4','mkv','mov'] : target.value === 'word-pdf' ? ['doc','docx'] : ['pdf'])

async function action(method: string, payload: Record<string,unknown> = {}) {
  if (pending.value) return
  pending.value = true; epoch++; error.value = ''
  try { state.value = await invokeModule(method, payload) }
  catch (e) { error.value = messageOf(e) }
  finally { pending.value = false }
}
async function poll() {
  if (pending.value || polling || disposed) return
  const current = epoch; polling = true
  try { const snapshot = await getState(); if (!disposed && current === epoch) state.value = snapshot }
  catch (e) { if (!disposed && current === epoch) error.value = messageOf(e) }
  finally { polling = false }
}
async function changeTab(tab: Category) {
  if (disabled.value || category.value === tab) return
  await action('clearFiles')
  if (error.value) return
  category.value = tab; target.value = tab === 'image' ? 'png' : tab === 'audio' ? 'mp3' : 'word-pdf'
}
async function changeDirection(value: Target) {
  if (disabled.value || target.value === value) return
  await action('clearFiles'); if (!error.value) target.value = value
}
async function addPaths(paths: string[]) { if (!disabled.value && paths.length) await action('addFiles', {paths, target:target.value}) }
async function chooseFiles() {
  if (disabled.value) return
  pending.value = true; error.value = ''
  try {
    const paths = await selectFiles({title:'添加待转换文件',multiple:true,filters:[{name:'支持的文件',extensions:extensions.value}]})
    pending.value = false
    if (paths) await addPaths(paths)
  } catch (e) { error.value = messageOf(e) }
  finally { pending.value = false }
}
async function chooseDirectory() {
  if (disabled.value) return
  pending.value = true
  try { const path = await selectDirectory({title:'选择输出目录'}); if (path) outputDirectory.value = path }
  catch (e) { error.value = messageOf(e) }
  finally { pending.value = false }
}
async function start() {
  await action('startConversion', { target:target.value, outputDirectory:outputDirectory.value, options:{quality:quality.value,maxWidth:maxWidth.value,background:background.value,audioBitrate:audioBitrate.value} })
}
function editEngines() { draft.value = {...state.value.settings}; settingsOpen.value = true }
async function chooseEngine(key: 'ffmpegPath' | 'libreOfficePath') {
  pending.value = true
  try { const paths = await selectFiles({title:key === 'ffmpegPath' ? '选择 ffmpeg.exe' : '选择 soffice.exe',multiple:false,filters:[{name:'转换引擎',extensions:['exe','com']}]}); if (paths?.[0]) draft.value[key] = paths[0] }
  catch (e) { error.value = messageOf(e) }
  finally { pending.value = false }
}
async function saveEngines() { await action('updateSettings', {...draft.value}); if (!error.value) settingsOpen.value = false }
async function openResult(id: string) {
  try { await invokeModule('openResult', {id}) } catch (e) { error.value = messageOf(e) }
}
onMounted(async () => {
  await poll(); outputDirectory.value = state.value.settings.outputDirectory
  if (disposed) return
  timer = setInterval(() => { void poll() }, 650)
  unlisten = await getCurrentWebview().onDragDropEvent(event => {
    if (settingsOpen.value || disabled.value) return
    if (event.payload.type === 'enter' || event.payload.type === 'over') dragOver.value = true
    else { dragOver.value = false; if (event.payload.type === 'drop') void addPaths(event.payload.paths) }
  }).catch(() => undefined)
  if (disposed) unlisten?.()
})
onUnmounted(() => { disposed = true; if (timer) clearInterval(timer); unlisten?.() })
</script>

<template>
  <main class="convert-workspace">
    <header class="workspace-header"><div class="brand"><span class="brand-mark"><ConvertIcon name="swap" :size="27" /></span><h1>格式转换</h1></div><div class="header-actions"><QButton :disabled="disabled" @click="editEngines"><QIcon name="settings"/>引擎设置</QButton><QIconButton label="刷新" title="刷新" :disabled="pending" @click="poll"><QIcon name="refresh"/></QIconButton></div></header>
    <nav class="category-tabs" role="tablist" aria-label="转换类型"><span class="tab-thumb" :style="{transform:`translateX(${tabIndex * 100}%)`}"/><button v-for="tab in tabs" :key="tab.id" role="tab" :aria-selected="category === tab.id" :disabled="disabled" :class="{selected:category === tab.id}" @click="changeTab(tab.id)"><ConvertIcon :name="tab.icon" :size="18"/>{{ tab.title }}</button></nav>
    <Transition name="notice"><div v-if="error" class="error-banner" role="alert"><QIcon name="statusDanger"/><span>{{ error }}</span><QIconButton label="关闭错误" @click="error = ''"><QIcon name="close"/></QIconButton></div></Transition>
    <div v-if="state.message" class="queue-message" role="status">{{ state.message }}</div>
    <div class="work-grid">
      <section class="queue-panel" :class="{'drag-over':dragOver}">
        <header class="panel-heading"><div><h2>转换队列 <span>{{ state.files.length }}</span></h2></div><div class="queue-actions"><QButton :disabled="disabled" @click="chooseFiles"><ConvertIcon name="plus" :size="17"/>添加文件</QButton><QIconButton label="清空队列" title="清空队列" :disabled="disabled || !state.files.length" @click="action('clearFiles')"><QIcon name="remove"/></QIconButton></div></header>
        <button v-if="!state.files.length" class="drop-zone" :disabled="disabled" @click="chooseFiles"><span class="drop-symbol"><ConvertIcon name="plus" :size="29"/></span><strong>拖入文件，或点击添加</strong><span>{{ extensions.join(' · ').toUpperCase() }}</span></button>
        <TransitionGroup v-else tag="div" name="file" class="file-list">
          <article v-for="file in state.files" :key="file.id" class="file-row" :class="file.status">
            <span class="file-mark"><ConvertIcon :name="category === 'documents' ? 'document' : category" :size="23"/></span>
            <div class="file-info"><strong :title="file.name">{{ file.name }}</strong><div class="file-meta"><span>{{ formatSize(file.size) }}</span><span class="status-label" role="status">{{ statuses[file.status] }}<template v-if="file.status === 'running' && file.progress !== null"> {{ Math.floor(file.progress * 100) }}%</template></span></div><div v-if="file.status === 'running'" class="file-progress" :class="{indeterminate:file.progress === null}" role="progressbar" :aria-label="`${file.name} 转换进度`" :aria-valuenow="file.progress === null ? undefined : Math.round(file.progress * 100)" aria-valuemin="0" aria-valuemax="100"><span :style="{width:`${(file.progress ?? .35) * 100}%`}"/></div><p v-if="file.message" class="file-note">{{ file.message }}</p></div>
            <QIconButton v-if="file.outputPath" label="打开结果目录" title="打开结果目录" @click="openResult(file.id)"><QIcon name="folder"/></QIconButton><QIconButton v-else label="移除文件" title="移除文件" :disabled="disabled" @click="action('removeFile',{id:file.id})"><QIcon name="close"/></QIconButton>
          </article>
        </TransitionGroup>
        <button v-if="state.files.length" class="add-more" :disabled="disabled" @click="chooseFiles"><ConvertIcon name="plus" :size="15"/>继续添加，或拖入文件</button>
        <footer class="queue-footer"><span v-if="summary.finished || state.busy" role="status">已完成 {{ summary.finished }} / {{ state.files.length }}<small v-if="!state.busy"> · 成功 {{ summary.success }}<template v-if="summary.failed"> · 失败 {{ summary.failed }}</template><template v-if="summary.cancelled"> · 取消 {{ summary.cancelled }}</template></small></span><span v-else>原文件保留 · 重名自动编号</span><QButton v-if="state.busy" :disabled="pending || state.cancelling" @click="action('cancel')">{{ state.cancelling ? '正在取消…' : '取消转换' }}</QButton></footer>
      </section>
      <section class="options-panel">
        <h2>转换设置</h2>
        <Transition name="options" mode="out-in"><div :key="category" class="option-fields">
          <template v-if="category !== 'documents'"><label class="field"><span>目标格式</span><select v-model="target" :disabled="disabled"><option v-for="format in targetFormats" :key="format" :value="format">{{ format.toUpperCase() }}</option></select></label>
            <template v-if="category === 'image'"><label class="field"><span>最大宽度 <small>0 为保持原尺寸</small></span><div class="input-unit"><input v-model.number="maxWidth" type="number" min="0" max="16384" step="1" :disabled="disabled"/><span>px</span></div></label><label v-if="target === 'jpg'" class="field"><span>JPEG 质量 <b>{{ quality }}</b></span><input v-model.number="quality" type="range" min="1" max="100" :disabled="disabled"/></label><label v-if="target === 'jpg' || target === 'bmp'" class="field"><span>透明区域背景色</span><div class="color-picker"><input v-model="background" type="color" :disabled="disabled"/><span>{{ background.toUpperCase() }}</span></div></label><span v-if="target === 'webp'" class="format-note">WebP 使用无损编码</span></template>
            <label v-else-if="target === 'mp3' || target === 'm4a'" class="field"><span>音频码率</span><select v-model.number="audioBitrate" :disabled="disabled"><option v-for="rate in [96,128,192,256,320]" :key="rate" :value="rate">{{ rate }} kbps</option></select></label>
          </template>
          <template v-else><div class="direction"><button :class="{selected:target === 'word-pdf'}" :disabled="disabled" @click="changeDirection('word-pdf')">Word <ConvertIcon name="arrow" :size="15"/> PDF</button><button :class="{selected:target === 'pdf-word'}" :disabled="disabled" @click="changeDirection('pdf-word')">PDF <ConvertIcon name="arrow" :size="15"/> Word</button></div><div v-if="target === 'pdf-word'" class="conversion-boundary"><QIcon name="statusInfo"/><div><strong>文字提取 · DOCX</strong><p>保留可编辑文字和分页，暂不还原图片、表格版式或识别扫描件。</p></div></div><span v-else-if="!engineMissing" class="engine-ready"><span/>{{ state.engines.libreOfficePath && state.settings.wordEngine !== 'word' ? 'LibreOffice' : 'Microsoft Word' }} 已就绪</span></template>
        </div></Transition>
        <div class="output-field"><label class="field"><span>输出目录</span><div class="folder-picker"><input :value="outputDirectory" readonly placeholder="源文件目录 / Converted" :disabled="disabled"/><QIconButton label="选择输出目录" :disabled="disabled" @click="chooseDirectory"><QIcon name="folder"/></QIconButton></div></label><button v-if="outputDirectory" class="text-button" :disabled="disabled" @click="outputDirectory = ''">使用默认目录</button></div>
        <div v-if="engineMissing" class="engine-missing"><QIcon name="statusWarning"/><span>{{ category === 'audio' ? '尚未设置 FFmpeg' : '需要 Word 或 LibreOffice' }}</span><button :disabled="disabled" @click="editEngines">设置</button></div>
        <QButton class="start-button" variant="primary" :disabled="disabled || engineMissing || !state.files.length" @click="start"><ConvertIcon name="swap" :size="20"/>{{ state.busy ? '正在转换…' : '开始转换' }}</QButton>
      </section>
    </div>
    <QModal :open="settingsOpen" title="转换引擎" :busy="pending" close-label="关闭引擎设置" @close="settingsOpen = false">
      <div class="engine-status"><span class="status-dot" :class="{ready:state.engines.wordAvailable}"/>Microsoft Word<span>{{ state.engines.wordAvailable ? '已检测到' : '未安装' }}</span></div>
      <label class="field"><span>Word → PDF 引擎</span><select v-model="draft.wordEngine" :disabled="pending"><option value="auto">自动选择</option><option value="word">Microsoft Word</option><option value="libreoffice">LibreOffice</option></select></label>
      <label class="field"><span>LibreOffice</span><div class="engine-path"><input :value="draft.libreOfficePath" readonly placeholder="自动检测，或选择 soffice.exe"/><QIconButton label="选择 LibreOffice" :disabled="pending" @click="chooseEngine('libreOfficePath')"><QIcon name="folder"/></QIconButton><QIconButton label="清除 LibreOffice 自定义路径" :disabled="pending || !draft.libreOfficePath" @click="draft.libreOfficePath = ''"><QIcon name="close"/></QIconButton></div></label>
      <label class="field"><span>FFmpeg</span><div class="engine-path"><input :value="draft.ffmpegPath" readonly placeholder="自动检测，或选择 ffmpeg.exe"/><QIconButton label="选择 FFmpeg" :disabled="pending" @click="chooseEngine('ffmpegPath')"><QIcon name="folder"/></QIconButton><QIconButton label="清除 FFmpeg 自定义路径" :disabled="pending || !draft.ffmpegPath" @click="draft.ffmpegPath = ''"><QIcon name="close"/></QIconButton></div></label>
      <template #actions><QButton :disabled="pending" @click="settingsOpen = false">取消</QButton><QButton variant="primary" :loading="pending" @click="saveEngines">确定</QButton></template>
    </QModal>
  </main>
</template>
