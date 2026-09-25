import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { dirname, resolve } from 'node:path'
import { mkdirSync, writeFileSync } from 'node:fs'

const executable = process.argv[2]
if (!executable) throw new Error('usage: node smoke-tauri-module-window.mjs <tauri-executable>')
const executablePath = resolve(executable)
const nativeDesktop = process.argv.includes('--native-desktop')

// A fixed WebView2 debugging port can still be owned by a previous smoke
// process whose renderer is shutting down. Pick an ephemeral free port for
// each run unless CI explicitly supplies one, and use a matching isolated
// browser profile so a stale target can never satisfy this smoke test.
const port = Number(process.env.QING_TAURI_DEBUG_PORT ?? await findFreePort())
const userDataFolder = `${process.env.TEMP ?? process.env.TMP ?? '.'}\\qingtoolbox-tauri-window-smoke-${port}-${process.pid}`
const isolatedEnvironment = nativeDesktop ? {
  LOCALAPPDATA: resolve(userDataFolder, 'local'),
  APPDATA: resolve(userDataFolder, 'roaming'),
} : {}
for (const path of Object.values(isolatedEnvironment)) mkdirSync(path, { recursive: true })
const host = spawn(executablePath, [], {
  cwd: dirname(executablePath),
  env: {
    ...process.env,
    ...isolatedEnvironment,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
    WEBVIEW2_USER_DATA_FOLDER: userDataFolder,
    QING_TAURI_DISABLE_SINGLE_INSTANCE: '1',
    QING_TAURI_DISABLE_AUTOSTART_SYNC: '1',
    QING_TAURI_STARTUP_PRESENTATION: 'main',
  },
  stdio: 'ignore',
})

let target
let moduleTarget
let nextId = 1
const spawnError = new Promise((_, reject) => host.once('error', reject))
const spawnExit = new Promise((_, reject) => host.once('exit', (code, signal) => reject(new Error(`Tauri host exited before smoke completed (code=${code}, signal=${signal ?? 'none'})`))))
// The race normally resolves on the WebView target before the process exits.
// Keep the loser promise observed so a fast startup failure cannot surface as
// an unhandled rejection while the cleanup path is terminating the host.
void spawnError.catch(() => {})
void spawnExit.catch(() => {})

try { await smoke() } finally {
  if (moduleTarget) await closeTarget(moduleTarget).catch(() => {})
  await terminate(host)
}

