<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { QButton, QIcon, QIconButton, QModal, QModalInput, selectSavePath, showDialog, showNotification } from '@qingtoolbox/module-ui'
import { call } from './bridge'
import type { Category, Draft, EditEntry, Entry, State } from './types'
import VaultIcon from './VaultIcon.vue'
import { RevealCache } from './RevealCache'

const state = ref<State | null>(null)
const busy = ref(false)
const error = ref('')
const status = ref('')
const selectedId = ref('')
const search = ref('')
const editing = ref(false)
const editError = ref('')
const draft = reactive<Draft>({ categoryId: '', name: '', link: '', remark: '', credentials: [] })
const revealRevision = ref(0)
const reveals = new RevealCache(() => { revealRevision.value++ })
const editVisible = ref(new Set<number>())
const pendingReveal = ref(new Set<string>())
let privacyEpoch = 0
let disposed = false
let statusTimer: ReturnType<typeof setTimeout> | undefined
const categories = computed(() => state.value?.categories ?? [])
const selected = computed(() => categories.value.find(c => c.id === selectedId.value))
const totalAccounts = computed(() => categories.value.reduce((n, c) => n + accountCount(c), 0))
const locked = computed(() => busy.value || !state.value || !!state.value.loadError)
const entries = computed(() => {
  const query = search.value.trim().toLocaleLowerCase()
  return (selected.value?.entries ?? []).filter(e => !query || [e.name, e.link, e.remark, ...e.credentials.flatMap(a => [a.account, a.note])].some(v => v.toLocaleLowerCase().includes(query)))
})
const accountCount = (c: Category) => c.entries.reduce((n, e) => n + e.credentials.length, 0)
function message(cause: unknown) { return typeof cause === 'object' && cause !== null && 'message' in cause && typeof cause.message === 'string' ? cause.message : '操作未完成，请重试。' }
function apply(next: State) {
  state.value = next
  if (!next.categories.some(c => c.id === selectedId.value)) selectedId.value = next.categories[0]?.id ?? ''
}
function hideSecrets() { privacyEpoch++; reveals.clear(); editVisible.value = new Set() }
function choose(id: string) { hideSecrets(); selectedId.value = id; search.value = '' }
function shown(id: string) { void revealRevision.value; return reveals.get(id) }
function announce(value: string) { status.value = value; clearTimeout(statusTimer); statusTimer = setTimeout(() => { status.value = '' }, 3500) }
async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true; error.value = ''
  try { await action() } catch (cause) { error.value = message(cause) } finally { busy.value = false }
}
async function reload() { await run(async () => { hideSecrets(); apply(await call<State>('getState')) }) }
async function categoryDialog(category?: Category) {
  if (locked.value) return
  await run(async () => {
    const answer = await showDialog({ title: category ? '修改分类' : '新建分类', inputs: [{ id: 'name', label: '分类名称', value: category?.name ?? '', placeholder: '例如：游戏、网站、工作', maxLength: 64 }], buttons: [{ id: 'cancel', label: '取消' }, { id: 'save', label: '确定', variant: 'primary' }] })
    if (answer?.buttonId !== 'save') return
    apply(await call<State>('saveCategory', { id: category?.id, name: answer.values.name }))
    const added = state.value?.categories.find(c => c.name === answer.values.name.trim())
    if (added) choose(added.id)
    announce('分类已保存')
  })
}
async function removeCategory() {
  const category = selected.value
  if (!category || locked.value) return
  await run(async () => {
    const answer = await showDialog({ title: '删除分类', labels: [`将删除“${category.name}”及其中 ${category.entries.length} 个条目、${accountCount(category)} 组账号。此操作不可撤销。`], buttons: [{ id: 'cancel', label: '取消' }, { id: 'delete', label: '删除', variant: 'danger' }] })
    if (answer?.buttonId !== 'delete') return
    hideSecrets(); apply(await call<State>('deleteCategory', { id: category.id })); announce('分类已删除')
  })
}
async function editEntry(entry?: Entry) {
  if (!selected.value || locked.value) return
  hideSecrets(); editError.value = ''
  await run(async () => {
    const value: EditEntry = entry ? await call<EditEntry>('getEntryForEdit', { id: entry.id }) : { name: '', link: '', remark: '', credentials: [{ account: '', password: '', note: '' }] }
    Object.assign(draft, { ...value, id: entry?.id, categoryId: selectedId.value, credentials: value.credentials.map(a => ({ ...a })) })
    editing.value = true
  })
}
function closeEditor() {
  if (busy.value) return
  editing.value = false; editVisible.value = new Set(); editError.value = ''
  for (const a of draft.credentials) { a.password = ''; a.account = ''; a.note = '' }
  Object.assign(draft, { id: undefined, categoryId: '', name: '', link: '', remark: '', credentials: [] })
}
function toggleEdit(index: number) { const next = new Set(editVisible.value); next.has(index) ? next.delete(index) : next.add(index); editVisible.value = next }
function removeDraftAccount(index: number) { draft.credentials[index].password = ''; draft.credentials.splice(index, 1); editVisible.value = new Set() }
async function saveEntry() {
  if (busy.value) return
  if (!draft.name.trim() || !draft.categoryId || !draft.credentials.length || draft.credentials.some(a => !a.account.trim() || !a.password)) { editError.value = '请填写游戏 / 网站名称，以及每组的账号和密码。'; return }
  busy.value = true; editError.value = ''; error.value = ''
  try {
    const next = await call<State>('saveEntry', { ...draft, credentials: draft.credentials.map(a => ({ ...a })) })
    selectedId.value = draft.categoryId; apply(next); busy.value = false; closeEditor(); announce('条目已加密保存')
  } catch (cause) { editError.value = message(cause) } finally { busy.value = false }
}
async function removeEntry(entry: Entry) {
  if (locked.value) return
  await run(async () => {
    const answer = await showDialog({ title: '删除条目', labels: [`删除“${entry.name}”及其中 ${entry.credentials.length} 组账号密码？此操作不可撤销。`], buttons: [{ id: 'cancel', label: '取消' }, { id: 'delete', label: '删除', variant: 'danger' }] })
    if (answer?.buttonId !== 'delete') return
    hideSecrets(); apply(await call<State>('deleteEntry', { id: entry.id })); announce('条目已删除')
  })
}
async function reveal(entryId: string, credentialId: string) {
  if (shown(credentialId) !== undefined) { reveals.hide(credentialId); return }
  if (pendingReveal.value.has(credentialId) || locked.value) return
  const epoch = privacyEpoch
  pendingReveal.value = new Set([...pendingReveal.value, credentialId])
  try {
    const result = await call<{ password: string }>('getPassword', { entryId, credentialId })
    // A response arriving after blur/navigation must not re-expose a password.
    if (!disposed && epoch === privacyEpoch && document.visibilityState !== 'hidden') reveals.set(credentialId, result.password)
    result.password = ''
  } catch (cause) { error.value = message(cause) }
  finally { const next = new Set(pendingReveal.value); next.delete(credentialId); pendingReveal.value = next }
}
async function copy(entryId: string, credentialId: string, field: 'account' | 'password') {
  if (locked.value) return
  await run(async () => { await call('copyCredential', { entryId, credentialId, field }); announce(field === 'account' ? '账号已复制' : '密码已复制') })
}
async function copyLink(entry: Entry) { await run(async () => { await call('copyLink', { id: entry.id }); announce('链接已复制') }) }
async function exportTxt(all: boolean) {
  if (locked.value || !totalAccounts.value) return
  const categoryId = all ? null : selectedId.value
  await run(async () => {
    hideSecrets()
    const answer = await showDialog({ title: '导出明文 TXT', labels: ['导出的文件包含完整账号和真实密码，任何能打开文件的人都能读取。', all ? '范围：全部分类。请不要上传公共网盘或发送给他人。' : `范围：${selected.value?.name ?? '当前分类'}。请妥善保管。`, '本机加密数据不会被删除或替换。'], buttons: [{ id: 'cancel', label: '取消' }, { id: 'export', label: '我了解，继续导出', variant: 'primary' }] })
    if (answer?.buttonId !== 'export') return
    const path = await selectSavePath({ title: '保存密码记事本 TXT', defaultPath: '密码记事本.txt', filters: [{ name: '文本文件', extensions: ['txt'] }] })
    if (!path) return
    await call('exportTxt', { path, categoryId, includePasswords: true })
    announce('TXT 已导出，请妥善保管')
    await showNotification({ title: 'TXT 导出完成', message: '导出的文件包含明文密码，请妥善保管。' }).catch(() => undefined)
  })
}
function visibility() { if (document.hidden) hideSecrets() }
onMounted(() => { window.addEventListener('blur', hideSecrets); document.addEventListener('visibilitychange', visibility); void reload() })
onBeforeUnmount(() => { disposed = true; hideSecrets(); clearTimeout(statusTimer); window.removeEventListener('blur', hideSecrets); document.removeEventListener('visibilitychange', visibility); for (const a of draft.credentials) a.password = '' })
</script>

