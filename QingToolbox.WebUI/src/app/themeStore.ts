import { defineStore } from 'pinia'
export type ThemeMode='system'|'light'|'dark'

export function notifyHostTheme(mode: ThemeMode) {
  const webview = (window as typeof window & { chrome?: { webview?: { postMessage(message: unknown): void } } }).chrome?.webview
  if (webview) webview.postMessage({ kind: 'qing.ui.theme', mode })
}

export const useThemeStore=defineStore('theme',{state:()=>({mode:(localStorage.getItem('qing.theme') as ThemeMode)||'system'}),actions:{set(mode:ThemeMode){this.mode=mode;localStorage.setItem('qing.theme',mode);document.documentElement.dataset.theme=mode;notifyHostTheme(mode)}}})
