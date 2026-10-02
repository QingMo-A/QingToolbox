export type ModuleContext = {
  moduleId: string
  name: string
  version: string
  iconDataUrl: string | null
  protocolVersion: number
  operations: string[]
}

export type DisplayBounds = { x: number; y: number; width: number; height: number }
export type Pin = { id: string; dataUrl: string; x: number; y: number; width: number; height: number }
export type PinState = { pins: Pin[]; status: string; error: string | null; displayBounds: DisplayBounds }
