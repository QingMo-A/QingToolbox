import { invoke as tauriInvoke } from '@tauri-apps/api/core'

export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type LauncherItem = {
  id: string
  name: string
  iconKey: string | null
  lastLaunchedAt: string | null
  source: 'custom' | 'desktop' | string
}

export type LauncherFolder = {
  id: string
  name: string
  items: LauncherItem[]
}

export type LauncherState = {
  sortMode: 'custom' | 'alphabetical' | 'desktop'
  items: LauncherItem[]
  folders: LauncherFolder[]
  customOrder: string[]
  recent: LauncherItem[]
  hotkey: { ctrl: boolean; alt: boolean; shift: boolean; win: boolean; virtualKey: number; keyLabel: string }
  hotkeyStatus: string
  active: boolean
}

export type EverythingSearchMode = 'normal' | 'everything-all' | 'everything-file' | 'everything-directory'

export type ParsedSearch = {
  mode: EverythingSearchMode
  query: string
}

export type EverythingResult = {
  id: string
  name: string
  parentPath: string
  isDirectory: boolean
  resultType: 'file' | 'directory' | string
}

export type EverythingSearchResponse = {
  requestId: string
  mode: Exclude<EverythingSearchMode, 'normal'>
  query: string
  status: 'ready' | 'indexing' | 'unavailable' | 'error' | string
  results: EverythingResult[]
  error?: string
}

/** The prefix is intentionally tiny and explicit; everything after it stays
 * untouched so native Everything syntax such as `*.exe` keeps working. */
export function parseSearchMode(value: string): ParsedSearch {
  const match = /^\/e(?::([fd]))?(?:\s([\s\S]*))?$/i.exec(value)
  if (!match) return { mode: 'normal', query: value }
  return {
    mode: match[1]?.toLowerCase() === 'f'
      ? 'everything-file'
      : match[1]?.toLowerCase() === 'd'
        ? 'everything-directory'
        : 'everything-all',
    query: match[2] ?? '',
  }
}

export function getContext() {
  return tauriInvoke<ModuleContext>('get_module_window_context')
}

export function invokeModule<T>(method: string, payload: Record<string, unknown> = {}) {
  return tauriInvoke<T>('invoke_module_window', { method, payload })
}

export function hideModuleWindow() {
  return tauriInvoke<void>('hide_module_window')
}
