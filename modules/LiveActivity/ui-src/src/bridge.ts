import { invoke as tauriInvoke } from '@tauri-apps/api/core'
import type {
  Diagnostics,
  ModuleContext,
  ModuleState,
  MockScenario,
  SettingsPatch,
  VisualPatch,
} from './types'

/**
 * Call one of this module's declared operations.
 *
 * The host refuses any method that is not in the manifest's `operations` list,
 * so a typo here surfaces as a rejected call rather than a silent no-op.
 */
export function invokeModule<T>(method: string, payload: Record<string, unknown> = {}): Promise<T> {
  return tauriInvoke<T>('invoke_module_window', { method, payload })
}

export function getContext(): Promise<ModuleContext> {
  return tauriInvoke<ModuleContext>('get_module_window_context')
}

export function hideModuleWindow(): Promise<void> {
  return tauriInvoke<void>('hide_module_window')
}

export function getState(): Promise<ModuleState> {
  return invokeModule<ModuleState>('getState')
}

export function setSettings(patch: SettingsPatch): Promise<ModuleState> {
  return invokeModule<ModuleState>('setSettings', patch as Record<string, unknown>)
}

/** Move the real island while a slider is held; nothing is saved. */
export function previewSettings(patch: VisualPatch): Promise<unknown> {
  return invokeModule<unknown>('previewSettings', patch as Record<string, unknown>)
}

/** Show a scripted island. Never touches real activity state. */
export function previewIsland(scenario = 'working'): Promise<ModuleState> {
  return invokeModule<ModuleState>('previewIsland', { scenario })
}

export function dismissPreview(): Promise<ModuleState> {
  return invokeModule<ModuleState>('dismissPreview')
}

export function emitMockActivity(scenario: MockScenario): Promise<ModuleState> {
  return invokeModule<ModuleState>('emitMockActivity', { scenario })
}

export function clearActivities(): Promise<ModuleState> {
  return invokeModule<ModuleState>('clearActivities')
}

export function readDiagnostics(): Promise<Diagnostics> {
  return invokeModule<Diagnostics>('readDiagnostics')
}

export function clearDiagnostics(): Promise<Diagnostics> {
  return invokeModule<Diagnostics>('clearDiagnostics')
}

export function setCodexEnabled(enabled: boolean): Promise<ModuleState> {
  return invokeModule<ModuleState>('setCodexEnabled', { enabled })
}

export function refreshProviders(): Promise<ModuleState> {
  return invokeModule<ModuleState>('refreshProviders')
}

export function timerCommand(kind: 'stopwatch' | 'countdown', action: 'start' | 'pause' | 'reset'): Promise<ModuleState> {
  return invokeModule<ModuleState>('timerCommand', { kind, action })
}
export function hideTemporarily(): Promise<ModuleState> { return invokeModule<ModuleState>('hideTemporarily') }
export function restoreIsland(): Promise<ModuleState> { return invokeModule<ModuleState>('restoreIsland') }