async function smoke() {
  target = await Promise.race([
    // The Vue shell localizes the document title after startup (for example
    // "首页 · QingToolbox"). Match the stable product suffix instead of the
    // initial index.html title so the smoke remains valid in every locale.
    waitForTarget((value) => typeof value.title === 'string' && value.title.includes('QingToolbox') && !value.url?.includes('surface=floating-badge')),
    spawnError,
    spawnExit,
  ])
  await waitFor(async () => Boolean(await evaluate(target, `!document.querySelector('.empty-state')?.textContent?.includes('正在读取') && !document.querySelector('.status-label')?.textContent?.includes('扫描模块')`)), 10000)
  await waitFor(() => evaluate(target, `document.querySelectorAll('.q-titlebar-actions button').length === 4`), 10000)
  const startup = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('get_startup_registration_status')`)
  if (startup?.canConfigure !== false || startup?.canRepair !== false) throw new Error('Development autostart controls remain enabled')
  const startupDenied = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('update_settings', { update: { launchAtLogin: true } }).then(() => false, e => e.code === 'autostartUnavailable')`)
  if (!startupDenied) throw new Error('Development host accepted an autostart preference mutation')
  const fontDiagnostics = []
  const fontSocket = await connect(target)
  fontSocket.addEventListener('message', event => { const value=JSON.parse(event.data); if (value.method === 'Log.entryAdded' || value.method === 'Network.loadingFailed' || value.method === 'Network.responseReceived') fontDiagnostics.push(value) })
  fontSocket.send(JSON.stringify({id:nextId++,method:'Log.enable'}))
  fontSocket.send(JSON.stringify({id:nextId++,method:'Network.enable'}))
  const fontResult = await evaluate(target, `(async () => {
    const settings = await window.__TAURI_INTERNALS__.invoke('get_settings');
    const font = settings.fonts?.find(value => value.source === 'imported');
    if (!font) return 'no-imported-font';
    try { const loaded = await new FontFace('Qing Smoke Font', 'url("'+font.resourceUrl+'")').load(); return loaded.status; }
    catch(e) { return { error:String(e),url:font.resourceUrl,origin:location.origin,fonts:settings.fonts,resources:performance.getEntriesByType('resource').filter(r=>r.name.includes('qfont')).map(r=>({name:r.name,size:r.transferSize})) }; }
  })()`)
  fontSocket.close()
  if (fontResult !== 'loaded' && fontResult !== 'no-imported-font') throw new Error('Imported font failed to load in WebView2: '+JSON.stringify({fontResult,fontDiagnostics}))
  console.log('Imported font check:', fontResult)
  if (!await evaluate(target, `getComputedStyle(document.querySelector('.q-desktop-frame')).userSelect === 'none'`)) {
    throw new Error('Desktop shell unexpectedly permits interface text selection')
  }
  await evaluate(target, `document.querySelector('.q-titlebar-actions button').click(); true`)
  const badgeTarget = await waitForTarget(value => value.url?.includes('surface=floating-badge'))
  await waitFor(() => evaluate(badgeTarget, `Boolean(document.querySelector('.floating-badge'))`), 10000)
  const badgeTransparent = await evaluate(badgeTarget, `(() => {
    const style = getComputedStyle(document.querySelector('.floating-badge'));
    return style.backgroundColor === 'rgba(0, 0, 0, 0)' && style.borderTopWidth === '0px' && style.boxShadow === 'none';
  })()`)
  if (!badgeTransparent) throw new Error('Floating badge has an unwanted background, border or shadow')
  const denied = await evaluate(badgeTarget, `window.__TAURI_INTERNALS__.invoke('control_main_window', { action: 'minimize' }).then(() => false, e => e.code === 'mainWindowUnauthorized')`)
  if (!denied) throw new Error('Floating badge can invoke privileged main window commands')
  await evaluate(badgeTarget, `window.__TAURI_INTERNALS__.invoke('show_main_from_floating_badge')`)
  // The current shell keeps module cards in the Modules workspace rather
  // than duplicating them on the dashboard. Navigate through the rendered
  // router link so the smoke follows the same user path as the UI.
  await evaluate(target, `document.querySelector('a[href="#/modules"]')?.click(); true`)
  await waitFor(async () => Boolean(await evaluate(target, `location.hash === '#/modules' && document.querySelector('.wpf-module-card')`)), 12000)
  moduleTarget = await openModuleCard('Qing Launcher', 'qing.launcher')
  // Opening a resident surface does not activate ongoing work. Explicitly
  // enable Launcher before the global-shortcut checks below.
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.launcher-shell')),
    loading: Boolean(document.querySelector('.loading-card')),
    error: document.querySelector('.error')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 12000)
  if (nativeDesktop) {
    // The fresh profile's default Ctrl+Alt+L can belong to an unrelated app.
    // Only change the isolated profile, never the user's persisted binding.
    await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('invoke_module_window', { method: 'setHotkey', payload: { ctrl: true, alt: true, shift: true, win: false, virtualKey: 135, keyLabel: 'F24' } })`)
  }
  await evaluate(target, `window.__TAURI_INTERNALS__.invoke('set_module_active', { moduleId: 'qing.launcher', active: true }).catch(error => { throw new Error(JSON.stringify(error)); })`)
  const launcherSnapshot = await evaluate(moduleTarget, `({
    text: document.body.innerText,
    icon: Boolean(document.querySelector('.brand-mark svg')),
  })`)
  if (!String(launcherSnapshot?.text).includes('启动台')) throw new Error('Launcher UI did not finish rendering')
  if (!launcherSnapshot?.icon) throw new Error('Launcher icon did not render')
  await waitFor(() => evaluate(moduleTarget, `document.querySelector('.launcher-shell').getAnimations().every(animation => animation.playState === 'finished')`), 3000)
  const overlaySnapshot = await evaluate(moduleTarget, `(async () => {
    const invoke=window.__TAURI_INTERNALS__.invoke;
    const label=window.__TAURI_INTERNALS__.metadata.currentWindow.label;
    const [decorated,topmost,resizable]=await Promise.all(['is_decorated','is_always_on_top','is_resizable'].map(method=>invoke('plugin:window|'+method,{label})));
    const r=document.querySelector('.launcher-shell').getBoundingClientRect();
    return {decorated,topmost,resizable,centered:Math.abs(r.x*2+r.width-innerWidth)<2 && Math.abs(r.y*2+r.height-innerHeight)<2,transparent:getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'};
  })()`)
  if(overlaySnapshot.decorated || !overlaySnapshot.topmost || overlaySnapshot.resizable || !overlaySnapshot.centered || !overlaySnapshot.transparent)throw new Error('Native launcher overlay presentation failed: '+JSON.stringify(overlaySnapshot))
  await saveLauncherScreenshot(moduleTarget)
  if (nativeDesktop) {
    await checkNativeDesktopInteractions()
    return
  }
  // Register a temporary in-memory shortcut. The user's persisted hotkey is
  // untouched, and terminating this isolated host releases the test binding.
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('set_module_hotkey',{hotkey:'Ctrl+Alt+Shift+F24'})`)
  await evaluate(moduleTarget, `document.querySelector('.hotkey-toggle').click(); true`)
  for (const [kind, expected] of [['AltSpace','Alt+Space'], ['CtrlShiftK','Ctrl+Shift+K']]) {
    console.log('Recording check:',kind)
    await evaluate(target,`window.__TAURI_INTERNALS__.invoke('open_module',{moduleId:'qing.launcher'})`)
    await delay(350) // allow launcher:shown focus/entry animation to settle
    await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('set_module_hotkey',{hotkey:'Ctrl+Alt+Shift+F24'})`)
    await evaluate(moduleTarget, `if(!document.querySelector('.hotkey-input'))document.querySelector('.hotkey-toggle').click(); true`)
    await waitFor(()=>evaluate(moduleTarget,`!!document.querySelector('.hotkey-input')`),3000)
    await evaluate(moduleTarget, `document.querySelector('.hotkey-input').click(); true`)
    try { await waitFor(()=>evaluate(moduleTarget, `document.activeElement === document.querySelector('.hotkey-input') && document.querySelector('.hotkey-input').value.includes('请按下')`),3000) }
    catch(error) { throw new Error('Start recorder '+kind+': '+JSON.stringify(await evaluate(moduleTarget,`({text:document.body.innerText,value:document.querySelector('.hotkey-input')?.value,focus:document.activeElement?.outerHTML})`))) }
    await delay(150)
    await sendLauncherHotkey(kind)
    try { await waitFor(()=>evaluate(moduleTarget, `document.querySelector('.hotkey-input')?.value === ${JSON.stringify(expected)}`),5000) }
    catch(error) { throw new Error('Recorder '+kind+': '+JSON.stringify(await evaluate(moduleTarget,`({text:document.body.innerText,value:document.querySelector('.hotkey-input')?.value,focus:document.activeElement?.outerHTML})`))) }
    if(await evaluate(moduleTarget, `!!document.querySelector('.hotkey-error')`))throw new Error('Native hotkey recorder reported an error')
  }
  await evaluate(moduleTarget, `document.querySelector('.hotkey-input').click(); true`)
  console.log('Recording check: manual cancel')
  await delay(200)
  await evaluate(moduleTarget, `document.querySelector('.hotkey-action').click(); true`)
  await waitFor(()=>evaluate(moduleTarget, `!document.querySelector('.hotkey-input').value.includes('请按下')`),3000)
  await evaluate(moduleTarget, `document.querySelector('.hotkey-input').click(); true`)
  console.log('Recording check: Escape')
  await delay(200)
  await sendLauncherHotkey('Escape')
  await waitFor(()=>evaluate(moduleTarget, `!document.querySelector('.hotkey-input').value.includes('请按下')`),3000)
  await evaluate(moduleTarget, `document.querySelector('.settings-close').click(); true`)
  console.log('Native hotkey recording: Alt+Space, Ctrl+Shift+K, manual cancel and Escape passed.')
  if(await evaluate(moduleTarget,`document.querySelectorAll('.launcher-tile').length > 0`)) {
    await waitFor(()=>evaluate(moduleTarget,`[...document.querySelectorAll('.launcher-tile .launcher-app-image img')].some(image=>image.naturalWidth>=128)`),15000)
    console.log('Native Launcher real high-resolution icon loading passed.')
    await saveLauncherScreenshot(moduleTarget)
  }
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('hide_module_window')`)
  const overlayVisible=()=>evaluate(moduleTarget,`window.__TAURI_INTERNALS__.invoke('plugin:window|is_visible',{label:window.__TAURI_INTERNALS__.metadata.currentWindow.label})`)
  await waitFor(async()=>!(await overlayVisible()),3000)
  await sendLauncherHotkey()
  await waitFor(overlayVisible,5000)
  await delay(350) // wait for the shown event/entry animation before clicking
  // A real pointer gesture outside the centered panel dismisses the overlay.
  for(const type of ['mousePressed','mouseReleased']) {
    const socket=await connect(moduleTarget)
    socket.send(JSON.stringify({id:nextId++,method:'Input.dispatchMouseEvent',params:{type,x:6,y:6,button:'left',buttons:type==='mousePressed'?1:0,clickCount:1}}))
    await delay(80);socket.close()
  }
  await waitFor(async()=>!(await overlayVisible()),3000)
  await sendLauncherHotkey()
  await waitFor(overlayVisible,5000)
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('hide_module_window')`)
  console.log('Native Launcher: borderless/topmost/transparent/centered, hotkey recall after hide and blank-area dismissal passed.')
  const residentBefore = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('get_module_runtime', { moduleId: 'qing.launcher' })`)
  const disabled = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('set_module_active', { moduleId: 'qing.launcher', active: false })`)
  if (disabled.state !== 'deactivated' || disabled.generation !== residentBefore.generation) throw new Error('Disable unloaded or restarted Launcher')
  await sendLauncherHotkey()
  await delay(300)
  if (await overlayVisible()) throw new Error('Disabled Launcher still responds to its hotkey')
  await evaluate(target, `window.__TAURI_INTERNALS__.invoke('open_module', { moduleId: 'qing.launcher' })`)
  const openedInactive = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('get_module_runtime', { moduleId: 'qing.launcher' })`)
  if (openedInactive.state !== 'deactivated') throw new Error('Opening UI silently enabled Launcher')
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('hide_module_window')`)
  const enabledAgain = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('set_module_active', { moduleId: 'qing.launcher', active: true })`)
  if (enabledAgain.state !== 'running' || enabledAgain.generation !== disabled.generation) throw new Error('Re-enable replaced the resident process')
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('set_module_hotkey', { hotkey: 'Ctrl+Alt+Shift+F24' })`)
  await sendLauncherHotkey()
  await waitFor(overlayVisible,5000)
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('hide_module_window')`)
  console.log('Launcher lifecycle: disable retains residency, disables hotkey, UI open stays inactive, re-enable reuses generation passed.')
  moduleTarget = undefined

  moduleTarget = await openModuleCard('Qing PDF', 'qing.pdf')
  await waitFor(() => evaluate(moduleTarget, `({
    workspace: Boolean(document.querySelector('.workspace')),
    loading: document.querySelector('.runtime')?.textContent?.includes('正在准备') ?? true,
    error: document.querySelector('.error-alert')?.textContent ?? '',
  })`).then((value) => value.workspace && !value.loading && !value.error), 12000)
  const pdfSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(pdfSnapshot?.text).includes('Qing PDF') || !String(pdfSnapshot?.text).includes('拼接')) {
    throw new Error('Qing PDF UI did not finish rendering')
  }
  await closeTarget(moduleTarget)
  moduleTarget = undefined

  moduleTarget = await openModuleCard('QingTransfer', 'qing.qingtransfer')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.querySelector('.runtime-pill')?.textContent?.includes('正在准备') ?? true,
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 15000)
  const transferSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(transferSnapshot?.text).includes('QingTransfer') || !String(transferSnapshot?.text).includes('附近设备')) {
    throw new Error('QingTransfer UI did not finish rendering')
  }
  await closeTarget(moduleTarget)
  moduleTarget = undefined

  moduleTarget = await openModuleCard('Text Tools', 'qing.texttools')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.querySelector('.status')?.textContent?.includes('正在准备') ?? true,
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 10000)
  const textToolsSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(textToolsSnapshot?.text).includes('Text Tools') || !String(textToolsSnapshot?.text).includes('格式化 JSON')) {
    throw new Error('Text Tools UI did not finish rendering')
  }
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('invoke_module_window', { method: 'setInput', payload: { text: 'resident unsaved text' } })`)
  await evaluate(target, `window.__TAURI_INTERNALS__.invoke('set_module_active', { moduleId: 'qing.texttools', active: true })`)
  await evaluate(target, `window.__TAURI_INTERNALS__.invoke('set_module_active', { moduleId: 'qing.texttools', active: false })`)
  const retained = await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('invoke_module_window', { method: 'getState', payload: {} })`)
  if (retained.input !== 'resident unsaved text') throw new Error('Disable discarded module memory')
  await evaluate(target, `window.__TAURI_INTERNALS__.invoke('stop_module', { moduleId: 'qing.texttools' })`)
  const unloaded = await evaluate(target, `window.__TAURI_INTERNALS__.invoke('get_module_runtime', { moduleId: 'qing.texttools' })`)
  if (unloaded.state !== 'stopped') throw new Error('Unload did not release module residency')
  console.log('Text Tools lifecycle: unsaved memory retained across disable; unload releases process/window passed.')
  moduleTarget = undefined

  moduleTarget = await openModuleCard('Window Topmost', 'qing.windowtopmost')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.body.innerText.includes('正在准备窗口列表'),
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 10000)
  const topmostSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(topmostSnapshot?.text).includes('窗口置顶') || !String(topmostSnapshot?.text).includes('拾取窗口')) {
    throw new Error('Window Topmost UI did not finish rendering')
  }
  await closeTarget(moduleTarget)
  moduleTarget = undefined

  moduleTarget = await openModuleCard('PowerGuard', 'qing.powerguard')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.body.innerText.includes('正在准备断网守护'),
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 10000)
  const powerGuardSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(powerGuardSnapshot?.text).includes('PowerGuard') || !String(powerGuardSnapshot?.text).includes('监测设置')) {
    throw new Error('PowerGuard UI did not finish rendering')
  }
  await closeTarget(moduleTarget)
  moduleTarget = undefined

  moduleTarget = await openModuleCard('Screen Pin', 'qing.screenpin')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.body.innerText.includes('正在准备屏幕钉住'),
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 10000)
  const screenPinSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(screenPinSnapshot?.text).includes('截图 Pin') || !String(screenPinSnapshot?.text).includes('框选截图')) {
    throw new Error('Screen Pin UI did not finish rendering')
  }
  // Exercise the native floating-window path with a small, valid region. The
  // screenshot remains module-owned; the host only receives the opaque pin id.
  await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('invoke_module_window', { method: 'captureRegion', payload: { x: 0, y: 0, width: 128, height: 96 } })`)
  await waitFor(() => evaluate(moduleTarget, `Boolean(document.querySelector('.pin'))`), 10000)
  await evaluate(moduleTarget, `document.querySelector('.float-button')?.click()`)
  await delay(500)
  const pinTarget = await waitForTarget((value) => value.url.includes('qpin') || (value.title === 'Screen Pin' && !value.url.includes('qmod')), 10000)
  const pinSnapshot = await evaluate(pinTarget, `({ image: Boolean(document.querySelector('img[src^="data:image/png;base64,"]')), body: document.body.innerText })`)
  if (!pinSnapshot?.image) throw new Error('Screen Pin floating window did not render its bounded image')
  const pinControls = await evaluate(pinTarget, `({ aspect: typeof document.querySelector('[data-action="aspect"]')?.onclick, topmost: typeof document.querySelector('[data-action="topmost"]')?.onclick })`)
  if (pinControls?.aspect !== 'function' || pinControls?.topmost !== 'function') throw new Error('Screen Pin controls were blocked or did not initialize')
  await evaluate(pinTarget, `document.querySelector('[data-action="aspect"]').click(); true`)
  if (!await evaluate(pinTarget, `document.querySelector('.tools [data-action="aspect"]').textContent === '自由'`)) throw new Error('Pin aspect toggle failed')
  if (await evaluate(pinTarget, `window.__TAURI_INTERNALS__.invoke('control_screenpin_window', { action: 'toggleTopmost' })`) !== false) throw new Error('Pin topmost toggle failed')
  if (await evaluate(pinTarget, `window.__TAURI_INTERNALS__.invoke('control_screenpin_window', { action: 'toggleTopmost' })`) !== true) throw new Error('Pin topmost restore failed')
  if (!await evaluate(pinTarget, `window.__TAURI_INTERNALS__.invoke('control_screenpin_window', { action: 'resize', width: 256, height: 192 })`)) throw new Error('Pin resize failed')
  await closeTarget(pinTarget)
  console.log('Tauri module window IPC smoke passed.')
}

async function checkNativeDesktopInteractions() {
  // A real OLE source window and real Windows mouse input, not synthetic
  // browser drag events. Keep the imported fixture out of the user's data.
  const fixtureName = 'Qing native desktop smoke'
  const fixturePath = resolve(userDataFolder, `${fixtureName}.url`)
  writeFileSync(fixturePath, '[InternetShortcut]\r\nURL=https://example.invalid/\r\n')
  for (const mode of ['Drop', 'OutsideDrag', 'Click']) {
    await evaluate(target, `window.__TAURI_INTERNALS__.invoke('open_module', { moduleId: 'qing.launcher' })`)
    await delay(350)
    await checkNativeDesktop(mode, fixturePath)
    if (mode === 'Drop') {
      await waitFor(() => evaluate(moduleTarget, `document.body.innerText.includes(${JSON.stringify(fixtureName)})`), 8000)
      const imported = await evaluate(moduleTarget, `window.__TAURI_INTERNALS__.invoke('invoke_module_window', { method: 'getState', payload: {} })`)
      if (!imported.items?.some(item => item.name === fixtureName)) throw new Error('Native OLE drop did not reach Launcher backend')
    }
  }
  console.log('Native desktop access: OLE import, drag-out-and-back protection, outside-click dismissal passed.')
}

async function openModuleCard(name, moduleId) {
  // Loading starts the process; opening is available only after it is ready.
  await evaluate(target, `(() => {
    const card = [...document.querySelectorAll('.wpf-module-card')].find(node => node.textContent?.includes(${JSON.stringify(name)}));
    const load = [...(card?.querySelectorAll('.module-card-actions .q-button') ?? [])].find(button => /^(加载|Load)$/.test(button.textContent?.trim() ?? ''));
    if (load) {
      if ([...card.querySelectorAll('.module-card-actions .q-button')].some(button => /^(打开|启用|Open|Enable)$/.test(button.textContent?.trim() ?? ''))) throw new Error('Unloaded module exposes running actions');
      load.click();
    }
    return true;
  })()`)
  await waitFor(() => evaluate(target, `(() => {
    const card = [...document.querySelectorAll('.wpf-module-card')].find(node => node.textContent?.includes(${JSON.stringify(name)}));
    return [...(card?.querySelectorAll('.module-card-actions .q-button') ?? [])].some(button => /^(打开|Open)$/.test(button.textContent?.trim() ?? ''));
  })()`), 12000)
  await evaluate(target, `(() => {
    const card = [...document.querySelectorAll('.wpf-module-card')]
      .find((node) => node.textContent?.includes(${JSON.stringify(name)}))
    const button = [...(card?.querySelectorAll('.module-card-actions .q-button') ?? [])]
      .find((candidate) => /^(打开|Open)$/.test(candidate.textContent?.trim() ?? ''))
    if (!button) throw new Error(${JSON.stringify(`${name} card/button was not rendered`)})
    button.click()
    return true
  })()`)
  await delay(250)
  return waitForTarget((value) => value.url.includes(moduleId) || value.title.includes(name), 15000)
}

async function listTargets() {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 1500)
  let response
  try {
    response = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: controller.signal })
  } finally {
    clearTimeout(timeout)
  }
  return response.json()
}

async function waitForTarget(predicate, timeout = 10000) {
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    const targets = await listTargets().catch(() => [])
    const found = targets.find(predicate)
    if (found) return found
    await delay(100)
  }
  throw new Error('Timed out waiting for WebView2 target')
}

async function connect(targetInfo) {
  const socket = new WebSocket(targetInfo.webSocketDebuggerUrl)
  await new Promise((resolve, reject) => {
    const timer=setTimeout(()=>{socket.close();reject(new Error('CDP connection timed out'))},5000)
    socket.addEventListener('open', ()=>{clearTimeout(timer);resolve()}, { once: true })
    socket.addEventListener('error', error=>{clearTimeout(timer);reject(error)}, { once: true })
  })
  return socket
}

async function evaluate(targetInfo, expression) {
  const socket = await connect(targetInfo)
  try {
    const id = nextId++
    const response = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('CDP evaluation timed out')), 4000)
      const listener = (event) => {
        const message = JSON.parse(event.data)
        if (message.id !== id) return
        clearTimeout(timer)
        socket.removeEventListener('message', listener)
        if (message.error) reject(new Error(message.error.message))
        else if (message.result?.exceptionDetails) reject(new Error(`${message.result.exceptionDetails.exception?.description ?? message.result.exceptionDetails.text}\nExpression: ${expression}`))
        else resolve(message.result?.result?.value)
      }
      socket.addEventListener('message', listener)
    })
    socket.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }))
    return await response
  } finally {
    socket.close()
  }
}

async function closeTarget(targetInfo) {
  const socket = await connect(targetInfo)
  try { socket.send(JSON.stringify({ id: nextId++, method: 'Page.close' })) } finally { socket.close() }
}

async function sendLauncherHotkey(kind='Toggle') {
  const child=spawn('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',resolve('scripts/send-launcher-smoke-hotkey.ps1'),'-Kind',kind],{stdio:'pipe',windowsHide:true})
  let error='';child.stderr.on('data',data=>error+=data)
  await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',code=>code===0?resolve():reject(new Error(error||'Native shortcut check failed')))})
}

async function checkNativeDesktop(mode, path) {
  const child = spawn('powershell.exe', ['-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File',
    resolve('scripts/test-launcher-native-desktop.ps1'), '-HostProcessId', String(host.pid), '-Mode', mode, '-ShortcutPath', path],
  { stdio: 'pipe', windowsHide: true })
  let output = ''
  child.stdout.on('data', data => output += data)
  child.stderr.on('data', data => output += data)
  await new Promise((resolve, reject) => {
    child.once('error', reject)
    child.once('exit', code => code === 0 ? resolve() : reject(new Error(output || 'Native desktop check failed')))
  })
  console.log(output.trim())
}

async function saveLauncherScreenshot(targetInfo) {
  const socket=await connect(targetInfo)
  try {
    const id=nextId++
    const result=new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>reject(new Error('Screenshot timed out')),5000)
      socket.addEventListener('message',event=>{const value=JSON.parse(event.data);if(value.id!==id)return;clearTimeout(timer);value.error?reject(new Error(value.error.message)):resolve(value.result)})
    })
    socket.send(JSON.stringify({id,method:'Page.captureScreenshot',params:{format:'png'}}))
    const image=await result
    mkdirSync('artifacts/ui-check',{recursive:true})
    writeFileSync('artifacts/ui-check/launcher-native.png',Buffer.from(image.data,'base64'))
  } finally { socket.close() }
}

async function waitFor(predicate, timeout) {
  const deadline = Date.now() + timeout
  while (Date.now() < deadline) {
    if (await predicate()) return
    await delay(100)
  }
  throw new Error('Timed out waiting for Launcher UI')
}

async function terminate(process) {
  if (!process || process.killed) return
  if (process.exitCode === null) {
    spawn('taskkill.exe', ['/PID', String(process.pid), '/T', '/F'], { stdio: 'ignore' })
    await new Promise((resolve) => process.once('exit', resolve))
  }
}

function delay(milliseconds) { return new Promise((resolve) => setTimeout(resolve, milliseconds)) }

async function findFreePort() {
  const server = createServer()
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  const address = server.address()
  await new Promise((resolve) => server.close(resolve))
  if (!address || typeof address === 'string') throw new Error('Unable to allocate a WebView2 debugging port')
  return address.port
}
