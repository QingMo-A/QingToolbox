import type { BridgeEvent, BridgeRequest, BridgeResponse } from '../../contracts/app'
export interface Transport {
  readonly mode:'WebView'|'Mock'|'Unavailable'
  request(message:BridgeRequest, timeoutMs?:number):Promise<BridgeResponse>
  subscribe(listener:(event:BridgeEvent)=>void):()=>void
  dispose():void
}
