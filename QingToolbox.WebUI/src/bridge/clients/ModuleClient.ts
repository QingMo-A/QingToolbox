import type { RequestClient } from '../protocol/RequestClient'
import { isModuleSnapshot, type ModuleSnapshot } from '../../contracts/modules'

export class ModuleClient {
  constructor(private readonly requests: RequestClient) {}
  getSnapshot() { return this.requestSnapshot('modules.getSnapshot') }
  load(moduleId: string) { return this.requestSnapshot('modules.load', { moduleId }) }
  activate(moduleId: string) { return this.requestSnapshot('modules.activate', { moduleId }) }
  private async requestSnapshot(command: string, payload: Record<string, unknown> = {}): Promise<ModuleSnapshot> {
    const value = await this.requests.request<unknown>(command, payload)
    if (!isModuleSnapshot(value)) throw new Error('Module snapshot validation failed.')
    return value
  }
}
