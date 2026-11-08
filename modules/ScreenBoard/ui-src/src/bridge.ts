import { invoke } from '@tauri-apps/api/core'
import type { Settings } from './model'
export interface State { settings: Settings; active: boolean; visible: boolean; passthrough: boolean; shortcutAvailable: boolean; error: string | null; bounds: { x: number; y: number; width: number; height: number } }
export const native = new URLSearchParams(location.search).has('surface')
export const getContext = () => invoke<{ appearancePreset: string; theme: string }>('get_module_window_context')
export const call = <T>(method: string, payload: object = {}) => invoke<T>('invoke_module_window', { method, payload })
export const getState = () => native ? invoke<State>('board_state') : call<State>('getState')
export const settings = (patch: Partial<Settings>) => native ? invoke<State>('board_settings', { patch }) : call<State>('setSettings', patch)
export const control = (action: string) => invoke('board_control', { action })
