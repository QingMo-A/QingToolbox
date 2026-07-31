import type { RequestClient } from '../protocol/RequestClient'
import { isHostUpdateSnapshot } from '../../contracts/hostUpdate'
import type { HostUpdateSnapshot } from '../../contracts/hostUpdate'

export class HostUpdateClient {
  constructor(private readonly requests:RequestClient) {}
  getSnapshot(){return this.request('hostUpdate.getSnapshot')}
  check(){return this.request('hostUpdate.check')}
  download(){return this.request('hostUpdate.download')}
  cancel(){return this.request('hostUpdate.cancel')}
  install(){return this.request('hostUpdate.install')}
  private async request(command:string):Promise<HostUpdateSnapshot>{
    const value=await this.requests.request<unknown>(command,{})
    if(!isHostUpdateSnapshot(value))throw new Error('Host update snapshot validation failed.')
    return value
  }
}
