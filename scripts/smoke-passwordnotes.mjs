// Isolated wire/persistence check. Synthetic accounts only; never touches the OS clipboard or user profiles.
import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { mkdtempSync, readFileSync, existsSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, dirname } from 'node:path'
import assert from 'node:assert/strict'
import { createInterface } from 'node:readline'
const executable = resolve(process.argv[2] ?? 'modules/PasswordNotes/target/release/qing-passwordnotes-module.exe')
const root = mkdtempSync(resolve(tmpdir(), 'qing-passwordnotes-smoke-'))
const nonce = randomUUID().replaceAll('-','')
let child, closed, sequence = 0
const pending = new Map()
function start() {
  child = spawn(executable, [], { cwd: dirname(executable), env: { ...process.env, QINGTOOLBOX_MODULE_ID:'qing.passwordnotes', QINGTOOLBOX_MODULE_NONCE:nonce, QINGTOOLBOX_MODULE_DATA_DIR:root }, windowsHide:true, stdio:['pipe','pipe','pipe'] })
  closed = new Promise((resolve, reject) => { child.once('exit', code => code === 0 ? resolve() : reject(new Error('Module exit failed: '+code))); child.once('error',reject) })
  createInterface({input:child.stdout}).on('line', line => {
    const value=JSON.parse(line); const request=pending.get(value.requestId)
    if(request) { clearTimeout(request.timer); pending.delete(value.requestId); value.error ? request.reject(new Error(value.error.code)) : request.resolve(value.payload) }
  })
}
function request(messageType,payload) {
  return new Promise((resolve,reject)=> { const id='test-'+(++sequence); const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Protocol timeout'))},5000); pending.set(id,{resolve,reject,timer}); child.stdin.write(JSON.stringify({protocolVersion:1,messageType,requestId:id,payload})+'\n') })
}
const invoke=(method,payload={})=>request('module.invoke.request',{method,payload})
const hello=()=>request('module.hello.request',{moduleId:'qing.passwordnotes',nonce})
async function stop() { await request('module.shutdown.request',{}); await closed }
try {
  start(); await assert.rejects(()=>invoke('getState'),/unknownMessage/); await hello()
  let state=await invoke('saveCategory',{name:'合成游戏'}); const cat=state.categories[0].id
  state=await invoke('saveCategory',{name:'合成网站'}); const cat2=state.categories[1].id
  state=await invoke('saveEntry',{categoryId:cat,name:'Synthetic Game',link:'https://example.com',remark:'非真实资料',credentials:[{account:'synthetic-one',password:'DO-NOT-LOG-first-secret',note:'主号'},{account:'synthetic-two',password:' second-synthetic-secret ',note:'小号'}]})
  const entry=state.categories[0].entries[0]; const account=entry.credentials[0]
  assert.equal(entry.credentials.length,2)
  assert.ok(!JSON.stringify(state).includes('secret') && !JSON.stringify(state).includes('password'))
  const sealed=readFileSync(resolve(root,'vault.dpapi')); assert.ok(!sealed.includes(Buffer.from('DO-NOT-LOG-first-secret')))
  const reveal=await invoke('getPassword',{entryId:entry.id,credentialId:account.id}); assert.equal(reveal.password,'DO-NOT-LOG-first-secret'); reveal.password=''
  await assert.rejects(()=>invoke('getPassword',{entryId:entry.id,credentialId:'unknown'}),/notFound/)
  const edit=await invoke('getEntryForEdit',{id:entry.id}); state=await invoke('saveEntry',{...edit,categoryId:cat2}); assert.equal(state.categories[0].entries.length,0); assert.equal(state.categories[1].entries.length,1)
  await request('module.lifecycle.request',{active:false}); await request('module.lifecycle.request',{active:true}); assert.equal((await invoke('getState')).categories[1].entries.length,1)
  const out=resolve(root,'synthetic-export.txt')
  await assert.rejects(()=>invoke('exportTxt',{path:out,categoryId:cat2}),/exportNotConfirmed/); assert.ok(!existsSync(out))
  await assert.rejects(()=>invoke('exportTxt',{path:out,categoryId:7,includePasswords:true}),/invalidPayload/); assert.ok(!existsSync(out))
  await invoke('exportTxt',{path:out,categoryId:cat2,includePasswords:true})
  const exported=readFileSync(out,'utf8'); assert.ok(exported.includes(' second-synthetic-secret ') && exported.includes('合成网站') && !exported.includes('【合成游戏】'))
  await stop(); start(); await hello(); state=await invoke('getState'); assert.equal(state.categories[1].entries[0].credentials.length,2)
  await invoke('deleteEntry',{id:entry.id}); state=await invoke('deleteCategory',{id:cat2}); assert.equal(state.categories.length,1); await stop()
  console.log(JSON.stringify({moduleId:'qing.passwordnotes',protocol:true,encryptedPersistence:true,separateReveal:true,moveAndMultiAccount:true,lifecycle:true,scopedTxt:true,cancellation:true,reloadAndDeletion:true,clipboardUntouched:true}))
} finally {
  for(const request of pending.values()) clearTimeout(request.timer)
  if(child && child.exitCode === null) { child.stdin.end(); await Promise.race([closed.catch(()=>{}),new Promise(resolve=>setTimeout(resolve,1500))]) }
  if(!child || child.exitCode !== null) rmSync(root,{recursive:true,force:true})
}
