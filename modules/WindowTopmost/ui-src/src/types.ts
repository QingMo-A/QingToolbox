export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type WindowRecord = {
  id: string
  title: string
  processName: string
  processId: number
  handleText: string
  isTopmost: boolean
}

export type WindowState = {
  windows: WindowRecord[]
  selectedWindowId: string | null
  status: string
  error: string | null
}
