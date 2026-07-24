import { assetBuildId, protocolVersion, type AppSnapshot, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
export class MockTransport implements Transport {
  readonly mode='Mock' as const; private readonly listeners=new Set<(event:BridgeEvent)=>void>(); private disposed=false
  private nonce:string|null=null;private sessionToken:string|null=null;private phase:'PreReady'|'ChallengeIssued'|'Activated'='PreReady'
  async request(message:BridgeRequest):Promise<BridgeResponse>{
    if(this.disposed)throw new Error('Bridge transport is disposed.')
    if(message.protocolVersion!==protocolVersion)return this.error(message,'ProtocolMismatch')
    const snapshot:AppSnapshot={environmentKind:'Mock',environmentDisplayName:'Browser Mock Environment',hostVersion:'mock',protocolVersion,totalModuleCount:4,validModuleCount:3,runningModuleCount:1,generatedAt:new Date().toISOString()}
    if(message.command==='web.ready'){
      if(this.phase!=='PreReady')return this.error(message,'InvalidBridgePhase')
      this.nonce=this.randomToken();this.phase='ChallengeIssued';return this.ok(message,{activationNonce:this.nonce,snapshot})
    }
    if(message.command==='app.ping'){
      const activation=typeof message.payload.activationNonce==='string';const session=typeof message.payload.sessionToken==='string'
      if(activation===session)return this.error(message,'InvalidPingCredential')
      if(activation){if(this.phase!=='ChallengeIssued'||message.payload.activationNonce!==this.nonce)return this.error(message,'InvalidActivationNonce');this.nonce=null;this.sessionToken=this.randomToken();this.phase='Activated';return this.ok(message,{pong:true,hostTime:new Date().toISOString(),sessionToken:this.sessionToken,activated:true})}
      if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated')
      if(message.payload.sessionToken!==this.sessionToken)return this.error(message,'InvalidSessionToken')
      return this.ok(message,{pong:true,hostTime:new Date().toISOString(),activated:true})
    }
    if(message.command==='app.getSnapshot'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');return this.ok(message,snapshot)}
    return this.error(message,'UnknownCommand')
  }
  subscribe(listener:(event:BridgeEvent)=>void){this.listeners.add(listener);return()=>this.listeners.delete(listener)} emit(event:BridgeEvent){this.listeners.forEach(x=>x(event))}
  dispose(){this.disposed=true;this.nonce=null;this.sessionToken=null;this.listeners.clear()}
  private randomToken(){return crypto.randomUUID().replaceAll('-','')+crypto.randomUUID().replaceAll('-','')}
  private ok(r:BridgeRequest,payload:unknown):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:true,payload,error:null}}
  private error(r:BridgeRequest,code:string):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:false,payload:{},error:{code,message:'Mock bridge rejected the request.'}}}
}
export const mockReadyPayload={assetBuildId,documentReadyState:'complete' as const,transportMode:'Mock' as const}
