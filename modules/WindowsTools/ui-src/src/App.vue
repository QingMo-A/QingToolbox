<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { QButton, QIcon, QModal } from '@qingtoolbox/module-ui'
import AssociationTable from './AssociationTable.vue'
import { call } from './bridge'
import iconUrl from '../../icon.svg'

type Operation = { running: boolean; action: string; error: string | null; revision: number }
type Profile = { name: 'Domain' | 'Private' | 'Public'; enabled: boolean | null; local: number | null; hasBackup: boolean; error: string | null; source?: string }
type State = { edition: string; managed: boolean; problems: string[]; update: { known: boolean; noAutoUpdate: number | null; supported: boolean; hasBackup: boolean }; firewall: Profile[]; firewallService: string; firewallServices?: { name: string; status: string; start: number | null }[]; operation: Operation }
type Confirmation = { action: string; title: string; detail: string; warning: boolean }
const state = ref<State | null>(null)
const loading = ref(false)
const running = ref(false)
const error = ref('')
const notice = ref('')
const confirmation = ref<Confirmation | null>(null)
const profileNames = { Domain: '域网络', Private: '专用网络', Public: '公用网络' }
const locked = computed(() => loading.value || running.value || !state.value || state.value.managed)
const updateLocked = computed(() => locked.value || !state.value?.update.known || !state.value?.update.supported)
const serviceStopped = computed(() => !!state.value && state.value.firewallService !== 'Running')
const serviceText = (s: string) => ({ Running: '运行中', Stopped: '已停止', StartPending: '启动中', unknown: '无法读取' }[s] || s)
const updateLabel = computed(() => !state.value?.update.known ? '状态未知' : state.value.update.noAutoUpdate === 1 ? '自动更新已关闭（策略）' : '未设置关闭策略')
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
let seenRevision = 0
function message(cause: unknown) {
  if (typeof cause === 'string') return cause
  return cause && typeof cause === 'object' && 'message' in cause ? String(cause.message) : '操作未完成，请重试。'
}
async function reload() {
  if (loading.value) return
  loading.value = true; error.value = ''
  try {
    const next = await call<State>('getState')
    if (disposed) return
    state.value = next; running.value = next.operation.running
    if (next.operation.revision > seenRevision) {
      seenRevision = next.operation.revision
      notice.value = ''
      if (next.operation.error) error.value = next.operation.error
      else notice.value = next.operation.action === 'firewall.repair' ? '服务修复操作已完成，状态已刷新。不会重置已有防火墙规则。' : '操作已完成，状态已刷新。更新策略可能需要稍后或重启后生效。'
    }
    if (running.value) schedulePoll()
  } catch (cause) { error.value = message(cause) }
  finally { loading.value = false }
}
function schedulePoll() { clearTimeout(timer); if (!disposed) timer = setTimeout(poll, 900) }
async function poll() {
  try {
    const op = await call<Operation>('getOperation')
    if (disposed) return
    running.value = op.running
    if (state.value) state.value.operation = op
    if (op.running) schedulePoll()
    else { await reload() }
  } catch (cause) { error.value = message(cause); schedulePoll() }
}
function askUpdate(operation: 'disable' | 'enable' | 'restore') {
  const title = { disable: '关闭 Windows 自动更新？', enable: '启用 Windows 自动更新？', restore: '恢复原自动更新配置？' }[operation]
  confirmation.value = { action: `update.${operation}`, title, warning: operation !== 'enable', detail: operation === 'disable'
    ? '将保存修改前的配置，并设置关闭自动更新策略。安全修复不会自动安装，你仍可手动检查更新。不会停止或破坏更新服务；组织策略及系统版本可能影响实际效果。'
    : operation === 'restore' ? '恢复本模块首次修改前的策略（包含原先“未配置”的状态）。这不一定等于启用更新；若其他程序更改了配置，会停止恢复。' : '设置允许自动更新，并保留首次修改前配置。实际下载和安装仍取决于 Windows 的其他设置。' }
}
function askFirewall(profile: Profile, operation: 'disable' | 'enable' | 'restore') {
  const verb = { disable: '关闭', enable: '开启', restore: '恢复' }[operation]
  confirmation.value = { action: `firewall.${profile.name}.${operation}`, title: `${verb}${profileNames[profile.name]}防火墙？`, warning: operation !== 'enable', detail: operation === 'disable'
    ? '关闭此网络配置的防火墙会增加网络攻击风险。不影响其他网络配置，也不会停止防火墙服务。将保存修改前配置，以便恢复。'
    : operation === 'restore' ? '恢复此配置在本模块首次修改前的本地值，可能恢复为关闭状态。组织策略可能优先生效。' : '开启此网络配置的防火墙，并保留首次修改前配置。不会更改现有防火墙规则。' }
}
async function confirmAction() {
  if (!confirmation.value || running.value) return
  const action = confirmation.value.action
  confirmation.value = null; error.value = ''; notice.value = ''; running.value = true
  if (state.value) state.value.operation.action = action
  try { await call('beginAction', { action, confirmed: true }); notice.value = '请完成 Windows 管理员授权，正在等待系统操作…'; schedulePoll() }
  catch (cause) { running.value = false; error.value = message(cause) }
}
async function settings(page: string) {
  try { await call('openSettings', { page }) } catch (cause) { error.value = message(cause) }
}
function askServiceRepair() {
  confirmation.value = { action: 'firewall.repair', title: '检查并修复防火墙服务？', warning: false, detail: '检查基础筛选引擎 BFE 和防火墙 MpsSvc，按依赖顺序启动；仅将已禁用的服务改为自动启动，并记录修复前状态。启动后现有防火墙规则会开始生效，可能影响部分网络连接。不会删除规则、修改服务权限，也不会自动关闭第三方安全软件。' }
}
onMounted(reload)
onBeforeUnmount(() => { disposed = true; clearTimeout(timer) })
</script>

