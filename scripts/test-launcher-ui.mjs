import { createServer } from 'node:http'
import { createServer as portServer } from 'node:net'
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'
import { spawn } from 'node:child_process'
import { verifyLauncherDrag } from './launcher-drag-check.mjs'

const root = resolve('QingToolbox.Tauri/native-launcher/ui-src/dist')
const fixture = `window.__callbacks={}; window.__listeners={}; window.__callbackId=0; window.__hides=0;
window.__emit=(event,payload)=>{for(const handler of window.__listeners[event]??[])window.__callbacks[handler]?.({event,id:handler,payload})};
window.__TAURI_INTERNALS__={transformCallback:fn=>{window.__callbacks[++window.__callbackId]=fn;return window.__callbackId},unregisterCallback:()=>{},invoke:async(cmd,args)=>{
 if(cmd==='plugin:event|listen'){(window.__listeners[args.event]??=[]).push(args.handler);return args.handler}
 if(cmd==='hide_module_window'){window.__hides++;return}
 if(cmd==='get_module_window_context')return {name:'Qing Launcher',version:'test',operations:[],iconDataUrl:null};
 if(cmd==='invoke_module_window')return {sortMode:'custom',items:[],folders:[],customOrder:[],recent:[],hotkey:{ctrl:true,alt:true,shift:false,win:false,virtualKey:76,keyLabel:'L'},hotkeyStatus:'HostManaged',active:true};
 return null;
}};`
const server = createServer((req,res) => {
  const path = req.url === '/' ? '/index.html' : req.url
  if (!/^\/(index\.html|assets\/[a-zA-Z0-9_.-]+)$/.test(path)) {res.writeHead(404).end();return}
  try {
    let body=readFileSync(join(root,path))
    if(path==='/index.html')body=Buffer.from(body.toString().replace('<head>','<head><script>'+fixture+'</script>'))
    res.setHeader('Content-Type',path.endsWith('.js')?'application/javascript':path.endsWith('.css')?'text/css':'text/html');res.end(body)
  }catch{res.writeHead(404).end()}
})
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve))
const reserve=portServer();await new Promise(resolve=>reserve.listen(0,'127.0.0.1',resolve));const port=reserve.address().port;await new Promise(resolve=>reserve.close(resolve))
const profile=mkdtempSync(join(tmpdir(),'qing-launcher-ui-'))
const browser=spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',[
 '--headless=new','--no-first-run',`--user-data-dir=${profile}`,`--remote-debugging-port=${port}`,'--window-size=960,680',`http://127.0.0.1:${server.address().port}/`,
],{stdio:'ignore',windowsHide:true})
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms))
async function waitFor(check,timeout=10000){const until=Date.now()+timeout;while(Date.now()<until){if(await check())return;await delay(80)}throw new Error('UI check timed out')}
let sequence=1
async function cdp(target,method,params={}){
 const socket=new WebSocket(target.webSocketDebuggerUrl)
 await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true})})
 try{return await new Promise((resolve,reject)=>{
  const id=sequence++,timer=setTimeout(()=>reject(new Error('CDP timed out')),5000)
  socket.addEventListener('message',event=>{const m=JSON.parse(event.data);if(m.id!==id)return;clearTimeout(timer);m.error?reject(new Error(m.error.message)):resolve(m.result)})
  socket.send(JSON.stringify({id,method,params}))
 })}finally{socket.close()}
}
async function evaluate(target,expression){const r=await cdp(target,'Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.exception?.description??r.exceptionDetails.text);return r.result?.value}
async function mouse(target,type,x,y){return cdp(target,'Input.dispatchMouseEvent',{type,x,y,button:type==='mouseMoved'?'none':'left',buttons:type==='mouseReleased'?0:1,clickCount:type==='mouseMoved'?0:1})}
try {
 let target
 await waitFor(async()=>{try{target=(await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t=>t.url.startsWith(`http://127.0.0.1:${server.address().port}`));return !!target}catch{return false}})
 await waitFor(()=>evaluate(target,`!!document.querySelector('.launcher-shell') && !document.querySelector('.loading-card')`))
 await verifyLauncherDrag(target,{evaluate,mouse,delay,waitFor})
 await evaluate(target, `(() => {
   window.__desktopState = { sortMode:'desktop', items:[{id:'desk-one',name:'桌面应用',iconKey:null,source:'desktop',lastLaunchedAt:null}], folders:[], customOrder:[], recent:[], hotkey:{ctrl:true,alt:true,shift:false,win:false,virtualKey:76,keyLabel:'L'},hotkeyStatus:'HostManaged',active:true };
   window.__desktopDropCalls = [];
   window.__everythingQueries = 0;
   const original = window.__TAURI_INTERNALS__.invoke;
   window.__TAURI_INTERNALS__.invoke = async (cmd,args) => {
     if (cmd === 'invoke_module_window') {
       if (args.method === 'addDesktopItemToCustom') {
         window.__desktopDropCalls.push(args.payload);
         window.__desktopState = {...window.__desktopState,sortMode:'custom',items:[{id:'custom-one',name:'桌面应用',iconKey:null,source:'custom',lastLaunchedAt:null}],customOrder:['custom-one']};
         return structuredClone(window.__desktopState);
       }
       if (args.method === 'refreshDesktop' || args.method === 'getState') return structuredClone(window.__desktopState);
       if (args.method === 'searchEverything') {
         window.__everythingQueries++;
         return {requestId:args.payload.requestId,mode:args.payload.mode,query:args.payload.query,status:window.__everythingQueries===1?'indexing':'ready',results:window.__everythingQueries===1?[]:[{id:'result-one',name:'match.exe',parentPath:'C:\\\\Apps',isDirectory:false,resultType:'file'}],error:window.__everythingQueries===1?'索引尚未完成。':undefined};
       }
     }
     return original(cmd,args);
   };
   document.querySelector('.refresh').click(); return true;
 })()`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('[data-id="desk-one"]')`))
 const desktopDrag = await evaluate(target,`(() => { const source=document.querySelector('[data-id="desk-one"]').getBoundingClientRect(); const tab=document.querySelector('[data-mode="custom"]').getBoundingClientRect(); return {sx:source.x+source.width/2,sy:source.y+44,tx:tab.x+tab.width/2,ty:tab.y+tab.height/2}; })()`)
 await mouse(target,'mousePressed',desktopDrag.sx,desktopDrag.sy)
 await mouse(target,'mouseMoved',desktopDrag.sx+12,desktopDrag.sy)
 if(!await evaluate(target,`document.querySelector('[data-mode="custom"]').classList.contains('desktop-drop-target')`))throw new Error('Custom tab does not signal desktop drag')
 await mouse(target,'mouseMoved',desktopDrag.tx,desktopDrag.ty)
 if(!await evaluate(target,`document.querySelector('[data-mode="custom"]').classList.contains('desktop-drop-hover')`))throw new Error('Custom tab does not highlight drop zone')
 await mouse(target,'mouseReleased',desktopDrag.tx,desktopDrag.ty)
 await waitFor(()=>evaluate(target,`document.querySelector('[data-mode="custom"]').classList.contains('active') && !!document.querySelector('[data-id="custom-one"]')`))
 if(!await evaluate(target,`window.__desktopDropCalls.length===1 && JSON.stringify(window.__desktopDropCalls[0])==='{"id":"desk-one"}'`))throw new Error('Desktop drop did not use an id-only backend call')
 await evaluate(target,`(() => { const input=document.querySelector('.search-row input');input.value='/e match';input.dispatchEvent(new Event('input',{bubbles:true}));return true })()`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('.everything-retry') && document.body.innerText.includes('索引尚未就绪')`))
 await evaluate(target,`document.querySelector('.everything-retry').click();true`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('#everything-result-result-one')`))
 await evaluate(target,`document.querySelector('.clear').click();true`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('[data-id="custom-one"]') && !document.querySelector('.everything-panel')`))
 console.log('Desktop-to-Custom drag, target animation, id-only save, Everything indexing hint and retry passed.')
 await evaluate(target, `(() => {
   window.__removeCalls = []; window.__removeLaunches = 0;
   const original = window.__TAURI_INTERNALS__.invoke;
   window.__TAURI_INTERNALS__.invoke = async (cmd,args) => {
     if (cmd === 'invoke_module_window' && args.method === 'removeItem') {
       window.__removeCalls.push(args.payload);
       window.__desktopState.items = window.__desktopState.items.filter(item => item.id !== args.payload.id);
       window.__desktopState.customOrder = window.__desktopState.customOrder.filter(id => id !== args.payload.id);
       return structuredClone(window.__desktopState);
     }
     if (cmd === 'invoke_module_window' && args.method === 'launchItem') { window.__removeLaunches++; return structuredClone(window.__desktopState) }
     if (cmd === 'invoke_module_window' && args.method === 'setSortMode') { window.__desktopState.sortMode = args.payload.mode; return structuredClone(window.__desktopState) }
     return original(cmd,args);
   };
   return true;
 })()`)
 if(!await evaluate(target,`!!document.querySelector('[data-id="custom-one"] .app-remove')`))throw new Error('Custom app delete control missing')
 const removeIcon = await evaluate(target,`(() => { const r=document.querySelector('[data-id="custom-one"] .app-icon').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2} })()`)
 await cdp(target,'Input.dispatchMouseEvent',{type:'mouseMoved',x:removeIcon.x,y:removeIcon.y,button:'none',buttons:0})
 await waitFor(()=>evaluate(target,`Number(getComputedStyle(document.querySelector('[data-id="custom-one"] .app-remove')).opacity) > .9`))
 if(!await evaluate(target,`(() => { const icon=document.querySelector('[data-id="custom-one"] .app-icon').getBoundingClientRect();const button=document.querySelector('[data-id="custom-one"] .app-remove').getBoundingClientRect();return button.x > icon.x+icon.width/2 && button.y < icon.y+icon.height/2 })()`))throw new Error('Delete control is not positioned at the icon upper-right')
 await evaluate(target,`document.querySelector('[data-id="custom-one"] .app-remove').click();true`)
 await waitFor(()=>evaluate(target,`!document.querySelector('[data-id="custom-one"]')`))
 if(!await evaluate(target,`window.__removeCalls.length===1 && JSON.stringify(window.__removeCalls[0])==='{"id":"custom-one"}' && window.__removeLaunches===0`))throw new Error('Custom delete did not remove only the app')
 await evaluate(target,`(() => { window.__desktopState.items=[{id:'custom-two',name:'第二个应用',iconKey:null,source:'custom',lastLaunchedAt:null}];window.__desktopState.customOrder=['custom-two'];document.querySelector('.refresh').click();return true })()`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('[data-id="custom-two"]')`))
 await evaluate(target,`document.querySelector('[data-mode="alphabetical"]').click();true`)
 await waitFor(()=>evaluate(target,`document.querySelector('[data-mode="alphabetical"]').classList.contains('active')`))
 if(!await evaluate(target,`!!document.querySelector('[data-id="custom-two"] .app-remove')`))throw new Error('Alphabetical app delete control missing')
 await evaluate(target,`document.querySelector('[data-id="custom-two"] .app-remove').click();true`)
 await waitFor(()=>evaluate(target,`!document.querySelector('[data-id="custom-two"]')`))
 if(!await evaluate(target,`window.__removeCalls.length===2 && window.__removeCalls[1].id==='custom-two' && window.__removeLaunches===0`))throw new Error('Alphabetical delete launched the app or failed')
 await evaluate(target,`(() => { window.__desktopState.sortMode='desktop';window.__desktopState.items=[{id:'desk-one',name:'桌面应用',iconKey:null,source:'desktop',lastLaunchedAt:null}];document.querySelector('.refresh').click();return true })()`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('[data-id="desk-one"]')`))
 if(await evaluate(target,`!!document.querySelector('[data-id="desk-one"] .app-remove')`))throw new Error('Desktop item incorrectly exposes delete control')
 console.log('Custom and alphabetical icon deletion, launch isolation and Desktop protection passed.')
 await evaluate(target, `(() => {
   const original = window.__TAURI_INTERNALS__.invoke;
   window.__folderMoves = [];
   window.__TAURI_INTERNALS__.invoke = async (cmd,args) => {
     if (cmd === 'invoke_module_window' && args.method === 'moveItemToFolder') {
       window.__folderMoves.push(args.payload);
       const item = window.__desktopState.items.find(value => value.id === args.payload.itemId);
       window.__desktopState.items = window.__desktopState.items.filter(value => value.id !== args.payload.itemId);
       window.__desktopState.customOrder = window.__desktopState.customOrder.filter(id => id !== args.payload.itemId);
       window.__desktopState.folders.find(folder => folder.id === args.payload.folderId).items.push(item);
       return structuredClone(window.__desktopState);
     }
     return original(cmd,args);
   };
   window.__desktopState = {...window.__desktopState,sortMode:'custom',items:[{id:'folder-app',name:'待归类应用',iconKey:null,source:'custom',lastLaunchedAt:null},{id:'other-app',name:'其他应用',iconKey:null,source:'custom',lastLaunchedAt:null}],folders:[{id:'folder-one',name:'工具',items:[]}],customOrder:['folder-app','other-app','folder-one']};
   document.querySelector('.refresh').click();return true;
 })()`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('[data-id="folder-app"]') && !!document.querySelector('[data-id="folder-one"]')`))
 if(!await evaluate(target,`(() => { const folder=document.querySelector('[data-id="folder-one"] .folder-preview'); const app=document.querySelector('[data-id="folder-app"] .app-icon');return folder.querySelectorAll('.tile-icon-action').length===2 && app.querySelector('.tile-icon-action') && folder.querySelector('.tile-icon-actions') && app.querySelector('.tile-icon-actions') })()`))throw new Error('Folder controls do not share the app icon action style')
 const folderDragSource = await evaluate(target,`(() => {const r=document.querySelector('[data-id="folder-app"]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+44}})()`)
 await mouse(target,'mousePressed',folderDragSource.x,folderDragSource.y)
 await mouse(target,'mouseMoved',folderDragSource.x+12,folderDragSource.y)
 await delay(380)
 const folderDrop = await evaluate(target,`(() => {const r=document.querySelector('[data-id="folder-one"] .folder-preview').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`)
 await mouse(target,'mouseMoved',folderDrop.x,folderDrop.y)
 await waitFor(()=>evaluate(target,`document.querySelector('[data-id="folder-one"]').classList.contains('folder-target')`))
 await mouse(target,'mouseReleased',folderDrop.x,folderDrop.y)
 await waitFor(()=>evaluate(target,`!document.querySelector('[data-id="folder-app"]') && document.querySelector('[data-id="folder-one"] .folder-preview-icon')`))
 if(!await evaluate(target,`window.__folderMoves.length===1 && window.__folderMoves[0].itemId==='folder-app' && window.__folderMoves[0].folderId==='folder-one'`))throw new Error('Drop did not move app into the folder')
 await delay(450)
 await evaluate(target,`document.querySelector('[data-id="folder-one"]').click();true`)
 await waitFor(()=>evaluate(target,`!!document.querySelector('.folder-panel-card')`))
 if(!await evaluate(target,`document.querySelector('.folder-panel-card').innerText.includes('待归类应用')`))throw new Error('Moved app is missing from the folder')
 if(!await evaluate(target,`(() => {const panel=document.querySelector('.folder-panel');return panel.classList.contains('folder-open-enter-active') || panel.classList.contains('folder-open-enter-from')})()`))throw new Error('Folder open transition did not run')
 await evaluate(target,`document.querySelector('.folder-close').click();true`)
 await waitFor(()=>evaluate(target,`!document.querySelector('.folder-panel')`))
 console.log('Folder icon controls, grid-to-folder move and open animation passed.')
 const geometry = await evaluate(target, `(() => { const r=document.querySelector('.launcher-shell').getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,vw:innerWidth,vh:innerHeight,bg:getComputedStyle(document.body).backgroundColor}})()`)
 if(Math.abs(geometry.x*2+geometry.w-geometry.vw)>2 || Math.abs(geometry.y*2+geometry.h-geometry.vh)>2 || geometry.bg!=='rgba(0, 0, 0, 0)')throw new Error('Launcher panel is not centered on a transparent surface')
 await delay(500)
 await mouse(target,'mousePressed',8,8);await mouse(target,'mouseReleased',8,8)
 if(await evaluate(target,'window.__hides')!==1)throw new Error('Blank-area click did not hide launcher')
 await evaluate(target,`window.__emit('launcher:external-drag',{active:true});true`)
 await mouse(target,'mousePressed',8,8);await mouse(target,'mouseReleased',8,8)
 if(await evaluate(target,'window.__hides')!==1)throw new Error('External file drag incorrectly dismissed launcher')
 if(!await evaluate(target,`!!document.querySelector('.external-drop-hint')`))throw new Error('External drop hint missing')
 await evaluate(target,`window.__emit('launcher:external-drag',{active:false});true`)
 await mouse(target,'mousePressed',8,8);await mouse(target,'mouseReleased',8,8)
 if(await evaluate(target,'window.__hides')!==1)throw new Error('Drop-release guard missing')
 await delay(700)
 await mouse(target,'mousePressed',8,8);await mouse(target,'mouseReleased',8,8)
 if(await evaluate(target,'window.__hides')!==2)throw new Error('Dismissal did not recover after drop')
 console.log('Launcher transparent centering, blank click and external-drop protection passed.')
 const screenshot=await cdp(target,'Page.captureScreenshot',{format:'png'})
 mkdirSync('artifacts/ui-check',{recursive:true});writeFileSync('artifacts/ui-check/launcher.png',Buffer.from(screenshot.data,'base64'))
 console.log('Launcher browser interaction checks passed.')
 await cdp(target,'Browser.close').catch(()=>{})
} finally {browser.kill();server.close()}
