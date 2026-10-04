import type { ModuleFilter, ModuleOperation } from '../app/moduleStore'
import type { StartupAuthorizationState } from '../contracts/modules'
import type { TranslationKey } from '../localization/messages/en-US'

export type LifecycleModuleOperation = Exclude<ModuleOperation, 'startupAuthorization'|'openDirectory'|'remove'|'checkUpdate'|'downloadUpdate'|'installUpdate'>

const operationKeys: Record<LifecycleModuleOperation, readonly [TranslationKey, TranslationKey]> = {
  load: ['modules.operation.load', 'modules.operation.loading'],
  activate: ['modules.operation.activate', 'modules.operation.activating'],
  open: ['modules.operation.open', 'modules.operation.opening'],
  deactivate: ['modules.operation.deactivate', 'modules.operation.deactivating'],
  unload: ['modules.operation.unload', 'modules.operation.unloading'],
}

const operationSuccessKeys: Record<LifecycleModuleOperation, TranslationKey> = {
  load: 'modules.toast.loaded', activate: 'modules.toast.activated', open: 'modules.toast.opened',
  deactivate: 'modules.toast.deactivated', unload: 'modules.toast.unloaded',
}
const operationFailureKeys: Record<LifecycleModuleOperation, TranslationKey> = {
  load: 'modules.toast.loadFailed', activate: 'modules.toast.activateFailed', open: 'modules.toast.openFailed',
  deactivate: 'modules.toast.deactivateFailed', unload: 'modules.toast.unloadFailed',
}
const operationErrorKeys: Record<string, TranslationKey> = {
  hotkeyUnavailable: 'modules.error.hotkeyUnavailable',
  hotkeyConflict: 'modules.error.hotkeyConflict',
  hotkeyInvalid: 'modules.error.hotkeyInvalid',
  moduleInvokeTimeout: 'modules.error.timeout',
  moduleHandshakeTimeout: 'modules.error.timeout',
  moduleHandshakeFailed: 'modules.error.handshake',
  moduleProtocolFailed: 'modules.error.protocol',
  moduleLifecycleInvalid: 'modules.error.lifecycle',
  moduleNotRunning: 'modules.error.notLoaded',
  moduleNotLoaded: 'modules.error.notLoaded',
  moduleExited: 'modules.error.exited',
  runtimeStartFailed: 'modules.error.startFailed',
  moduleInvokeFailed: 'modules.error.rejected',
  Busy: 'modules.error.busy',
}
export function moduleOperationErrorKey(error: unknown): TranslationKey | null {
  const text = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
  const code = text.match(/^([A-Za-z][A-Za-z0-9]*):/)?.[1]
  return code ? operationErrorKeys[code] ?? null : null
}
const runtimeKeys: Record<string, TranslationKey> = {
  NotLoaded: 'moduleState.notLoaded', Loaded: 'moduleState.loaded', Running: 'moduleState.running',
  Deactivated: 'moduleState.deactivated', Unloaded: 'moduleState.unloaded', Failed: 'moduleState.failed',
}
const startupKeys: Record<StartupAuthorizationState, TranslationKey> = {
  NotEnabled: 'modules.startup.notEnabled', Enabled: 'modules.startup.enabled',
  ChangedNeedsConfirmation: 'modules.startup.changed', Unavailable: 'modules.startup.unavailable',
  Missing: 'modules.startup.missing',
}
const filterKeys: Record<ModuleFilter, TranslationKey> = {
  all: 'modules.filter.all', running: 'modules.filter.running', notLoaded: 'modules.filter.notLoaded',
  issues: 'modules.filter.issues', invalid: 'modules.filter.invalid',
}

export const moduleOperationLabelKey = (operation: LifecycleModuleOperation, busy: boolean): TranslationKey =>
  operationKeys[operation][busy ? 1 : 0]
export const moduleOperationSuccessKey = (operation: LifecycleModuleOperation): TranslationKey => operationSuccessKeys[operation]
export const moduleOperationFailureKey = (operation: LifecycleModuleOperation): TranslationKey => operationFailureKeys[operation]
export const moduleRuntimeStateKey = (runtimeState: string): TranslationKey | null => runtimeKeys[runtimeState] ?? null
export const startupAuthorizationMessageKey = (state: StartupAuthorizationState): TranslationKey => startupKeys[state]
export const moduleFilterLabelKey = (filter: ModuleFilter): TranslationKey => filterKeys[filter]
