import { isRecord } from './app'

export interface OfficialModule {
  id: string; name: Record<string, string>; description: Record<string, string>
  version: string | null; apiVersion: number | null; size: number; canDownload: boolean; unavailableReason: string
}
export interface ModuleRepositoryDownload {
  jobId: number; moduleId: string; name: string; status: '' | 'Downloading' | 'Verifying' | 'Installing' | 'Completed' | 'Failed' | 'InstallFailed'
  bytesReceived: number; expectedBytes: number; savedPath: string; error: string
}
const count = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0
const translations = (v: unknown, limit: number) => isRecord(v) && ['en-US', 'zh-CN'].every(key => typeof v[key] === 'string' && v[key].length > 0 && v[key].length <= limit)
const reasons = ['', 'NoRelease', 'IncompatibleApi', 'IncompatibleHost', 'IncompatiblePlatform']
export function isOfficialModules(value: unknown): value is OfficialModule[] {
  return Array.isArray(value) && value.length <= 128 && new Set(value.map(v => v?.id)).size === value.length && value.every(v =>
    isRecord(v) && typeof v.id === 'string' && /^qing\.[a-z0-9-]{1,64}$/.test(v.id)
    && translations(v.name, 256) && translations(v.description, 4096)
    && (v.version === null || (typeof v.version === 'string' && v.version.length <= 128))
    && (v.apiVersion === null || (count(v.apiVersion) && v.apiVersion > 0))
    && count(v.size) && v.size <= 256 * 1024 * 1024 && typeof v.canDownload === 'boolean'
    && typeof v.unavailableReason === 'string' && reasons.includes(v.unavailableReason)
    && (!v.canDownload || (v.version !== null && v.apiVersion !== null && v.size > 0 && v.unavailableReason === '')))
}
export function isModuleRepositoryDownload(value: unknown): value is ModuleRepositoryDownload {
  return isRecord(value) && count(value.jobId) && typeof value.moduleId === 'string' && typeof value.name === 'string'
    && typeof value.status === 'string' && ['', 'Downloading', 'Verifying', 'Installing', 'Completed', 'Failed', 'InstallFailed'].includes(value.status)
    && count(value.bytesReceived) && count(value.expectedBytes) && value.bytesReceived <= value.expectedBytes
    && value.expectedBytes <= 256 * 1024 * 1024 && typeof value.savedPath === 'string' && typeof value.error === 'string'
}
export const repositoryDownloadActive = (value: ModuleRepositoryDownload | null) => value?.status === 'Downloading' || value?.status === 'Verifying' || value?.status === 'Installing'
