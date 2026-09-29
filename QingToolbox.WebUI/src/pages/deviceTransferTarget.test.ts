import { describe, expect, it } from 'vitest'
import { matchesDeviceTransferTarget } from './deviceTransferTarget'

describe('device transfer target', () => {
  const target = { deviceId: 'paired-id', platform: 'android', addresses: ['192.168.1.7'] }
  const peer = { deviceId: 'paired-id', platform: 'android', addresses: ['192.168.1.7'], online: true }

  it('shows only the selected paired device transfer endpoint', () => {
    expect(matchesDeviceTransferTarget(peer, target)).toBe(true)
    expect(matchesDeviceTransferTarget({ ...peer, deviceId: 'another-id' }, target)).toBe(false)
    expect(matchesDeviceTransferTarget({ ...peer, addresses: ['192.168.1.8'] }, target)).toBe(true)
    expect(matchesDeviceTransferTarget({ ...peer, platform: 'windows' }, target)).toBe(false)
    expect(matchesDeviceTransferTarget({ ...peer, online: false }, target)).toBe(false)
    expect(matchesDeviceTransferTarget({ ...peer, deviceId: null }, target)).toBe(false)
  })
})
