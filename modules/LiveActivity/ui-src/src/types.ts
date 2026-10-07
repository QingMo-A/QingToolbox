// Wire types for the Live Activity module.
//
// These mirror the Rust payloads exactly. Every field is optional-friendly on
// the read path — the island must render something useful when a provider has
// nothing to say — but the settings type is strict, because a settings patch
// with a typo should be rejected rather than silently ignored.

export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type ProviderKind = 'mock' | 'codex' | 'media' | 'transfer'

export type ActivityState =
  | 'running'
  | 'waiting'
  | 'paused'
  | 'success'
  | 'failed'
  | 'idle'
  | 'cancelled'
  | 'unknown'

export type ActivityProgress = {
  value: number
  total: number | null
}

export type ActivityAction = {
  id: string
  label: string
}

export type LiveActivity = {
  id: string
  provider: ProviderKind
  kind: string
  title: string
  subtitle: string | null
  state: ActivityState
  priority: number | null
  progress: ActivityProgress | null
  startedAt: number | null
  updatedAt: number
  details: Record<string, string>
  actions: ActivityAction[]
  metadata: Record<string, unknown>
}

export type ProviderHealth = 'connected' | 'disconnected' | 'unavailable' | 'disabled'

export type ProviderStatus = {
  kind: ProviderKind
  health: ProviderHealth
  detail?: string
  activityCount: number
}

export type RateLimitWindow = {
  usedFraction: number | null
  windowMinutes: number | null
  resetsAt: number | null
}
export type CodexAccount = {
  limits: { primary: RateLimitWindow | null; secondary: RateLimitWindow | null } | null
  updatedAtMs: number | null
  attemptAtMs: number | null
  error: string | null
  pollIntervalSeconds: number
}

export type Anchor = 'topLeft' | 'topCenter' | 'topRight' | 'bottomLeft' | 'bottomCenter' | 'bottomRight'
export type MonitorStrategy = 'primary' | 'active'
export type FullscreenPolicy = 'always' | 'hide' | 'important'
export type SurfaceStyle = 'solid' | 'translucent' | 'frosted'
export type RgbColor = { r: number; g: number; b: number }

export type Settings = {
  version: number
  enabled: boolean
  anchor: Anchor
  monitorStrategy: MonitorStrategy
  fullscreenPolicy: FullscreenPolicy
  scale: number
  compactWidth: number
  offsetX: number
  offsetY: number
  peekOnHover: boolean
  showClock: boolean
  showSeconds: boolean
  clock24Hour: boolean
  customText: string
  surfaceStyle: SurfaceStyle
  backgroundOpacity: number
  backgroundColor: RgbColor
  codexEnabled: boolean
  codexIdleShutdownSeconds: number
}

/** A partial update. Only the fields the user touched are sent. */
export type SettingsPatch = Partial<Omit<Settings, 'version'>>

export type IslandStateName = 'dormant' | 'compact' | 'peek' | 'expanded'

export type IslandView = {
  state: IslandStateName
  suppressed: boolean
  previewActive: boolean
  focus: LiveActivity | null
  stack: LiveActivity[]
  overflow: number
  account: string | null
  accountHeader: string | null
  ambient: { clock: string | null; date: string; text: string } | null
}

export type DiagnosticsEntry = {
  atMs: number
  level: 'information' | 'warning' | 'error'
  scope: string
  message: string
}

export type ModuleState = {
  active: boolean
  settings: Settings
  island: IslandView
  counts: {
    activities: number
    revision: number
    dropped: number
  }
  providers: ProviderStatus[]
  codexAccount: CodexAccount
  overlay: {
    failure: string | null
    visible: boolean
    state: IslandStateName | null
    hwnd: number
    renderCount: number
    material: SurfaceStyle | null
    materialFallback: string | null
    glassSamples: number | null
    glassSampleMicros: number | null
  }
  uptimeSeconds: number
  platform: 'windows' | 'unsupported'
}

export type Diagnostics = {
  entries: DiagnosticsEntry[]
  totalRecorded: number
  providers: ProviderStatus[]
}

export type MockScenario = 'working' | 'waiting' | 'success' | 'failed' | 'progress' | 'all'
