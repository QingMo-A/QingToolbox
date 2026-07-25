import type { RequestClient } from '../protocol/RequestClient'
import { isSettingsSnapshot } from '../../contracts/settings'
export class SettingsClient { constructor(private readonly requests:RequestClient){} async getSnapshot(){const value=await this.requests.request<unknown>('settings.getSnapshot');if(!isSettingsSnapshot(value))throw new Error('Settings snapshot validation failed.');return value} }
