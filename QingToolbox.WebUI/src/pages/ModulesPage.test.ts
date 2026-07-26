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
  const snapshot=()=>({generatedAt:new Date().toISOString(),modules:[module]})
  const client={getSnapshot:vi.fn(async()=>snapshot()),load:vi.fn(async()=>snapshot()),activate:vi.fn(async()=>snapshot()),...clientOverrides}
  const wrapper=mount(ModulesPage,{attachTo:document.body,global:{plugins:[pinia],provide:{moduleClient:client}}});mounted.push(wrapper);return{wrapper,client}
}
describe('ModulesPage lifecycle controls',()=>{
  it('keeps the card a non-interactive container with an independent Details button',()=>{const{wrapper}=page();const card=wrapper.get('.wpf-module-card');expect(card.attributes('role')).toBeUndefined();expect(card.attributes('tabindex')).toBeUndefined();expect(wrapper.get('.module-details-button').element.tagName).toBe('BUTTON')})
  it('Load Enter neither operates nor opens details, while click loads exactly once',async()=>{const{wrapper,client}=page();const load=wrapper.findAll('.module-actions .q-button').find(button=>button.text()==='Load')!;await load.trigger('keydown',{key:'Enter'});expect(client.load).not.toHaveBeenCalled();expect(useModuleStore().selectedModuleId).toBeNull();await load.trigger('click');await flushPromises();expect(client.load).toHaveBeenCalledTimes(1);expect(useModuleStore().selectedModuleId).toBeNull()})
  it('Activate Enter neither operates nor opens details, while click activates exactly once',async()=>{const{wrapper,client}=page(item({runtimeState:'Loaded',canLoad:false,canActivate:true}));const activate=wrapper.findAll('.module-actions .q-button').find(button=>button.text()==='Activate')!;await activate.trigger('keydown',{key:'Enter'});expect(client.activate).not.toHaveBeenCalled();expect(useModuleStore().selectedModuleId).toBeNull();await activate.trigger('click');await flushPromises();expect(client.activate).toHaveBeenCalledTimes(1);expect(useModuleStore().selectedModuleId).toBeNull()})
  it.each([{event:'click',label:'mouse click'},{event:'keydown',options:{key:'Enter'},label:'Enter'},{event:'keydown',options:{key:' '},label:'Space'}])('opens details with $label',async({event,options})=>{const{wrapper}=page();await wrapper.get('.module-details-button').trigger(event,options);expect(useModuleStore().selectedModuleId).toBe('qing.text')})
  it('uses the returned snapshot without optimistic runtime changes',async()=>{let resolve!:(value:any)=>void;const pending=new Promise(value=>resolve=value);const{wrapper}=page(item(),{load:vi.fn(()=>pending)});await wrapper.get('.module-actions .q-button').trigger('click');expect(useModuleStore().modules[0].runtimeState).toBe('NotLoaded');expect(wrapper.text()).toContain('Loading…');resolve({generatedAt:new Date().toISOString(),modules:[item({runtimeState:'Loaded',canLoad:false,canActivate:true})]});await flushPromises();expect(useModuleStore().modules[0].runtimeState).toBe('Loaded');expect(useToastStore().message).toBe('Text Tools loaded.')})
  it('preserves the snapshot, reports the safe error and resyncs at most once',async()=>{const before=item();const getSnapshot=vi.fn().mockRejectedValue(new Error('resync failed'));const{wrapper}=page(before,{load:vi.fn().mockRejectedValue(new Error('ModuleBusy: The requested module is busy.')),getSnapshot});await wrapper.get('.module-actions .q-button').trigger('click');await flushPromises();expect(useModuleStore().modules[0]).toEqual(before);expect(getSnapshot).toHaveBeenCalledTimes(1);expect(useToastStore().message).toContain('ModuleBusy')})
  it('shows recovery blocking and provides the same action in details',async()=>{const blocked=page(item({canLoad:false,isExecutionBlocked:true})).wrapper;expect(blocked.text()).toContain('recovery is pending');expect(blocked.findAll('.module-actions .q-button').map(button=>button.text())).not.toContain('Load');blocked.unmount();const{wrapper}=page(item({canActivate:true,canLoad:false,runtimeState:'Loaded'}));await wrapper.get('.module-details-button').trigger('click');expect(wrapper.get('.wpf-module-details').text()).toContain('Activate')})
  it('closes details with Escape',async()=>{const{wrapper}=page();await wrapper.get('.module-details-button').trigger('click');expect(document.body.textContent).toContain('Minimum host version');window.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}));await wrapper.vm.$nextTick();expect(useModuleStore().selectedModuleId).toBeNull()})
})
