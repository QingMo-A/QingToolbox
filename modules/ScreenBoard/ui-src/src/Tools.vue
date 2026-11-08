<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, computed } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { QButton, applyAppearance } from '@qingtoolbox/module-ui'
import Palette from './Palette.vue'
import { control, getState, settings, type State } from './bridge'
import { defaults, tools, type Settings, type Tool } from './model'
const state=ref<State>(),s=ref({...defaults}),error=ref('');let unlisten:UnlistenFn|undefined
function key(e:KeyboardEvent){if(e.key==='Escape'){e.preventDefault();void act('hide')}}
const extras=computed(()=>s.value.tool==='graph'?'graph':s.value.tool==='text'?'text':'help')
async function patch(p:Partial<Settings>){Object.assign(s.value,p);try{state.value=await settings(p)}catch(e){error.value=String(e)}}
async function choose(tool:Tool){await patch({tool});if(state.value?.passthrough)await control('pass')}
async function act(v:string){try{await control(v)}catch(e){error.value=String(e)}}
onMounted(async()=>{try{state.value=await getState();s.value={...state.value.settings};applyAppearance(s.value);unlisten=await listen<State>('board-state',e=>{state.value=e.payload;s.value={...e.payload.settings};applyAppearance(s.value)});await invoke('board_ready');window.addEventListener('keydown',key)}catch(e){error.value=String(e)}})
onBeforeUnmount(()=>{unlisten?.();window.removeEventListener('keydown',key)})
</script>
<template><div class="floating-tools"><header><button class="drag-grip" title="拖动工具条" aria-label="拖动工具条" @pointerdown="act('drag')">⠿</button><strong>屏幕画板</strong><span class="mode-badge" :class="{pass:state?.passthrough}">{{state?.passthrough?'鼠标穿透':'正在绘画'}}</span><span class="toolbar-spacer" /><span class="shortcut-label">Ctrl+Shift+D 切换穿透</span><QButton @click="act('pass')">{{state?.passthrough?'返回画板':'操作桌面'}}</QButton><button class="plain-close" title="收起画板" aria-label="收起画板" @click="act('hide')">×</button></header><div class="tool-row"><button v-for="tool in tools" :key="tool.id" class="tool" :class="{active:s.tool===tool.id}" :aria-pressed="s.tool===tool.id" @click="choose(tool.id)"><b>{{tool.glyph}}</b><span>{{tool.name}}</span></button><span class="divider"/><button class="tool" title="Ctrl+Z" @click="act('undo')"><b>↶</b><span>撤销</span></button><button class="tool" title="Ctrl+Y" @click="act('redo')"><b>↷</b><span>重做</span></button><button class="tool" title="清空可撤销" @click="act('clear')"><b>⌧</b><span>清空</span></button><label class="width-control">线宽<input type="range" min="1" max="40" :value="s.width" @input="s.width=Number(($event.target as HTMLInputElement).value)" @change="patch({width:s.width})"/><span>{{s.width}}</span></label></div><div class="color-row"><Palette :color="s.color" @change="patch({color:$event})"/><button class="apply-style" title="对选择工具选中的对象应用当前颜色、线宽与字号" @click="act('applyStyle')">应用到选中项</button></div><div class="tool-details"><template v-if="extras==='graph'"><label>y = <input v-model="s.expression" aria-label="函数表达式" placeholder="sin(x)" maxlength="256" @change="patch({expression:s.expression})"/></label><span>在屏幕点击放置 · 范围在模块设置中调整 · 选择后滚轮缩放</span></template><template v-else-if="extras==='text'"><label>字号<input v-model.number="s.fontSize" aria-label="文字字号" type="number" min="10" max="160" @change="patch({fontSize:s.fontSize})"/></label><span>点击屏幕输入文字 · 选择工具双击文字可编辑</span></template><span v-else>Alt+点击取色 · Ctrl 规整/约束 · 选择后拖动或滚轮缩放 · Esc 收起</span><span v-if="error||state?.error" class="inline-error">{{error||state?.error}}</span></div></div></template>
