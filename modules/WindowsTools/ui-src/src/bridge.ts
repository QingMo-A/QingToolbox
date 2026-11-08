import { invoke } from '@tauri-apps/api/core'
export const getContext = () => invoke<{ appearancePreset: string; theme: string }>('get_module_window_context')
export const call = <T>(method: string, payload: object = {}) => invoke<T>('invoke_module_window', { method, payload })
