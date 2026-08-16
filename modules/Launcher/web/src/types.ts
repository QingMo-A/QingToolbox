export type Presentation = { appearancePresetId: string; languageCode: string }
export type Item = { id: string; name: string; iconKey: string | null; lastLaunchedAt: string | null; source?: 'custom' | 'desktop' }
export type Hotkey = { ctrl: boolean; alt: boolean; shift: boolean; win: boolean; virtualKey: number; keyLabel: string }
export type State = {
  sortMode: 'custom' | 'alphabetical' | 'desktop'
  items: Item[]
  recent: Item[]
  hotkey: Hotkey
  hotkeyStatus: 'Registered' | 'Conflict' | 'Inactive' | string
  active: boolean
}
export type DropResult = { added: string[]; skipped: string[] }
export type SearchMode = 'normal' | 'everything-all' | 'everything-file' | 'everything-directory'
export type ParsedSearch = { mode: SearchMode; query: string }
export type EverythingResult = { id: string; name: string; parentPath: string; type: 'file' | 'directory' }
export type EverythingSearchResponse = { requestId: number; status: 'Loading' | 'Ready' | 'Error'; error: string | null; stale: boolean; results: EverythingResult[] }
