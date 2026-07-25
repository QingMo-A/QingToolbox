import type { RequestClient } from '../protocol/RequestClient'
import { isLogSnapshot } from '../../contracts/logs'
export class LogClient { constructor(private readonly requests:RequestClient){} async getSnapshot(){const value=await this.requests.request<unknown>('logs.getSnapshot');if(!isLogSnapshot(value))throw new Error('Log snapshot validation failed.');return value} }
