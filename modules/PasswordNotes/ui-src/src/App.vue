<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { QButton, QIcon, QIconButton, QModal, QModalInput, selectSavePath, showDialog, showNotification } from '@qingtoolbox/module-ui'
import { call } from './bridge'
import { MAX_SECURITY_FIELDS, defaultTotpSettings, securityFieldLabel, type Category, type Draft, type DraftAccount, type EditEntry, type Entry, type SecurityField, type State } from './types'
import TotpCode from './TotpCode.vue'
import VaultIcon from './VaultIcon.vue'
import { RevealCache } from './RevealCache'

const REVEAL_SECONDS = 30
const state = ref<State | null>(null)
const busy = ref(false)
const error = ref('')
const status = ref('')
const selectedId = ref('')
const search = ref('')
const searchInput = ref<HTMLInputElement | null>(null)
const editing = ref(false)
const editError = ref('')
const draft = reactive<Draft>({ categoryId: '', name: '', link: '', remark: '', credentials: [] })
const revealRevision = ref(0)
const reveals = new RevealCache(() => { revealRevision.value++ }, REVEAL_SECONDS * 1000)
const editVisible = ref(new Set<number>())
const pendingReveal = ref(new Set<string>())
const copied = ref(new Set<string>())
const expandedSecurity = ref(new Set<string>())
const securityKeys = new WeakMap<SecurityField, number>()
let nextSecurityKey = 0
function securityFieldKey(field: SecurityField) {
  if (!securityKeys.has(field)) securityKeys.set(field, ++nextSecurityKey)
  return securityKeys.get(field)!
}
const newAccount = (): DraftAccount => ({ account: '', password: '', note: '', securityFields: [], totpEnabled: false, totpSecret: '', totpSettings: defaultTotpSettings() })
const totpPrivacyEpoch = ref(0)
let privacyEpoch = 0
let disposed = false
let statusTimer: ReturnType<typeof setTimeout> | undefined
const copyTimers = new Map<string, ReturnType<typeof setTimeout>>()
const categories = computed(() => state.value?.categories ?? [])
const selected = computed(() => categories.value.find(c => c.id === selectedId.value))
const totalAccounts = computed(() => categories.value.reduce((n, c) => n + accountCount(c), 0))
const totalEntries = computed(() => categories.value.reduce((n, c) => n + c.entries.length, 0))
const locked = computed(() => busy.value || !state.value || !!state.value.loadError)
const query = computed(() => search.value.trim().toLocaleLowerCase())
const matches = (e: Entry) => [e.name, e.link, e.remark, ...e.credentials.flatMap(a => [a.account, a.note])].some(v => v.toLocaleLowerCase().includes(query.value))
/** Searching looks across every category; passwords are never part of the haystack. */
const visible = computed(() => query.value
  ? categories.value.flatMap(category => category.entries.filter(matches).map(entry => ({ entry, category })))
  : (selected.value?.entries ?? []).map(entry => ({ entry, category: selected.value! })))
