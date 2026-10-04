import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join, relative, isAbsolute } from 'node:path'
import { spawn } from 'node:child_process'

const root = resolve('modules/Launcher/ui-src/dist')
const fixture = `window.__callbacks={};window.__listeners={};window.__callbackId=0;window.__hides=0;
window.__state={sortMode:'custom',items:[{id:'app',name:'Browser',source:'custom',iconKey:null}],folders:[],customOrder:['app'],recent:[],hotkey:{ctrl:true,alt:true,shift:false,win:false,virtualKey:76,keyLabel:'L'},hotkeyStatus:'HostManaged',active:true,everythingSettings:{resultLimit:200,batchLoading:false}};
window.__TAURI_INTERNALS__={transformCallback:fn=>{window.__callbacks[++window.__callbackId]=fn;return window.__callbackId},unregisterCallback:()=>{},invoke:async(cmd,args)=>{
if(cmd==='plugin:event|listen'){(window.__listeners[args.event]??=[]).push(args.handler);return args.handler}
if(cmd==='get_module_window_context')return {name:'Qing Launcher',version:'test',operations:[],iconDataUrl:null,appearancePreset:'qing-default',theme:'light'};
if(cmd==='hide_module_window'){window.__hides++;return}
if(cmd==='set_module_hotkey')return {status:'Registered'};
if(cmd==='invoke_module_window'){
if(args.method==='getEverythingStatus')return {status:'ready'};
if(args.method==='setEverythingSettings'){window.__state.everythingSettings=structuredClone(args.payload);return structuredClone(window.__state)}
if(args.method==='searchEverything'){const q=args.payload.query,s=structuredClone(window.__state.everythingSettings);if(q==='delayed')await new Promise(r=>setTimeout(r,500));return {status:'ready',results:q?Array.from({length:s.resultLimit},(_,i)=>({id:q+'-'+i,name:q+'-'+i,parentPath:'C:\\Files',isDirectory:false,resultType:'file'})):[],nextCursor:q&&s.batchLoading?'cursor-'+q:null}}
if(args.method==='loadMoreEverything'){window.__moreCalls=(window.__moreCalls??0)+1;await new Promise(r=>setTimeout(r,300));if(window.__failMore){window.__failMore=false;throw new Error('test offline')}return {status:'ready',results:[0,1,2].map(i=>({id:'more-'+i,name:'more-'+i,parentPath:'C:\\Files',isDirectory:false,resultType:'file'})),nextCursor:null}}
return structuredClone(window.__state)
}return null}};`
const server = createServer((req, res) => {
  const path = req.url === '/' ? '/index.html' : req.url
  if (!/^\/(index\.html|assets\/[a-zA-Z0-9_.-]+)$/.test(path)) { res.writeHead(404).end(); return }
  try {
    let body = readFileSync(join(root, path))
    if (path === '/index.html') body = Buffer.from(body.toString().replace('<head>', `<head><script>${fixture}</script>`))
    res.setHeader('Content-Type', path.endsWith('.js') ? 'application/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html')
    res.end(body)
  } catch { res.writeHead(404).end() }
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
const reserve = createServer()
await new Promise(resolve => reserve.listen(0, '127.0.0.1', resolve))
const port = reserve.address().port
await new Promise(resolve => reserve.close(resolve))
const profile = mkdtempSync(join(tmpdir(), 'qing-search-ui-'))
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new', '--no-first-run', `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`, '--window-size=960,680', `http://127.0.0.1:${server.address().port}/`], { stdio: 'ignore', windowsHide: true })
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
async function waitFor(check) {
  const until = Date.now() + 10000
  while (Date.now() < until) { if (await check()) return; await delay(60) }
  throw new Error('Search UI timed out')
}
let target, sequence = 0
async function cdp(method, params = {}) {
  const socket = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }) })
  try {
    return await new Promise((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(new Error('CDP timeout')), 5000)
      socket.addEventListener('message', event => { const result = JSON.parse(event.data); if (result.id !== id) return; clearTimeout(timer); result.error ? reject(new Error(result.error.message)) : resolve(result.result) })
      socket.send(JSON.stringify({ id, method, params }))
    })
  } finally { socket.close() }
}
async function evaluate(expression) {
  const result = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text)
  return result.result?.value
}
const input = text => evaluate(`(() => {const input=document.querySelector('.search-row input');input.focus();input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input',{bubbles:true}));return true})()`)
const key = name => evaluate(`document.querySelector('.search-row input').dispatchEvent(new KeyboardEvent('keydown',{key:${JSON.stringify(name)},bubbles:true,cancelable:true}));true`)
try {
  await waitFor(async () => { try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(tab => tab.url.startsWith(`http://127.0.0.1:${server.address().port}`)); return !!target } catch { return false } })
  await waitFor(() => evaluate(`!!document.querySelector('.launcher-shell') && !document.querySelector('.loading-card')`))
  await evaluate(`document.querySelector('.hotkey-toggle').click();true`)
  await waitFor(() => evaluate(`!!document.querySelector('.search-limit-input')`))
  assert(await evaluate(`document.querySelector('.search-limit-input').value==='200' && !document.querySelector('.batch-loading-field input').checked`))
  mkdirSync('artifacts/search-ui', { recursive: true })
  writeFileSync('artifacts/search-ui/settings.png', Buffer.from((await cdp('Page.captureScreenshot', { format: 'png' })).data, 'base64'))
  await evaluate(`(() => {const input=document.querySelector('.search-limit-input');input.value='0';input.dispatchEvent(new Event('input',{bubbles:true}));[...document.querySelectorAll('.launcher-settings-card button')].find(b=>b.textContent==='保存').click();return true})()`)
  await waitFor(() => evaluate(`document.querySelector('.launcher-search-settings').innerText.includes('1–1000')`))
  await evaluate(`(() => {const input=document.querySelector('.search-limit-input');input.value='200';input.dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('.batch-loading-field input').click();[...document.querySelectorAll('.launcher-settings-card button')].find(b=>b.textContent==='保存').click();return true})()`)
  await waitFor(() => evaluate(`!document.querySelector('.launcher-settings-card') && window.__state.everythingSettings.batchLoading`))
  await input('/')
  await waitFor(() => evaluate(`document.querySelectorAll('.command-suggestions button').length===3`))
  await key('ArrowDown'); await key('Tab')
  assert.equal(await evaluate(`document.querySelector('.search-row input').value`), '/e:f ')
  await input('/E:d')
  await waitFor(() => evaluate(`document.querySelectorAll('.command-suggestions button').length===1`))
  await evaluate(`document.querySelector('.command-suggestions button').click();true`)
  assert.equal(await evaluate(`document.querySelector('.search-row input').value`), '/e:d ')
  await input('/')
  await waitFor(() => evaluate(`!!document.querySelector('.command-suggestions')`))
  await key('Escape')
  await waitFor(() => evaluate(`!document.querySelector('.command-suggestions')`))
  assert.equal(await evaluate('window.__hides'), 0)
  await input('/e first')
  await waitFor(() => evaluate(`document.querySelectorAll('.everything-result').length===200`))
  await evaluate(`document.querySelector('.everything-result:last-of-type').dispatchEvent(new MouseEvent('mouseenter'));true`)
  await key('ArrowDown')
  await waitFor(() => evaluate(`document.querySelectorAll('.everything-result').length===203 && document.querySelector('.everything-result.selected')?.id==='everything-result-more-0'`))
  assert(await evaluate(`document.querySelector('.everything-list').scrollTop>0`))
  await input('/e retry')
  await waitFor(() => evaluate(`document.querySelectorAll('.everything-result').length===200`))
  await evaluate(`window.__failMore=true;document.querySelector('.everything-more button').click();true`)
  await waitFor(() => evaluate(`document.querySelector('.everything-more').innerText.includes('test offline')`))
  assert.equal(await evaluate(`document.querySelectorAll('.everything-result').length`), 200)
  await evaluate(`document.querySelector('.everything-more button').click();true`)
  await waitFor(() => evaluate(`document.querySelectorAll('.everything-result').length===203`))
  await input('/e delayed'); await delay(140); await input('/e fresh')
  await waitFor(() => evaluate(`document.querySelector('.everything-result')?.innerText.includes('fresh-0')`))
  await delay(600)
  assert(await evaluate(`document.querySelectorAll('.everything-result').length===200 && !document.querySelector('.everything-list').innerText.includes('delayed-')`))
  await evaluate(`document.querySelector('.everything-more button').click();true`)
  await input('/e newest')
  await waitFor(() => evaluate(`document.querySelector('.everything-result')?.innerText.includes('newest-0')`))
  await delay(500)
  assert(await evaluate(`document.querySelectorAll('.everything-result').length===200 && !document.querySelector('.everything-list').innerText.includes('more-')`))
  await key('Escape')
  await waitFor(() => evaluate(`!!document.querySelector('[data-id="app"]') && !document.querySelector('.everything-panel')`))
  console.log('Search UI passed: defaults, validation, settings save, slash/mouse/Tab completion with space, Escape isolation, 200-row scrolling, keyboard batch selection, page retry, stale query/page suppression and normal-mode restoration.')
} finally {
  browser.kill()
  await new Promise(resolve => server.close(resolve))
  const withinTemp = relative(resolve(tmpdir()), resolve(profile))
  assert(withinTemp && !withinTemp.startsWith('..') && !isAbsolute(withinTemp))
  try { rmSync(profile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }) } catch { /* Edge may still be closing its own temporary profile. */ }
}
