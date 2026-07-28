<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useModuleStore } from '../../app/moduleStore'
import type { ModuleSnapshotItem } from '../../contracts/modules'
import QIcon from './QIcon.vue'
import { useLocalization, translate } from '../../localization/localization'
import type { TranslationKey } from '../../localization/messages/en-US'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()
const router = useRouter()
const moduleStore = useModuleStore()
const { t } = useLocalization()
const dialog = ref<HTMLDialogElement | null>(null)
const input = ref<HTMLInputElement | null>(null)
const resultsContainer = ref<HTMLElement | null>(null)
const query = ref('')
const activeIndex = ref(-1)
let previousFocus: HTMLElement | null = null

type PageIconName = 'home' | 'modules' | 'running' | 'logs' | 'settings' | 'diagnostics'
type PageResultDefinition = { kind: 'page'; titleKey: TranslationKey; descriptionKey: TranslationKey; path: string; icon: PageIconName }
type PageResult = { kind: 'page'; title: string; description: string; englishTitle: string; englishDescription: string; path: string; icon: PageIconName }
type ModuleResult = { kind: 'module'; module: ModuleSnapshotItem }
type Result = PageResult | ModuleResult

const pageDefinitions: PageResultDefinition[] = [
  { kind: 'page', titleKey: 'navigation.home', descriptionKey: 'page.home.description', path: '/', icon: 'home' },
  { kind: 'page', titleKey: 'navigation.modules', descriptionKey: 'page.modules.description', path: '/modules', icon: 'modules' },
  { kind: 'page', titleKey: 'navigation.running', descriptionKey: 'page.running.description', path: '/running', icon: 'running' },
  { kind: 'page', titleKey: 'navigation.logs', descriptionKey: 'page.logs.description', path: '/logs', icon: 'logs' },
  { kind: 'page', titleKey: 'navigation.settings', descriptionKey: 'page.settings.description', path: '/settings', icon: 'settings' },
  { kind: 'page', titleKey: 'navigation.diagnostics', descriptionKey: 'page.diagnostics.description', path: '/diagnostics', icon: 'diagnostics' },
]
const pages = computed<PageResult[]>(() => pageDefinitions.map(page => ({
  kind: page.kind,
  title: t(page.titleKey),
  description: t(page.descriptionKey),
  englishTitle: translate('en-US', page.titleKey),
  englishDescription: translate('en-US', page.descriptionKey),
  path: page.path,
  icon: page.icon,
})))

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())
const pageResults = computed(() => {
  const term = normalizedQuery.value
  return term ? pages.value.filter(page => [page.title, page.description, page.englishTitle, page.englishDescription, page.path].some(value => value.toLocaleLowerCase().includes(term))) : pages.value
})
const moduleResults = computed(() => {
  const term = normalizedQuery.value
  if (!term) return moduleStore.runningModules.slice(0, 5)
  return moduleStore.modules.filter(module =>
    [module.displayName, module.displayDescription, module.author, module.id]
      .some(value => value.toLocaleLowerCase().includes(term)),
  ).slice(0, 8)
})
const results = computed<Result[]>(() => [
  ...pageResults.value,
  ...moduleResults.value.map(module => ({ kind: 'module' as const, module })),
])
const resultId = (index: number) => `quick-open-result-${index}`

async function syncOpen(open: boolean) {
  const element = dialog.value
  if (!element) return
  if (open) {
    if (!element.open) {
      previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      element.showModal()
    }
    activeIndex.value = results.value.length ? 0 : -1
    await nextTick()
    input.value?.focus()
    await revealActiveResult()
  } else if (element.open) {
    element.close()
  }
}
watch(() => props.open, syncOpen)
watch(results, async value => {
  activeIndex.value = value.length ? 0 : -1
  await revealActiveResult()
}, { immediate: true })

