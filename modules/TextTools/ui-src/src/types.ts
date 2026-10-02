export interface ModuleContext {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  /** Appearances the shell can be drawn in. Absent from hosts that predate module theming. */
  appearancePreset?: string
  theme?: string
  operations: string[]
}

export interface State {
  input: string
  output: string
  status: string
  error: string | null
}
