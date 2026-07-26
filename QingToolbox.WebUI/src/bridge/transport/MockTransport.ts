import { assetBuildId, protocolVersion, type AppSnapshot, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'
import type { ModuleSnapshot, ModuleSnapshotItem } from '../../contracts/modules'
export class MockTransport implements Transport {
  readonly mode='Mock' as const; private readonly listeners=new Set<(event:BridgeEvent)=>void>(); private disposed=false
  private nonce:string|null=null;private sessionToken:string|null=null;private phase:'PreReady'|'ChallengeIssued'|'Activated'='PreReady'
  private showLogsInSidebar=true
  private mainWindowCloseBehavior:'Ask'|'MinimizeToNotificationArea'|'ExitApplication'='MinimizeToNotificationArea'
  private startupPresentationMode:'MainWindow'|'Minimized'|'FloatingBadge'='FloatingBadge'
  private modules:ModuleSnapshotItem[]=[
    {id:'qing.hello',displayName:'Hello Module',displayDescription:'A small example module for validating the Qing workspace.',version:'0.1.0',author:'QingMo-A',runtimeType:'InProcess',loadMode:'Manual',runtimeState:'NotLoaded',isValid:true,errorCount:0,errors:[],permissions:[],minimumHostVersion:'0.2.0-alpha',isUserInstalled:false,canLoad:true,canActivate:false,canOpen:false,canDeactivate:false,canUnload:false,isBusy:false,isExecutionBlocked:false},
    {id:'qing.texttools',displayName:'Text Tools',displayDescription:'Lightweight text conversion and formatting tools.',version:'0.1.0',author:'QingMo-A',runtimeType:'OutOfProcess',loadMode:'Manual',runtimeState:'Running',isValid:true,errorCount:0,errors:[],permissions:['Clipboard'],minimumHostVersion:'0.2.0-alpha',isUserInstalled:true,canLoad:false,canActivate:false,canOpen:true,canDeactivate:true,canUnload:true,isBusy:false,isExecutionBlocked:false}
  ]
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
    if(message.command==='modules.getSnapshot'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');if(Object.keys(message.payload).length)return this.error(message,'InvalidPayload');return this.ok(message,this.moduleSnapshot())}
    if(message.command==='modules.load'||message.command==='modules.activate'||message.command==='modules.open'||message.command==='modules.deactivate'||message.command==='modules.unload'){
      if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated')
      if(Object.keys(message.payload).length!==1||typeof message.payload.moduleId!=='string'||!message.payload.moduleId.trim())return this.error(message,'InvalidPayload')
      const index=this.modules.findIndex(item=>item.id===message.payload.moduleId);if(index<0)return this.error(message,'ModuleNotFound')
      const item=this.modules[index];if(item.isBusy)return this.error(message,'ModuleBusy');if(item.isExecutionBlocked)return this.error(message,'ModuleExecutionBlocked')
      if(message.command==='modules.load'){
        if(!item.canLoad)return this.error(message,'ModuleOperationUnavailable')
        this.modules[index]={...item,runtimeState:'Loaded',canLoad:false,canActivate:true,canOpen:true,canDeactivate:false,canUnload:true}
      }else if(message.command==='modules.activate'){
        if(!item.canActivate)return this.error(message,'ModuleOperationUnavailable')
        this.modules[index]={...item,runtimeState:'Running',canLoad:false,canActivate:false,canOpen:true,canDeactivate:true,canUnload:true}
      }else if(message.command==='modules.open'){if(!item.canOpen)return this.error(message,'ModuleOperationUnavailable')}
      else if(message.command==='modules.deactivate'){
        if(!item.canDeactivate)return this.error(message,'ModuleOperationUnavailable')
        this.modules[index]={...item,runtimeState:'Deactivated',canActivate:true,canOpen:true,canDeactivate:false,canUnload:true}
      }else{
        if(!item.canUnload)return this.error(message,'ModuleOperationUnavailable')
        this.modules[index]={...item,runtimeState:'Unloaded',canLoad:true,canActivate:false,canOpen:false,canDeactivate:false,canUnload:false}
      }
      return this.ok(message,this.moduleSnapshot())
    }
    if(message.command==='logs.getSnapshot'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');if(Object.keys(message.payload).length!==0)return this.error(message,'InvalidPayload');return this.ok(message,{generatedAt:'2026-07-25T12:00:03.000Z',entries:[{timestamp:'2026-07-25T12:00:01.000Z',level:'Information',category:'Application',message:'Session started.'},{timestamp:'2026-07-25T12:00:02.000Z',level:'Warning',category:'Modules',message:'Example warning.'},{timestamp:'2026-07-25T12:00:03.000Z',level:'Error',category:'Bridge',message:'Example error.'}]})}
    if(message.command==='settings.getSnapshot'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');if(Object.keys(message.payload).length!==0)return this.error(message,'InvalidPayload');return this.ok(message,this.settingsSnapshot())}
    if(message.command==='settings.setShowLogsInSidebar'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');if(Object.keys(message.payload).length!==1||typeof message.payload.showLogsInSidebar!=='boolean')return this.error(message,'InvalidPayload');this.showLogsInSidebar=message.payload.showLogsInSidebar;return this.ok(message,this.settingsSnapshot())}
    if(message.command==='settings.setMainWindowCloseBehavior'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');const value=message.payload.mainWindowCloseBehavior;if(Object.keys(message.payload).length!==1||value!=='Ask'&&value!=='MinimizeToNotificationArea'&&value!=='ExitApplication')return this.error(message,'InvalidPayload');this.mainWindowCloseBehavior=value;return this.ok(message,this.settingsSnapshot())}
    if(message.command==='settings.setStartupPresentationMode'){if(this.phase!=='Activated')return this.error(message,'BridgeNotActivated');const value=message.payload.startupPresentationMode;if(Object.keys(message.payload).length!==1||value!=='MainWindow'&&value!=='Minimized'&&value!=='FloatingBadge')return this.error(message,'InvalidPayload');this.startupPresentationMode=value;return this.ok(message,this.settingsSnapshot())}
    return this.error(message,'UnknownCommand')
  }
  subscribe(listener:(event:BridgeEvent)=>void){this.listeners.add(listener);return()=>this.listeners.delete(listener)} emit(event:BridgeEvent){this.listeners.forEach(x=>x(event))}
  dispose(){this.disposed=true;this.nonce=null;this.sessionToken=null;this.listeners.clear()}
  private randomToken(){return crypto.randomUUID().replaceAll('-','')+crypto.randomUUID().replaceAll('-','')}
  private settingsSnapshot(){return{generatedAt:'2026-07-25T12:00:00.000Z',language:{code:'en-US',displayName:'English'},showLogsInSidebar:this.showLogsInSidebar,mainWindowCloseBehavior:this.mainWindowCloseBehavior,closeBehaviorMessage:'The selected behavior applies the next time the main window is closed.',launchAtLogin:false,canConfigureLaunchAtLogin:true,startupPresentationMode:this.startupPresentationMode,startupBackend:'Registry Run',startupStatus:'Healthy',startupMessage:'Windows startup registration is healthy.'}}
  private moduleSnapshot():ModuleSnapshot{return{generatedAt:new Date().toISOString(),modules:this.modules.map(item=>({...item,errors:[...item.errors],permissions:[...item.permissions]}))}}
  private ok(r:BridgeRequest,payload:unknown):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:true,payload,error:null}}
  private error(r:BridgeRequest,code:string):BridgeResponse{return{protocolVersion,requestId:r.requestId,success:false,payload:{},error:{code,message:'Mock bridge rejected the request.'}}}
}
export const mockReadyPayload={assetBuildId,documentReadyState:'complete' as const,transportMode:'Mock' as const}
