import type { BridgeEvent,BridgeRequest,BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
export class UnavailableTransport implements Transport { readonly mode='Unavailable' as const; request(_m:BridgeRequest):Promise<BridgeResponse>{return Promise.reject(new Error('WebView bridge is unavailable.'))} subscribe(_l:(e:BridgeEvent)=>void){return()=>{}} dispose(){} }
