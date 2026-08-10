export type StartupAuthorizationState = 'NotEnabled'|'Enabled'|'ChangedNeedsConfirmation'|'Missing'|'Unavailable'
export type ModuleUpdateStatus = 'NotChecked'|'Checking'|'NotOfficial'|'NoPublishedRelease'|'UpToDate'|'UpdateAvailable'|'HostUpdateRequired'|'ModuleApiIncompatible'|'HostVersionIncompatible'|'LocalVersionNewer'|'InvalidLocalVersion'|'SourceUnavailable'|'SourceInvalid'|'DisabledByEnvironment'
export type ModuleDownloadStatus = 'NotDownloaded'|'ConfirmingMetadata'|'MetadataChanged'|'MetadataStale'|'Downloading'|'Verifying'|'Verified'|'AlreadyVerified'|'Cancelled'|'SizeMismatch'|'HashMismatch'|'SourceUnavailable'|'SourceInvalid'|'UntrustedRedirect'|'StorageUnavailable'|'Failed'|'DisabledByEnvironment'|'TransferTimedOut'
export interface ModuleSnapshotItem {
  id: string; displayName: string; displayDescription: string; version: string; author: string
  runtimeType: string; loadMode: string; runtimeState: string; isValid: boolean; errorCount: number
  errors: string[]; permissions: string[]; minimumHostVersion: string; isUserInstalled: boolean; canRemove: boolean
  canLoad: boolean; canActivate: boolean; canOpen: boolean; canDeactivate: boolean; canUnload: boolean; isBusy: boolean; isExecutionBlocked: boolean
  isStartupEnabled: boolean; startupAuthorizationState: StartupAuthorizationState; canChangeStartupAuthorization: boolean; isStartupAuthorizationBusy: boolean
  updateStatus: ModuleUpdateStatus; targetVersion: string|null; releaseNotes: string|null; isFromStaleCache: boolean
  canCheckForUpdate: boolean; isUpdateCheckBusy: boolean; canDownloadUpdate: boolean; downloadStatus: ModuleDownloadStatus
  isDownloadActive: boolean; downloadBytesReceived: number; downloadExpectedBytes: number; canInstallVerifiedUpdate: boolean
  /** Host-projected SVG icon. Older hosts may omit this optional field. */
  iconDataUrl?: string|null
}
export interface ModuleSnapshot { generatedAt: string; modules: ModuleSnapshotItem[] }
export interface ModuleImportResult {
  disposition: 'Imported'|'Cancelled'
  importedModuleId: string|null
  snapshot: ModuleSnapshot
}
export interface ModuleManagementResult {
  disposition: 'Succeeded'|'SucceededWithWarning'
  snapshot: ModuleSnapshot
}
export interface ModuleUpdateInstallResult {
  disposition: 'Installed'|'RolledBack'|'RecoveryRequired'
  sourceVersion: string
  targetVersion: string
  snapshot: ModuleSnapshot
}
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
const moduleIconDataPrefix = 'data:image/svg+xml;base64,'
export const maximumModuleIconBytes = 256 * 1024
export const maximumModuleIconDataUrlLength = moduleIconDataPrefix.length + Math.ceil(maximumModuleIconBytes / 3) * 4
export const isModuleIconDataUrl = (value: unknown): value is string => {
  if (typeof value !== 'string' || !value.startsWith(moduleIconDataPrefix) || value.length > maximumModuleIconDataUrlLength)
    return false
  const encoded = value.slice(moduleIconDataPrefix.length)
  if (encoded.length === 0 || encoded.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) return false
  const padding = encoded.endsWith('==') ? 2 : encoded.endsWith('=') ? 1 : 0
  const decodedBytes = encoded.length / 4 * 3 - padding
  return decodedBytes > 0 && decodedBytes <= maximumModuleIconBytes
}
const updateStatuses: ModuleUpdateStatus[] = ['NotChecked','Checking','NotOfficial','NoPublishedRelease','UpToDate','UpdateAvailable','HostUpdateRequired','ModuleApiIncompatible','HostVersionIncompatible','LocalVersionNewer','InvalidLocalVersion','SourceUnavailable','SourceInvalid','DisabledByEnvironment']
const downloadStatuses: ModuleDownloadStatus[] = ['NotDownloaded','ConfirmingMetadata','MetadataChanged','MetadataStale','Downloading','Verifying','Verified','AlreadyVerified','Cancelled','SizeMismatch','HashMismatch','SourceUnavailable','SourceInvalid','UntrustedRedirect','StorageUnavailable','Failed','DisabledByEnvironment','TransferTimedOut']
const nonNegativeFinite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0
const isItem = (value: any): value is ModuleSnapshotItem => !!value &&
  ['id','displayName','displayDescription','version','author','runtimeType','loadMode','runtimeState','minimumHostVersion'].every(key => typeof value[key] === 'string') &&
  typeof value.isValid === 'boolean' && Number.isInteger(value.errorCount) && value.errorCount >= 0 &&
  strings(value.errors) && strings(value.permissions) && typeof value.isUserInstalled === 'boolean' && typeof value.canRemove === 'boolean' &&
  typeof value.canLoad === 'boolean' && typeof value.canActivate === 'boolean' && typeof value.canOpen === 'boolean' && typeof value.canDeactivate === 'boolean' && typeof value.canUnload === 'boolean' &&
  typeof value.isBusy === 'boolean' && typeof value.isExecutionBlocked === 'boolean' && typeof value.isStartupEnabled === 'boolean' &&
  ['NotEnabled','Enabled','ChangedNeedsConfirmation','Missing','Unavailable'].includes(value.startupAuthorizationState) &&
  typeof value.canChangeStartupAuthorization === 'boolean' && typeof value.isStartupAuthorizationBusy === 'boolean' &&
  updateStatuses.includes(value.updateStatus) && (value.targetVersion === null || typeof value.targetVersion === 'string') &&
  (value.releaseNotes === null || typeof value.releaseNotes === 'string') && typeof value.isFromStaleCache === 'boolean' &&
  typeof value.canCheckForUpdate === 'boolean' && typeof value.isUpdateCheckBusy === 'boolean' &&
  typeof value.canDownloadUpdate === 'boolean' && downloadStatuses.includes(value.downloadStatus) &&
  typeof value.isDownloadActive === 'boolean' && nonNegativeFinite(value.downloadBytesReceived) &&
  nonNegativeFinite(value.downloadExpectedBytes) && typeof value.canInstallVerifiedUpdate === 'boolean' &&
  (value.iconDataUrl === undefined || value.iconDataUrl === null || isModuleIconDataUrl(value.iconDataUrl))
