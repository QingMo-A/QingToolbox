export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type Settings = {
  guardEnabled: boolean
  startupGraceSeconds: number
  offlineConfirmationSeconds: number
  shutdownCountdownSeconds: number
  recoveryConfirmationSeconds: number
  showRecoveryNotification: boolean
}

export type EndpointResult = {
  name: string
  succeeded: boolean
  elapsedMillis: number
  failureCategory: string | null
}

export type EventItem = {
  timestampEpochMillis: number
  kind: string
  detail: string | null
}

export type PowerState = {
  settings: Settings
  guardEnabled: boolean
  state: string
  isOnline: boolean
  lastProbeEpochMillis: number | null
  lastSuccessfulProbeEpochMillis: number | null
  consecutiveProbeFailures: number
  countdownRemainingSeconds: number | null
  testRemainingSeconds: number | null
  isSuppressed: boolean
  lastProbe: EndpointResult[]
  events: EventItem[]
}
