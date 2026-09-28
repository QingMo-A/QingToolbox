import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import QInfoPopup from './QInfoPopup.vue'

describe('QInfoPopup', () => {
  it('renders message labels as text and emits its button action', async () => {
    const wrapper = mount(QInfoPopup, { props: {
      deviceName: 'Phone', appName: 'Messages', title: '<code>', label: '<script>alert(1)</script>', buttonLabel: 'Close',
    } })
    expect(wrapper.get('.q-info-popup-device').text()).toBe('Phone')
    expect(wrapper.get('.q-info-popup-app').text()).toBe('Messages')
    expect(wrapper.get('.q-info-popup-title').text()).toBe('<code>')
    expect(wrapper.get('.q-info-popup-label').text()).toBe('<script>alert(1)</script>')
    expect(wrapper.html()).not.toContain('<script>alert(1)</script>')
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('action')).toHaveLength(1)
  })
})
