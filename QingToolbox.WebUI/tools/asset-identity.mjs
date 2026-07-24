import { createHash } from 'node:crypto'
import { readdir, readFile, stat } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceNames = ['index.html','package.json','package-lock.json','tsconfig.json','vite.config.ts','src']
const slash = value => value.split(path.sep).join('/')
async function filesUnder(target) { const s=await stat(target);if(s.isFile())return[target];const entries=await readdir(target);const nested=await Promise.all(entries.sort().map(x=>filesUnder(path.join(target,x))));return nested.flat() }
export async function shaFile(file){return createHash('sha256').update(await readFile(file)).digest('hex')}
export async function sourceIdentity(){const files=(await Promise.all(sourceNames.map(x=>filesUnder(path.join(root,x))))).flat().sort();const hash=createHash('sha256');for(const file of files){hash.update(slash(path.relative(root,file)));hash.update('\0');hash.update(await readFile(file));hash.update('\0')}return hash.digest('hex')}
export async function distFiles(){return(await filesUnder(path.join(root,'dist'))).filter(x=>path.basename(x)!=='qing-web-assets.json').sort()}
export { slash }
