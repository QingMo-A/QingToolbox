import { invoke } from '@tauri-apps/api/core'
import type { ModuleContext, State } from './types'

export const getContext = (): Promise<ModuleContext> => invoke('get_module_window_context')
export const invokeModule = <T>(method: string, payload: unknown = {}): Promise<T> =>
  invoke('invoke_module_window', { method, payload })
export const getState = (): Promise<State> => invokeModule<State>('getState')
export const hideModuleWindow = (): Promise<void> => invoke('hide_module_window')
