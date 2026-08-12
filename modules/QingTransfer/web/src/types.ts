export type Presentation = { appearancePresetId: string; languageCode: string }
export type Peer = { serviceName: string; displayName: string; platform: string; protocolVersion: string; addresses: string[]; port: number; online: boolean }
export type Transfer = { name: string; completed: number; total: number; receiving: boolean } | null
export type State = {
  discovery: { running: boolean; peers: Peer[] }
  session: { state: string; peer: Peer | null }
  incomingConnection: { platform: string; name: string } | null
  incomingFile: { name: string; size: number } | null
  receive: { defaultDirectory: string | null; useDefaultDirectory: boolean; autoAccept: boolean }
  transfer: Transfer
  lastError: string | null
}
