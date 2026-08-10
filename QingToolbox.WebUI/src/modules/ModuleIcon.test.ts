import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ModuleIcon from './ModuleIcon.vue'

const iconDataUrl = 'data:image/svg+xml;base64,PHN2Zy8+'

describe('ModuleIcon', () => {
  it('renders a host-projected SVG only through an image element', () => {
    const wrapper = mount(ModuleIcon, { props: { iconDataUrl, alt: 'Text Tools', fallback: 'Text Tools' } })
    const icon = wrapper.get('.module-icon')
    expect(icon.attributes('role')).toBe('img')
    expect(icon.attributes('aria-label')).toBe('Text Tools')
    expect(icon.attributes('data-icon-state')).toBe('image')
    expect(icon.get('img').attributes('src')).toBe(iconDataUrl)
    expect(icon.find('svg').exists()).toBe(false)
    expect(icon.find('[aria-hidden="true"]').text()).toBe('')
  })

  it.each([
    [undefined, 'T'],
    [null, 'T'],
    ['https://example.invalid/icon.svg', 'T'],
  ])('keeps the first-letter fallback for missing or unsafe icon data (%s)', (value, expected) => {
    const wrapper = mount(ModuleIcon, { props: { iconDataUrl: value, alt: 'Text Tools' } })
    const icon = wrapper.get('.module-icon')
    expect(icon.attributes('data-icon-state')).toBe('fallback')
    expect(icon.find('img').exists()).toBe(false)
    expect(icon.get('[aria-hidden="true"]').text()).toBe(expected)
  })

  it('falls back when the browser rejects the projected image', async () => {
    const wrapper = mount(ModuleIcon, { props: { iconDataUrl, alt: 'PowerGuard', fallback: 'PowerGuard' } })
    await wrapper.get('img').trigger('error')
    expect(wrapper.get('.module-icon').attributes('data-icon-state')).toBe('fallback')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.get('[aria-hidden="true"]').text()).toBe('P')
  })

  it('keeps the first-character fallback independent of the display language', () => {
    const wrapper = mount(ModuleIcon, { props: { alt: '文本工具', fallback: '文本工具' } })
    expect(wrapper.get('[aria-hidden="true"]').text()).toBe('文')
  })
})
