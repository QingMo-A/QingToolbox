import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { invoke } from '@tauri-apps/api/core'
import { createPinia, setActivePinia } from 'pinia'
import DevicesPage from './DevicesPage.vue'
import { useAppStore } from '../app/store'
import { useSettingsStore } from '../app/settingsStore'
import { TauriTransport } from '../bridge/transport/TauriTransport'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
afterEach(() => {
  vi.restoreAllMocks()
  vi.mocked(invoke).mockReset()
  document.querySelectorAll('.q-modal-layer').forEach(element => element.remove())
})

function page(deviceName: string | null = 'QING-PC') {
  const pinia = createPinia()
  setActivePinia(pinia)
  const app = useAppStore()
  app.rebuild({ environmentKind: 'Production', environmentDisplayName: 'QingToolbox', hostVersion: '1', deviceName, protocolVersion: 4, totalModuleCount: 0, validModuleCount: 0, runningModuleCount: 0, generatedAt: new Date().toISOString() })
  const wrapper = mount(DevicesPage, { global: { plugins: [pinia] } })
  return { app, wrapper, settings: useSettingsStore() }
}

describe('DevicesPage', () => {
  it('shows the host-provided name without claiming an unpaired device is trusted', () => {
    const { wrapper } = page()
    expect(wrapper.get('.devices-local h2').text()).toBe('QING-PC')
    expect(wrapper.text()).toContain('Nearby discovery is off')
    expect(wrapper.text()).toContain('Nearby devices are online but not identity-verified.')
    expect(wrapper.findAll('.devices-tabs button')).toHaveLength(2)
    expect(wrapper.get('.devices-upper').findAll('section')).toHaveLength(2)
    expect(wrapper.find('.devices-paired-scroll').exists()).toBe(true)
    expect(wrapper.find('.devices-nearby-scroll').exists()).toBe(true)
    expect(wrapper.find('.devices-transfer-action').exists()).toBe(false)
    expect(wrapper.text()).toContain('File receiving settings')
  })

  it('shows an unavailable state rather than fabricating a device name', () => {
    const { wrapper } = page(null)
    expect(wrapper.get('.devices-local h2').text()).toBe('Device name unavailable')
  })

  it('switches relation groups and localizes the page', async () => {
    const { wrapper, settings } = page()
    settings.complete({ generatedAt: new Date().toISOString(), language: { code: 'zh-CN', effectiveCode: 'zh-CN', displayName: '简体中文', options: [] }, showLogsInSidebar: true, mainWindowCloseBehavior: 'Ask', closeBehaviorMessage: '', launchAtLogin: false, canConfigureLaunchAtLogin: false, canRepairStartup: false, startupPresentationMode: 'MainWindow', startupBackend: 'None', startupStatus: 'Unavailable', startupMessage: '' })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).not.toContain('这台设备')
    expect(wrapper.text()).toContain('QING-PC')
    expect(wrapper.text()).toContain('亲密设备')
    expect(wrapper.text()).toContain('附近的陌生设备')
    const pairedScroll = wrapper.get('.devices-paired-scroll').element as HTMLElement
    pairedScroll.scrollTop = 42
    await wrapper.get('#device-tab-connected').trigger('click')
    expect(wrapper.get('#device-tab-connected').attributes('aria-selected')).toBe('true')
    expect(pairedScroll.scrollTop).toBe(0)
    expect(wrapper.text()).toContain('尚无已配对设备')
    await wrapper.get('#device-tab-connected').trigger('keydown', { key: 'ArrowRight' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('#device-tab-intimate').attributes('aria-selected')).toBe('true')
    expect(wrapper.text()).toContain('开启附近发现以寻找设备')
  })

  it('shows live discovery candidates only in the unverified nearby area', async () => {
    vi.spyOn(TauriTransport, 'isAvailable').mockReturnValue(true)
    vi.mocked(invoke).mockResolvedValue({ enabled: true, error: null, nearby: [{ id: 'candidate-1', name: 'TEST-PC', platform: 'windows', status: 'Unverified' }] })
    const { wrapper } = page()
    await flushPromises()
    expect(wrapper.get('.devices-nearby-item').text()).toContain('TEST-PC')
    expect(wrapper.get('.devices-nearby-item').text()).toContain('Unverified')
    expect(wrapper.find('.devices-nearby-item .devices-transfer-action').exists()).toBe(false)
    expect(wrapper.get('.devices-paired-scroll').text()).not.toContain('TEST-PC')
    expect(wrapper.text()).toContain('Discovering nearby devices')
    await wrapper.get('.devices-discovery-button').trigger('click')
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('set_devices_discovery_enabled', { enabled: false })
    wrapper.unmount()
  })

  it('requires an explicit code confirmation and keeps newly paired devices in Connected', async () => {
    vi.spyOn(TauriTransport, 'isAvailable').mockReturnValue(true)
    vi.mocked(invoke).mockResolvedValue({
      enabled: true, error: null,
      nearby: [{ id: 'discovery-2', name: 'SECOND-PC', platform: 'windows', status: 'Unverified' }],
      pairing: {
        error: null,
        pending: [{ sessionId: 'pair-1', discoveryId: 'discovery-2', name: 'SECOND-PC', platform: 'windows', code: '12345678', incoming: true, localApproved: false }],
        paired: [],
      },
    })
    const { wrapper } = page()
    await flushPromises()
    expect(document.querySelector('.devices-pair-code')?.textContent).toBe('1234 5678')
    expect(document.querySelector('[role="dialog"]')?.textContent).toContain('Check that both devices show the same verification code.')
    expect(wrapper.get('.devices-paired-scroll').text()).not.toContain('SECOND-PC')
    vi.mocked(invoke).mockResolvedValueOnce({
      enabled: true, error: null, nearby: [],
      pairing: { error: null, pending: [], paired: [{ id: 'key-2', discoveryId: 'discovery-2', name: 'SECOND-PC', platform: 'windows', relationship: 'Connected' }] },
    })
    ;(document.querySelector('.q-modal-actions .is-primary') as HTMLButtonElement).click()
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('decide_device_pairing', { sessionId: 'pair-1', approve: true })
    await flushPromises()
    expect(wrapper.get('#device-tab-connected').attributes('aria-selected')).toBe('true')
    expect(wrapper.get('.devices-paired-item').text()).toContain('SECOND-PC')
    wrapper.unmount()
  })

  it('does not auto-promote a verified pair and offers an explicit move to intimate devices', async () => {
    vi.spyOn(TauriTransport, 'isAvailable').mockReturnValue(true)
    vi.mocked(invoke).mockResolvedValue({
      enabled: true, error: null,
      nearby: [{ id: 'discovery-2', name: 'SECOND-PC', platform: 'windows', status: 'Unverified' }],
      pairing: { error: null, pending: [], paired: [{ id: 'key-2', discoveryId: 'discovery-2', name: 'SECOND-PC', platform: 'windows', relationship: 'Connected' }], online: ['key-2'] },
    })
    const { wrapper } = page()
    await flushPromises()
    expect(wrapper.find('.devices-nearby-item').exists()).toBe(false)
    expect(wrapper.get('#device-tab-intimate').attributes('aria-selected')).toBe('true')
    expect(wrapper.get('.devices-paired-scroll').text()).not.toContain('SECOND-PC')
    await wrapper.get('#device-tab-connected').trigger('click')
    expect(wrapper.get('#device-tab-connected').text()).toContain('(1)')
    expect(wrapper.get('.devices-paired-item').text()).toContain('SECOND-PC')
    expect(wrapper.get('.devices-presence').text()).toBe('Online')
    expect(wrapper.get('.devices-transfer-action').attributes('disabled')).toBeUndefined()
    await wrapper.get('.devices-paired-item .devices-small-action').trigger('click')
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('set_device_relationship', { peerId: 'key-2', intimate: true })
    wrapper.unmount()
  })

  it('distinguishes Windows and Android peers with platform-specific icons', async () => {
    vi.spyOn(TauriTransport, 'isAvailable').mockReturnValue(true)
    vi.mocked(invoke).mockResolvedValue({
      enabled: true, error: null,
      nearby: [{ id: 'near-phone', name: 'Nearby Android', platform: 'android', status: 'Unverified' }],
      pairing: { error: null, pending: [], online: ['pc-key'], paired: [
        { id: 'pc-key', discoveryId: 'pc', name: 'Windows peer', platform: 'windows', relationship: 'Connected' },
        { id: 'phone-key', discoveryId: 'phone', name: 'Android peer', platform: 'android', relationship: 'Connected' },
      ] },
    })
    const { wrapper } = page()
    await flushPromises()
    await wrapper.get('#device-tab-connected').trigger('click')
    const peers = wrapper.findAll('.devices-paired-item')
    expect(peers).toHaveLength(2)
    expect(peers[0].find('[data-platform="windows"] svg').exists()).toBe(true)
    expect(peers[1].find('[data-platform="android"] svg').exists()).toBe(true)
    expect(peers[0].get('.devices-transfer-action').attributes('disabled')).toBeUndefined()
    expect(peers[1].get('.devices-transfer-action').attributes('disabled')).toBeDefined()
    expect(peers[0].get('svg').html()).not.toBe(peers[1].get('svg').html())
    expect(wrapper.find('.devices-nearby-item [data-platform="android"] svg').exists()).toBe(true)
    wrapper.unmount()
  })

  it('uses the project modal for relationship requests and completion notices', async () => {
    vi.spyOn(TauriTransport, 'isAvailable').mockReturnValue(true)
    vi.mocked(invoke).mockResolvedValue({
      enabled: true, error: null, nearby: [],
      pairing: { error: null, pending: [], paired: [], actions: [{ sessionId: 'act-1', peerId: 'key-2', name: 'PHONE', action: 'upgrade', localApproved: false }], notices: [] },
    })
    const { wrapper } = page()
    await flushPromises()
    expect(document.querySelector('[role="dialog"]')?.textContent).toContain('Request to become intimate devices')
    expect(wrapper.find('.devices-pending-card').exists()).toBe(false)
    vi.mocked(invoke).mockResolvedValueOnce({
      enabled: true, error: null, nearby: [],
      pairing: { error: null, pending: [], paired: [], actions: [], notices: [{ id: 'notice-1', peerName: 'PHONE', action: 'upgrade' }] },
    })
    ;(document.querySelector('.q-modal-actions .is-primary') as HTMLButtonElement).click()
    await flushPromises()
    expect(vi.mocked(invoke)).toHaveBeenCalledWith('decide_device_action', { sessionId: 'act-1', approve: true })
    expect(document.querySelector('[role="dialog"]')?.textContent).toContain('PHONE is now an intimate device.')
    wrapper.unmount()
  })
})
