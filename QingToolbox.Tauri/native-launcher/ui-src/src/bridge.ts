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

export type LauncherState = {
  sortMode: 'custom' | 'alphabetical' | 'desktop'
  items: LauncherItem[]
  folders: unknown[]
  customOrder: string[]
  recent: LauncherItem[]
  hotkey: { ctrl: boolean; alt: boolean; shift: boolean; win: boolean; virtualKey: number; keyLabel: string }
  hotkeyStatus: string
  active: boolean
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
