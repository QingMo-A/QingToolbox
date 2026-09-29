<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { open } from '@tauri-apps/plugin-dialog'
import QButton from '../design-system/components/QButton.vue'
import QModal from '../design-system/components/QModal.vue'
import { useLocalization } from '../localization/localization'

type Receive = { defaultDirectory: string | null; useDefaultDirectory: boolean; autoAccept: boolean }
const emit = defineEmits<{ close: [] }>()
const { t } = useLocalization()
const receive = ref<Receive | null>(null)
const busy = ref(false)
const error = ref('')
const moduleId = 'qing.qingtransfer'

async function getSettings() {
  await invoke('list_modules')
  await invoke('start_module', { moduleId })
  const state = await invoke<{ receive: Receive }>('invoke_module', { moduleId, method: 'getState', payload: {} })
  receive.value = state.receive
}

async function change(payload: Partial<Receive>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    const state = await invoke<{ receive: Receive }>('invoke_module', { moduleId, method: 'setReceivePreferences', payload })
    receive.value = state.receive
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { busy.value = false }
}

async function chooseDirectory() {
  const path = await open({ directory: true, multiple: false, title: t('devices.transfer.chooseDirectory') })
  if (typeof path === 'string' && path) await change({ defaultDirectory: path })
}

onMounted(async () => {
  busy.value = true
  try { await getSettings() }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { busy.value = false }
})
</script>

<template>
  <QModal :open="true" :title="t('devices.transfer.receiveSettings')" :busy="busy" :close-label="t('devices.transfer.close')" @close="emit('close')">
    <p v-if="error" role="alert" class="receive-error">{{ error }}</p>
    <div class="receive-directory">
      <span>{{ receive?.defaultDirectory || t('devices.transfer.noDirectory') }}</span>
      <QButton :disabled="busy" @click="chooseDirectory">{{ t('devices.transfer.chooseDirectory') }}</QButton>
    </div>
    <label class="receive-toggle"><input type="checkbox" :checked="receive?.useDefaultDirectory || false" :disabled="busy || !receive" @change="change({ useDefaultDirectory: ($event.target as HTMLInputElement).checked })" />{{ t('devices.transfer.useDefaultDirectory') }}</label>
    <label class="receive-toggle"><input type="checkbox" :checked="receive?.autoAccept || false" :disabled="busy || !receive" @change="change({ autoAccept: ($event.target as HTMLInputElement).checked })" />{{ t('devices.transfer.autoAccept') }}</label>
    <p class="receive-hint">{{ t('devices.transfer.autoAcceptHint') }}</p>
    <template #actions><QButton variant="primary" @click="emit('close')">{{ t('devices.transfer.close') }}</QButton></template>
  </QModal>
</template>

<style scoped>
.receive-directory{display:flex;align-items:center;gap:10px;min-width:0}.receive-directory span{flex:1;min-width:0;overflow-wrap:anywhere;color:var(--q-text-2);font-size:13px}.receive-toggle{display:flex;align-items:center;gap:10px;font-size:14px;cursor:pointer}.receive-toggle input{accent-color:var(--q-brand);width:17px;height:17px}.receive-hint{margin:0;color:var(--q-text-2);font-size:12px}.receive-error{margin:0;color:var(--q-color-danger,#c43f4e);font-size:13px}
</style>
