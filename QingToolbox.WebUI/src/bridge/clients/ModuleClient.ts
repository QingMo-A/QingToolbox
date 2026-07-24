import type { RequestClient } from '../protocol/RequestClient'
import { isModuleSnapshot } from '../../contracts/modules'
export class ModuleClient {
  constructor(private readonly requests:RequestClient){}
  async getSnapshot(){const value=await this.requests.request<unknown>('modules.getSnapshot');if(!isModuleSnapshot(value))throw new Error('Module snapshot validation failed.');return value}
}
