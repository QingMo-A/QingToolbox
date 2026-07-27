import type { ModuleSnapshotItem } from '../contracts/modules'

const NOT_LOADED_STATES = new Set([
  'NotLoaded',
  'Unloaded',
])

const LOADED_STATES = new Set([
  'Loaded',
  'Deactivated',
])

export interface ModuleStateSummary {
  total: number
  valid: number
  issues: number
  notLoaded: number
  loaded: number
  running: number
}

export function isNotLoadedState(runtimeState: string): boolean {
  return NOT_LOADED_STATES.has(runtimeState)
}

export function isLoadedState(runtimeState: string): boolean {
  return LOADED_STATES.has(runtimeState)
}

export function isRunningState(runtimeState: string): boolean {
  return runtimeState === 'Running'
}

export function hasModuleIssues(module: ModuleSnapshotItem): boolean {
  return !module.isValid || module.errorCount > 0 || module.runtimeState === 'Failed'
}

export function summarizeModuleStates(
  modules: readonly ModuleSnapshotItem[],
): ModuleStateSummary {
  return modules.reduce<ModuleStateSummary>((summary, module) => {
    summary.total += 1
    if (module.isValid) summary.valid += 1
    if (hasModuleIssues(module)) summary.issues += 1
    if (isNotLoadedState(module.runtimeState)) summary.notLoaded += 1
    if (isLoadedState(module.runtimeState)) summary.loaded += 1
    if (isRunningState(module.runtimeState)) summary.running += 1
    return summary
  }, {
    total: 0,
    valid: 0,
    issues: 0,
    notLoaded: 0,
    loaded: 0,
    running: 0,
  })
}
