export type Target = 'png' | 'jpg' | 'webp' | 'bmp' | 'mp3' | 'wav' | 'flac' | 'm4a' | 'word-pdf' | 'pdf-word'
export type Settings = { ffmpegPath: string; libreOfficePath: string; outputDirectory: string; wordEngine: 'auto' | 'word' | 'libreoffice' }
export type FileItem = { id: string; name: string; size: number; status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'; progress: number | null; message: string; outputPath: string | null }
export type State = { files: FileItem[]; busy: boolean; cancelling: boolean; settings: Settings; engines: { wordAvailable: boolean; ffmpegPath: string | null; libreOfficePath: string | null }; message: string }
export type ModuleContext = { moduleId: string; name: string; version: string; iconDataUrl: string | null; protocolVersion: number; appearancePreset?: string; theme?: 'system' | 'light' | 'dark'; operations: string[] }