<template>
  <main class="workspace">
    <header class="page-header">
      <div class="identity"><img :src="iconUrl" alt="" /><div><span class="eyebrow">WINDOWS WORKSPACE</span><h1>Windows 设置助手</h1><p>常用系统设置，集中管理，可追溯恢复。</p></div></div>
      <QButton :loading="loading" :disabled="running" @click="reload"><QIcon name="refresh" :size="15" />刷新状态</QButton>
    </header>
    <div class="environment"><span><i class="dot" />{{ state?.edition || '正在读取系统…' }}</span><span>API 1 · 仅按需操作</span><span>系统修改需管理员授权</span></div>
    <Transition name="notice"><div v-if="error" class="banner error" role="alert">{{ error }}</div></Transition>
    <Transition name="notice"><div v-if="notice" class="banner" role="status">{{ notice }}</div></Transition>
    <div v-if="state?.managed" class="banner warning">此电脑受组织管理或无法确认管理状态，已禁用系统修改。你仍可查看状态及调整个人默认应用。</div>
    <div v-for="problem in state?.problems" :key="problem" class="banner warning">{{ problem }}</div>

    <section class="system-grid" aria-label="系统设置">
      <article class="panel update-panel">
        <header class="panel-header"><div class="section-icon"><svg viewBox="0 0 24 24"><path d="M20 8a8 8 0 0 0-14-3L3 8m0-5v5h5m-4 8a8 8 0 0 0 14 3l3-3m0 5v-5h-5" /></svg></div><div><h2>自动更新</h2><p>控制自动下载和安装策略</p></div></header>
        <div class="status-line"><span class="status-pill" :class="{ caution: state?.update.noAutoUpdate === 1 }">{{ updateLabel }}</span><span v-if="state?.update.hasBackup" class="backup-label">已有配置备份</span></div>
        <p class="explanation">不关闭更新服务、不破坏系统文件。关闭后请定期手动安装安全更新。</p>
        <p v-if="state && !state.update.supported" class="inline-warning">此版本不支持该策略。家庭版等不受支持版本请使用 Windows 自带的暂停更新。</p>
        <div class="actions"><QButton :disabled="updateLocked || state?.update.noAutoUpdate === 1" @click="askUpdate('disable')">关闭自动更新</QButton><QButton :disabled="updateLocked || state?.update.noAutoUpdate === 0" @click="askUpdate('enable')">启用</QButton><QButton variant="ghost" :disabled="updateLocked || !state?.update.hasBackup" @click="askUpdate('restore')">恢复原配置</QButton></div>
        <button class="text-link" @click="settings('update')">打开 Windows 更新设置 <span aria-hidden="true">↗</span></button>
      </article>
      <article class="panel firewall-panel">
        <header class="panel-header"><div class="section-icon"><svg viewBox="0 0 24 24"><path d="m12 3 8 3v6c0 5-8 9-8 9S4 17 4 12V6zM9 12l2 2 4-5" /></svg></div><div><h2>Windows 防火墙</h2><p>分别管理三种网络环境</p></div></header>
        <div v-if="serviceStopped" class="service-repair"><div><strong>防火墙服务尚未运行</strong><p>模块可检查并尝试修复，无需手动查找系统服务。</p></div><div class="service-chips"><span v-for="service in state?.firewallServices" :key="service.name">{{ service.name }} · {{ serviceText(service.status) }}{{ service.start === 4 ? ' / 已禁用' : '' }}</span></div><QButton :disabled="locked" :loading="running && state?.operation.action === 'firewall.repair'" @click="askServiceRepair">检查并修复服务</QButton></div>
        <div class="profiles"><div v-for="profile in state?.firewall" :key="profile.name" class="profile"><div class="profile-info"><strong>{{ profileNames[profile.name] }}</strong><span :class="{ 'off-label': profile.enabled === false }"><i class="dot" :class="{ off: profile.enabled === false, unknown: profile.enabled === null }" />{{ profile.enabled === null ? serviceStopped ? '等待服务恢复' : '状态未知' : profile.enabled ? '已开启' : '已关闭' }}</span><small v-if="profile.source === 'saved'">已保存配置：{{ profile.local === 1 ? '开启' : profile.local === 0 ? '关闭' : '未配置' }}（不代表当前已生效）</small><small v-else-if="profile.error">{{ profile.error }}</small><small v-else-if="profile.local === 2 || (profile.local === 1) !== profile.enabled">实际状态可能受继承或策略影响</small></div><div class="profile-actions"><QButton :disabled="locked || serviceStopped || !!profile.error" @click="askFirewall(profile, profile.enabled ? 'disable' : 'enable')">{{ profile.enabled ? '关闭' : '开启' }}</QButton><QButton variant="ghost" :disabled="locked || serviceStopped || !!profile.error || !profile.hasBackup" @click="askFirewall(profile, 'restore')">恢复</QButton></div></div><p v-if="!state" class="explanation">正在读取网络配置…</p></div>
        <button class="text-link" @click="settings('firewall')">打开 Windows 安全中心 <span aria-hidden="true">↗</span></button>
      </article>
    </section>
    <AssociationTable />
    <footer class="footnote">配置备份保存在本机，卸载模块不会自动恢复系统设置。删除前请按需恢复；本模块不会随启用、停用反复修改配置。</footer>
    <QModal :open="!!confirmation" :title="confirmation?.title || ''" close-label="取消操作" @close="confirmation = null">
      <p class="confirm-detail">{{ confirmation?.detail }}</p><div class="confirm-note" :class="{ warning: confirmation?.warning }">继续后 Windows 会请求管理员权限。取消授权不会执行修改。</div>
      <template #actions><QButton @click="confirmation = null">取消</QButton><QButton :variant="confirmation?.warning ? 'danger' : 'primary'" @click="confirmAction">确认并授权</QButton></template>
    </QModal>
  </main>
</template>
