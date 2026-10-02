import { invoke as tauriInvoke } from '@tauri-apps/api/core'
import type { ModuleContext, PinState } from './types'

export function invokeModule<T>(method: string, payload: Record<string, unknown> = {}): Promise<T> { return tauriInvoke<T>('invoke_module_window', { method, payload }) }
export function getContext(): Promise<ModuleContext> { return tauriInvoke<ModuleContext>('get_module_window_context') }
export function hideModuleWindow(): Promise<void> { return tauriInvoke<void>('hide_module_window') }
export function getState(): Promise<PinState> { return invokeModule<PinState>('getState') }
export function selectRegion(): Promise<PinState | { cancelled: true }> { return tauriInvoke('select_screenpin_region') }
export function openPinWindow(pinId: string): Promise<{ pinId: string; windowLabel: string }> {
  return tauriInvoke<{ pinId: string; windowLabel: string }>('open_screenpin_pin', { pinId })
}
