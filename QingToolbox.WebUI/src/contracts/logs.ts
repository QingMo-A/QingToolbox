import { isRecord } from './app'
export type LogLevel = 'Information'|'Warning'|'Error'
export interface LogSnapshotEntry { timestamp:string;level:LogLevel;category:string;message:string }
export interface LogSnapshot { generatedAt:string;entries:LogSnapshotEntry[] }
const date=(value:unknown):value is string=>typeof value==='string'&&!Number.isNaN(Date.parse(value))
const entry=(value:unknown):value is LogSnapshotEntry=>isRecord(value)&&date(value.timestamp)&&(value.level==='Information'||value.level==='Warning'||value.level==='Error')&&typeof value.category==='string'&&typeof value.message==='string'
export const isLogSnapshot=(value:unknown):value is LogSnapshot=>isRecord(value)&&date(value.generatedAt)&&Array.isArray(value.entries)&&value.entries.every(entry)
