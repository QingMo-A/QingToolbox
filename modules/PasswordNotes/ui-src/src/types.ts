export type SecurityFieldKind = 'phone' | 'email' | 'custom'
export type SecurityField = { kind: SecurityFieldKind; label: string; value: string }
export const MAX_SECURITY_FIELDS = 16
export const securityFieldLabel = (field: SecurityField) => field.kind === 'phone' ? '安全手机' : field.kind === 'email' ? '安全邮箱' : field.label
export type Credential = { id: string; account: string; note: string; securityFields?: SecurityField[] }
export type Entry = { id: string; name: string; link: string; remark: string; credentials: Credential[] }
export type Category = { id: string; name: string; entries: Entry[] }
export type State = { categories: Category[]; loadError: { code: string; message: string } | null; storage: string }
export type DraftAccount = { id?: string; account: string; password: string; note: string; securityFields: SecurityField[] }
export type Draft = { id?: string; categoryId: string; name: string; link: string; remark: string; credentials: DraftAccount[] }
export type EditEntry = Omit<Draft, 'categoryId'>
