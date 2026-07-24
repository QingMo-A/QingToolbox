import { defineStore } from 'pinia'
export const useToastStore=defineStore('toast',{state:()=>({message:'',kind:'info' as 'info'|'success'|'error',visible:false,timer:0}),actions:{show(message:string,kind:'info'|'success'|'error'='info'){this.message=message;this.kind=kind;this.visible=true;window.clearTimeout(this.timer);this.timer=window.setTimeout(()=>this.visible=false,3200)}}})
