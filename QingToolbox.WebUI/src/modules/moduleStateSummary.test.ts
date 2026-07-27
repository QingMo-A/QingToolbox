import { describe, expect, it } from 'vitest'
import type { ModuleSnapshotItem } from '../contracts/modules'
import {
  hasModuleIssues,
  isLoadedState,
  isNotLoadedState,
  isRunningState,
  summarizeModuleStates,
} from './moduleStateSummary'

const item = (runtimeState: string, overrides: Partial<ModuleSnapshotItem> = {}): ModuleSnapshotItem => ({
  id: `qing.${runtimeState}`,
  displayName: runtimeState,
  displayDescription: runtimeState,
  version: '1.0.0',
  author: 'Qing',
  runtimeType: 'OutOfProcess',
  loadMode: 'Manual',
  runtimeState,
  isValid: true,
  errorCount: 0,
  errors: [],
  permissions: [],
  minimumHostVersion: '0.2',
  isUserInstalled: true,
  canLoad: false,
  canActivate: false,
  canOpen: false,
  canDeactivate: false,
  canUnload: false,
  isBusy: false,
  isExecutionBlocked: false,
  isStartupEnabled: false,
  startupAuthorizationState: 'NotEnabled',
  canChangeStartupAuthorization: true,
  isStartupAuthorizationBusy: false,
  ...overrides,
})

describe('module state summary', () => {
  it('returns zero counts for an empty snapshot', () => {
    expect(summarizeModuleStates([])).toEqual({
      total: 0,
      valid: 0,
      issues: 0,
      notLoaded: 0,
      loaded: 0,
      running: 0,
    })
  })

  it('classifies all supported lifecycle states', () => {
    expect(isNotLoadedState('NotLoaded')).toBe(true)
    expect(isNotLoadedState('Unloaded')).toBe(true)
    expect(isLoadedState('Loaded')).toBe(true)
    expect(isLoadedState('Deactivated')).toBe(true)
    expect(isRunningState('Running')).toBe(true)
  })

  it('counts a mixed authoritative snapshot consistently', () => {
    const modules = [
      item('NotLoaded'),
      item('Unloaded'),
      item('Loaded'),
      item('Deactivated'),
      item('Running'),
      item('Failed'),
      item('UnknownFutureState'),
    ]

    expect(summarizeModuleStates(modules)).toEqual({
      total: 7,
      valid: 7,
      issues: 1,
      notLoaded: 2,
      loaded: 2,
      running: 1,
    })
  })

  it('counts invalid, error-bearing, and failed modules as unique issues', () => {
    const affected = item('Failed', { isValid: false, errorCount: 3 })
    expect(hasModuleIssues(affected)).toBe(true)
    expect(summarizeModuleStates([affected])).toEqual({
      total: 1,
      valid: 0,
      issues: 1,
      notLoaded: 0,
      loaded: 0,
      running: 0,
    })
  })

  it.each([
    item('Loaded', { isValid: false }),
    item('Loaded', { errorCount: 1 }),
    item('Failed'),
  ])('recognizes every issue source', module => {
    expect(hasModuleIssues(module)).toBe(true)
  })

  it('does not force an unknown valid state into a lifecycle bucket or mutate input', () => {
    const modules = Object.freeze([Object.freeze(item('Transitioning'))])
    const before = JSON.stringify(modules)

    expect(summarizeModuleStates(modules)).toEqual({
      total: 1,
      valid: 1,
      issues: 0,
      notLoaded: 0,
      loaded: 0,
      running: 0,
    })
    expect(JSON.stringify(modules)).toBe(before)
  })
})
