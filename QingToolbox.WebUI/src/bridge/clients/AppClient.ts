import type { AppSnapshot } from '../../contracts/app'
import { assetBuildId, isAppSnapshot, isPingResponse, isReadyChallenge } from '../../contracts/app'
import type { RequestClient } from '../protocol/RequestClient'
export class AppClient {
  private activationNonce:string|null=null
  private sessionToken:string|null=null
  constructor(private readonly requests:RequestClient){}
  async ready(mode:'WebView'|'Mock') { const value=await this.requests.request<unknown>('web.ready',{assetBuildId,documentReadyState:document.readyState,transportMode:mode});if(!isReadyChallenge(value))throw new Error('Ready challenge validation failed.');this.activationNonce=value.activationNonce;return value.snapshot }
  async ping(){const activating=this.sessionToken===null;if(activating&&!this.activationNonce)throw new Error('Activation nonce is unavailable.');const payload=activating?{activationNonce:this.activationNonce}:{sessionToken:this.sessionToken};const value=await this.requests.request<unknown>('app.ping',payload);if(!isPingResponse(value))throw new Error('Ping response validation failed.');if(activating){if(!value.sessionToken)throw new Error('Session token validation failed.');this.sessionToken=value.sessionToken;this.activationNonce=null}else if(value.sessionToken)throw new Error('Repeated ping returned a new session token.');return value}
  async getSnapshot(){const value=await this.requests.request<unknown>('app.getSnapshot');if(!isAppSnapshot(value))throw new Error('Snapshot validation failed.');return value}
  dispose(){this.activationNonce=null;this.sessionToken=null;this.requests.dispose()}
}
