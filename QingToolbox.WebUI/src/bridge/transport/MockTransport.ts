import { protocolVersion, type AppSnapshot, type BridgeEvent, type BridgeRequest, type BridgeResponse } from '../../contracts/app'
import type { Transport } from './Transport'

export class MockTransport implements Transport {
  readonly mode = 'Mock' as const
  private readonly listeners = new Set<(event: BridgeEvent) => void>()
  async request(message: BridgeRequest): Promise<BridgeResponse> {
    if (message.protocolVersion !== protocolVersion) return this.error(message, 'ProtocolMismatch')
    if (message.command === 'app.ping') return this.ok(message, { pong: true, hostTime: new Date().toISOString() })
    if (message.command === 'web.ready' || message.command === 'app.getSnapshot') {
      const snapshot: AppSnapshot = { environmentKind: 'Mock', environmentDisplayName: 'Browser Mock Environment', hostVersion: 'mock', protocolVersion, totalModuleCount: 4, validModuleCount: 3, runningModuleCount: 1, generatedAt: new Date().toISOString() }
      if (message.command === 'web.ready') queueMicrotask(() => this.listeners.forEach(listener => listener({ protocolVersion, event: 'app.hostEvent', payload: { name: 'mock.ready' } })))
      return this.ok(message, snapshot)
    }
    return this.error(message, 'UnknownCommand')
  }
  subscribe(listener: (event: BridgeEvent) => void) { this.listeners.add(listener); return () => this.listeners.delete(listener) }
  emit(event: BridgeEvent) { this.listeners.forEach(listener => listener(event)) }
  private ok(request: BridgeRequest, payload: unknown): BridgeResponse { return { protocolVersion, requestId: request.requestId, success: true, payload, error: null } }
  private error(request: BridgeRequest, code: string): BridgeResponse { return { protocolVersion, requestId: request.requestId, success: false, payload: {}, error: { code, message: 'Mock bridge rejected the request.' } } }
}
