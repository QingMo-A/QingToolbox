import { describe, expect, it, vi } from 'vitest'
import {
  moduleFilterLabelKey, moduleOperationFailureKey, moduleOperationLabelKey,
  moduleOperationSuccessKey, moduleRuntimeStateKey, startupAuthorizationMessageKey,
  type LifecycleModuleOperation,
} from './modulePresentation'

describe('modulePresentation', () => {
  const operations: LifecycleModuleOperation[] = ['load','activate','open','deactivate','unload']
  it('maps every normal and busy operation label without side effects', () => {
    expect(operations.map(value => moduleOperationLabelKey(value, false))).toEqual([
      'modules.operation.load','modules.operation.activate','modules.operation.open','modules.operation.deactivate','modules.operation.unload',
    ])
    expect(operations.map(value => moduleOperationLabelKey(value, true))).toEqual([
      'modules.operation.loading','modules.operation.activating','modules.operation.opening','modules.operation.deactivating','modules.operation.unloading',
    ])
  })
  it('maps success and failure feedback for all lifecycle operations', () => {
    expect(operations.map(moduleOperationSuccessKey)).toEqual(['modules.toast.loaded','modules.toast.activated','modules.toast.opened','modules.toast.deactivated','modules.toast.unloaded'])
    expect(operations.map(moduleOperationFailureKey)).toEqual(['modules.toast.loadFailed','modules.toast.activateFailed','modules.toast.openFailed','modules.toast.deactivateFailed','modules.toast.unloadFailed'])
  })
  it('maps known states and leaves unknown host states alone', () => {
    expect(['NotLoaded','Loaded','Running','Deactivated','Unloaded','Failed'].map(moduleRuntimeStateKey)).toEqual([
      'moduleState.notLoaded','moduleState.loaded','moduleState.running','moduleState.deactivated','moduleState.unloaded','moduleState.failed',
    ])
    expect(moduleRuntimeStateKey('FutureState')).toBeNull()
  })
  it('maps all startup authorization states and filters', () => {
    expect(['NotEnabled','Enabled','ChangedNeedsConfirmation','Unavailable','Missing'].map(value => startupAuthorizationMessageKey(value as never))).toEqual([
      'modules.startup.notEnabled','modules.startup.enabled','modules.startup.changed','modules.startup.unavailable','modules.startup.missing',
    ])
    expect(['all','running','notLoaded','issues','invalid'].map(value => moduleFilterLabelKey(value as never))).toEqual([
      'modules.filter.all','modules.filter.running','modules.filter.notLoaded','modules.filter.issues','modules.filter.invalid',
    ])
  })
  it('does not mutate inputs or execute external behavior', () => {
    const operation = Object.freeze({ value: 'open' as const }); const client = vi.fn()
    expect(moduleOperationLabelKey(operation.value, false)).toBe('modules.operation.open')
    expect(operation.value).toBe('open'); expect(client).not.toHaveBeenCalled()
  })
})
