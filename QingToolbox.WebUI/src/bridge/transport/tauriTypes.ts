export type ModuleIssue = { code: string; message: string }
export type ModuleSummary = { id: string; name: string; description: string | null; version: string; author: string | null; uiKind: string | null; runtimeType: string | null; runtimeIsolation: string | null; source: 'bundled' | 'user'; iconDataUrl: string | null; valid: boolean; issues: ModuleIssue[] }
export type ModuleListPayload = { modules: ModuleSummary[]; roots: unknown[]; scannedAtUnixMs: number }
export type ModuleRuntimeSnapshot = { moduleId: string; state: 'notStarted' | 'starting' | 'running' | 'stopped' | 'failed'; generation: number; lastError: string | null }
export type SettingsSnapshot = { language: string; appearancePresetId: string; closeBehavior: string; startupPresentation: string; toggleHotkey: string; launchAtLogin: boolean; showLogsInSidebar: boolean; recentModuleIds: string[]; startupModuleIds: string[]; settingsSchemaVersion: number }
export type SessionLogEntry = { timestamp: string; level: 'Information' | 'Warning' | 'Error'; category: string; message: string }
export type SessionLogSnapshot = { generatedAt: string; entries: SessionLogEntry[] }
