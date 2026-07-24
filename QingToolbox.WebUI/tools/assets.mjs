import { readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { distFiles, root, shaFile, slash, sourceIdentity } from './asset-identity.mjs'
const manifestPath=path.join(root,'dist','qing-web-assets.json');const command=process.argv[2]
const sourceTreeSha256=await sourceIdentity();const packageLockSha256=await shaFile(path.join(root,'package-lock.json'))
if(command==='generate'){
  const outputFiles=[];for(const file of await distFiles()){const s=(await import('node:fs/promises')).stat(file);outputFiles.push({path:slash(path.relative(path.join(root,'dist'),file)),size:(await s).size,sha256:await shaFile(file)})}
  const manifest={schemaVersion:1,assetBuildId:sourceTreeSha256.slice(0,32),packageLockSha256,sourceTreeSha256,outputFiles};await writeFile(manifestPath,JSON.stringify(manifest,null,2)+'\n','utf8')
}else if(command==='verify'){
  const manifest=JSON.parse(await readFile(manifestPath,'utf8'));if(manifest.schemaVersion!==1||manifest.assetBuildId!==sourceTreeSha256.slice(0,32)||manifest.sourceTreeSha256!==sourceTreeSha256||manifest.packageLockSha256!==packageLockSha256)throw new Error('Web asset source identity is stale.')
  const actual=[];for(const file of await distFiles())actual.push({path:slash(path.relative(path.join(root,'dist'),file)),size:(await (await import('node:fs/promises')).stat(file)).size,sha256:await shaFile(file)})
  if(JSON.stringify(actual)!==JSON.stringify(manifest.outputFiles))throw new Error('Web asset output set or hash does not match the manifest.')
  if(actual.some(x=>x.path.endsWith('.map')||/^[A-Za-z]:|^\//.test(x.path)))throw new Error('Web assets contain a source map or unsafe path.')
  console.log(`Verified ${actual.length} WebUI assets; buildId=${manifest.assetBuildId}`)
}else if(command==='source')console.log(sourceTreeSha256)
else throw new Error('Use generate, verify or source.')
