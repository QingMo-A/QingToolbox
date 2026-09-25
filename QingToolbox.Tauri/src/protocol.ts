/** Keep this shape in lockstep with src-tauri/src/protocol.rs. */
export const PROTOCOL_VERSION = 1 as const

export type ProtocolEnvelope<T> = {
  protocolVersion: number
  messageType: string
  requestId: string
  payload: T
  error?: ProtocolError
}

export type ProtocolError = {
  code: string
  message: string
  details?: unknown
}

export type ModuleIssue = {
  code: string
  message: string
}

export type ModuleSummary = {
  id: string
  name: string
  description: string | null
  version: string
  author: string | null
  uiKind: string | null
  runtimeType: string | null
  runtimeIsolation: string | null
  source: 'bundled' | 'user'
  iconDataUrl: string | null
  valid: boolean
  issues: ModuleIssue[]
}

export type ModuleRootSummary = {
  source: 'bundled' | 'user'
  label: string
  available: boolean
  error: string | null
}

export type ModuleListPayload = {
  modules: ModuleSummary[]
  roots: ModuleRootSummary[]
  scannedAtUnixMs: number
}

export type ModuleRuntimeState = 'notStarted' | 'starting' | 'loaded' | 'deactivated' | 'running' | 'stopped' | 'failed'

export type ModuleRuntimeSnapshot = {
  moduleId: string
  state: ModuleRuntimeState
  generation: number
  lastError: string | null
}

export type HostInfo = {
  productName: string
  version: string
  backend: 'rust' | 'browser-preview'
  protocolVersion: string
}

export type SettingsSnapshot = {
  settingsSchemaVersion: number
  language: 'system' | 'zh-CN' | 'en-US' | string
  appearancePresetId: string
  closeBehavior: 'ask' | 'tray' | 'exit' | string
  startupPresentation: 'main' | 'minimized' | 'tray' | string
  toggleHotkey: string
  launchAtLogin: boolean
  showLogsInSidebar: boolean
  recentModuleIds: string[]
}

export type SettingsUpdate = {
  language?: string
  appearancePresetId?: string
  closeBehavior?: string
  startupPresentation?: string
  toggleHotkey?: string
  launchAtLogin?: boolean
  showLogsInSidebar?: boolean
  recentModuleIds?: string[]
}

export function isProtocolEnvelope<T>(
  value: unknown,
  messageType: string,
): value is ProtocolEnvelope<T> {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Partial<ProtocolEnvelope<T>>
  return (
    candidate.protocolVersion === PROTOCOL_VERSION &&
    candidate.messageType === messageType &&
    typeof candidate.requestId === 'string' &&
    candidate.requestId.length > 0 &&
    'payload' in candidate
  )
}
