import { isBridgeEvent, type BridgeEvent } from '../../contracts/app'
export class EventDispatcher {
  private readonly handlers = new Map<string, Set<(payload: unknown) => void>>()
  dispatch(event: unknown) { if (isBridgeEvent(event)) this.handlers.get(event.event)?.forEach(handler => handler(event.payload)) }
  on(event: string, handler: (payload: unknown) => void) { const set = this.handlers.get(event) ?? new Set(); set.add(handler); this.handlers.set(event, set); return () => set.delete(handler) }
}
