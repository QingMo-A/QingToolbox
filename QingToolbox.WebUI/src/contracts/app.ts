export const protocolVersion = 4
declare const __QING_ASSET_BUILD_ID__: string
export const assetBuildId = __QING_ASSET_BUILD_ID__

export interface AppSnapshot { environmentKind:string; environmentDisplayName:string; hostVersion:string; protocolVersion:number; totalModuleCount:number; validModuleCount:number; runningModuleCount:number; generatedAt:string }
export interface ReadyPayload { assetBuildId:string; documentReadyState:'complete'; transportMode:'WebView'|'Mock' }
export interface ReadyChallenge { activationNonce:string; snapshot:AppSnapshot }
export interface PingResponse { pong:true; hostTime:string; sessionToken?:string|null; activated:true }
export interface BridgeRequest { protocolVersion:number; requestId:string; command:string; payload:Record<string, unknown> }
export interface BridgeError { code:string; message:string }
export interface BridgeResponse<T=unknown> { protocolVersion:number; requestId:string; success:boolean; payload:T; error:BridgeError|null }
export interface BridgeEvent<T=unknown> { protocolVersion:number; event:string; payload:T }

export function isRecord(value:unknown): value is Record<string,unknown> { return value !== null && typeof value === 'object' && !Array.isArray(value) }
export function isBridgeResponse(value:unknown): value is BridgeResponse {
  if (!isRecord(value) || value.protocolVersion !== protocolVersion || typeof value.requestId !== 'string' || typeof value.success !== 'boolean') return false
  return value.success ? 'payload' in value : isRecord(value.error) && typeof value.error.code === 'string' && typeof value.error.message === 'string'
}
export function isBridgeEvent(value:unknown): value is BridgeEvent {
  return isRecord(value) && value.protocolVersion === protocolVersion && typeof value.event === 'string' && 'payload' in value
}
const validDate=(value:unknown)=>typeof value==='string'&&!Number.isNaN(Date.parse(value))
const count=(value:unknown)=>typeof value==='number'&&Number.isFinite(value)&&Number.isInteger(value)&&value>=0
export function isAppSnapshot(value:unknown):value is AppSnapshot{return isRecord(value)&&typeof value.environmentKind==='string'&&typeof value.environmentDisplayName==='string'&&typeof value.hostVersion==='string'&&value.protocolVersion===protocolVersion&&count(value.totalModuleCount)&&count(value.validModuleCount)&&count(value.runningModuleCount)&&validDate(value.generatedAt)}
export function isReadyChallenge(value:unknown):value is ReadyChallenge{return isRecord(value)&&typeof value.activationNonce==='string'&&value.activationNonce.length>=32&&value.activationNonce.length<=256&&isAppSnapshot(value.snapshot)}
const token=(value:unknown)=>typeof value==='string'&&value.length>=64&&value.length<=256
export function isPingResponse(value:unknown):value is PingResponse{return isRecord(value)&&value.pong===true&&value.activated===true&&validDate(value.hostTime)&&(value.sessionToken===undefined||value.sessionToken===null||token(value.sessionToken))}
