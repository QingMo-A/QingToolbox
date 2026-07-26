import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import ModulesPage from './ModulesPage.vue'
import { useAppStore } from '../app/store'
import { useModuleStore } from '../app/moduleStore'
import { useToastStore } from '../app/toastStore'
import type { ModuleSnapshotItem } from '../contracts/modules'

const item = (overrides: Partial<ModuleSnapshotItem> = {}): ModuleSnapshotItem => ({
  id:'qing.text',displayName:'Text Tools',displayDescription:'Formatting tools',version:'1.0.0',author:'Qing',runtimeType:'OutOfProcess',loadMode:'Manual',runtimeState:'NotLoaded',isValid:true,errorCount:0,errors:[],permissions:[],minimumHostVersion:'0.2',isUserInstalled:true,canLoad:true,canActivate:false,isBusy:false,isExecutionBlocked:false,...overrides
})
const mounted: ReturnType<typeof mount>[]=[]
afterEach(()=>{mounted.splice(0).forEach(x=>x.unmount());document.body.innerHTML='';vi.restoreAllMocks()})
function page(module=item(), clientOverrides:Record<string,unknown>={}) {
  const pinia=createPinia();setActivePinia(pinia);useAppStore().bridge='Connected'
  useModuleStore().complete({generatedAt:new Date().toISOString(),modules:[module]})
  const client={getSnapshot:vi.fn(async()=>({generatedAt:new Date().toISOString(),modules:[module]})),load:vi.fn(),activate:vi.fn(),...clientOverrides}
  const wrapper=mount(ModulesPage,{attachTo:document.body,global:{plugins:[pinia],provide:{moduleClient:client}}});mounted.push(wrapper);return{wrapper,client}
}
describe('ModulesPage lifecycle controls',()=>{
  it('shows only host-authorized actions and keeps action clicks out of details',async()=>{const{wrapper}=page();expect(wrapper.text()).toContain('Load');expect(wrapper.text()).not.toContain('Activate');await wrapper.get('.module-actions .q-button').trigger('click');expect(useModuleStore().selectedModuleId).toBeNull()})
  it('uses the returned snapshot without optimistic runtime changes',async()=>{let resolve!:(value:any)=>void;const pending=new Promise(value=>resolve=value);const{wrapper}=page(item(),{load:vi.fn(()=>pending)});await wrapper.get('.module-actions .q-button').trigger('click');expect(useModuleStore().modules[0].runtimeState).toBe('NotLoaded');expect(wrapper.text()).toContain('Loading…');resolve({generatedAt:new Date().toISOString(),modules:[item({runtimeState:'Loaded',canLoad:false,canActivate:true})]});await flushPromises();expect(useModuleStore().modules[0].runtimeState).toBe('Loaded');expect(useToastStore().message).toBe('Text Tools loaded.')})
  it('preserves the snapshot, reports the safe error and resyncs at most once',async()=>{const before=item();const getSnapshot=vi.fn().mockRejectedValue(new Error('resync failed'));const{wrapper}=page(before,{load:vi.fn().mockRejectedValue(new Error('ModuleBusy: The requested module is busy.')),getSnapshot});await wrapper.get('.module-actions .q-button').trigger('click');await flushPromises();expect(useModuleStore().modules[0]).toEqual(before);expect(getSnapshot).toHaveBeenCalledTimes(1);expect(useToastStore().message).toContain('ModuleBusy')})
  it('shows recovery blocking and provides the same action in details',async()=>{const blocked=page(item({canLoad:false,isExecutionBlocked:true})).wrapper;expect(blocked.text()).toContain('recovery is pending');expect(blocked.findAll('.module-actions .q-button').map(button=>button.text())).not.toContain('Load');blocked.unmount();const{wrapper}=page(item({canActivate:true,canLoad:false,runtimeState:'Loaded'}));await wrapper.get('.wpf-module-card').trigger('click');expect(wrapper.get('.wpf-module-details').text()).toContain('Activate')})
  it('opens details normally and closes it with Escape',async()=>{const{wrapper}=page();await wrapper.get('.wpf-module-card').trigger('click');expect(document.body.textContent).toContain('Minimum host version');window.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}));await wrapper.vm.$nextTick();expect(useModuleStore().selectedModuleId).toBeNull()})
})
