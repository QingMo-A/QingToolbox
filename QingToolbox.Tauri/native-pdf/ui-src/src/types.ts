export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type PdfFile = {
  id: string
  name: string
  directory: string
  pages: number
  size: number
}

export type PdfResult = {
  id: string
  operation: string
  outputDirectory: string
  files: string[]
}

export type PdfState = {
  mergeFiles: PdfFile[]
  source: PdfFile | null
  busy: boolean
  activeOperation: string | null
  runtimeStatus: string
  message: string | null
  result: PdfResult | null
}
