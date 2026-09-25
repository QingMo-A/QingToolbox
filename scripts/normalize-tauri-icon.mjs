// Tauri 2 reads the first ICO directory entry as its runtime window icon.
// Keep every Windows size, but put the largest first to avoid scaling 16px
// pixels up for the taskbar. Image payloads are preserved byte-for-byte.
import { readFileSync, writeFileSync } from 'node:fs'
const file = new URL('../QingToolbox.Tauri/src-tauri/icons/icon.ico', import.meta.url)
const bytes = readFileSync(file)
if (bytes.readUInt16LE(0) !== 0 || bytes.readUInt16LE(2) !== 1) throw new Error('Invalid ICO header')
const count = bytes.readUInt16LE(4)
const entries = Array.from({ length: count }, (_, i) => Buffer.from(bytes.subarray(6 + i * 16, 22 + i * 16)))
entries.sort((a, b) => (b[0] || 256) * (b[1] || 256) - (a[0] || 256) * (a[1] || 256))
entries.forEach((entry, i) => entry.copy(bytes, 6 + i * 16))
writeFileSync(file, bytes)
console.log(`Preserved ${count} ICO frames; runtime icon starts at ${entries[0][0] || 256}px`)
