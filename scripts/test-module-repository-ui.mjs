// Browser regression only: fixtures cannot download/install actual packages.
import { createServer } from 'node:net'
import { spawn } from 'node:child_process'
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'
import assert from 'node:assert/strict'

const reserve = async () => {
  const server = createServer(); await new Promise(r => server.listen(0, '127.0.0.1', r))
  const port = server.address().port; await new Promise(r => server.close(r)); return port
}
const port = await reserve(), debug = await reserve()
const root = resolve('QingToolbox.WebUI')
const vite = spawn(process.execPath, [join(root, 'node_modules/vite/bin/vite.js'), '--mode', 'mock', '--host', '127.0.0.1', '--port', String(port)], { cwd: root, stdio: 'ignore', windowsHide: true })
const profile = mkdtempSync(join(tmpdir(), 'qing-module-repository-ui-'))
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new','--no-first-run',`--user-data-dir=${profile}`,`--remote-debugging-port=${debug}`,'--window-size=1100,760','about:blank'], { stdio: 'ignore', windowsHide: true })
const delay = ms => new Promise(r => setTimeout(r, ms))
async function wait(check) { const deadline=Date.now()+15000; while(Date.now()<deadline) { if(await check())return; await delay(80) } throw Error('Repository UI timed out') }
let sequence=1
let socket
const pending=new Map()
async function cdp(target,method,params={}) {
  if(!socket) {
    socket=new WebSocket(target.webSocketDebuggerUrl)
    await new Promise((r,j)=>{socket.addEventListener('open',r,{once:true});socket.addEventListener('error',j,{once:true})})
    socket.addEventListener('message',event=>{const msg=JSON.parse(event.data);const request=pending.get(msg.id);if(!request)return;pending.delete(msg.id);clearTimeout(request.timeout);msg.error?request.reject(Error(msg.error.message)):request.resolve(msg.result)})
  }
  return await new Promise((resolve,reject)=>{const id=sequence++,timeout=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout'))},5000);pending.set(id,{resolve,reject,timeout});socket.send(JSON.stringify({id,method,params}))})
}
async function evaluate(target,expression) { const result=await cdp(target,'Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(result.exceptionDetails)throw Error(result.exceptionDetails.exception?.description??result.exceptionDetails.text);return result.result?.value }
const fixture = `window.__calls=[];window.__catalogError=false;window.__download={jobId:0,moduleId:'',name:'',status:'',bytesReceived:0,expectedBytes:0,savedPath:'',error:''};
window.__TAURI_INTERNALS__={transformCallback:()=>1,unregisterCallback:()=>{},invoke:async(command,args)=>{
    window.__calls.push({command,args});if(command==='get_official_modules'){if(window.__catalogError)throw {code:'NetworkUnavailable'};return ['launcher','pdf','texttools','screenpin','windowtopmost','powerguard'].map((id,index)=>({id:'qing.'+id,name:{'en-US':'Module '+id,'zh-CN':['启动台','PDF 工具','文本工具','截图贴图','窗口置顶','断电保护'][index]},description:{'en-US':'Local module tools','zh-CN':'独立模块，按需下载和安装'},version:'0.3.0',apiVersion:1,size:1400000,canDownload:true,unavailableReason:''}))}
if(command==='get_module_repository_download')return structuredClone(window.__download);
if(command==='download_official_module'){window.__download={jobId:1,moduleId:args.moduleId,name:'启动台',status:'Downloading',bytesReceived:420000,expectedBytes:1400000,savedPath:'',error:''};return structuredClone(window.__download)};return null}};`
let target
try {
  await wait(async()=>{try{target=(await(await fetch(`http://127.0.0.1:${debug}/json/list`)).json()).find(t=>t.type==='page');return !!target}catch{return false}})
  await wait(async()=>{try{return(await fetch(`http://127.0.0.1:${port}/`)).ok}catch{return false}})
  await cdp(target,'Page.enable')
  await evaluate(target,fixture)
  await cdp(target,'Page.addScriptToEvaluateOnNewDocument',{source:fixture})
  await cdp(target,'Page.navigate',{url:`http://127.0.0.1:${port}/#/modules`})
  await wait(()=>evaluate(target,`!!document.querySelector('.module-import-button')&&!document.querySelector('.module-import-button').disabled`))
  await evaluate(target,`document.querySelector('#app').__vue_app__.config.globalProperties.$pinia._s.get('settings').snapshot.language.effectiveCode='zh-CN';true`)
  await evaluate(target,`document.querySelector('.module-import-button').click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.module-import-repository')`))
  assert(await evaluate(target,`document.querySelector('.module-import-menu').textContent.includes('从本地导入')`))
  await evaluate(target,`document.querySelector('.module-import-repository').click();true`)
  await wait(()=>evaluate(target,`document.querySelectorAll('.official-module-option').length===6`))
  assert(await evaluate(target,`document.querySelector('.official-module-download').disabled`))
  await evaluate(target,`document.querySelector('.official-module-option').click();true`)
  assert(await evaluate(target,`!document.querySelector('.official-module-download').disabled`))
  mkdirSync('artifacts/ui-check',{recursive:true})
  const capture=async(name)=>{await delay(300);const shot=await cdp(target,'Page.captureScreenshot',{format:'png'});writeFileSync(`artifacts/ui-check/${name}.png`,Buffer.from(shot.data,'base64'))}
  await capture('official-module-dialog')
  for(const [width,height] of [[760,520],[1600,1000]]) {
    await cdp(target,'Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false})
    assert(await evaluate(target,`(()=>{const r=document.querySelector('.official-module-dialog').getBoundingClientRect();const b=document.querySelector('.official-module-download').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&b.bottom<=innerHeight})()`))
  }
  await cdp(target,'Emulation.setDeviceMetricsOverride',{width:1100,height:760,deviceScaleFactor:1,mobile:false})
  await evaluate(target,`document.documentElement.dataset.theme='dark';true`)
  await capture('official-module-dialog-dark')
  await evaluate(target,`document.documentElement.dataset.theme='light';document.querySelector('.official-module-download').click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.q-module-download-status')&&!document.querySelector('.official-module-dialog')`))
  assert(await evaluate(target,`document.querySelector('.q-module-download-status').textContent.includes('30%')`))
  await evaluate(target,`document.querySelector('a[href="#/"]').click();true`)
  await delay(600)
  assert(await evaluate(target,`!!document.querySelector('.q-module-download-status')`))
  await evaluate(target,`window.__download.status='Installing';window.__download.bytesReceived=1400000;true`)
  await wait(()=>evaluate(target,`document.querySelector('.q-module-download-status')?.textContent.includes('正在安装')`))
  await evaluate(target,`location.hash='#/modules';true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.module-import-button')`))
  assert(await evaluate(target,`document.querySelector('.module-import-button').disabled`))
  await evaluate(target,`window.__download.status='Completed';window.__download.bytesReceived=1400000;true`)
  await wait(()=>evaluate(target,`!document.querySelector('.q-module-download-status')`))
  assert(await evaluate(target,`window.__calls.filter(c=>c.command==='download_official_module').length===1&&window.__calls.every(c=>c.command!=='import_module')`))
  await evaluate(target,`location.hash='#/modules';true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.module-import-button')`))
  await evaluate(target,`window.__catalogError=true;document.querySelector('.module-import-button').click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.module-import-repository')`))
  await evaluate(target,`document.querySelector('.module-import-repository').click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.official-module-error')`))
  await evaluate(target,`location.hash='#/settings';true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.settings-section-nav')`))
  await evaluate(target,`[...document.querySelectorAll('.settings-section-nav button')].find(b=>b.textContent.includes('启动')).click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.update-check-settings')`))
  assert(await evaluate(target,`document.querySelector('.check-host-updates').getAttribute('aria-checked')==='true'&&document.querySelector('.check-module-updates').getAttribute('aria-checked')==='true'`))
  await evaluate(target,`document.querySelector('.check-host-updates').click();true`)
  await wait(()=>evaluate(target,`document.querySelector('.check-host-updates').getAttribute('aria-checked')==='false'`))
  await evaluate(target,`(()=>{for(const [selector,value] of [['.host-update-interval','0'],['.module-update-interval','15']]){const input=document.querySelector(selector);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}))}return true})()`)
  await delay(100)
  await evaluate(target,`document.querySelector('.save-update-intervals').click();true`)
  await wait(()=>evaluate(target,`document.querySelector('#app').__vue_app__.config.globalProperties.$pinia._s.get('settings').snapshot.moduleUpdateIntervalMinutes===15`))
  await evaluate(target,`document.querySelector('#app').__vue_app__.config.globalProperties.$pinia._s.get('settings').snapshot.language.effectiveCode='zh-CN';true`)
  await capture('update-check-settings')
  await cdp(target,'Emulation.setDeviceMetricsOverride',{width:760,height:760,deviceScaleFactor:1,mobile:false})
  assert(await evaluate(target,`(()=>{const r=document.querySelector('.update-check-settings').getBoundingClientRect();return r.right<=innerWidth&&r.left>=0})()`))
  await cdp(target,'Emulation.setDeviceMetricsOverride',{width:1100,height:760,deviceScaleFactor:1,mobile:false})
  await evaluate(target,`location.hash='#/modules';true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.wpf-module-card')`))
  await evaluate(target,`document.querySelector('.wpf-module-card').click();true`)
  await wait(()=>evaluate(target,`!!document.querySelector('.module-update-check-toggle')`))
  await evaluate(target,`document.querySelector('.module-update-check-toggle').click();true`)
  await wait(()=>evaluate(target,`document.querySelector('.module-update-check-toggle').getAttribute('aria-checked')==='false'`))
  await evaluate(target,`(()=>{const store=document.querySelector('#app').__vue_app__.config.globalProperties.$pinia._s.get('modules');store.modules.find(m=>m.id===store.selectedModuleId).updateStatus='NotOfficial';return true})()`)
  await delay(100)
  assert(await evaluate(target,`document.querySelector('.wpf-module-details').textContent.includes('该模块未在官方仓库中记录')`))
  await evaluate(target,`document.querySelector('.module-update-check-setting').scrollIntoView({block:'center'});true`)
  await capture('module-update-checks')
  console.log('Repository flow and progress; startup switches, persisted intervals, responsive settings, per-module check switch and official-identity warning passed.')
  await cdp(target,'Browser.close').catch(()=>{})
} catch(error) {
  if(target)console.error(await evaluate(target,`({text:document.body.innerText.slice(0,2000),calls:window.__calls})`).catch(()=>null))
  throw error
} finally { socket?.close(); browser.kill(); vite.kill() }
