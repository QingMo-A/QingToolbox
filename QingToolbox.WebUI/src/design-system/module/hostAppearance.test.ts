import { beforeEach, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { adoptHostAppearance, applyAppearance } from './hostAppearance'

describe('independently packaged module design system', () => {
  beforeEach(() => {
    document.documentElement.removeAttribute('data-appearance-preset')
    document.documentElement.removeAttribute('data-theme')
    document.documentElement.classList.remove('q-module-document')
  })

  it('uses the host projection before rendering without duplicate context IPC', async () => {
    applyAppearance({ appearancePreset: 'greenline', theme: 'dark' })
    const load = vi.fn()
    await adoptHostAppearance(load)
    expect(load).not.toHaveBeenCalled()
    expect(document.documentElement.classList.contains('q-module-document')).toBe(true)
    expect(document.documentElement.dataset).toMatchObject({ appearancePreset: 'greenline', theme: 'dark' })
  })

  it('falls back to context for standalone previews / older hosts', async () => {
    await adoptHostAppearance(async () => ({ appearancePreset: 'qing-nova', theme: 'light' }))
    expect(document.documentElement.dataset).toMatchObject({ appearancePreset: 'qing-nova', theme: 'light' })
  })

  it('does not prevent mounting when context is unavailable', async () => {
    await adoptHostAppearance(async () => { throw new Error('IPC unavailable') })
    expect(document.documentElement.dataset).toMatchObject({ appearancePreset: 'qing-default', theme: 'system' })
  })

  it('normalizes invalid projections and follows later host updates', async () => {
    document.documentElement.dataset.theme = 'invalid'
    await adoptHostAppearance(async () => ({ appearancePreset: 42, theme: 'invalid' }))
    expect(document.documentElement.dataset).toMatchObject({ appearancePreset: 'qing-default', theme: 'system' })
    applyAppearance({ appearancePreset: 'aurora-flow', theme: 'dark' })
    expect(document.documentElement.dataset).toMatchObject({ appearancePreset: 'aurora-flow', theme: 'dark' })
  })

  it('exports existing primitives rather than shell stores or copied components', () => {
    const entry = readFileSync(resolve('src/design-system/module/index.ts'), 'utf8')
    for (const component of ['QButton', 'QIconButton', 'QIcon', 'QModal', 'QModalInput', 'QModalLabel']) {
      expect(entry).toContain(`../components/${component}.vue`)
    }
    expect(entry).not.toMatch(/from ['"].*(?:app\/|router|Store|pinia)/)
    const css = readFileSync(resolve('src/design-system/module/module.css'), 'utf8')
    expect(css).toContain("@import '../tokens/tokens.css'")
    expect(css).toContain("@import '../tokens/appearancePresets.css'")
    expect(css).not.toContain('main.css')
    for (const module of ['launcher', 'pdf', 'powerguard', 'screenpin', 'texttools', 'windowtopmost']) {
      const root = resolve(`../QingToolbox.Tauri/native-${module}/ui-src`)
      expect(readFileSync(resolve(root, 'src/main.ts'), 'utf8')).toContain('design-system/module/module.css')
      expect(readFileSync(resolve(root, 'src/App.vue'), 'utf8')).toContain('design-system/module')
      expect(readFileSync(resolve(root, 'vite.config.ts'), 'utf8')).toContain("dedupe: ['vue']")
    }
  })
})
