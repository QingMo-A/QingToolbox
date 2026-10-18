import { invoke } from '@tauri-apps/api/core'
import type { ModuleContext, State } from './types'
export const getContext = () => invoke<ModuleContext>('get_module_window_context')
export const invokeModule = <T = State>(method: string, payload: Record<string, unknown> = {}) => invoke<T>('invoke_module_window', { method, payload })
export const getState = () => invokeModule('getState')
