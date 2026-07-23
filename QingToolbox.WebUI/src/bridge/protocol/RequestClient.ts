import { protocolVersion, type BridgeResponse } from '../../contracts/app'
import type { Transport } from '../transport/Transport'

export class RequestClient {
  constructor(private readonly transport: Transport) {}
  async request<T>(command: string): Promise<T> {
    const requestId = crypto.randomUUID()
    const response: BridgeResponse = await this.transport.request({ protocolVersion, requestId, command, payload: {} })
    if (response.protocolVersion !== protocolVersion || response.requestId !== requestId) throw new Error('Bridge response identity mismatch.')
    if (!response.success) throw new Error(`${response.error?.code ?? 'BridgeError'}: ${response.error?.message ?? 'Request failed.'}`)
    return response.payload as T
  }
}
