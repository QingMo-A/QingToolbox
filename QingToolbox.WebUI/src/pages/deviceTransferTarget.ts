export type DeviceTransferTarget = { deviceId: string; platform: string; addresses: string[] }
export type DeviceTransferPeer = { deviceId?: string | null; platform: string; addresses: string[]; online: boolean }

export function matchesDeviceTransferTarget(peer: DeviceTransferPeer, target: DeviceTransferTarget): boolean {
  return peer.online && peer.deviceId === target.deviceId && peer.platform === target.platform
}
