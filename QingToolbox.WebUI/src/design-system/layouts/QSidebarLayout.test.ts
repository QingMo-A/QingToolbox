import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import QSidebarLayout from './QSidebarLayout.vue'

describe('QSidebarLayout icons', () => {
  it('uses local SVG navigation icons and exposes the pin action', () => {
    const wrapper = mount(QSidebarLayout, { global: { stubs: { RouterLink: { template: '<a><slot/></a>' } } } })
    expect(wrapper.findAll('nav svg').length).toBeGreaterThanOrEqual(4)
    expect(wrapper.text()).not.toMatch(/[⌂▦⌁⌖]/)
    expect(wrapper.get('button').attributes('aria-label')).toBe('Pin sidebar')
    expect(wrapper.get('button').attributes('title')).toBe('Pin sidebar')
  })
})
