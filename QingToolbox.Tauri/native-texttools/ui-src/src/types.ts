export interface ModuleContext {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export interface State {
  input: string
  output: string
  status: string
  error: string | null
}