<template>
  <div class="notebook">
    <header class="notebook-header">
      <div class="brand"><span class="brand-mark"><VaultIcon name="book" :size="27" /></span><div><p class="eyebrow">PRIVATE NOTES</p><h1>密码记事本</h1><p class="subtitle">把账号整理好，把密码留在本机。</p></div></div>
      <div class="header-actions"><span class="privacy-chip"><VaultIcon name="lock" :size="14" />本机加密</span><QButton type="button" :disabled="locked || !totalAccounts" @click="exportTxt(true)"><QIcon name="download" :size="16" />导出全部 TXT</QButton><QIconButton type="button" label="刷新记事本" :disabled="busy" @click="reload"><QIcon name="refresh" :size="17" /></QIconButton></div>
    </header>
    <p v-if="state?.loadError" class="error-banner" role="alert">{{ state.loadError.message }} 原文件保留，请勿删除数据文件。</p>
    <p v-if="error" class="error-banner" role="alert">{{ error }}<button aria-label="关闭错误提示" @click="error = ''">×</button></p>
    <div class="workspace">
      <aside class="categories">
        <div class="sidebar-heading"><span>我的分类 <small>{{ categories.length }}</small></span><QIconButton type="button" label="新建分类" :disabled="locked" @click="categoryDialog()"><VaultIcon name="plus" :size="18" /></QIconButton></div>
        <nav aria-label="账号分类"><button v-for="c in categories" :key="c.id" class="category" :class="{ selected: c.id === selectedId }" :aria-current="c.id === selectedId ? 'page' : undefined" :disabled="busy" @click="choose(c.id)"><QIcon name="folder" :size="18" /><span>{{ c.name }}</span><small>{{ accountCount(c) }}</small></button></nav>
        <p v-if="!categories.length" class="category-hint">按用途建立分类，<br />例如游戏、网站或工作。</p>
        <QButton type="button" class="new-category" variant="ghost" :disabled="locked" @click="categoryDialog()"><VaultIcon name="plus" :size="16" />新建分类</QButton>
        <div class="sidebar-foot"><span><VaultIcon name="lock" :size="16" />{{ totalAccounts }} 组账号 · 仅在本机</span><p>TXT 是明文文件，<br />导出后请妥善保管。</p></div>
      </aside>
      <main class="contents">
        <template v-if="selected">
          <div class="content-header"><div><p class="eyebrow">{{ selected.entries.length }} 个条目 · {{ accountCount(selected) }} 组账号</p><div class="category-title"><h2>{{ selected.name }}</h2><QIconButton type="button" label="修改分类" :disabled="locked" @click="categoryDialog(selected)"><QIcon name="edit" :size="16" /></QIconButton><QIconButton type="button" label="删除分类" :disabled="locked" @click="removeCategory"><QIcon name="remove" :size="16" /></QIconButton></div></div><div class="content-actions"><QButton type="button" :disabled="locked || !accountCount(selected)" @click="exportTxt(false)">导出分类</QButton><QButton type="button" variant="primary" :disabled="locked" @click="editEntry()"><VaultIcon name="plus" :size="17" />添加条目</QButton></div></div>
          <label class="search"><QIcon name="search" :size="18" /><input v-model="search" aria-label="搜索条目、账号或备注" placeholder="搜索游戏、网站、账号或备注…" /><button v-if="search" aria-label="清空搜索" @click="search = ''"><QIcon name="close" :size="14" /></button></label>
          <TransitionGroup name="record" tag="div" class="entry-list">
            <article v-for="entry in entries" :key="entry.id" class="entry-card">
              <header class="entry-header"><span class="entry-mark">{{ Array.from(entry.name)[0] }}</span><div class="entry-heading"><h3>{{ entry.name }}</h3><button v-if="entry.link" class="entry-link" :title="`${entry.link} · 点击复制链接`" :disabled="locked" @click="copyLink(entry)"><VaultIcon name="link" :size="13" /><span>{{ entry.link }}</span></button><p v-if="entry.remark" class="entry-remark">{{ entry.remark }}</p></div><div class="entry-tools"><QIconButton type="button" :label="`编辑 ${entry.name}`" :disabled="locked" @click="editEntry(entry)"><QIcon name="edit" :size="16" /></QIconButton><QIconButton type="button" :label="`删除 ${entry.name}`" :disabled="locked" @click="removeEntry(entry)"><QIcon name="remove" :size="16" /></QIconButton></div></header>
              <div class="account-labels" aria-hidden="true"><span>账号 / 备注</span><span>密码</span></div>
              <div v-for="a in entry.credentials" :key="a.id" class="account-row">
                <div class="account-cell"><div class="credential-text"><strong>{{ a.account }}</strong><small v-if="a.note">{{ a.note }}</small></div><QIconButton type="button" :label="`复制账号 ${a.account}`" :disabled="locked" @click="copy(entry.id, a.id, 'account')"><VaultIcon name="copy" :size="16" /></QIconButton></div>
                <div class="password-cell"><code :class="{ revealed: shown(a.id) !== undefined }" :aria-label="shown(a.id) !== undefined ? '已显示密码' : '密码已隐藏'">{{ shown(a.id) ?? '***' }}</code><QIconButton type="button" :label="shown(a.id) !== undefined ? '隐藏密码' : '显示密码'" :disabled="locked" :loading="pendingReveal.has(a.id)" @click="reveal(entry.id, a.id)"><VaultIcon :name="shown(a.id) !== undefined ? 'eyeOff' : 'eye'" :size="18" /></QIconButton><QIconButton type="button" label="复制密码" :disabled="locked" @click="copy(entry.id, a.id, 'password')"><VaultIcon name="copy" :size="16" /></QIconButton></div>
              </div>
            </article>
          </TransitionGroup>
          <div v-if="!entries.length" class="empty-state"><span class="empty-mark"><VaultIcon :name="search ? 'key' : 'book'" :size="30" /></span><h3>{{ search ? '没有找到匹配的条目' : '这个分类还没有账号' }}</h3><p>{{ search ? '换个关键词试试；搜索不会匹配密码。' : '添加游戏或网站名称，再为它保存一组或多组账号密码。' }}</p><QButton type="button" v-if="!search" variant="primary" :disabled="locked" @click="editEntry()"><VaultIcon name="plus" :size="17" />添加第一个条目</QButton></div>
          <p class="security-footnote"><VaultIcon name="eyeOff" :size="14" />密码显示 30 秒后自动隐藏，离开窗口也会隐藏。剪贴板可能被其他软件读取。</p>
        </template>
        <div v-else class="empty-state first-use"><span class="empty-mark"><VaultIcon name="book" :size="35" /></span><p class="eyebrow">A PLACE FOR YOUR ACCOUNTS</p><h2>从一个分类开始</h2><p>游戏、网站或工作，各有一页。<br />同一条目可以保存多个账号，并分别复制账号或密码。</p><QButton type="button" variant="primary" :disabled="locked" @click="categoryDialog()"><VaultIcon name="plus" :size="17" />创建第一个分类</QButton><span class="first-use-note"><VaultIcon name="lock" :size="14" />使用当前 Windows 用户的 DPAPI 加密保存</span></div>
      </main>
    </div>
    <Transition name="notice"><div v-if="status" class="notice" role="status"><VaultIcon name="check" :size="17" />{{ status }}</div></Transition>
    <QModal :open="editing" :title="draft.id ? '编辑条目' : '添加条目'" :busy="busy" close-label="关闭条目编辑" panel-class="entry-editor" @close="closeEditor">
      <form class="edit-form" autocomplete="off" @submit.prevent="saveEntry" @keydown.ctrl.enter.prevent="saveEntry">
        <p class="form-hint">一个游戏或网站，多个账号。保存后密码会回到隐藏状态。</p>
        <div class="edit-grid"><QModalInput v-model="draft.name" label="游戏 / 网站名称" placeholder="例如：Steam、某个游戏或网站" :max-length="128" :disabled="busy" /><label class="local-field"><span>分类</span><select v-model="draft.categoryId" :disabled="busy"><option v-for="c in categories" :key="c.id" :value="c.id">{{ c.name }}</option></select></label></div>
        <QModalInput v-model="draft.link" label="对应链接（可选）" placeholder="https://example.com" :max-length="1024" :disabled="busy" />
        <label class="local-field"><span>条目备注（可选）</span><textarea v-model="draft.remark" rows="2" maxlength="2048" placeholder="例如：服务器、区服、角色或用途" :disabled="busy" /></label>
        <div class="accounts-heading"><h3>账号与密码 <small>{{ draft.credentials.length }}</small></h3><QButton type="button" variant="ghost" :disabled="busy || draft.credentials.length >= 64" @click="draft.credentials.push({ account: '', password: '', note: '' })"><VaultIcon name="plus" :size="15" />再加一组</QButton></div>
        <TransitionGroup name="record" tag="div" class="draft-accounts"><section v-for="(a, index) in draft.credentials" :key="a.id ?? `new-${index}`" class="draft-account"><div class="draft-account-heading"><span>账号 {{ index + 1 }}</span><QIconButton type="button" label="移除此组账号" :disabled="busy || draft.credentials.length <= 1" @click="removeDraftAccount(index)"><QIcon name="remove" :size="15" /></QIconButton></div><div class="edit-grid"><QModalInput v-model="a.account" label="账号" placeholder="用户名、邮箱或账号 ID" :max-length="256" :disabled="busy" /><div class="password-editor"><QModalInput v-model="a.password" label="密码" :type="editVisible.has(index) ? 'text' : 'password'" placeholder="输入密码" :max-length="4096" :disabled="busy" /><QIconButton type="button" :label="editVisible.has(index) ? '隐藏输入的密码' : '显示输入的密码'" :disabled="busy" @click="toggleEdit(index)"><VaultIcon :name="editVisible.has(index) ? 'eyeOff' : 'eye'" :size="17" /></QIconButton></div></div><QModalInput v-model="a.note" label="账号备注（可选）" placeholder="例如：主号、小号、角色名" :max-length="256" :disabled="busy" /></section></TransitionGroup>
        <p v-if="editError" class="edit-error" role="alert">{{ editError }}</p>
      </form>
      <template #actions><span class="save-hint"><VaultIcon name="lock" :size="13" />本机加密保存</span><QButton type="button" :disabled="busy" @click="closeEditor">取消</QButton><QButton type="button" variant="primary" :loading="busy" @click="saveEntry">保存条目</QButton></template>
    </QModal>
  </div>
</template>