function close() {
  if (dialog.value?.open) dialog.value.close()
  else emit('close')
}
function onDialogClose() {
  query.value = ''
  activeIndex.value = -1
  emit('close')
  nextTick(() => previousFocus?.focus())
}
function onCancel(event: Event) {
  event.preventDefault()
  close()
}
function move(delta: number) {
  const count = results.value.length
  if (!count) { activeIndex.value = -1; return }
  activeIndex.value = (activeIndex.value + delta + count) % count
  void revealActiveResult()
}
async function revealActiveResult() {
  await nextTick()
  if (!props.open || !dialog.value?.open || !resultsContainer.value) return
  const index = activeIndex.value
  if (index < 0) return
  dialog.value.querySelector<HTMLElement>(`#${resultId(index)}`)?.scrollIntoView({ block: 'nearest' })
}
async function select(result: Result) {
  if (result.kind === 'page') {
    await router.push(result.path)
  } else {
    moduleStore.stateFilter = 'all'
    moduleStore.searchQuery = ''
    moduleStore.selectedModuleId = result.module.id
    await router.push('/modules')
  }
  close()
}
function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') { event.preventDefault(); move(1) }
  else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1) }
  else if (event.key === 'Enter' && activeIndex.value >= 0) {
    event.preventDefault()
    void select(results.value[activeIndex.value])
  }
}

onMounted(() => { void syncOpen(props.open) })
onBeforeUnmount(() => {
  if (dialog.value?.open) dialog.value.close()
  previousFocus = null
})
</script>

<template>
  <dialog ref="dialog" class="q-command-palette" aria-labelledby="quick-open-title" @close="onDialogClose" @cancel="onCancel">
    <header>
      <QIcon name="search" :size="22" />
      <div class="q-command-search">
        <h2 id="quick-open-title" class="sr-only">{{ t('quickOpen.title') }}</h2>
        <input
          ref="input"
          v-model="query"
          type="search"
          role="combobox"
          aria-autocomplete="list"
          :aria-label="t('quickOpen.searchLabel')"
          :placeholder="t('quickOpen.searchPlaceholder')"
          :aria-expanded="true"
          aria-controls="quick-open-results"
          :aria-activedescendant="activeIndex >= 0 ? resultId(activeIndex) : undefined"
          @keydown="onKeydown"
        />
      </div>
      <kbd>Ctrl K</kbd>
      <button type="button" class="q-command-close" :aria-label="t('quickOpen.closeLabel')" @click="close"><QIcon name="close" /></button>
    </header>

    <div ref="resultsContainer" id="quick-open-results" class="q-command-results" role="listbox" :aria-label="t('quickOpen.resultsLabel')">
      <template v-if="results.length">
        <section v-if="pageResults.length">
          <h3>{{ t('quickOpen.pages') }}</h3>
          <button
            v-for="page in pageResults"
            :id="resultId(results.indexOf(page))"
            :key="page.path"
            type="button"
            role="option"
            :aria-selected="activeIndex === results.indexOf(page)"
            :class="{ active: activeIndex === results.indexOf(page) }"
            @mouseenter="activeIndex = results.indexOf(page)"
            @click="select(page)"
          >
            <span class="q-command-result-icon"><QIcon :name="page.icon" /></span>
            <span><strong>{{ page.title }}</strong><small>{{ page.description }}</small></span>
            <em>{{ page.path }}</em>
          </button>
        </section>
        <section v-if="moduleResults.length">
          <h3>{{ normalizedQuery ? t('quickOpen.modules') : t('quickOpen.runningNow') }}</h3>
          <button
            v-for="module in moduleResults"
            :id="resultId(results.findIndex(result => result.kind === 'module' && result.module.id === module.id))"
            :key="module.id"
            type="button"
            role="option"
            :aria-selected="activeIndex === results.findIndex(result => result.kind === 'module' && result.module.id === module.id)"
            :class="{ active: activeIndex === results.findIndex(result => result.kind === 'module' && result.module.id === module.id) }"
            @mouseenter="activeIndex = results.findIndex(result => result.kind === 'module' && result.module.id === module.id)"
            @click="select({ kind: 'module', module })"
          >
            <span class="q-command-result-icon module">{{ module.displayName.slice(0, 1).toUpperCase() }}</span>
            <span><strong>{{ module.displayName }} <small>v{{ module.version }}</small></strong><small>{{ module.displayDescription }}</small></span>
            <em>{{ module.runtimeState }}</em>
          </button>
        </section>
      </template>
      <div v-else class="q-command-empty">
        <strong>{{ t('quickOpen.noResults') }}</strong>
        <p>{{ t('quickOpen.noResultsHint') }}</p>
      </div>
    </div>

    <footer><span>{{ t('quickOpen.navigateHint') }}</span><span>{{ t('quickOpen.openHint') }}</span><span>{{ t('quickOpen.closeHint') }}</span></footer>
  </dialog>
