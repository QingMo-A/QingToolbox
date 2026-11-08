<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { QButton, QIcon, QIconButton, QModal, QModalInput, selectFiles } from '@qingtoolbox/module-ui'
import { call } from './bridge'

type Row = { extension: string; label: string; executable: string | null; currentApplication: string | null; currentExecutable: string | null; status: string; error: string | null }
type Table = { rows: Row[]; checkOnStartup: boolean; prepareOnStartup: boolean; busy: boolean; revision: number; checkedAt: number | null; message: string; error: string | null; pendingConfirmation: string[] }
const table = ref<Table | null>(null)
const selected = ref(new Set<string>())
const query = ref('')
const localBusy = ref(false)
const error = ref('')
const adding = ref(false)
const suffixes = ref('')
const typeLabel = ref('')
const addError = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
const locked = computed(() => localBusy.value || !table.value || table.value.busy)
const rows = computed(() => table.value?.rows ?? [])
const visible = computed(() => rows.value.filter(row => [row.extension, row.label, row.executable, row.currentApplication].some(v => v?.toLocaleLowerCase().includes(query.value.trim().toLocaleLowerCase()))))
const selectedRows = computed(() => rows.value.filter(row => selected.value.has(row.extension)))
const allVisible = computed(() => visible.value.length > 0 && visible.value.every(row => selected.value.has(row.extension)))
const someVisible = computed(() => !allVisible.value && visible.value.some(row => selected.value.has(row.extension)))
const changes = computed(() => rows.value.filter(r => ['changed', 'unassigned', 'needsConfirmation'].includes(r.status)).length)
const names: Record<string, string> = { unconfigured: '未设置目标', unchecked: '待检查', matched: '符合设置', changed: '已被更改', unassigned: '尚无默认值', missingTarget: '目标程序已失效', unknown: '无法确认', needsConfirmation: '等待 Windows 确认' }
function message(cause: unknown) { return typeof cause === 'string' ? cause : cause && typeof cause === 'object' && 'message' in cause ? String(cause.message) : '关联表操作未完成，请重试。' }
function app(path: string | null) { return path?.split(/[\\/]/).pop() || '尚未选择' }
function toggle(ext: string) { const next = new Set(selected.value); next.has(ext) ? next.delete(ext) : next.add(ext); selected.value = next }
function toggleAll() { const next = new Set(selected.value); for (const row of visible.value) allVisible.value ? next.delete(row.extension) : next.add(row.extension); selected.value = next }
function apply(next: Table) {
  table.value = next
  selected.value = new Set([...selected.value].filter(id => next.rows.some(row => row.extension === id)))
}
async function read(): Promise<Table> { const next = await call<Table>('getAssociationTable'); if (!disposed) apply(next); return next }
function schedule() { clearTimeout(timer); if (!disposed) timer = setTimeout(() => { void load() }, 400) }
async function load() {
  try { const next = await read(); if (next.busy) schedule() } catch (cause) { error.value = message(cause) }
}
async function job(method: string, payload: object = {}): Promise<Table> {
  apply(await call<Table>(method, payload))
  const until = Date.now() + 60_000
  while (table.value?.busy && !disposed) {
    if (Date.now() > until) throw new Error('关联检查耗时较长，请稍后重新检查。')
    await new Promise(r => setTimeout(r, 180)); await read()
  }
  if (table.value?.error) throw new Error(table.value.error)
  return table.value!
}
async function run(action: () => Promise<unknown>) {
  if (locked.value) return
  localBusy.value = true; error.value = ''; clearTimeout(timer)
  try { await action() } catch (cause) { error.value = message(cause) }
  finally { localBusy.value = false; if (table.value?.busy) schedule() }
}
async function check() { await run(() => job('checkAssociations', { extensions: [...selected.value] })) }
async function pick(ids: string[] = [...selected.value]): Promise<boolean> {
  const paths = await selectFiles({ title: `为 ${ids.length} 个后缀选择打开程序`, filters: [{ name: 'Windows 应用程序', extensions: ['exe'] }], multiple: false })
  if (!paths?.[0] || disposed) return false
  await job('setAssociationTarget', { extensions: ids, executable: paths[0] }); return true
}
async function choose(ids: string[]) { await run(() => pick(ids)) }
async function register() {
  await run(async () => {
    const ids = [...selected.value]
    const missing = selectedRows.value.filter(row => !row.executable).map(row => row.extension)
    if (missing.length && !await pick(missing)) return
    const result = await job('registerAssociationBatch', { extensions: ids })
    // Only this explicit button may open system Settings. Startup checks and
    // preparation never launch windows or generate prompt loops.
    if (result.pendingConfirmation.length > 0 && !disposed) await call('openSettings', { page: 'registered' })
  })
}
async function settings() { try { await call('openSettings', { page: table.value?.pendingConfirmation.length ? 'registered' : 'defaults' }) } catch (cause) { error.value = message(cause) } }
async function preferences(check: boolean, prepare: boolean) { await run(() => job('setAssociationPreferences', { checkOnStartup: check, prepareOnStartup: prepare })) }
async function add() {
  addError.value = ''
  const extensions = suffixes.value.split(/[\s,，;；]+/).filter(Boolean)
  if (!extensions.length) { addError.value = '请输入至少一个后缀。'; return }
  await run(async () => {
    try { await job('addAssociationRows', { extensions, label: typeLabel.value.trim() || '自定义' }); adding.value = false }
    catch (cause) { addError.value = message(cause); throw cause }
  })
}
onMounted(load)
onBeforeUnmount(() => { disposed = true; clearTimeout(timer) })
</script>

