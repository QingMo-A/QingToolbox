export interface ModuleSnapshotItem {
  id: string; displayName: string; displayDescription: string; version: string; author: string
  runtimeType: string; loadMode: string; runtimeState: string; isValid: boolean; errorCount: number
  errors: string[]; permissions: string[]; minimumHostVersion: string; isUserInstalled: boolean
  canLoad: boolean; canActivate: boolean; canOpen: boolean; isBusy: boolean; isExecutionBlocked: boolean
}
export interface ModuleSnapshot { generatedAt: string; modules: ModuleSnapshotItem[] }
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
const isItem = (value: any): value is ModuleSnapshotItem => !!value &&
  ['id','displayName','displayDescription','version','author','runtimeType','loadMode','runtimeState','minimumHostVersion'].every(key => typeof value[key] === 'string') &&
  typeof value.isValid === 'boolean' && Number.isInteger(value.errorCount) && value.errorCount >= 0 &&
  strings(value.errors) && strings(value.permissions) && typeof value.isUserInstalled === 'boolean' &&
  typeof value.canLoad === 'boolean' && typeof value.canActivate === 'boolean' && typeof value.canOpen === 'boolean' &&
  typeof value.isBusy === 'boolean' && typeof value.isExecutionBlocked === 'boolean'
export const isModuleSnapshot = (value: any): value is ModuleSnapshot => !!value &&
  typeof value.generatedAt === 'string' && !Number.isNaN(Date.parse(value.generatedAt)) &&
  Array.isArray(value.modules) && value.modules.every(isItem)
