<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { QButton } from '@qingtoolbox/module-ui'
import { call } from './bridge'
import VaultIcon from './VaultIcon.vue'
import type { TotpCode, TotpSettings } from './types'

const props = defineProps<{ entryId: string; credentialId: string; config: TotpSettings; privacyEpoch: number; disabled?: boolean }>()
const emit = defineEmits<{ copied: []; error: [message: string] }>()
const visible = ref(false)
const value = ref<TotpCode | null>(null)
const now = ref(Date.now())
const pending = ref(false)
const copying = ref(false)
const copied = ref(false)
let generation = 0
let timer: ReturnType<typeof setInterval> | undefined
let copyTimer: ReturnType<typeof setTimeout> | undefined
let retryAfter = 0
let disposed = false
const current = computed(() => value.value && now.value >= value.value.validFromMs && now.value < value.value.validUntilMs ? value.value : null)
const remaining = computed(() => current.value ? Math.ceil((current.value.validUntilMs - now.value) / 1000) : 0)
const progress = computed(() => current.value ? Math.max(0, Math.min(1, (current.value.validUntilMs - now.value) / (current.value.periodSeconds * 1000))) : 0)
const formatted = computed(() => current.value?.code.replace(current.value.code.length === 8 ? /^(\d{4})(\d{4})$/ : /^(\d{3})(\d{3})$/, '$1 $2') ?? '')
function clearCode() { if (value.value) value.value.code = ''; value.value = null }
function hide() { generation++; visible.value = false; pending.value = false; copied.value = false; clearTimeout(copyTimer); clearCode(); clearInterval(timer); timer = undefined }
function valid(result: TotpCode) {
  return typeof result.code === 'string' && new RegExp(`^\\d{${props.config.digits}}$`).test(result.code)
    && [result.generatedAtMs, result.validFromMs, result.validUntilMs].every(Number.isSafeInteger)
    && result.periodSeconds === props.config.periodSeconds
    && result.validUntilMs - result.validFromMs === result.periodSeconds * 1000
    && result.generatedAtMs >= result.validFromMs && result.generatedAtMs < result.validUntilMs
    && Date.now() >= result.validFromMs && Date.now() < result.validUntilMs
}
function accept(result: TotpCode, epoch: number) {
  if (!disposed && visible.value && epoch === generation && !document.hidden && valid(result)) {
    if (value.value?.validUntilMs !== result.validUntilMs) { copied.value = false; clearTimeout(copyTimer) }
    clearCode(); value.value = { ...result }; now.value = Date.now(); return true
  }
  return false
}
async function refresh() {
  if (pending.value || props.disabled || !visible.value || document.hidden || Date.now() < retryAfter) return
  const epoch = generation
  pending.value = true
  let result: TotpCode | undefined
  try {
    result = await call<TotpCode>('getTotp', { entryId: props.entryId, credentialId: props.credentialId })
    if (!accept(result, epoch) && epoch === generation && visible.value) retryAfter = Date.now() + 1000
  } catch {
    if (epoch === generation && visible.value) { retryAfter = Date.now() + 3000; emit('error', '验证码读取失败，请检查系统时间或重新查看。') }
  } finally { if (result) result.code = ''; if (epoch === generation) pending.value = false }
}
function tick() {
  now.value = Date.now()
  if (!current.value) { clearCode(); void refresh() }
}
function toggle() {
  if (visible.value) { hide(); return }
  if (props.disabled || document.hidden) return
  visible.value = true; retryAfter = 0; tick(); timer = setInterval(tick, 200)
}
async function copy() {
  if (copying.value || props.disabled || document.hidden) return
  const epoch = generation
  copying.value = true
  let result: TotpCode | undefined
  try {
    // The backend computes again at click time; never copy a cached UI value.
    result = await call<TotpCode>('copyTotp', { entryId: props.entryId, credentialId: props.credentialId })
    if (!disposed && epoch === generation && !document.hidden) {
      accept(result, epoch); copied.value = true; clearTimeout(copyTimer)
      copyTimer = setTimeout(() => { copied.value = false }, 1600); emit('copied')
    }
  } catch { if (!disposed && epoch === generation) emit('error', '验证码复制失败，请重试。') }
  finally { if (result) result.code = ''; copying.value = false }
}
function visibility() { if (document.hidden) hide() }
watch(() => [props.config, props.entryId, props.credentialId, props.privacyEpoch], hide)
onMounted(() => { window.addEventListener('blur', hide); document.addEventListener('visibilitychange', visibility) })
onBeforeUnmount(() => { disposed = true; hide(); clearTimeout(copyTimer); window.removeEventListener('blur', hide); document.removeEventListener('visibilitychange', visibility) })
</script>
<template>
  <section class="totp-card" aria-label="身份验证器验证码">
    <div class="totp-card-heading"><VaultIcon name="shield" :size="14" /><span>身份验证器</span><small>{{ config.digits }} 位 · {{ config.periodSeconds }} 秒</small></div>
    <div class="totp-card-body">
      <template v-if="visible"><code class="totp-value" :aria-label="current ? '当前验证码' : '正在更新验证码'">{{ formatted || '正在更新…' }}</code><span v-if="current" class="totp-countdown" :style="{ '--totp-progress': `${progress * 360}deg` }" :aria-label="`${remaining} 秒后更新`"><b>{{ remaining }}<small>秒</small></b></span></template>
      <span v-else class="totp-placeholder">点击查看当前验证码</span>
    </div>
    <div class="totp-card-actions"><QButton type="button" variant="ghost" :disabled="disabled" @click="toggle"><VaultIcon :name="visible ? 'eyeOff' : 'eye'" :size="14" />{{ visible ? '隐藏验证码' : '查看验证码' }}</QButton><QButton type="button" variant="ghost" :disabled="disabled || copying" @click="copy"><VaultIcon :name="copied ? 'check' : 'copy'" :size="14" />{{ copied ? '已复制' : '复制验证码' }}</QButton></div>
  </section>
</template>
