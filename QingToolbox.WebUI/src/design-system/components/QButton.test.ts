import { describe, expect, it } from 'vitest'
import { h } from 'vue'
import { mount } from '@vue/test-utils'
import QButton from './QButton.vue'
import QIconButton from './QIconButton.vue'

describe('shared button states', () => {
  it('projects every button variant and keeps loading non-activatable', () => {
    const wrapper = mount(QButton, { props: { variant: 'danger', loading: true }, slots: { default: 'Remove' } })
    const button = wrapper.get('button')
    expect(button.classes()).toEqual(expect.arrayContaining(['q-button', 'is-danger', 'is-loading']))
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.attributes('aria-busy')).toBe('true')
    expect(button.find('.q-button-spinner').exists()).toBe(true)
    expect(button.text()).toContain('Remove')
  })

  it('supports disabled icon-only actions without losing the accessible label', () => {
    const wrapper = mount(QIconButton, { props: { label: 'Close details', disabled: true }, slots: { default: '×' } })
    const button = wrapper.get('button')
    expect(button.classes()).toContain('q-icon-button')
    expect(button.attributes('aria-label')).toBe('Close details')
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.text()).toBe('×')
  })

  it('keeps icon and text slots together for shared flex alignment', () => {
    const wrapper = mount(QButton, {
      props: { variant: 'primary' },
      slots: { default: () => [h('span', { class: 'slot-icon' }, '↻'), 'Refresh'] },
    })
    expect(wrapper.get('.q-button-content .slot-icon').text()).toBe('↻')
    expect(wrapper.get('.q-button-content').text()).toContain('Refresh')
  })
})
