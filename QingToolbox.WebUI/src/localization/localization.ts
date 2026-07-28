import { computed, type ComputedRef } from 'vue'
import { useSettingsStore } from '../app/settingsStore'
import type { EffectiveLanguageCode } from '../contracts/settings'
import { enUSMessages, type TranslationKey } from './messages/en-US'
import { zhCNMessages } from './messages/zh-CN'

export type WebLocale = EffectiveLanguageCode
export type TranslationParameters = Record<string, string | number>

const messages: Record<WebLocale, Partial<Record<TranslationKey, string>>> = {
  'en-US': enUSMessages,
  'zh-CN': zhCNMessages,
}

export function translate(
  locale: WebLocale,
  key: TranslationKey,
  parameters?: TranslationParameters,
): string {
  const value = messages[locale][key] ?? enUSMessages[key]
  if (value === undefined) {
    if (import.meta.env.DEV || import.meta.env.MODE === 'test')
      console.warn(`Missing translation: ${key}`)
    return key
  }

  return value.replace(/\{([A-Za-z0-9_]+)\}/g, (placeholder, name: string) =>
    parameters && Object.prototype.hasOwnProperty.call(parameters, name)
      ? String(parameters[name])
      : placeholder,
  )
}

export function useLocalization(): {
  currentLocale: ComputedRef<WebLocale>
  t: (key: TranslationKey, parameters?: TranslationParameters) => string
} {
  const settings = useSettingsStore()
  const currentLocale = computed<WebLocale>(() =>
    settings.snapshot?.language.effectiveCode ?? 'en-US')
  return {
    currentLocale,
    t: (key, parameters) => translate(currentLocale.value, key, parameters),
  }
}
