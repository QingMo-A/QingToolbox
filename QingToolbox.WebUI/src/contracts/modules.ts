import { isRecord } from './app'

export interface ModuleSnapshotItem { id:string;displayName:string;displayDescription:string;version:string;author:string;runtimeType:string;loadMode:string;runtimeState:string;isValid:boolean;errorCount:number;errors:string[];permissions:string[];minimumHostVersion:string;isUserInstalled:boolean }
export interface ModuleSnapshot { generatedAt:string;modules:ModuleSnapshotItem[] }

const strings=(value:unknown):value is string[]=>Array.isArray(value)&&value.every(item=>typeof item==='string')
const item=(value:unknown):value is ModuleSnapshotItem=>isRecord(value)&&
  ['id','displayName','displayDescription','version','author','runtimeType','loadMode','runtimeState','minimumHostVersion'].every(key=>typeof value[key]==='string')&&
  typeof value.isValid==='boolean'&&Number.isInteger(value.errorCount)&&Number(value.errorCount)>=0&&strings(value.errors)&&strings(value.permissions)&&typeof value.isUserInstalled==='boolean'
export function isModuleSnapshot(value:unknown):value is ModuleSnapshot{return isRecord(value)&&typeof value.generatedAt==='string'&&!Number.isNaN(Date.parse(value.generatedAt))&&Array.isArray(value.modules)&&value.modules.every(item)}