</template>

<style scoped>
.q-command-palette{width:680px;max-width:calc(100vw - 32px);max-height:calc(100vh - 48px);margin:auto;padding:0;border:1px solid var(--q-border);border-radius:14px;background:var(--q-surface);color:var(--q-text);box-shadow:0 24px 70px rgba(17,35,65,.24);overflow:hidden}
.q-command-palette::backdrop{background:rgba(18,32,54,.38)}
.q-command-palette>header{display:grid;grid-template-columns:22px minmax(0,1fr) auto 36px;align-items:center;gap:12px;padding:14px 16px;border-bottom:1px solid var(--q-border)}
.q-command-search input{width:100%;height:38px;padding:0;border:0;outline:0;background:transparent;color:var(--q-text);font-size:16px}
.q-command-search input::placeholder{color:var(--q-text-3)}
kbd{padding:4px 7px;border:1px solid var(--q-border);border-radius:6px;background:var(--q-surface-soft);color:var(--q-text-2);font-size:11px;white-space:nowrap}
.q-command-close{width:36px;height:36px;padding:0;border:0;border-radius:8px;background:transparent;color:var(--q-text-2);display:grid;place-items:center;cursor:pointer}
.q-command-close:hover{background:var(--q-brand-soft);color:var(--q-brand)}
.q-command-results{max-height:480px;padding:10px;overflow:auto;scroll-padding-block:8px;overscroll-behavior:contain}
.q-command-results section+section{margin-top:10px}
.q-command-results h3{margin:5px 9px 6px;color:var(--q-text-3);font-size:11px;text-transform:uppercase;letter-spacing:.05em}
.q-command-results button{display:grid;grid-template-columns:36px minmax(0,1fr) auto;align-items:center;gap:11px;width:100%;min-height:56px;padding:8px 10px;border:0;border-radius:9px;background:transparent;color:var(--q-text);text-align:left;cursor:pointer}
.q-command-results button:hover,.q-command-results button.active{background:var(--q-brand-soft)}
.q-command-results button>span:nth-child(2){min-width:0}
.q-command-results strong,.q-command-results small{display:block}
.q-command-results strong{font-size:13px}
.q-command-results strong small{display:inline;color:var(--q-text-3);font-size:10px;font-weight:500}
.q-command-results button>span:nth-child(2)>small{margin-top:3px;overflow:hidden;color:var(--q-text-2);font-size:11px;line-height:1.35;text-overflow:ellipsis;white-space:nowrap}
.q-command-results em{max-width:130px;overflow:hidden;color:var(--q-text-3);font-size:10px;font-style:normal;text-overflow:ellipsis;white-space:nowrap}
.q-command-result-icon{width:34px;height:34px;border-radius:8px;background:var(--q-surface-soft);color:var(--q-brand);display:grid;place-items:center}
.q-command-result-icon.module{background:var(--q-brand-soft);font-weight:750}
.q-command-empty{padding:50px 20px;text-align:center}
.q-command-empty p{margin:7px 0 0;color:var(--q-text-2);font-size:12px}
.q-command-palette>footer{display:flex;flex-wrap:wrap;gap:16px;padding:10px 16px;border-top:1px solid var(--q-border);background:var(--q-surface-soft);color:var(--q-text-3);font-size:10px}
@media(max-width:650px){.q-command-palette>header{grid-template-columns:22px minmax(0,1fr) 36px;padding-inline:12px}.q-command-palette kbd{display:none}.q-command-results{max-height:calc(100vh - 170px)}.q-command-results button{grid-template-columns:34px minmax(0,1fr)}.q-command-results button>em{grid-column:2}.q-command-results button>span:nth-child(2)>small{white-space:normal}.q-command-palette>footer{gap:10px}}
</style>
