import { assetBuildId, protocolVersion, type AppSnapshot, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
export class MockTransport implements Transport {
  readonly mode='Mock' as const; private readonly listeners=new Set<(event:BridgeEvent)=>void>(); private disposed=false
  async request(message:BridgeRequest):Promise<BridgeResponse>{if(this.disposed)throw new Error('Bridge transport is disposed.');if(message.protocolVersion!==protocolVersion)return this.error(message,'ProtocolMismatch');if(message.command==='app.ping')return this.ok(message,{pong:true,hostTime:new Date().toISOString()});if(message.command==='web.ready'||message.command==='app.getSnapshot'){const snapshot:AppSnapshot={environmentKind:'Mock',environmentDisplayName:'Browser Mock Environment',hostVersion:'mock',protocolVersion,totalModuleCount:4,validModuleCount:3,runningModuleCount:1,generatedAt:new Date().toISOString()};return this.ok(message,snapshot)}return this.error(message,'UnknownCommand')}
  subscribe(listener:(event:BridgeEvent)=>void){this.listeners.add(listener);return()=>this.listeners.delete(listener)} emit(event:BridgeEvent){this.listeners.forEach(x=>x(event))} dispose(){this.disposed=true;this.listeners.clear()}
  private ok(r:BridgeRequest,payload:unknown):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:true,payload,error:null}} private error(r:BridgeRequest,code:string):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:false,payload:{},error:{code,message:'Mock bridge rejected the request.'}}}
}
export const mockReadyPayload={assetBuildId,documentReadyState:'complete' as const,transportMode:'Mock' as const}
