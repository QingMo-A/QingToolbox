export type SecurityFieldKind = 'phone' | 'email' | 'custom'
export type SecurityField = { kind: SecurityFieldKind; label: string; value: string }
export const MAX_SECURITY_FIELDS = 16
export const securityFieldLabel = (field: SecurityField) => field.kind === 'phone' ? '安全手机' : field.kind === 'email' ? '安全邮箱' : field.label
export type TotpSettings = { algorithm: 'SHA1' | 'SHA256' | 'SHA512'; digits: 6 | 8; periodSeconds: number }
export type TotpCode = { code: string; generatedAtMs: number; validFromMs: number; validUntilMs: number; periodSeconds: number }
export const defaultTotpSettings = (): TotpSettings => ({ algorithm: 'SHA1', digits: 6, periodSeconds: 30 })
export type Credential = { id: string; account: string; note: string; securityFields?: SecurityField[]; totp?: TotpSettings | null }
export type Entry = { id: string; name: string; link: string; remark: string; credentials: Credential[] }
export type Category = { id: string; name: string; entries: Entry[] }
export type State = { categories: Category[]; loadError: { code: string; message: string } | null; storage: string }
export type DraftAccount = { id?: string; account: string; password: string; note: string; securityFields: SecurityField[]; totp?: TotpSettings | null; totpEnabled: boolean; totpSecret: string; totpSettings: TotpSettings }
export type Draft = { id?: string; categoryId: string; name: string; link: string; remark: string; credentials: DraftAccount[] }
export type EditEntry = Omit<Draft, 'categoryId' | 'credentials'> & { credentials: (Omit<DraftAccount, 'totpEnabled' | 'totpSecret' | 'totpSettings'>)[] }
