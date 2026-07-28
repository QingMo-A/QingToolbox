import type { LogLevel } from '../contracts/logs'
import type { TranslationKey } from '../localization/messages/en-US'
export const bridgeStateKey=(state:string):TranslationKey|null=>({Connected:'bridgeState.connected',Connecting:'bridgeState.connecting',Unavailable:'bridgeState.unavailable'} as Record<string,TranslationKey>)[state]??null
export const logLevelKey=(level:LogLevel):TranslationKey=>({Information:'logs.severity.information',Warning:'logs.severity.warning',Error:'logs.severity.error'} satisfies Record<LogLevel,TranslationKey>)[level]