const accountCount = (c: Category) => c.entries.reduce((n, e) => n + e.credentials.length, 0)
function message(cause: unknown) { return typeof cause === 'object' && cause !== null && 'message' in cause && typeof cause.message === 'string' ? cause.message : '操作未完成，请重试。' }
function hue(text: string) { let h = 0; for (const ch of text) h = (h * 31 + (ch.codePointAt(0) ?? 0)) % 360; return h }
function initial(text: string) { return Array.from(text.trim())[0]?.toUpperCase() ?? '·' }
function host(link: string) { try { return new URL(link).hostname.replace(/^www\./, '') } catch { return link } }
function apply(next: State) {
  state.value = next
  if (!next.categories.some(c => c.id === selectedId.value)) selectedId.value = next.categories[0]?.id ?? ''
}
function hideSecrets() { privacyEpoch++; totpPrivacyEpoch.value++; reveals.clear(); editVisible.value = new Set(); expandedSecurity.value = new Set() }
function toggleSecurity(id: string) { const next = new Set(expandedSecurity.value); next.has(id) ? next.delete(id) : next.add(id); expandedSecurity.value = next }
function choose(id: string) { hideSecrets(); selectedId.value = id; search.value = '' }
function shown(id: string) { void revealRevision.value; return reveals.get(id) }
function announce(value: string) { status.value = value; clearTimeout(statusTimer); statusTimer = setTimeout(() => { status.value = '' }, 3200) }
function flashCopied(key: string) {
  copied.value = new Set([...copied.value, key])
  clearTimeout(copyTimers.get(key))
  copyTimers.set(key, setTimeout(() => { const next = new Set(copied.value); next.delete(key); copied.value = next; copyTimers.delete(key) }, 1600))
}
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
async function editEntry(entry?: Entry, category = selected.value) {
  if (!category || locked.value) return
  hideSecrets(); editError.value = ''
  await run(async () => {
    const value: EditEntry = entry ? await call<EditEntry>('getEntryForEdit', { id: entry.id }) : { name: '', link: '', remark: '', credentials: [newAccount()] }
    Object.assign(draft, { ...value, id: entry?.id, categoryId: category.id, credentials: value.credentials.map(a => ({ ...a, securityFields: (a.securityFields ?? []).map(field => ({ ...field })), totpEnabled: !!a.totp, totpSecret: '', totpSettings: { ...(a.totp ?? defaultTotpSettings()) } })) })
    editing.value = true
  })
}
function closeEditor() {
  if (busy.value) return
  editing.value = false; editVisible.value = new Set(); editError.value = ''
  for (const a of draft.credentials) clearAccount(a)
  Object.assign(draft, { id: undefined, categoryId: '', name: '', link: '', remark: '', credentials: [] })
}
function toggleEdit(index: number) { const next = new Set(editVisible.value); next.has(index) ? next.delete(index) : next.add(index); editVisible.value = next }
function clearAccount(account: DraftAccount) {
  account.password = ''; account.account = ''; account.note = ''
  account.totpSecret = ''
  for (const field of account.securityFields) { field.label = ''; field.value = '' }
}
function removeDraftAccount(index: number) { clearAccount(draft.credentials[index]); draft.credentials.splice(index, 1); editVisible.value = new Set() }
function addSecurityField(account: DraftAccount) {
  if (busy.value || account.securityFields.length >= MAX_SECURITY_FIELDS) return
  account.securityFields.push({ kind: 'phone', label: '', value: '' })
}
function removeSecurityField(account: DraftAccount, index: number) {
  const field = account.securityFields[index]
  field.label = ''; field.value = ''; account.securityFields.splice(index, 1)
}
async function saveEntry() {
  if (busy.value) return
  if (!draft.name.trim() || !draft.categoryId || !draft.credentials.length || draft.credentials.some(a => !a.account.trim() || !a.password)) { editError.value = '请填写游戏 / 网站名称，以及每组的账号和密码。'; return }
  if (draft.credentials.some(a => a.securityFields.some(field => field.value.trim() && field.kind === 'custom' && !field.label.trim()))) { editError.value = '请为已填写的自定义安全信息设置名称。'; return }
  if (draft.credentials.some(a => a.totpEnabled && !a.totp && !a.totpSecret.trim())) { editError.value = '请为已开启身份验证器的账号填写密钥。'; return }
  busy.value = true; editError.value = ''; error.value = ''
  try {
    const next = await call<State>('saveEntry', { ...draft, credentials: draft.credentials.map(a => ({ id: a.id, account: a.account, password: a.password, note: a.note, securityFields: a.securityFields.filter(field => field.value.trim()).map(field => ({ ...field, label: field.kind === 'custom' ? field.label.trim() : '', value: field.value.trim() })), totp: { action: !a.totpEnabled ? 'remove' : a.totpSecret.trim() ? 'set' : 'keep', ...(a.totpEnabled && a.totpSecret.trim() ? { secret: a.totpSecret.trim() } : {}), settings: { ...a.totpSettings } } })) })
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
async function copy(entryId: string, credentialId: string, field: 'account' | 'password' | 'securityField', securityFieldIndex?: number) {
  if (locked.value) return
  await run(async () => {
    await call('copyCredential', { entryId, credentialId, field, ...(field === 'securityField' ? { securityFieldIndex } : {}) })
    flashCopied(`${credentialId}:${field}${field === 'securityField' ? `:${securityFieldIndex}` : ''}`)
    announce(field === 'account' ? '账号已复制' : field === 'password' ? '密码已复制' : '安全信息已复制')
  })
}
async function copyLink(entry: Entry) { await run(async () => { await call('copyLink', { id: entry.id }); flashCopied(`${entry.id}:link`); announce('链接已复制') }) }
async function exportTxt(all: boolean) {
  if (locked.value || !totalAccounts.value) return
  const categoryId = all ? null : selectedId.value
  await run(async () => {
    hideSecrets()
    const answer = await showDialog({ title: '导出明文 TXT', labels: ['导出的文件包含完整账号、真实密码和已填写的安全信息，任何能打开文件的人都能读取。', all ? '范围：全部分类。请不要上传公共网盘或发送给他人。' : `范围：${selected.value?.name ?? '当前分类'}。请妥善保管。`, '本机加密数据不会被删除或替换。'], buttons: [{ id: 'cancel', label: '取消' }, { id: 'export', label: '我了解，继续导出', variant: 'primary' }] })
    if (answer?.buttonId !== 'export') return
    const path = await selectSavePath({ title: '保存密码记事本 TXT', defaultPath: '密码记事本.txt', filters: [{ name: '文本文件', extensions: ['txt'] }] })
    if (!path) return
    await call('exportTxt', { path, categoryId, includePasswords: true })
    announce('TXT 已导出，请妥善保管')
    await showNotification({ title: 'TXT 导出完成', message: '导出的文件包含明文密码，请妥善保管。' }).catch(() => undefined)
  })
}
function visibility() { if (document.hidden) hideSecrets() }
function shortcuts(event: KeyboardEvent) {
  if (editing.value) return
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') { event.preventDefault(); void nextTick(() => searchInput.value?.select()) }
  else if (event.key === 'Escape' && search.value && document.activeElement === searchInput.value) search.value = ''
}
onMounted(() => { window.addEventListener('blur', hideSecrets); window.addEventListener('keydown', shortcuts); document.addEventListener('visibilitychange', visibility); void reload() })
onBeforeUnmount(() => {
  disposed = true; hideSecrets(); clearTimeout(statusTimer); for (const timer of copyTimers.values()) clearTimeout(timer)
  window.removeEventListener('blur', hideSecrets); window.removeEventListener('keydown', shortcuts); document.removeEventListener('visibilitychange', visibility)
  for (const a of draft.credentials) clearAccount(a)
})
</script>

<template>
  <div class="notebook">
    <header class="topbar">
      <div class="brand">
        <span class="brand-mark"><VaultIcon name="vault" :size="24" /></span>
        <div><h1>密码记事本</h1><p class="subtitle"><span class="secure-dot" />本机加密 · {{ totalAccounts }} 组账号</p></div>
      </div>
      <label class="search" :class="{ active: !!search }">
        <QIcon name="search" :size="17" />
        <input ref="searchInput" v-model="search" aria-label="搜索条目、账号或备注" placeholder="搜索全部分类的游戏、网站或账号…" />
        <button v-if="search" type="button" aria-label="清空搜索" @click="search = ''"><QIcon name="close" :size="13" /></button>
        <kbd v-else>Ctrl F</kbd>
      </label>
      <div class="header-actions">
        <QButton type="button" :disabled="locked || !totalAccounts" @click="exportTxt(true)"><QIcon name="download" :size="16" />导出全部 TXT</QButton>
        <QIconButton type="button" label="刷新记事本" :disabled="busy" @click="reload"><QIcon name="refresh" :size="17" /></QIconButton>
      </div>
    </header>

    <p v-if="state?.loadError" class="error-banner" role="alert"><VaultIcon name="shield" :size="16" />{{ state.loadError.message }} 原文件保留，请勿删除数据文件。</p>
    <p v-if="error" class="error-banner" role="alert"><span>{{ error }}</span><button type="button" aria-label="关闭错误提示" @click="error = ''"><QIcon name="close" :size="13" /></button></p>

    <div class="workspace">
      <aside class="categories">
        <section class="vault-card" aria-label="加密状态">
          <span class="vault-lock"><VaultIcon name="lock" :size="20" /></span>
          <div>
            <strong>保险库已加密</strong>
            <small>仅当前 Windows 用户可读</small>
          </div>
          <dl>
            <div><dt>分类</dt><dd>{{ categories.length }}</dd></div>
            <div><dt>条目</dt><dd>{{ totalEntries }}</dd></div>
            <div><dt>账号</dt><dd>{{ totalAccounts }}</dd></div>
          </dl>
        </section>
        <div class="sidebar-heading"><span>分类</span><QIconButton type="button" label="新建分类" :disabled="locked" @click="categoryDialog()"><VaultIcon name="plus" :size="16" /></QIconButton></div>
        <nav aria-label="账号分类">
          <button v-for="c in categories" :key="c.id" type="button" class="category" :class="{ selected: c.id === selectedId && !search }" :style="{ '--hue': hue(c.name) }" :aria-current="c.id === selectedId ? 'page' : undefined" :disabled="busy" @click="choose(c.id)">
            <i aria-hidden="true" /><span>{{ c.name }}</span><small>{{ accountCount(c) }}</small>
          </button>
        </nav>
        <p v-if="!categories.length" class="category-hint">按用途建立分类，例如游戏、网站或工作。</p>
        <div class="sidebar-foot"><VaultIcon name="shield" :size="15" /><p>TXT 导出是明文文件，导出后请妥善保管，不要上传网盘。</p></div>
      </aside>

      <main class="contents">
        <template v-if="query || selected">
          <div class="content-header">
            <div v-if="query" class="content-title">
              <p class="eyebrow">全部分类 · {{ visible.length }} 个结果</p>
              <h2>搜索“{{ search.trim() }}”</h2>
            </div>
            <div v-else-if="selected" class="content-title" :style="{ '--hue': hue(selected.name) }">
              <p class="eyebrow"><i aria-hidden="true" />{{ selected.entries.length }} 个条目 · {{ accountCount(selected) }} 组账号</p>
              <div class="category-title"><h2>{{ selected.name }}</h2><QIconButton type="button" label="修改分类" :disabled="locked" @click="categoryDialog(selected)"><QIcon name="edit" :size="15" /></QIconButton><QIconButton type="button" label="删除分类" :disabled="locked" @click="removeCategory"><QIcon name="remove" :size="15" /></QIconButton></div>
            </div>
            <div v-if="!query && selected" class="content-actions">
              <QButton type="button" variant="ghost" :disabled="locked || !accountCount(selected)" @click="exportTxt(false)">导出分类</QButton>
              <QButton type="button" variant="primary" :disabled="locked" @click="editEntry()"><VaultIcon name="plus" :size="16" />添加条目</QButton>
            </div>
          </div>

          <TransitionGroup name="record" tag="div" class="entry-list">
            <article v-for="{ entry, category } in visible" :key="entry.id" class="entry-card" :style="{ '--hue': hue(entry.name) }">
              <header class="entry-header">
                <span class="entry-mark" aria-hidden="true">{{ initial(entry.name) }}</span>
                <div class="entry-heading">
                  <h3>{{ entry.name }}<span v-if="query" class="entry-category">{{ category.name }}</span></h3>
                  <button v-if="entry.link" type="button" class="entry-link" :class="{ copied: copied.has(`${entry.id}:link`) }" :title="`${entry.link} · 点击复制链接`" :disabled="locked" @click="copyLink(entry)"><VaultIcon :name="copied.has(`${entry.id}:link`) ? 'check' : 'link'" :size="12" /><span>{{ host(entry.link) }}</span></button>
                  <p v-if="entry.remark" class="entry-remark">{{ entry.remark }}</p>
                </div>
                <div class="entry-tools"><QIconButton type="button" :label="`编辑 ${entry.name}`" :disabled="locked" @click="editEntry(entry, category)"><QIcon name="edit" :size="15" /></QIconButton><QIconButton type="button" :label="`删除 ${entry.name}`" :disabled="locked" @click="removeEntry(entry)"><QIcon name="remove" :size="15" /></QIconButton></div>
              </header>
              <div class="credentials">
                <div v-for="a in entry.credentials" :key="a.id" class="account-row">
                  <div class="account-cell">
                    <span class="account-avatar" aria-hidden="true"><VaultIcon name="user" :size="15" /></span>
                    <div class="credential-text"><strong>{{ a.account }}</strong><small v-if="a.note">{{ a.note }}</small></div>
                    <button type="button" class="cell-action" :class="{ copied: copied.has(`${a.id}:account`) }" :aria-label="`复制账号 ${a.account}`" :title="`复制账号 ${a.account}`" :disabled="locked" @click="copy(entry.id, a.id, 'account')"><VaultIcon :name="copied.has(`${a.id}:account`) ? 'check' : 'copy'" :size="15" /></button>
                  </div>
                  <div class="password-cell" :class="{ revealed: shown(a.id) !== undefined }">
                    <VaultIcon class="password-key" name="key" :size="15" />
                    <code :class="{ revealed: shown(a.id) !== undefined }" :aria-label="shown(a.id) !== undefined ? '已显示密码' : '密码已隐藏'">{{ shown(a.id) ?? '***' }}</code>
                    <button type="button" class="cell-action reveal" :class="{ 'is-loading': pendingReveal.has(a.id) }" :aria-label="shown(a.id) !== undefined ? '隐藏密码' : '显示密码'" :title="shown(a.id) !== undefined ? `隐藏密码（${REVEAL_SECONDS} 秒后自动隐藏）` : '显示密码'" :disabled="locked || pendingReveal.has(a.id)" @click="reveal(entry.id, a.id)">
                      <svg v-if="shown(a.id) !== undefined" class="reveal-ring" viewBox="0 0 32 32" aria-hidden="true"><circle cx="16" cy="16" r="14.5" pathLength="100" :style="{ animationDuration: `${REVEAL_SECONDS}s` }" /></svg>
                      <VaultIcon :name="shown(a.id) !== undefined ? 'eyeOff' : 'eye'" :size="16" />
                    </button>
                    <button type="button" class="cell-action" :class="{ copied: copied.has(`${a.id}:password`) }" aria-label="复制密码" title="复制密码" :disabled="locked" @click="copy(entry.id, a.id, 'password')"><VaultIcon :name="copied.has(`${a.id}:password`) ? 'check' : 'copy'" :size="15" /></button>
                  </div>
                  <section v-if="a.securityFields?.length" class="security-info">
                    <button type="button" class="security-info-toggle" :aria-expanded="expandedSecurity.has(a.id)" :aria-controls="`security-${a.id}`" @click="toggleSecurity(a.id)"><VaultIcon name="shield" :size="13" /><span>安全信息</span><small>{{ a.securityFields.length }}</small><span class="security-chevron" :class="{ expanded: expandedSecurity.has(a.id) }" aria-hidden="true">⌄</span></button>
                    <Transition name="security-info">
                      <div v-if="expandedSecurity.has(a.id)" :id="`security-${a.id}`" class="security-info-list">
                        <div v-for="(field, index) in a.securityFields" :key="index" class="security-info-row">
                          <span class="security-info-label">{{ securityFieldLabel(field) }}</span><span class="security-info-value">{{ field.value }}</span>
                          <button type="button" class="cell-action" :class="{ copied: copied.has(`${a.id}:securityField:${index}`) }" :aria-label="`复制${securityFieldLabel(field)} ${a.account} 第${index + 1}条`" :title="`复制${securityFieldLabel(field)}`" :disabled="locked" @click="copy(entry.id, a.id, 'securityField', index)"><VaultIcon :name="copied.has(`${a.id}:securityField:${index}`) ? 'check' : 'copy'" :size="14" /></button>
                        </div>
                      </div>
                    </Transition>
                  </section>
                  <TotpCode v-if="a.totp" :entry-id="entry.id" :credential-id="a.id" :config="a.totp" :privacy-epoch="totpPrivacyEpoch" :disabled="locked" @copied="announce('验证码已复制')" @error="error = $event" />
                </div>
              </div>
            </article>
          </TransitionGroup>

          <div v-if="!visible.length" class="empty-state">
            <span class="empty-mark"><VaultIcon :name="query ? 'search' : 'key'" :size="28" /></span>
            <h3>{{ query ? '没有找到匹配的条目' : '这个分类还没有账号' }}</h3>
            <p>{{ query ? '换个关键词试试；搜索不会匹配密码。' : '添加游戏或网站名称，再为它保存一组或多组账号密码。' }}</p>
            <QButton v-if="!query" type="button" variant="primary" :disabled="locked" @click="editEntry()"><VaultIcon name="plus" :size="16" />添加第一个条目</QButton>
          </div>
          <p v-if="visible.length" class="security-footnote"><VaultIcon name="eyeOff" :size="13" />密码显示 {{ REVEAL_SECONDS }} 秒后自动隐藏，离开窗口也会隐藏。剪贴板可能被其他软件读取。</p>
        </template>

        <div v-else class="empty-state first-use">
          <span class="vault-door" aria-hidden="true"><i /><i /><i /><VaultIcon name="vault" :size="34" /></span>
          <p class="eyebrow">A PLACE FOR YOUR ACCOUNTS</p>
          <h2>从一个分类开始</h2>
          <p>游戏、网站或工作，各有一页。同一条目可以保存多个账号，并分别复制账号或密码。</p>
          <QButton type="button" variant="primary" :disabled="locked" @click="categoryDialog()"><VaultIcon name="plus" :size="16" />创建第一个分类</QButton>
          <span class="first-use-note"><VaultIcon name="lock" :size="13" />使用当前 Windows 用户的 DPAPI 加密保存</span>
        </div>
      </main>
    </div>

    <Transition name="notice"><div v-if="status" class="notice" role="status"><span><VaultIcon name="check" :size="14" /></span>{{ status }}</div></Transition>

    <QModal :open="editing" :title="draft.id ? '编辑条目' : '添加条目'" :busy="busy" close-label="关闭条目编辑" panel-class="entry-editor" @close="closeEditor">
      <form class="edit-form" autocomplete="off" @submit.prevent="saveEntry" @keydown.ctrl.enter.prevent="saveEntry">
        <p class="form-hint"><VaultIcon name="lock" :size="13" />一个游戏或网站，多个账号。保存后密码会回到隐藏状态。</p>
        <div class="edit-grid"><QModalInput v-model="draft.name" label="游戏 / 网站名称" placeholder="例如：Steam、某个游戏或网站" :max-length="128" :disabled="busy" /><label class="local-field"><span>分类</span><select v-model="draft.categoryId" :disabled="busy"><option v-for="c in categories" :key="c.id" :value="c.id">{{ c.name }}</option></select></label></div>
        <QModalInput v-model="draft.link" label="对应链接（可选）" placeholder="https://example.com" :max-length="1024" :disabled="busy" />
        <label class="local-field"><span>条目备注（可选）</span><textarea v-model="draft.remark" rows="2" maxlength="2048" placeholder="例如：服务器、区服、角色或用途" :disabled="busy" /></label>
        <div class="accounts-heading"><h3>账号与密码 <small>{{ draft.credentials.length }}</small></h3><QButton type="button" variant="ghost" :disabled="busy || draft.credentials.length >= 64" @click="draft.credentials.push(newAccount())"><VaultIcon name="plus" :size="15" />再加一组</QButton></div>
        <TransitionGroup name="record" tag="div" class="draft-accounts">
          <section v-for="(a, index) in draft.credentials" :key="a.id ?? `new-${index}`" class="draft-account">
            <div class="draft-account-heading"><span><b>{{ index + 1 }}</b>账号</span><QIconButton type="button" label="移除此组账号" :disabled="busy || draft.credentials.length <= 1" @click="removeDraftAccount(index)"><QIcon name="remove" :size="14" /></QIconButton></div>
            <div class="edit-grid"><QModalInput v-model="a.account" label="账号" placeholder="用户名、邮箱或账号 ID" :max-length="256" :disabled="busy" /><div class="password-editor"><QModalInput v-model="a.password" label="密码" :type="editVisible.has(index) ? 'text' : 'password'" placeholder="输入密码" :max-length="4096" :disabled="busy" /><QIconButton type="button" :label="editVisible.has(index) ? '隐藏输入的密码' : '显示输入的密码'" :disabled="busy" @click="toggleEdit(index)"><VaultIcon :name="editVisible.has(index) ? 'eyeOff' : 'eye'" :size="16" /></QIconButton></div></div>
            <QModalInput v-model="a.note" label="账号备注（可选）" placeholder="例如：主号、小号、角色名" :max-length="256" :disabled="busy" />
            <section class="security-editor">
              <div class="security-editor-heading"><div><strong>安全信息 <small>可选 · {{ a.securityFields.length }}/{{ MAX_SECURITY_FIELDS }}</small></strong><p>可添加多条安全手机、邮箱或自定义信息；空白行不保存。</p></div><QButton type="button" class="add-security-field" variant="ghost" :disabled="busy || a.securityFields.length >= MAX_SECURITY_FIELDS" @click="addSecurityField(a)"><VaultIcon name="plus" :size="13" />添加安全信息</QButton></div>
              <TransitionGroup name="record" tag="div" class="security-field-list">
                <div v-for="(field, fieldIndex) in a.securityFields" :key="securityFieldKey(field)" class="security-field-edit">
                  <div class="security-field-inputs" :class="{ 'is-custom': field.kind === 'custom' }">
                    <label class="local-field"><span>信息类型</span><select v-model="field.kind" :disabled="busy" aria-label="安全信息类型"><option value="phone">安全手机</option><option value="email">安全邮箱</option><option value="custom">自定义</option></select></label>
                    <QModalInput v-if="field.kind === 'custom'" v-model="field.label" label="自定义名称" placeholder="例如：备用联系方式" :max-length="32" :disabled="busy" />
                    <QModalInput v-model="field.value" class="security-field-value" :label="field.kind === 'phone' ? '手机号码' : field.kind === 'email' ? '邮箱地址' : '信息内容'" :placeholder="field.kind === 'phone' ? '可包含国家或地区区号' : field.kind === 'email' ? 'name@example.com' : '填写需要保存的信息'" :max-length="256" :disabled="busy" />
                  </div>
                  <QIconButton type="button" label="移除这条安全信息" :disabled="busy" @click="removeSecurityField(a, fieldIndex)"><QIcon name="remove" :size="14" /></QIconButton>
                </div>
              </TransitionGroup>
            </section>
            <section class="totp-editor">
              <label class="totp-enable"><input v-model="a.totpEnabled" type="checkbox" :disabled="busy" /><VaultIcon name="shield" :size="14" /><strong>身份验证器（TOTP）</strong><small>可选</small></label>
              <div v-if="a.totpEnabled" class="totp-editor-fields">
                <QModalInput v-model="a.totpSecret" :label="a.totp ? '替换密钥（留空保留原密钥）' : '身份验证器密钥'" type="password" placeholder="Base32 密钥或 otpauth://totp/ 绑定链接" :max-length="4096" :disabled="busy" />
                <p class="totp-editor-hint">{{ a.totp ? '已有密钥不会显示在编辑界面；取消勾选并保存可移除。' : '使用网站提供的绑定密钥，保存后可离线生成验证码。' }} 密钥加密保存，不包含在 TXT 导出中。</p>
                <details class="totp-settings"><summary>验证码参数 <span>{{ a.totpSettings.algorithm }} · {{ a.totpSettings.digits }} 位 · {{ a.totpSettings.periodSeconds }} 秒</span></summary><div>
                  <label class="local-field"><span>算法</span><select v-model="a.totpSettings.algorithm" :disabled="busy"><option>SHA1</option><option>SHA256</option><option>SHA512</option></select></label>
                  <label class="local-field"><span>验证码位数</span><select v-model.number="a.totpSettings.digits" :disabled="busy"><option :value="6">6 位</option><option :value="8">8 位</option></select></label>
                  <label class="local-field"><span>更新周期（秒）</span><input v-model.number="a.totpSettings.periodSeconds" type="number" min="15" max="300" step="1" :disabled="busy" /></label>
                </div><p class="totp-editor-hint">仅按网站要求修改；绑定链接中的参数优先。电脑系统时间需准确。</p></details>
              </div>
            </section>
          </section>
        </TransitionGroup>
        <p v-if="editError" class="edit-error" role="alert">{{ editError }}</p>
      </form>
      <template #actions><span class="save-hint"><VaultIcon name="lock" :size="13" />本机加密保存 · Ctrl Enter</span><QButton type="button" :disabled="busy" @click="closeEditor">取消</QButton><QButton type="button" variant="primary" :loading="busy" @click="saveEntry">保存条目</QButton></template>
    </QModal>
  </div>
</template>
