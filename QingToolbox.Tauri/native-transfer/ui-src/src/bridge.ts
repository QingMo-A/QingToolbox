import { invoke } from '@tauri-apps/api/core'
import type { ModuleContext, State } from './types'

export function getContext(): Promise<ModuleContext> {
  return invoke<ModuleContext>('get_module_window_context')
}

export function getState(): Promise<State> {
  return invokeModule<State>('getState')
}

export function invokeModule<T>(method: string, payload: unknown = {}): Promise<T> {
  return invoke<T>('invoke_module_window', { method, payload })
}

export function hideModuleWindow(): Promise<void> {
  return invoke('hide_module_window')
}
