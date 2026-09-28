import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import QModal from './QModal.vue'
import QModalInput from './QModalInput.vue'
import QModalLabel from './QModalLabel.vue'

describe('QModal building blocks', () => {
  it('shows an accessible themed dialog with buttons and closes via Escape', async () => {
    const wrapper = mount(QModal, { attachTo: document.body, props: { open: true, title: 'Version mismatch' }, slots: { default: '<p>Details</p>', actions: '<button type="button">Continue</button>' } })
    const dialog = document.querySelector<HTMLElement>('.q-modal-card')!
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(dialog.textContent).toContain('Details')
    expect(dialog.querySelector('.q-modal-actions button')?.textContent).toBe('Continue')
    let leakedEscapes = 0
    const onWindowKeydown = (event: KeyboardEvent) => { if (event.key === 'Escape') leakedEscapes += 1 }
    window.addEventListener('keydown', onWindowKeydown)
    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    expect(wrapper.emitted('close')).toHaveLength(1)
    expect(leakedEscapes).toBe(0)
    window.removeEventListener('keydown', onWindowKeydown)
    wrapper.unmount()
  })

  it('provides reusable text and labeled input controls', async () => {
    const label = mount(QModalLabel, { slots: { default: 'A short message' } })
    expect(label.text()).toBe('A short message')
    const input = mount(QModalInput, { props: { modelValue: '', label: 'Module name', placeholder: 'Name' } })
    await input.get('input').setValue('Example')
    expect(input.emitted('update:modelValue')?.[0]).toEqual(['Example'])
    expect(input.get('label').text()).toContain('Module name')
    label.unmount(); input.unmount()
  })
})
