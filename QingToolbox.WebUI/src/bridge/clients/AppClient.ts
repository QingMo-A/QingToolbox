import type { AppSnapshot } from '../../contracts/app'
import { assetBuildId, isAppSnapshot, isPingResponse, isReadyChallenge } from '../../contracts/app'
import type { RequestClient } from '../protocol/RequestClient'
export class AppClient {
  private activationNonce:string|null=null
  constructor(private readonly requests:RequestClient){}
  async ready(mode:'WebView'|'Mock') { const value=await this.requests.request<unknown>('web.ready',{assetBuildId,documentReadyState:document.readyState,transportMode:mode});if(!isReadyChallenge(value))throw new Error('Ready challenge validation failed.');this.activationNonce=value.activationNonce;return value.snapshot }
  async ping(){if(!this.activationNonce)throw new Error('Activation nonce is unavailable.');const value=await this.requests.request<unknown>('app.ping',{activationNonce:this.activationNonce});if(!isPingResponse(value))throw new Error('Ping response validation failed.');return value}
  async getSnapshot(){const value=await this.requests.request<unknown>('app.getSnapshot');if(!isAppSnapshot(value))throw new Error('Snapshot validation failed.');return value}
  dispose(){this.activationNonce=null;this.requests.dispose()}
}
