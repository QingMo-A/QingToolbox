import { defineStore } from 'pinia'
import type { SettingsSnapshot } from '../contracts/settings'
export const useSettingsStore=defineStore('settings',{state:()=>({status:'idle' as 'idle'|'loading'|'ready'|'error',snapshot:null as SettingsSnapshot|null,generatedAt:null as string|null,errorMessage:''}),actions:{begin(){this.status='loading';this.errorMessage=''},complete(snapshot:SettingsSnapshot){this.snapshot=snapshot;this.generatedAt=snapshot.generatedAt;this.status='ready'},fail(error:unknown){this.status='error';this.errorMessage=error instanceof Error?error.message:String(error)}}})
