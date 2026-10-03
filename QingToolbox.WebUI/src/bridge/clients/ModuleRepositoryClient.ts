import { invoke } from '@tauri-apps/api/core'
import { isRecord } from '../../contracts/app'
import { isOfficialModules, isModuleRepositoryDownload } from '../../contracts/moduleRepository'

export class ModuleRepositoryClient {
  async list() {
    const value = await invoke<unknown>('get_official_modules')
    if (!isOfficialModules(value)) throw new Error('InvalidCatalog')
    return value
  }
  async download(moduleId: string) {
    const value = await invoke<unknown>('download_official_module', { moduleId })
    if (!isModuleRepositoryDownload(value)) throw new Error('InvalidCatalog')
    return value
  }
  async snapshot() {
    const value = await invoke<unknown>('get_module_repository_download')
    if (!isModuleRepositoryDownload(value)) throw new Error('InvalidCatalog')
    return value
  }
}
export function repositoryError(error: unknown): string {
  const code = isRecord(error) ? error.code : error instanceof Error ? error.message : error
  return typeof code === 'string' && ['InvalidCatalog', 'NetworkUnavailable', 'Busy', 'SelectionUnavailable', 'StorageUnavailable', 'HashMismatch', 'SizeMismatch', 'InvalidPackage'].includes(code) ? code : 'NetworkUnavailable'
}
export const moduleRepositoryClient = new ModuleRepositoryClient()
