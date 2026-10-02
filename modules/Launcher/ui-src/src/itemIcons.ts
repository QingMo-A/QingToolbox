import { invokeModule } from './bridge'

// Per-module-window cache shared by grid, folders, recent and drag ghosts.
// Small concurrency avoids flooding the module IPC when a large grid appears.
const cache = new Map<string, Promise<string | null>>()
const resolved = new Map<string, string | null>()
export function cachedItemIcon(key: string | null): string | null | undefined {
  return key ? (key.startsWith('launcher-icon:') ? resolved.get(key) : key) : null
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
    queue.push(() => {
      void invokeModule<{ dataUrl: string | null }>('getItemIcon', { id: key.slice('launcher-icon:'.length) })
        .then(value => {
          const url = value.dataUrl?.startsWith('data:image/png;base64,') ? value.dataUrl : null
          resolved.set(key, url); resolve(url)
        }, () => { cache.delete(key); resolve(null) })
        .finally(() => { running--; pump() })
    })
  })
  cache.set(key, promise)
  pump()
  return promise
}
