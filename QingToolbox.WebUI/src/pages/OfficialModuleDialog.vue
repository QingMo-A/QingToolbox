<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import QModal from '../design-system/components/QModal.vue'
import QButton from '../design-system/components/QButton.vue'
import QIcon from '../design-system/components/QIcon.vue'
import type { OfficialModule } from '../contracts/moduleRepository'
import { moduleRepositoryClient, repositoryError } from '../bridge/clients/ModuleRepositoryClient'
import { useModuleRepositoryStore } from '../app/moduleRepositoryStore'
import { useLocalization } from '../localization/localization'
import type { TranslationKey } from '../localization/messages/en-US'
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()
const { t, currentLocale } = useLocalization()
const downloads = useModuleRepositoryStore()
const modules = ref<OfficialModule[]>([])
const selected = ref('')
const loading = ref(false)
const error = ref('')
let generation = 0
const selectedModule = computed(() => modules.value.find(m => m.id === selected.value && m.canDownload))
const canDownload = computed(() => !!selectedModule.value && !loading.value && !downloads.active)
const label = (value: Record<string, string>) => value[currentLocale.value] ?? value['en-US']
const message = (code: string) => t(`modules.repository.error.${code}` as TranslationKey)
const size = (bytes: number) => bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`
async function load() {
  const request = ++generation
  modules.value = []; selected.value = ''; error.value = ''; loading.value = true
  try { const result = await moduleRepositoryClient.list(); if (request === generation && props.open) modules.value = result }
  catch (failure) { if (request === generation && props.open) error.value = repositoryError(failure) }
  finally { if (request === generation) loading.value = false }
}
watch(() => props.open, open => { if (open) void load(); else { generation++; loading.value = false } }, { immediate: true })
async function download() {
  if (!canDownload.value || !selectedModule.value) return
  error.value = ''
  try { if (await downloads.download(selectedModule.value.id)) emit('close') }
  catch (failure) { error.value = repositoryError(failure) }
}
function navigate(event: KeyboardEvent, index: number) {
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const options = [...(event.currentTarget as HTMLElement).parentElement!.querySelectorAll<HTMLButtonElement>('[role="option"]')]
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length
  options[next]?.focus()
  const item = modules.value[next]
  selected.value = item?.canDownload ? item.id : ''
}
</script>
<template>
  <QModal :open="open" :title="t('modules.repository.title')" :close-label="t('modules.repository.cancel')" panel-class="official-module-dialog" @close="emit('close')">
    <div class="official-repository-origin"><QIcon name="modules" /><span>QingMo-A / QingToolbox · modules</span></div>
    <div v-if="loading" class="official-module-state" role="status"><span class="module-operation-spinner" />{{ t('modules.repository.loading') }}</div>
    <div v-else-if="error" class="official-module-error" role="alert"><QIcon name="statusWarning" /><p>{{ message(error) }}</p><QButton v-if="!modules.length" @click="load" :disabled="downloads.active">{{ t('modules.repository.retry') }}</QButton></div>
    <div v-if="!loading && modules.length" class="official-module-list" role="listbox" :aria-label="t('modules.repository.title')">
      <button v-for="(module,index) in modules" :key="module.id" type="button" role="option" :aria-selected="selected === module.id" :aria-disabled="!module.canDownload"
        class="official-module-option" :class="{ 'is-selected': selected === module.id, 'is-unavailable': !module.canDownload }"
        @click="selected = module.canDownload ? module.id : ''" @keydown="navigate($event,index)">
        <span class="official-module-glyph"><QIcon name="modules" /></span>
        <span class="official-module-copy"><span class="official-module-name"><strong>{{ label(module.name) }}</strong><small v-if="module.version">v{{ module.version }}</small></span>
          <span class="official-module-description">{{ label(module.description) }}</span>
          <span class="official-module-meta">{{ module.id }}<template v-if="module.apiVersion"> · API {{ module.apiVersion }}</template><template v-if="module.size"> · {{ size(module.size) }}</template></span>
          <span v-if="module.unavailableReason" class="official-module-reason">{{ message(module.unavailableReason) }}</span>
        </span><span class="official-module-selection" aria-hidden="true"><QIcon v-if="selected === module.id" name="statusSuccess" /></span>
      </button>
    </div>
    <div v-else-if="!loading && !error" class="official-module-state">{{ t('modules.repository.empty') }}</div>
    <p v-if="downloads.active" class="official-download-busy" role="status">{{ t('modules.repository.busy') }}</p>
    <template #actions><QButton @click="emit('close')">{{ t('modules.repository.cancel') }}</QButton><QButton class="official-module-download" variant="primary" :disabled="!canDownload" :loading="downloads.starting" @click="download"><QIcon name="download" />{{ t('modules.repository.download') }}</QButton></template>
  </QModal>
</template>
<style>
.q-modal-card.official-module-dialog { width: min(620px,calc(100vw - 44px)); max-height: calc(100dvh - 44px); display: flex; flex-direction: column; overflow: hidden; }
.official-module-dialog .q-modal-header,.official-module-dialog .q-modal-actions { flex-shrink: 0; }
.official-module-dialog .q-modal-content { min-height: 0; overflow-y: auto; }
.official-repository-origin { display: flex; align-items: center; gap: 8px; color: var(--q-text-3); font-size: 12px; margin-bottom: 14px; }
.official-module-list { display: grid; gap: 8px; max-height: min(460px,calc(100dvh - 280px)); overflow-y: auto; padding: 2px; scrollbar-width: thin; scrollbar-color: var(--q-border) transparent; }
.official-module-option { display: flex; align-items: center; gap: 12px; width: 100%; padding: 14px; border: 1px solid var(--q-border); border-radius: 12px; background: var(--q-surface-soft); color: var(--q-text); text-align: left; font: inherit; cursor: pointer; transition: background 160ms ease,border-color 160ms ease,transform 180ms ease; }
.official-module-option:hover { border-color: var(--q-brand); transform: translateY(-1px); }
.official-module-option.is-selected { border-color: var(--q-brand); background: color-mix(in srgb,var(--q-brand) 8%,var(--q-surface)); }
.official-module-option.is-unavailable { cursor: default; }
.official-module-glyph { display: grid; place-items: center; width: 40px; height: 40px; flex-shrink: 0; border-radius: 11px; color: var(--q-brand); background: color-mix(in srgb,var(--q-brand) 10%,var(--q-surface)); }
.official-module-copy { display: grid; gap: 5px; flex: 1; min-width: 0; }
.official-module-name { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; }
.official-module-name strong { font-size: 15px; }
.official-module-name small,.official-module-meta { color: var(--q-text-3); font-size: 11px; }
.official-module-description { font-size: 12px; color: var(--q-text-2); line-height: 1.5; }
.official-module-reason { color: var(--q-warning); font-size: 12px; }
.official-module-selection { width: 20px; color: var(--q-brand); }
.official-module-state { display: flex; align-items: center; justify-content: center; gap: 10px; min-height: 220px; color: var(--q-text-2); font-size: 13px; }
.official-module-error { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; padding: 18px; border-radius: 12px; background: color-mix(in srgb,var(--q-danger) 6%,var(--q-surface)); color: var(--q-danger); }
.official-module-error p { flex: 1; min-width: 170px; margin: 0; font-size: 13px; }
.official-download-busy { color: var(--q-text-2); font-size: 12px; }
@media(prefers-reduced-motion:reduce) { .official-module-option { transition:none; } }
</style>
