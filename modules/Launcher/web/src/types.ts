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
