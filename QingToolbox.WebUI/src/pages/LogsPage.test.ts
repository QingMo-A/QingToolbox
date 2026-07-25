import { afterEach,describe,expect,it,vi } from 'vitest'
import { flushPromises,mount,type VueWrapper } from '@vue/test-utils'
import { createPinia,setActivePinia } from 'pinia'
import LogsPage from './LogsPage.vue'
import { router } from '../app/router'
import { useAppStore } from '../app/store'
import { useLogStore } from '../app/logStore'
const wrappers:VueWrapper[]=[];afterEach(()=>wrappers.splice(0).forEach(x=>x.unmount()))
const snapshot={generatedAt:'2026-07-25T12:00:03Z',entries:[{timestamp:'2026-07-25T12:00:01Z',level:'Information' as const,category:'App',message:'Ready'},{timestamp:'2026-07-25T12:00:02Z',level:'Warning' as const,category:'Modules',message:'Warning'},{timestamp:'2026-07-25T12:00:03Z',level:'Error' as const,category:'Bridge',message:'Failed'}]}
function page(bridge:'Connecting'|'Connected'='Connected',status:'idle'|'loading'|'ready'|'error'='idle'){
 const pinia=createPinia();setActivePinia(pinia);useAppStore().bridge=bridge;const logs=useLogStore();logs.status=status;if(status==='ready')logs.complete(snapshot);if(status==='error')logs.fail(new Error('offline'));const getSnapshot=vi.fn(async()=>snapshot);const wrapper=mount(LogsPage,{global:{plugins:[pinia],provide:{logClient:{getSnapshot}}}});wrappers.push(wrapper);return{wrapper,logs,getSnapshot}}
describe('LogsPage',()=>{
 it('is registered at /logs',()=>expect(router.resolve('/logs').matched).toHaveLength(1))
 it('shows connecting state before activation',()=>expect(page('Connecting').wrapper.text()).toContain('Connecting to the host'))
 it('loads once after activation',async()=>{const x=page();await flushPromises();expect(x.getSnapshot).toHaveBeenCalledTimes(1);expect(x.logs.status).toBe('ready')})
 it('renders time level category and message',()=>{const text=page('Connected','ready').wrapper.text();expect(text).toContain('Information');expect(text).toContain('App');expect(text).toContain('Ready')})
 it('renders all supported severity classes',()=>{const x=page('Connected','ready').wrapper;expect(x.find('.is-information').exists()).toBe(true);expect(x.find('.is-warning').exists()).toBe(true);expect(x.find('.is-error').exists()).toBe(true)})
 it('shows count and refresh time',()=>expect(page('Connected','ready').wrapper.text()).toContain('3 entries'))
 it('shows an empty state',async()=>{const x=page('Connected','ready');x.logs.complete({generatedAt:snapshot.generatedAt,entries:[]});await x.wrapper.vm.$nextTick();expect(x.wrapper.text()).toContain('No session entries yet')})
 it('shows loading skeletons',()=>expect(page('Connected','loading').wrapper.findAll('.q-skeleton')).toHaveLength(6))
 it('shows safe error text and retry',async()=>{const x=page('Connected','error');expect(x.wrapper.text()).toContain('offline');await x.wrapper.get('button').trigger('click');await flushPromises();expect(x.getSnapshot).toHaveBeenCalledTimes(1)})
 it('refreshes only on demand after ready',async()=>{const x=page('Connected','ready');await x.wrapper.get('button').trigger('click');await flushPromises();expect(x.getSnapshot).toHaveBeenCalledTimes(1)})
 it('does not expose mutation controls',()=>expect(page('Connected','ready').wrapper.text()).not.toMatch(/Clear|Open file|Export|Delete/))
})
