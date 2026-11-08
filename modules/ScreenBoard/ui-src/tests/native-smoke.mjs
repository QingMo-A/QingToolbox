// Opt-in native check: creates isolated module data and briefly shows only
// this module's overlay. Never drives unrelated applications or desktop input.
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {resolve} from 'node:path'
import {mkdir} from 'node:fs/promises'
import assert from 'node:assert/strict'
const {chromium}=await import(process.env.QING_PLAYWRIGHT_PATH??'playwright')
const exe=resolve(process.argv[2]??'../target/release/qing-screenboard-module.exe')
const data=resolve('../../../artifacts/screenboard/native-'+Date.now());await mkdir(data,{recursive:true})
const child=spawn(exe,[],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,QINGTOOLBOX_MODULE_NONCE:'screenboard-isolated-smoke',QINGTOOLBOX_MODULE_DATA_DIR:data,WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:'--remote-debugging-port=14971'}})
const pending=new Map();let sequence=0,stderr='',browser
child.stderr.on('data',v=>{stderr=(stderr+v).slice(-6000)})
createInterface({input:child.stdout}).on('line',line=>{try{const v=JSON.parse(line),job=pending.get(v.requestId);if(job){clearTimeout(job.timer);pending.delete(v.requestId);v.error?job.reject(Error(v.error.message)):job.resolve(v.payload)}}catch(e){stderr+='\nUnexpected protocol output: '+String(e)}})
function request(kind,payload={}){return new Promise((resolve,reject)=>{const requestId=String(++sequence),timer=setTimeout(()=>{pending.delete(requestId);reject(Error('Protocol timeout: '+kind+'\n'+stderr))},20000);pending.set(requestId,{resolve,reject,timer});child.stdin.write(JSON.stringify({protocolVersion:1,messageType:kind,requestId,payload})+'\n')})}
const call=(method,payload={})=>request('module.invoke.request',{method,payload})
const sleep=ms=>new Promise(r=>setTimeout(r,ms))
try{
 const hello=await request('module.hello.request',{moduleId:'qing.screenboard',nonce:'screenboard-isolated-smoke'});assert.equal(hello.moduleId,'qing.screenboard')
 await request('module.lifecycle.request',{active:true})
 for(let i=0;i<40;i++){try{browser=await chromium.connectOverCDP('http://127.0.0.1:14971');break}catch{await sleep(250)}}
 assert.ok(browser,'native WebView2 debugging endpoint ready')
 let canvas,tools
 for(let i=0;i<60;i++){const pages=browser.contexts().flatMap(c=>c.pages());canvas=pages.find(p=>p.url().includes('surface=canvas'));tools=pages.find(p=>p.url().includes('surface=tools'));if(canvas&&tools)break;await sleep(250)}
 assert.ok(canvas&&tools,'independent native canvas and toolbar exist: '+browser.contexts().flatMap(c=>c.pages()).map(p=>p.url()).join(', '))
 const errors=[];for(const p of [canvas,tools])p.on('pageerror',e=>errors.push(e.message))
 await canvas.waitForSelector('canvas',{state:'attached'});await tools.waitForSelector('.floating-tools',{state:'attached'})
 await call('showBoard');await sleep(200)
 await canvas.evaluate(()=>window.__TAURI_INTERNALS__.invoke('board_settings',{patch:{tool:'pen',color:'#ef4444'}}))
 // Synthetic pointer capture is not accepted by WebView2; use its own CDP
 // input target for a real gesture without controlling another application.
 await canvas.mouse.move(250,350);await canvas.mouse.down();await canvas.mouse.move(450,400,{steps:10});await canvas.mouse.up()
 await sleep(60)
 assert.ok(await canvas.evaluate(()=>{const c=document.querySelector('canvas'),d=c.getContext('2d').getImageData(0,0,c.width,c.height).data;return d.some((v,i)=>i%4===3&&v>20)}),'native canvas rendered a stroke')
 await tools.getByRole('button',{name:'操作桌面',exact:true}).click();await sleep(60);assert.equal((await call('getState')).passthrough,true)
 await tools.getByRole('button',{name:'返回画板',exact:true}).click();await sleep(60);assert.equal((await call('getState')).passthrough,false)
 const color=await canvas.evaluate(()=>window.__TAURI_INTERNALS__.invoke('board_sample',{x:100,y:300}));assert.match(color,/^#[a-f0-9]{6}$/);assert.equal((await call('getState')).settings.color,color)
 await call('hideBoard');assert.equal((await call('getState')).visible,false)
 await request('module.lifecycle.request',{active:false});assert.equal((await call('getState')).shortcutAvailable,false)
 await request('module.lifecycle.request',{active:true});await call('showBoard');await call('hideBoard')
 assert.deepEqual(errors,[])
 await request('module.shutdown.request');console.log('Native smoke passed: authenticated lifecycle, embedded WebView2 surfaces, drawing, real pass-through API, underlying-screen eyedropper, hide and disable/re-enable.')
}finally{
 await browser?.close().catch(()=>{});child.stdin.end();if(child.exitCode===null){await Promise.race([new Promise(r=>child.once('exit',r)),sleep(3000)]);if(child.exitCode===null)child.kill()}
 for(const job of pending.values())clearTimeout(job.timer)
}
