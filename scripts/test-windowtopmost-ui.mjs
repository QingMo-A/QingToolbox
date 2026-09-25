import { createServer } from 'node:http'
import { createServer as portServer } from 'node:net'
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'
import { spawn } from 'node:child_process'

const root = resolve('QingToolbox.Tauri/native-windowtopmost/ui-src/dist')
const fixture = `window.__callbacks={};window.__callbackId=0;window.__calls=[];
window.__state={windows:[{id:'w-AA',title:'Alpha window',processName:'alpha.exe',processId:101,handleText:'0xAA',isTopmost:false},{id:'w-BB',title:'Beta window',processName:'beta.exe',processId:202,handleText:'0xBB',isTopmost:false}],selectedWindowId:null,status:'ready',error:null};
window.__TAURI_INTERNALS__={transformCallback:fn=>{window.__callbacks[++window.__callbackId]=fn;return window.__callbackId},unregisterCallback:()=>{},invoke:async(cmd,args)=>{
 if(cmd==='get_module_window_context')return {moduleId:'qing.windowtopmost',name:'Window Topmost',version:'test',iconDataUrl:null,protocolVersion:1,operations:[]};
 if(cmd==='hide_module_window')return;
 if(cmd!=='invoke_module_window')return null;
 const {method,payload}=args;window.__calls.push({method,payload});
 if(method==='getState'){
   const snapshot=structuredClone(window.__state);
   if(window.__holdNextPoll){window.__holdNextPoll=false;return new Promise(resolve=>{window.__releasePoll=()=>{window.__releasePoll=null;resolve(snapshot)}})}
   return snapshot;
 }
 if(method==='selectWindow'){window.__state.selectedWindowId=payload.windowId;window.__state.status='selected'}
 if(method==='setTopmost'||method==='removeTopmost'){window.__state.windows.find(item=>item.id===payload.windowId).isTopmost=method==='setTopmost';window.__state.status=method==='setTopmost'?'topmostSet':'topmostRemoved'}
 if(method==='clearSelection'){window.__state.selectedWindowId=null;window.__state.status='ready'}
 if(method==='pickWindow'){window.__state.selectedWindowId='w-AA';window.__state.status='selected'}
 return structuredClone(window.__state);
}};`
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
const reserve = portServer()
await new Promise(resolve => reserve.listen(0, '127.0.0.1', resolve))
const port = reserve.address().port
await new Promise(resolve => reserve.close(resolve))
const profile = mkdtempSync(join(tmpdir(), 'qing-windowtopmost-ui-'))
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
  '--headless=new', '--no-first-run', `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`,
  '--window-size=1050,700', `http://127.0.0.1:${server.address().port}/`,
], { stdio: 'ignore', windowsHide: true })
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
async function waitFor(check, timeout = 8000) {
  const until = Date.now() + timeout
  while (Date.now() < until) { if (await check()) return; await delay(70) }
  throw new Error('Window Topmost UI check timed out')
}
let sequence = 1
async function cdp(target, method, params = {}) {
  const socket = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }) })
  try {
    return await new Promise((resolve, reject) => {
      const id = sequence++, timer = setTimeout(() => reject(new Error('CDP timed out')), 5000)
      socket.addEventListener('message', event => { const message = JSON.parse(event.data); if (message.id !== id) return; clearTimeout(timer); message.error ? reject(new Error(message.error.message)) : resolve(message.result) })
      socket.send(JSON.stringify({ id, method, params }))
    })
  } finally { socket.close() }
}
async function evaluate(target, expression) {
  const result = await cdp(target, 'Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text)
  return result.result?.value
}
try {
  let target
  await waitFor(async () => { try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(tab => tab.url.startsWith(`http://127.0.0.1:${server.address().port}`)); return !!target } catch { return false } })
  await waitFor(() => evaluate(target, `document.querySelectorAll('.window-row').length===2`))
  const layout = await evaluate(target, `(() => { const actions=document.querySelector('.actions').getBoundingClientRect();const table=document.querySelector('.table-card').getBoundingClientRect();return actions.bottom<table.top })()`)
  if (!layout) throw new Error('Topmost action buttons are not above the window list')
  await evaluate(target, `document.querySelectorAll('.window-row')[0].click();true`)
  await waitFor(() => evaluate(target, `document.querySelectorAll('.window-row')[0].getAttribute('aria-pressed')==='true' && !document.querySelector('.actions .primary').disabled`))
  await delay(1250)
  if (!await evaluate(target, `document.querySelectorAll('.window-row')[0].classList.contains('selected')`)) throw new Error('Polling cleared the selected list row')
  await evaluate(target, `document.querySelector('.actions .primary').click();true`)
  await waitFor(() => evaluate(target, `document.querySelectorAll('.window-row')[0].querySelector('.pill').textContent==='是'`))
  if (!await evaluate(target, `window.__calls.some(call=>call.method==='setTopmost' && call.payload.windowId==='w-AA')`)) throw new Error('Topmost action did not use the selected row id')
  await evaluate(target, `window.__holdNextPoll=true;true`)
  await waitFor(() => evaluate(target, `typeof window.__releasePoll==='function'`), 2500)
  await evaluate(target, `document.querySelectorAll('.window-row')[1].click();true`)
  await waitFor(() => evaluate(target, `document.querySelectorAll('.window-row')[1].classList.contains('selected')`))
  await evaluate(target, `window.__releasePoll();true`)
  await delay(120)
  if (!await evaluate(target, `document.querySelectorAll('.window-row')[1].classList.contains('selected')`)) throw new Error('An older poll response overwrote the newer selection')
  await evaluate(target, `document.querySelector('.actions button:last-child').click();true`)
  await waitFor(() => evaluate(target, `!document.querySelector('.window-row.selected') && document.querySelector('.actions .primary').disabled`))
  await evaluate(target, `document.querySelector('.pick-button').click();true`)
  await waitFor(() => evaluate(target, `document.querySelectorAll('.window-row')[0].classList.contains('selected')`))
  console.log('Window Topmost list selection, action placement, polling race, clear and picker passed.')
  const screenshot = await cdp(target, 'Page.captureScreenshot', { format: 'png' })
  mkdirSync('artifacts/ui-check', { recursive: true })
  writeFileSync('artifacts/ui-check/windowtopmost.png', Buffer.from(screenshot.data, 'base64'))
  await cdp(target, 'Browser.close').catch(() => {})
} finally { browser.kill(); server.close() }
