import { defineStore } from 'pinia'
export type ThemeMode='system'|'light'|'dark'
export const useThemeStore=defineStore('theme',{state:()=>({mode:(localStorage.getItem('qing.theme') as ThemeMode)||'system'}),actions:{set(mode:ThemeMode){this.mode=mode;localStorage.setItem('qing.theme',mode);document.documentElement.dataset.theme=mode}}})
