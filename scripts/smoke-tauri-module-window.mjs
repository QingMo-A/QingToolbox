import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { dirname, resolve } from 'node:path'

const executable = process.argv[2]
if (!executable) throw new Error('usage: node smoke-tauri-module-window.mjs <tauri-executable>')
const executablePath = resolve(executable)

// A fixed WebView2 debugging port can still be owned by a previous smoke
// process whose renderer is shutting down. Pick an ephemeral free port for
// each run unless CI explicitly supplies one, and use a matching isolated
// browser profile so a stale target can never satisfy this smoke test.
const port = Number(process.env.QING_TAURI_DEBUG_PORT ?? await findFreePort())
const userDataFolder = `${process.env.TEMP ?? process.env.TMP ?? '.'}\\qingtoolbox-tauri-window-smoke-${port}-${process.pid}`
const host = spawn(executablePath, [], {
  cwd: dirname(executablePath),
  env: {
    ...process.env,
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

try {
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
  if (!await evaluate(target, `getComputedStyle(document.querySelector('.q-desktop-frame')).userSelect === 'none'`)) {
    throw new Error('Desktop shell unexpectedly permits interface text selection')
  }
  await evaluate(target, `document.querySelector('.q-titlebar-actions button').click(); true`)
  const badgeTarget = await waitForTarget(value => value.url?.includes('surface=floating-badge'))
  await waitFor(() => evaluate(badgeTarget, `Boolean(document.querySelector('.floating-badge'))`), 10000)
  const denied = await evaluate(badgeTarget, `window.__TAURI_INTERNALS__.invoke('control_main_window', { action: 'minimize' }).then(() => false, e => e.code === 'mainWindowUnauthorized')`)
  if (!denied) throw new Error('Floating badge can invoke privileged main window commands')
  await evaluate(badgeTarget, `window.__TAURI_INTERNALS__.invoke('show_main_from_floating_badge')`)
  // The current shell keeps module cards in the Modules workspace rather
  // than duplicating them on the dashboard. Navigate through the rendered
  // router link so the smoke follows the same user path as the UI.
  await evaluate(target, `document.querySelector('a[href="#/modules"]')?.click(); true`)
  await waitFor(async () => Boolean(await evaluate(target, `location.hash === '#/modules' && document.querySelector('.wpf-module-card')`)), 12000)
  moduleTarget = await openModuleCard('Qing Launcher', 'qing.launcher')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.launcher-shell')),
    loading: Boolean(document.querySelector('.loading-card')),
    error: document.querySelector('.error')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 12000)
  const launcherSnapshot = await evaluate(moduleTarget, `({
    text: document.body.innerText,
    icon: Boolean(document.querySelector('.brand-mark img')),
  })`)
  if (!String(launcherSnapshot?.text).includes('启动台')) throw new Error('Launcher UI did not finish rendering')
  if (!launcherSnapshot?.icon) throw new Error('Launcher did not receive its validated module icon')
  await closeTarget(moduleTarget)
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
  await closeTarget(moduleTarget)
  moduleTarget = undefined

  moduleTarget = await openModuleCard('Window Topmost', 'qing.windowtopmost')
  await waitFor(() => evaluate(moduleTarget, `({
    shell: Boolean(document.querySelector('.shell')),
    loading: document.body.innerText.includes('正在准备窗口列表'),
    error: document.querySelector('.alert.danger')?.textContent ?? '',
  })`).then((value) => value.shell && !value.loading && !value.error), 10000)
  const topmostSnapshot = await evaluate(moduleTarget, `({ text: document.body.innerText })`)
  if (!String(topmostSnapshot?.text).includes('Window Topmost') || !String(topmostSnapshot?.text).includes('拾取窗口')) {
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
  if (!String(screenPinSnapshot?.text).includes('Screen Pin') || !String(screenPinSnapshot?.text).includes('截取区域')) {
    throw new Error('Screen Pin UI did not finish rendering')
  }
  // Exercise the native floating-window path with a small, valid region. The
  // screenshot remains module-owned; the host only receives the opaque pin id.
  await evaluate(moduleTarget, `document.querySelector('button.capture')?.click()`)
  await waitFor(() => evaluate(moduleTarget, `Boolean(document.querySelector('.pin'))`), 10000)
  await evaluate(moduleTarget, `document.querySelector('.float-button')?.click()`)
  await delay(500)
  const pinTarget = await waitForTarget((value) => value.url.includes('qpin') || (value.title === 'Screen Pin' && !value.url.includes('qmod')), 10000)
  const pinSnapshot = await evaluate(pinTarget, `({ image: Boolean(document.querySelector('img[src^="data:image/png;base64,"]')), body: document.body.innerText })`)
  if (!pinSnapshot?.image) throw new Error('Screen Pin floating window did not render its bounded image')
  await closeTarget(pinTarget)
  console.log('Tauri module window IPC smoke passed.')
} finally {
  if (moduleTarget) await closeTarget(moduleTarget).catch(() => {})
  await terminate(host)
}

async function openModuleCard(name, moduleId) {
  await waitFor(async () => Boolean(await evaluate(target, `Boolean([...document.querySelectorAll('.wpf-module-card')]
    .find((node) => node.textContent?.includes(${JSON.stringify(name)}))
    ?.querySelector('.module-card-actions .q-button'))`)), 12000)
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
    socket.addEventListener('open', resolve, { once: true })
    socket.addEventListener('error', reject, { once: true })
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
