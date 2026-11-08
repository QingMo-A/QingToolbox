<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, nextTick, computed } from 'vue'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { invoke } from '@tauri-apps/api/core'
import { applyAppearance, QButton } from '@qingtoolbox/module-ui'
import { control, getState, type State } from './bridge'
import { defaults, History, regularize, shapeEnd, snap, hit, bounds, clone, type Item, type Point } from './model'
import { compile } from './math'
import { render } from './render'
const canvas=ref<HTMLCanvasElement>(),state=ref<State>(),history=new History(),selected=ref<number|null>(null),note=ref(''),alt=ref(false)
const editor=ref<{x:number;y:number;text:string;id:number|null}|null>(null),textarea=ref<HTMLTextAreaElement>()
const s=computed(()=>state.value?.settings??defaults)
const viewport=ref({w:innerWidth,h:innerHeight})
let draft:Item|null=null, samples:Point[]=[],start:Point|null=null,original:Item|null=null,dragId:number|null=null,constrained=false,id=1,raf=0,noteTimer=0,transaction=false
let unlisten:UnlistenFn[]=[]
function message(v:string){note.value=v;clearTimeout(noteTimer);noteTimer=window.setTimeout(()=>note.value='',4500)}
function paint(){cancelAnimationFrame(raf);raf=requestAnimationFrame(()=>{const c=canvas.value;if(!c)return;const ctx=c.getContext('2d');if(!ctx)return;const ratio=Math.min(devicePixelRatio,2,Math.sqrt(16_000_000/(innerWidth*innerHeight)));const w=Math.round(innerWidth*ratio),h=Math.round(innerHeight*ratio);if(c.width!==w||c.height!==h){c.width=w;c.height=h}ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,innerWidth,innerHeight);render(ctx,draft?[...history.items,draft]:history.items,selected.value,s.value.theme==='dark')})}
function checkpoint(){history.checkpoint();transaction=true}
function point(e:PointerEvent):Point{return{x:e.clientX,y:e.clientY}}
function itemAt(p:Point){return [...history.items].reverse().find(i=>hit(i,p))}
function newItem(kind:Item['kind'],p:Point):Item{return{id:id++,kind,points:[p],color:s.value.color,width:s.value.width}}
function erase(p:Point){const item=itemAt(p);if(item){history.items=history.items.filter(i=>i.id!==item.id);paint()}}
function finishDraft(){if(draft){history.items.push(draft);selected.value=draft.id;draft=null}samples=[];start=null;original=null;transaction=false;paint()}
async function edit(p:Point,existing?:Item){editor.value={...p,text:existing?.text??s.value.text,id:existing?.id??null};await nextTick();textarea.value?.focus()}
function commitText(){const ed=editor.value;if(!ed)return;if(ed.text.trim()){checkpoint();if(ed.id!==null){const old=history.items.find(i=>i.id===ed.id);if(old)old.text=ed.text}else history.items.push({...newItem('text',ed),fontSize:s.value.fontSize,text:ed.text})}editor.value=null;paint();canvas.value?.focus()}
async function down(e:PointerEvent){
  if(e.button!==0||state.value?.passthrough||editor.value)return
  e.preventDefault();const p=point(e)
  if(e.altKey){try{const b=state.value!.bounds;await invoke('board_sample',{x:Math.round(b.x+p.x/innerWidth*b.width),y:Math.round(b.y+p.y/innerHeight*b.height)})}catch(err){message(String(err))}return}
  if(history.items.length>=500&& !['eraser','select'].includes(s.value.tool)){message('已达到 500 个对象；请清理部分画面。');return}
  if(s.value.tool==='text'){void edit(p);return}
  if(s.value.tool==='graph'){
    try{compile(s.value.expression)}catch(err){message(String(err));return}
    checkpoint();const w=Math.min(480,innerWidth-32),h=Math.min(300,innerHeight-32);history.items.push({...newItem('graph',{x:Math.max(0,Math.min(p.x,innerWidth-w)),y:Math.max(0,Math.min(p.y,innerHeight-h))}),expression:s.value.expression,w,h,range:[s.value.xMin,s.value.xMax,s.value.yMin,s.value.yMax],grid:s.value.grid});selected.value=id-1;paint();return
  }
  transaction=false;dragId=e.pointerId;canvas.value?.setPointerCapture(e.pointerId);start=p;constrained=e.ctrlKey;samples=[p]
  if(s.value.tool==='select'){const found=itemAt(p);selected.value=found?.id??null;original=found?clone(found):null;if(found)checkpoint();paint();return}
  checkpoint();selected.value=null
  if(s.value.tool==='eraser'){erase(p);return}
  draft=newItem(['rect','ellipse'].includes(s.value.tool)?s.value.tool as 'rect'|'ellipse':'stroke',p);draft.points.push(p);paint()
}
function move(e:PointerEvent){
  alt.value=e.altKey;if(dragId!==e.pointerId||!start)return
  const p=point(e);constrained=e.ctrlKey
  if(s.value.tool==='eraser'){erase(p);return}
  if(original){const item=history.items.find(i=>i.id===original!.id);if(item)item.points=original.points.map(q=>({x:q.x+p.x-start!.x,y:q.y+p.y-start!.y}));paint();return}
  if(!draft)return
  if(s.value.tool==='line')draft.points=[start,e.ctrlKey?snap(start,p):p]
  else if(['rect','ellipse'].includes(s.value.tool))draft.points=[start,shapeEnd(start,p,e.ctrlKey)]
  else{const last=samples[samples.length-1];if(Math.hypot(last.x-p.x,last.y-p.y)>=1.2&&samples.length<4096)samples.push(p);draft.points=s.value.tool==='curve'||e.ctrlKey?regularize(samples):clone(samples)}
  paint()
}
function up(e:PointerEvent){if(dragId!==e.pointerId)return;move(e);if(draft?.kind==='stroke'&&(s.value.tool==='curve'||constrained))draft.points=regularize(samples.length>1?samples:draft.points);finishDraft();dragId=null;if(canvas.value?.hasPointerCapture(e.pointerId))canvas.value.releasePointerCapture(e.pointerId)}
function cancel(){if(dragId!==null){draft=null;start=null;dragId=null;original=null;if(transaction)history.undo();transaction=false;paint()}}
function action(action:string){
  if(editor.value)return
  if(action==='undo')history.undo();if(action==='redo')history.redo()
  if(action==='clear'&&history.items.length){checkpoint();history.items=[]}
  if(action==='delete'&&selected.value!==null){checkpoint();history.items=history.items.filter(i=>i.id!==selected.value)}
  if(action==='applyStyle'){const item=history.items.find(i=>i.id===selected.value);if(item){checkpoint();item.color=s.value.color;item.width=s.value.width;if(item.kind==='text')item.fontSize=s.value.fontSize}}
  selected.value=null;paint()
}
function key(e:KeyboardEvent){alt.value=e.altKey;if(editor.value){if(e.key==='Escape'){editor.value=null;e.preventDefault()}return}if(e.key==='Escape'){e.preventDefault();void control('hide')}if(e.ctrlKey&&['z','y'].includes(e.key.toLowerCase())){e.preventDefault();action(e.key.toLowerCase()==='z'&&!e.shiftKey?'undo':'redo')}if(e.key==='Delete'){e.preventDefault();action('delete')}if(e.altKey)e.preventDefault()}
function keyUp(e:KeyboardEvent){alt.value=e.altKey}
function resize(){viewport.value={w:innerWidth,h:innerHeight};paint()}
function wheel(e:WheelEvent){const item=history.items.find(i=>i.id===selected.value);if(!item||s.value.tool!=='select')return;e.preventDefault();const r=bounds(item),factor=e.deltaY<0?1.08:1/1.08;if(r.w*factor>2000||r.h*factor>1600||Math.max(r.w,r.h)*factor<12)return;checkpoint();item.points=item.points.map(p=>({x:r.x+(p.x-r.x)*factor,y:r.y+(p.y-r.y)*factor}));if(item.kind==='graph'){item.w=(item.w??480)*factor;item.h=(item.h??300)*factor}if(item.kind==='text')item.fontSize=Math.max(10,Math.min(160,(item.fontSize??30)*factor));paint()}
function double(e:MouseEvent){if(s.value.tool!=='select')return;const found=itemAt({x:e.clientX,y:e.clientY});if(found?.kind==='text')void edit(found.points[0],found)}
onMounted(async()=>{try{state.value=await getState();applyAppearance(state.value.settings);unlisten.push(await listen<State>('board-state',e=>{state.value=e.payload;applyAppearance(e.payload.settings);if(e.payload.passthrough){cancel();editor.value=null}paint()}),await listen<string>('board-action',e=>action(e.payload)));await invoke('board_ready');paint()}catch(e){message(String(e))}window.addEventListener('resize',resize);window.addEventListener('keydown',key);window.addEventListener('keyup',keyUp)})
onBeforeUnmount(()=>{unlisten.forEach(f=>f());cancelAnimationFrame(raf);clearTimeout(noteTimer);window.removeEventListener('resize',resize);window.removeEventListener('keydown',key);window.removeEventListener('keyup',keyUp)})
</script>
<template><div class="drawing-surface" :class="{'is-pass':state?.passthrough,'is-eyedropper':alt}" @contextmenu.prevent><canvas ref="canvas" tabindex="0" aria-label="屏幕画板绘画区域" @pointerdown="down" @pointermove="move" @pointerup="up" @pointercancel="cancel" @dblclick="double" @wheel="wheel" /><div v-if="editor" class="text-editor" :style="{left:Math.min(editor.x,Math.max(0,viewport.w-360))+'px',top:Math.min(editor.y,Math.max(0,viewport.h-200))+'px'}" @pointerdown.stop><textarea ref="textarea" v-model="editor.text" maxlength="2000" placeholder="输入文字，支持中文和多行" :style="{color:s.color,fontSize:s.fontSize+'px'}" @keydown.ctrl.enter.prevent="commitText" /><div><span>Ctrl+Enter 完成 · Esc 取消</span><QButton @click="editor=null">取消</QButton><QButton variant="primary" @click="commitText">完成</QButton></div></div><div v-if="note" class="board-toast" role="status">{{note}}</div><div v-if="alt&&!state?.passthrough" class="eyedropper-hint">Alt + 点击：取下方屏幕颜色</div></div></template>
