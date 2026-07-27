import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import App from './App.vue'

const wrappers: VueWrapper[] = []
afterEach(() => wrappers.splice(0).forEach(wrapper => wrapper.unmount()))

function app() {
  const pinia = createPinia(); setActivePinia(pinia)
  const preventDefault = vi.fn()
  const wrapper = mount(App, {
    global: {
      plugins: [pinia],
      provide: { settingsClient: { getSnapshot: vi.fn() } },
      stubs: {
        RouterView: { template: '<div />' },
        QToast: { template: '<div />' },
        QSidebarLayout: { emits: ['openCommandPalette'], template: '<button class="open" @click="$emit(\'openCommandPalette\')">Open</button><slot />' },
        QCommandPalette: { props: ['open'], emits: ['close'], template: '<div class="palette" :data-open="open"><button @click="$emit(\'close\')">Close</button></div>' },
      },
    },
  })
  wrappers.push(wrapper)
  return { wrapper, preventDefault }
}

describe('App Quick Open integration', () => {
  it.each([
    { ctrlKey: true, metaKey: false },
    { ctrlKey: false, metaKey: true },
  ])('opens with the platform shortcut', async modifiers => {
    const { wrapper, preventDefault } = app()
    window.dispatchEvent(Object.assign(new Event('keydown'), { ...modifiers, altKey: false, key: 'K', preventDefault }))
    await wrapper.vm.$nextTick()
    expect(preventDefault).toHaveBeenCalled()
    expect(wrapper.get('.palette').attributes('data-open')).toBe('true')
  })

  it('ignores plain K and Alt+Ctrl+K', async () => {
    const { wrapper } = app()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k' }))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, altKey: true }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.palette').attributes('data-open')).toBe('false')
  })

  it('opens from the sidebar and closes from the palette', async () => {
    const { wrapper } = app()
    await wrapper.get('.open').trigger('click')
    expect(wrapper.get('.palette').attributes('data-open')).toBe('true')
    await wrapper.get('.palette button').trigger('click')
    expect(wrapper.get('.palette').attributes('data-open')).toBe('false')
  })

  it('removes the global listener when unmounted', () => {
    const remove = vi.spyOn(window, 'removeEventListener')
    const { wrapper } = app()
    wrapper.unmount()
    expect(remove).toHaveBeenCalledWith('keydown', expect.any(Function))
    remove.mockRestore()
  })
})
