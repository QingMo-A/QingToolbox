import { defineStore } from 'pinia'
import type { HostUpdateSnapshot } from '../contracts/hostUpdate'

export const useHostUpdateStore=defineStore('hostUpdate',{
  state:()=>({snapshot:null as HostUpdateSnapshot|null,busy:false,error:''}),
  actions:{complete(snapshot:HostUpdateSnapshot){this.snapshot=snapshot;this.error=''},fail(error:unknown){this.error=error instanceof Error?error.message:String(error)}}
})
