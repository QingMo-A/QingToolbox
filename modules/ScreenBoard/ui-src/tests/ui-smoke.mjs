import { createServer } from 'vite'
import { resolve } from 'node:path'
import { mkdir } from 'node:fs/promises'
import assert from 'node:assert/strict'
const {chromium}=await import(process.env.QING_PLAYWRIGHT_PATH ?? 'playwright')
const output=resolve('../../../artifacts/screenboard');await mkdir(output,{recursive:true})
const server=await createServer({server:{host:'127.0.0.1',port:14970,strictPort:true}});await server.listen()
const browser=await chromium.launch({channel:'msedge',headless:true})
let failures=[]
try {
 const page=await browser.newPage({viewport:{width:1100,height:820}})
 page.on('pageerror',e=>failures.push(e.message))
 await page.addInitScript(()=>{
  const callbacks=new Map(),listeners=new Map();let next=1
  const state={settings:{color:'#3478f6',width:4,fontSize:30,text:'',expression:'sin(x)',xMin:-10,xMax:10,yMin:-5,yMax:5,grid:true,tool:'pen',theme:'light',appearancePreset:'qing-default'},active:true,visible:true,passthrough:false,shortcutAvailable:true,error:null,bounds:{x:0,y:0,width:1100,height:820}}
  const emit=(event,payload)=>{for(const item of listeners.values())if(item.event===event)callbacks.get(item.handler)?.({event,payload,id:1})}
  window.__boardTest={state,patch:p=>{Object.assign(state.settings,p);emit('board-state',structuredClone(state))}}
  window.__TAURI_INTERNALS__={
   transformCallback(fn){const id=next++;callbacks.set(id,fn);return id},unregisterCallback(id){callbacks.delete(id)},
   async invoke(cmd,args={}){
    if(cmd==='plugin:event|listen'){const id=next++;listeners.set(id,args);return id}
    if(cmd==='plugin:event|unlisten'){listeners.delete(args.eventId);return}
    if(cmd==='get_module_window_context')return{theme:'light',appearancePreset:'qing-default'}
    if(cmd==='board_state')return structuredClone(state)
    if(cmd==='board_ready')return
    if(cmd==='board_settings'){Object.assign(state.settings,args.patch);emit('board-state',structuredClone(state));return structuredClone(state)}
    if(cmd==='board_control'){if(args.action==='pass'){state.passthrough=!state.passthrough;emit('board-state',structuredClone(state))}else emit('board-action',args.action);return structuredClone(state)}
    if(cmd==='board_sample')return '#11aa44'
    if(cmd==='invoke_module_window'){if(args.method==='setSettings')Object.assign(state.settings,args.payload);return structuredClone(state)}
    throw Error('Unexpected command: '+cmd)
   }
  }
 })
 const base='http://127.0.0.1:14970'
 await page.goto(base);await page.getByRole('heading',{name:'屏幕画板',exact:true}).waitFor()
 assert.equal(await page.getByLabel('R',{exact:true}).inputValue(),'52')
 await page.getByLabel('函数表达式').fill('x^2');await page.getByRole('button',{name:'保存设置',exact:true}).click();await page.getByText('设置已保存',{exact:true}).waitFor()
 await page.screenshot({path:resolve(output,'settings-light.png'),fullPage:true})
 await page.evaluate(()=>document.documentElement.dataset.theme='dark')
 await page.screenshot({path:resolve(output,'settings-dark.png'),fullPage:true})
 await page.goto(base+'/?surface=canvas');const canvas=page.locator('canvas');await canvas.waitFor()
 const count=()=>page.evaluate(()=>{const c=document.querySelector('canvas'),data=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let count=0;for(let i=3;i<data.length;i+=4)if(data[i]>10)count++;return count})
 await page.mouse.move(100,180);await page.keyboard.down('Control');await page.mouse.down();await page.mouse.move(350,183,{steps:20});await page.mouse.up();await page.keyboard.up('Control');await page.waitForTimeout(80)
 assert.ok(await count()>400,'Ctrl line drawn')
 await page.keyboard.press('Control+z');await page.waitForTimeout(80);assert.equal(await count(),0,'one key press undoes exactly one gesture')
 await page.keyboard.press('Control+y');await page.waitForTimeout(80);assert.ok(await count()>400,'redo restores stroke')
 await page.evaluate(()=>window.__boardTest.patch({tool:'text'}));await page.mouse.click(150,280);await page.getByPlaceholder('输入文字，支持中文和多行').fill('屏幕画板 · 中文测试');await page.keyboard.press('Control+Enter');await page.waitForTimeout(80)
 assert.equal(await page.locator('textarea').count(),0,'text committed')
 await page.evaluate(()=>window.__boardTest.patch({tool:'graph',expression:'sin(x)'}));await page.mouse.click(490,320);await page.waitForTimeout(80);assert.ok(await count()>10000,'function plot visible')
 await page.screenshot({path:resolve(output,'drawing.png')})
 await page.evaluate(()=>window.__boardTest.patch({tool:'select'}));await page.mouse.move(600,400);await page.mouse.down();await page.mouse.move(680,440,{steps:10});await page.mouse.up();await page.mouse.wheel(0,-100);await page.waitForTimeout(80)
 await page.goto(base+'/?surface=tools');await page.getByRole('button',{name:'操作桌面',exact:true}).waitFor();await page.getByRole('button',{name:'操作桌面',exact:true}).click();await page.getByText('鼠标穿透',{exact:true}).waitFor();await page.getByRole('button',{name:'返回画板',exact:true}).click();await page.getByText('正在绘画',{exact:true}).waitFor()
 await page.setViewportSize({width:1080,height:192});await page.screenshot({path:resolve(output,'toolbar.png')})
 const bottom=await page.locator('.floating-tools').evaluate(el=>el.getBoundingClientRect().bottom);assert.ok(bottom<192,'toolbar fits native window')
 await page.setViewportSize({width:680,height:192});assert.ok(await page.evaluate(()=>document.body.scrollWidth)<=680,'compact toolbar has no horizontal overflow')
 assert.deepEqual(failures,[])
 console.log('UI smoke passed: settings, palette, Ctrl drawing, single-step undo/redo, Chinese text, functions, movement, toolbar/pass-through controls, responsive layout.')
} finally {await browser.close();await server.close()}
