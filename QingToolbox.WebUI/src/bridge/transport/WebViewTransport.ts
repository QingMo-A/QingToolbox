import { isBridgeEvent, isBridgeResponse, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
interface WebViewApi { postMessage(message:unknown):void; addEventListener(type:'message',listener:(event:MessageEvent)=>void):void; removeEventListener(type:'message',listener:(event:MessageEvent)=>void):void }
declare global { interface Window { chrome?:{webview?:WebViewApi} } }

export class WebViewTransport implements Transport {
  readonly mode='WebView' as const
  private readonly pending=new Map<string,{resolve:(value:BridgeResponse)=>void;reject:(reason:Error)=>void;timer:number}>()
  private readonly listeners=new Set<(event:BridgeEvent)=>void>()
  private readonly api:WebViewApi
  private disposed=false
  private readonly onMessage=(event:MessageEvent)=>{
    if (isBridgeResponse(event.data)) { const p=this.pending.get(event.data.requestId); if(!p)return; clearTimeout(p.timer); this.pending.delete(event.data.requestId); p.resolve(event.data) }
    else if(isBridgeEvent(event.data)) this.listeners.forEach(listener=>listener(event.data))
  }
  static isAvailable(){return Boolean(window.chrome?.webview)}
  constructor(){const api=window.chrome?.webview;if(!api)throw new Error('WebView transport is unavailable.');this.api=api;api.addEventListener('message',this.onMessage)}
  request(message:BridgeRequest,timeoutMs=5000):Promise<BridgeResponse>{
    if(this.disposed)return Promise.reject(new Error('Bridge transport is disposed.'))
    if(this.pending.has(message.requestId))return Promise.reject(new Error('Duplicate bridge request ID.'))
    return new Promise((resolve,reject)=>{const timer=window.setTimeout(()=>{this.pending.delete(message.requestId);reject(new Error('Bridge request timed out.'))},timeoutMs);this.pending.set(message.requestId,{resolve,reject,timer});this.api.postMessage(message)})
  }
  subscribe(listener:(event:BridgeEvent)=>void){if(this.disposed)return()=>{};this.listeners.add(listener);return()=>this.listeners.delete(listener)}
  dispose(){if(this.disposed)return;this.disposed=true;this.api.removeEventListener('message',this.onMessage);for(const p of this.pending.values()){clearTimeout(p.timer);p.reject(new Error('Bridge transport was disposed.'))}this.pending.clear();this.listeners.clear()}
}
