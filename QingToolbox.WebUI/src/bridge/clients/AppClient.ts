import type { AppSnapshot } from '../../contracts/app'
import type { RequestClient } from '../protocol/RequestClient'
export class AppClient {
  constructor(private readonly requests: RequestClient) {}
  ping() { return this.requests.request<{ pong: boolean; hostTime: string }>('app.ping') }
  ready() { return this.requests.request<AppSnapshot>('web.ready') }
  getSnapshot() { return this.requests.request<AppSnapshot>('app.getSnapshot') }
}
