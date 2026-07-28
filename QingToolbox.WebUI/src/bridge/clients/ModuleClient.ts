import type { RequestClient } from '../protocol/RequestClient'
import { isModuleImportResult, isModuleManagementResult, isModuleSnapshot, type ModuleImportResult, type ModuleManagementResult, type ModuleSnapshot } from '../../contracts/modules'

export class ModuleClient {
  constructor(private readonly requests: RequestClient) {}
  getSnapshot() { return this.requestSnapshot('modules.getSnapshot') }
  async importModule(): Promise<ModuleImportResult> {
    const value = await this.requests.request<unknown>('modules.import', {})
    if (!isModuleImportResult(value)) throw new Error('Module import result validation failed.')
    return value
  }
  load(moduleId: string) { return this.requestSnapshot('modules.load', { moduleId }) }
  activate(moduleId: string) { return this.requestSnapshot('modules.activate', { moduleId }) }
  open(moduleId: string) { return this.requestSnapshot('modules.open', { moduleId }) }
  deactivate(moduleId: string) { return this.requestSnapshot('modules.deactivate', { moduleId }) }
  unload(moduleId: string) { return this.requestSnapshot('modules.unload', { moduleId }) }
  openDirectory(moduleId: string) { return this.requestManagement('modules.openDirectory', moduleId) }
  remove(moduleId: string) { return this.requestManagement('modules.remove', moduleId) }
  setStartupAuthorization(moduleId: string, enabled: boolean) { return this.requestSnapshot('modules.setStartupAuthorization', { moduleId, enabled }) }
  private async requestSnapshot(command: string, payload: Record<string, unknown> = {}): Promise<ModuleSnapshot> {
    const value = await this.requests.request<unknown>(command, payload)
    if (!isModuleSnapshot(value)) throw new Error('Module snapshot validation failed.')
    return value
  }
  private async requestManagement(command: string, moduleId: string): Promise<ModuleManagementResult> {
    const value = await this.requests.request<unknown>(command, { moduleId })
    if (!isModuleManagementResult(value)) throw new Error('Module management result validation failed.')
    return value
  }
}
