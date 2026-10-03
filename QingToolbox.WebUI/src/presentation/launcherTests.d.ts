// Test-only contracts for sources resolved by Vitest from the modules checkout.
declare module '@launcher-test/gridOrder' {
  export function insertionSlot(x: number, y: number, width: number, height: number, columns: number, count: number): number
  export function moveToSlot(ids: string[], id: string, slot: number): string[]
  export function previewSlot(ids: string[], id: string, dragId: string, slot: number | null): number
}
declare module '@launcher-test/itemIcons' {
  export function loadItemIcon(source: string): Promise<string | null>
  export function cachedItemIcon(source: string): string | null
}
declare module '@launcher-test/overlayInteraction' {
  export class OverlayInteraction {
    begin(id: number, x: number, y: number, blank: boolean, now: number): void
    end(id: number, x: number, y: number, blank: boolean, now: number): boolean
    cancel(): void
    externalDrag(active: boolean, now: number): void
    internalDrag(active: boolean, now: number): void
  }
}
