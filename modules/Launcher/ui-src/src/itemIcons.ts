import { invokeModule } from './bridge'
import { ref } from 'vue'

// Per-module-window cache shared by grid, folders, recent and drag ghosts.
// Small concurrency avoids flooding the module IPC when a large grid appears.
const cache = new Map<string, Promise<string | null>>()
const resolved = new Map<string, { url: string | null; expires: number }>()
export const iconCacheEpoch = ref(0)
let enabled = true
let generation = 0
export function resetIconCache(nextEnabled = enabled, reload = true): void {
  enabled = nextEnabled
  generation++
  cache.clear()
  resolved.clear()
  if (reload) iconCacheEpoch.value++
}
export function cachedItemIcon(key: string | null): string | null | undefined {
  if (!key) return null
  if (!key.startsWith('launcher-icon:')) return key
  const entry = resolved.get(key)
  if (entry && entry.expires > Date.now()) return entry.url
  if (entry) { resolved.delete(key); cache.delete(key) }
  return undefined
}
const queue: Array<() => void> = []
let running = 0
function pump(): void {
  while (running < 2 && queue.length) { running++; queue.shift()!() }
}
export function loadItemIcon(key: string | null): Promise<string | null> {
  if (!key) return Promise.resolve(null)
  if (!key.startsWith('launcher-icon:')) return Promise.resolve(key)
  const existing = cache.get(key)
  if (existing) return existing
  const promise = new Promise<string | null>(resolve => {
    const epoch = generation
    queue.push(() => {
      if (epoch !== generation) { resolve(null); running--; pump(); return }
      void invokeModule<{ dataUrl: string | null }>('getItemIcon', { id: key.slice('launcher-icon:'.length) })
        .then(value => {
          const url = value.dataUrl?.startsWith('data:image/png;base64,') ? value.dataUrl : null
          if (epoch === generation) {
            if (enabled && url) {
              if (resolved.size >= 512) { const oldest = resolved.keys().next().value; if (oldest) { resolved.delete(oldest); cache.delete(oldest) } }
              resolved.set(key, { url, expires: Date.now() + 600_000 })
            }
            else cache.delete(key)
            resolve(url)
          } else resolve(null)
        }, () => { if (epoch === generation) cache.delete(key); resolve(null) })
        .finally(() => { running--; pump() })
    })
  })
  cache.set(key, promise)
  pump()
  return promise
}
