export const protocolVersion = 1

export interface AppSnapshot {
  environmentKind: string
  environmentDisplayName: string
  hostVersion: string
  protocolVersion: number
  totalModuleCount: number
  validModuleCount: number
  runningModuleCount: number
  generatedAt: string
}

export interface BridgeRequest { protocolVersion: number; requestId: string; command: string; payload: object }
export interface BridgeError { code: string; message: string }
export interface BridgeResponse<T = unknown> { protocolVersion: number; requestId: string; success: boolean; payload: T; error: BridgeError | null }
export interface BridgeEvent<T = unknown> { protocolVersion: number; event: string; payload: T }
