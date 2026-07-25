import { defineStore } from 'pinia'
import type { LogSnapshot,LogSnapshotEntry } from '../contracts/logs'
export const useLogStore=defineStore('logs',{state:()=>({status:'idle' as 'idle'|'loading'|'ready'|'error',entries:[] as LogSnapshotEntry[],generatedAt:null as string|null,errorMessage:''}),actions:{begin(){this.status='loading';this.errorMessage=''},complete(snapshot:LogSnapshot){this.entries=snapshot.entries;this.generatedAt=snapshot.generatedAt;this.status='ready'},fail(error:unknown){this.status='error';this.errorMessage=error instanceof Error?error.message:String(error)}}})
