import type { AppSnapshot } from '../../contracts/app'
import type { RequestClient } from '../protocol/RequestClient'
import { assetBuildId } from '../../contracts/app'
export class AppClient {
  constructor(private readonly requests: RequestClient) {}
  ping() { return this.requests.request<{ pong: boolean; hostTime: string }>('app.ping') }
  ready(mode: 'WebView'|'Mock') { return this.requests.request<AppSnapshot>('web.ready', { assetBuildId, documentReadyState: document.readyState, transportMode: mode }) }
  getSnapshot() { return this.requests.request<AppSnapshot>('app.getSnapshot') }
}