<template>
  <section class="panel association-panel">
    <header class="panel-header"><div class="section-icon"><svg viewBox="0 0 24 24"><path d="M14 3H6v18h12V7zm0 0v5h5M9 13h6m-6 4h6" /></svg></div><div><h2>默认应用</h2><p>保存自己的打开方式，检查变化，批量恢复。</p></div><span class="tag">当前用户 · {{ rows.length }}/128 项</span></header>
    <div class="association-options">
      <label><input type="checkbox" :checked="table?.checkOnStartup" :disabled="locked" @change="preferences(($event.target as HTMLInputElement).checked, table?.prepareOnStartup ?? false)" /><span>模块启动后检查默认应用</span></label>
      <label><input type="checkbox" :checked="table?.prepareOnStartup" :disabled="locked || !table?.checkOnStartup" @change="preferences(true, ($event.target as HTMLInputElement).checked)" /><span>发现变化时自动准备恢复</span></label>
      <p>自动准备会重新注册已保存的候选程序；Windows 仍需你确认，<strong>不会静默改回默认值</strong>，也不会启动时反复弹出系统设置。</p>
    </div>
    <div class="table-toolbar">
      <div class="table-search"><QIcon name="search" :size="15" /><input v-model="query" aria-label="搜索后缀或程序" placeholder="搜索后缀、类型或程序…" /></div>
      <div class="actions"><QButton :disabled="locked" @click="check"><QIcon name="refresh" :size="14" />{{ selected.size ? '检查选中项' : '检查全部' }}</QButton><QButton :disabled="locked" @click="run(() => job('addCommonAssociations'))">常用格式</QButton><QButton :disabled="locked" @click="suffixes = ''; typeLabel = ''; adding = true"><QIcon name="plus" :size="14" />添加后缀</QButton></div>
    </div>
    <div v-if="error || table?.error" class="table-notice table-error" role="alert">{{ error || table?.error }}</div>
    <div v-else-if="table?.message" class="table-notice" role="status">{{ table.message }}</div>
    <div class="association-table-wrap" :aria-busy="locked">
      <table class="association-table">
        <thead><tr><th class="check-cell"><input type="checkbox" aria-label="全选当前显示项" :checked="allVisible" :indeterminate="someVisible" :disabled="locked || !visible.length" @change="toggleAll" /></th><th>文件后缀</th><th>保存的目标应用</th><th>系统当前默认应用</th><th>检查状态</th><th class="row-action">操作</th></tr></thead>
        <tbody><tr v-for="row in visible" :key="row.extension" :class="{ selected: selected.has(row.extension) }"><td class="check-cell"><input type="checkbox" :aria-label="`选择 ${row.extension}`" :checked="selected.has(row.extension)" :disabled="locked" @change="toggle(row.extension)" /></td><td><strong class="extension-name">{{ row.extension }}</strong><small>{{ row.label || '自定义' }}</small></td><td><button class="target-app" :title="row.executable || '为此后缀选择目标应用'" :disabled="locked" @click="choose([row.extension])"><span>{{ app(row.executable) }}</span><QIcon name="edit" :size="13" /></button><small v-if="row.executable" class="file-path" :title="row.executable">{{ row.executable }}</small></td><td><span>{{ row.currentApplication || (row.status === 'unchecked' || !table?.checkedAt ? '尚未检查' : '未设置或无法识别') }}</span><small v-if="row.currentExecutable" class="file-path" :title="row.currentExecutable">{{ row.currentExecutable }}</small></td><td><span class="association-status" :class="`status-${row.status}`">{{ names[row.status] || '待检查' }}</span><small v-if="row.error" class="table-row-error">{{ row.error }}</small></td><td class="row-action"><QIconButton :label="`移除 ${row.extension}`" title="仅移出管理表，不改变现有默认应用" :disabled="locked" @click="run(() => job('removeAssociationRows', { extensions: [row.extension] }))"><QIcon name="remove" :size="15" /></QIconButton></td></tr></tbody>
      </table>
      <div v-if="!visible.length" class="table-empty">{{ !table || table.busy ? '正在载入关联表…' : rows.length ? '没有匹配的后缀。' : '表格还是空的，添加后缀或补充常用格式即可开始。' }}</div>
    </div>
    <div class="batch-bar"><div class="batch-summary"><strong>已选择 {{ selected.size }} 项</strong><span v-if="table?.busy">正在后台处理…</span><span v-else-if="changes">{{ changes }} 项需要确认</span><span v-else>保存目标后，系统关联仍以 Windows 确认为准</span></div><div class="actions"><QButton :disabled="locked || !selected.size" @click="choose([...selected])">选择应用…</QButton><QButton variant="primary" :disabled="locked || !selected.size" :loading="localBusy" @click="register">设置选中项 <span aria-hidden="true">↗</span></QButton></div></div>
    <div class="table-bottom"><span>移除条目只影响管理表，不会破坏已有打开方式。</span><button class="text-link" @click="settings">{{ table?.pendingConfirmation.length ? '前往 Windows 确认' : '系统默认应用' }} <span aria-hidden="true">↗</span></button></div>
    <QModal :open="adding" title="添加文件后缀" :busy="localBusy" close-label="取消添加" @close="adding = false">
      <QModalInput v-model="suffixes" label="文件后缀" placeholder="例如 .abc .xyz，多个用空格或逗号分隔" :max-length="1024" @keydown.enter="add" />
      <QModalInput v-model="typeLabel" label="类型说明（可选）" placeholder="例如 自定义文档" :max-length="48" />
      <p>最多管理 128 项，重复后缀自动跳过；不修改可执行文件、脚本和系统快捷方式的关联。</p><p v-if="addError" class="table-row-error" role="alert">{{ addError }}</p>
      <template #actions><QButton :disabled="localBusy" @click="adding = false">取消</QButton><QButton variant="primary" :loading="localBusy" :disabled="!suffixes.trim()" @click="add">添加</QButton></template>
    </QModal>
  </section>
</template>
