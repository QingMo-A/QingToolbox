export const protocolVersion = 2
declare const __QING_ASSET_BUILD_ID__: string
export const assetBuildId = __QING_ASSET_BUILD_ID__

export interface AppSnapshot { environmentKind:string; environmentDisplayName:string; hostVersion:string; protocolVersion:number; totalModuleCount:number; validModuleCount:number; runningModuleCount:number; generatedAt:string }
export interface ReadyPayload { assetBuildId:string; documentReadyState:'complete'; transportMode:'WebView'|'Mock' }
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
