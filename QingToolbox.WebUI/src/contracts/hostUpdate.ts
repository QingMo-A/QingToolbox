import { isRecord } from './app'

export interface HostUpdateSnapshot {
  generatedAt:string; state:string; currentVersion:string; latestVersion:string; publishedAt:string; lastChecked:string;
  summary:string; showBanner:boolean; downloadState:string; bytesReceived:number; expectedBytes:number; downloadError:string;
  canCheck:boolean; canDownload:boolean; canCancelDownload:boolean; canInstall:boolean; installationSupported:boolean; installMessage:string
}

export const isHostUpdateSnapshot = (value:unknown):value is HostUpdateSnapshot => isRecord(value) &&
  ['generatedAt','state','currentVersion','latestVersion','publishedAt','lastChecked','summary','downloadState','downloadError','installMessage']
    .every(key => typeof value[key] === 'string') &&
  ['showBanner','canCheck','canDownload','canCancelDownload','canInstall','installationSupported']
    .every(key => typeof value[key] === 'boolean') &&
  Number.isSafeInteger(value.bytesReceived) && Number(value.bytesReceived) >= 0 &&
  Number.isSafeInteger(value.expectedBytes) && Number(value.expectedBytes) >= 0
