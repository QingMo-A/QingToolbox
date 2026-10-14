export type Credential = { id: string; account: string; note: string }
export type Entry = { id: string; name: string; link: string; remark: string; credentials: Credential[] }
export type Category = { id: string; name: string; entries: Entry[] }
export type State = { categories: Category[]; loadError: { code: string; message: string } | null; storage: string }
export type DraftAccount = { id?: string; account: string; password: string; note: string }
export type Draft = { id?: string; categoryId: string; name: string; link: string; remark: string; credentials: DraftAccount[] }
export type EditEntry = Omit<Draft, 'categoryId'>