export const isModuleSnapshot = (value: any): value is ModuleSnapshot => !!value &&
  typeof value.generatedAt === 'string' && !Number.isNaN(Date.parse(value.generatedAt)) &&
  Array.isArray(value.modules) && value.modules.every(isItem)
export const isModuleImportResult = (value: any): value is ModuleImportResult => !!value &&
  typeof value === 'object' && Object.keys(value).length === 3 &&
  ['disposition', 'importedModuleId', 'snapshot'].every(key => Object.prototype.hasOwnProperty.call(value, key)) &&
  (value.disposition === 'Imported' || value.disposition === 'Cancelled') &&
  (value.importedModuleId === null || typeof value.importedModuleId === 'string') &&
  (value.disposition === 'Imported' ? typeof value.importedModuleId === 'string' && value.importedModuleId.length > 0 : value.importedModuleId === null) &&
  isModuleSnapshot(value.snapshot) &&
  (value.disposition !== 'Imported' || value.snapshot.modules.some((module: ModuleSnapshotItem) => module.id === value.importedModuleId))
export const isModuleManagementResult = (value: any): value is ModuleManagementResult => !!value &&
  typeof value === 'object' && Object.keys(value).length === 2 &&
  Object.prototype.hasOwnProperty.call(value, 'disposition') && Object.prototype.hasOwnProperty.call(value, 'snapshot') &&
  (value.disposition === 'Succeeded' || value.disposition === 'SucceededWithWarning') && isModuleSnapshot(value.snapshot)
export const isModuleUpdateInstallResult = (value: any): value is ModuleUpdateInstallResult => !!value &&
  typeof value === 'object' && Object.keys(value).length === 4 &&
  ['disposition','sourceVersion','targetVersion','snapshot'].every(key => Object.prototype.hasOwnProperty.call(value, key)) &&
  ['Installed','RolledBack','RecoveryRequired'].includes(value.disposition) &&
  typeof value.sourceVersion === 'string' && value.sourceVersion.length > 0 &&
  typeof value.targetVersion === 'string' && value.targetVersion.length > 0 && isModuleSnapshot(value.snapshot)
