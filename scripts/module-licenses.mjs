// Collect the actual locked Rust dependency license texts for a local package.
import { execFileSync } from 'node:child_process'
import { readFileSync, readdirSync, existsSync, writeFileSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
const [moduleRoot, output] = process.argv.slice(2)
if (!moduleRoot || !output) throw Error('usage: module-licenses.mjs module-root output-file')
const data = JSON.parse(execFileSync('cargo',['metadata','--manifest-path',resolve(moduleRoot,'Cargo.toml'),'--locked','--offline','--format-version','1','--filter-platform','x86_64-pc-windows-msvc'],{encoding:'utf8',maxBuffer:16*1024*1024,windowsHide:true}))
const sections = [], missing = []
for (const pkg of data.packages.filter(v=>v.source).sort((a,b)=>a.name.localeCompare(b.name))) {
  const directory = dirname(pkg.manifest_path)
  const files = readdirSync(directory).filter(n=>/^(licen[cs]e|copying|copyright|notice)([._-]|$)/i.test(n)).map(n=>resolve(directory,n)).filter(p=>statSync(p).isFile())
  if (pkg.license_file && existsSync(resolve(directory,pkg.license_file))) files.push(resolve(directory,pkg.license_file))
  const unique = [...new Set(files)]
  let body = unique.map(path=>readFileSync(path,'utf8')).join('\n\n')
  if (!unique.length) {
    const vendored = resolve(moduleRoot,'third-party',`${pkg.name}-LICENSE.txt`)
    if (existsSync(vendored)) body = readFileSync(vendored,'utf8')
    else if (pkg.license === 'MIT' && ['pdf-extract','adobe-cmap-parser','type1-encoding-parser'].includes(pkg.name)) {
      body = `Original package declaration:\n${readFileSync(resolve(directory,'Cargo.toml.orig'),'utf8')}\n${readFileSync(resolve(moduleRoot,'third-party/MIT-declaration-notice.txt'),'utf8')}`
    } else { missing.push(`${pkg.name} ${pkg.version} (${pkg.license})`); continue }
  }
  sections.push(`${'='.repeat(72)}\n${pkg.name} ${pkg.version}\nLicense: ${pkg.license}\nSource: ${pkg.repository ?? `https://crates.io/crates/${pkg.name}/${pkg.version}`}\n\n${body}`)
}
if (missing.length) throw Error(`Upstream license text missing from the crate package:\n${missing.join('\n')}`)
writeFileSync(resolve(output), `QingToolbox module locked Rust dependency licenses\n\n${sections.join('\n\n')}`)
console.log(`Collected license texts for ${sections.length} crates.`)
