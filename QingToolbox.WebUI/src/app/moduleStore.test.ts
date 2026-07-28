import{beforeEach,describe,expect,it,vi}from'vitest';import{readFileSync}from'node:fs';import{createPinia,setActivePinia}from'pinia';import{filterModules,useModuleStore}from'./moduleStore';import{isModuleSnapshot}from'../contracts/modules';import{useThemeStore}from'./themeStore';import{useToastStore}from'./toastStore';
const module={id:'qing.text',displayName:'Text Tools',displayDescription:'Formatting',version:'1.0.0',author:'Qing',runtimeType:'OutOfProcess',loadMode:'Manual',runtimeState:'Running',isValid:true,errorCount:0,errors:[],permissions:[],minimumHostVersion:'0.2',isUserInstalled:true,canRemove:true,canLoad:false,canActivate:false,canOpen:true,canDeactivate:true,canUnload:true,isBusy:false,isExecutionBlocked:false,isStartupEnabled:false,startupAuthorizationState:'NotEnabled' as const,canChangeStartupAuthorization:true,isStartupAuthorizationBusy:false};
describe('module center state',()=>{beforeEach(()=>{setActivePinia(createPinia());localStorage.clear()});it('validates a complete snapshot and rejects unsafe missing fields',()=>{expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[module]})).toBe(true);const{runtimeType,...invalid}=module;expect(isModuleSnapshot({generatedAt:new Date().toISOString(),modules:[invalid]})).toBe(false)});it('searches and filters authoritative state',()=>{const modules=[module,{...module,id:'bad',displayName:'Broken',runtimeState:'NotLoaded',isValid:false,errorCount:1,errors:['Invalid']}];expect(filterModules(modules,'text','all')).toHaveLength(1);expect(filterModules(modules,'','running')).toEqual([module]);expect(filterModules(modules,'','issues')).toHaveLength(1);expect(filterModules(modules,'','invalid')).toHaveLength(1);expect(filterModules(modules,'missing','all')).toHaveLength(0)});it('represents loading ready error and repeated refresh states',()=>{const store=useModuleStore();store.begin();expect(store.status).toBe('loading');store.complete({generatedAt:'2026-01-01T00:00:00Z',modules:[module]});store.begin();store.complete({generatedAt:'2026-01-02T00:00:00Z',modules:[]});expect(store.modules).toHaveLength(0);store.fail(new Error('offline'));expect(store.status).toBe('error')});it('persists local theme preview without host settings',()=>{const theme=useThemeStore();theme.set('dark');expect(document.documentElement.dataset.theme).toBe('dark');expect(localStorage.getItem('qing.theme')).toBe('dark')});it('shows and dismisses toast state',()=>{vi.useFakeTimers();const toast=useToastStore();toast.show('Ready','success');expect(toast.visible).toBe(true);vi.advanceTimersByTime(3200);expect(toast.visible).toBe(false);vi.useRealTimers()});it('provides a reduced-motion rendering override',()=>{expect(readFileSync('src/styles/main.css','utf8')).toContain('@media(prefers-reduced-motion:reduce)')})});

describe('module state filters', () => {
  const state = (id: string, runtimeState: string, overrides = {}) => ({
    ...module,
    id,
    displayName: id,
    runtimeState,
    ...overrides,
  })

  it('treats NotLoaded and Unloaded as not loaded only', () => {
    const modules = [
      state('not-loaded', 'NotLoaded'),
      state('unloaded', 'Unloaded'),
      state('loaded', 'Loaded'),
      state('deactivated', 'Deactivated'),
    ]
    expect(filterModules(modules, '', 'notLoaded').map(item => item.id)).toEqual(['not-loaded', 'unloaded'])
  })

  it('uses the shared issue definition without duplicating modules', () => {
    const modules = [
      state('invalid', 'Loaded', { isValid: false }),
      state('errors', 'Loaded', { errorCount: 2 }),
      state('failed', 'Failed'),
      state('all-three', 'Failed', { isValid: false, errorCount: 2 }),
      state('healthy', 'Loaded'),
    ]
    expect(filterModules(modules, '', 'issues').map(item => item.id)).toEqual([
      'invalid',
      'errors',
      'failed',
      'all-three',
    ])
  })

  it('combines search with state filters and preserves running and invalid behavior', () => {
    const modules = [
      state('running-alpha', 'Running', { displayDescription: 'Alpha service' }),
      state('running-beta', 'Running', { displayDescription: 'Beta service' }),
      state('invalid-alpha', 'NotLoaded', { displayDescription: 'Alpha invalid', isValid: false }),
    ]
    expect(filterModules(modules, 'alpha', 'running').map(item => item.id)).toEqual(['running-alpha'])
    expect(filterModules(modules, 'alpha', 'invalid').map(item => item.id)).toEqual(['invalid-alpha'])
  })
})
