// Opt-in, installed-profile acceptance: starts the registered local program,
// reads its real settings/module state and opens Launcher without editing it.
// Unlike isolated smoke tests, normal autostart synchronization is intentional.
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { dirname, resolve } from 'node:path'
import { writeFileSync, mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
const [executable, expectedHost, expectedLauncher, report] = process.argv.slice(2)
if (!executable || !expectedHost || !expectedLauncher || !report) throw new Error('Pass installed executable, host version, Launcher version and report path')
const server = createServer()
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
const port = server.address().port
await new Promise(resolve => server.close(resolve))
const env = { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`, QING_TAURI_STARTUP_PRESENTATION: 'main' }
delete env.QING_TAURI_DISABLE_AUTOSTART_SYNC
// Share a temporary browser profile so each module's separate environment
// does not compete for the same debugging port. APPDATA/LOCALAPPDATA remain
// untouched: settings, module imports and startup preferences are real.
env.WEBVIEW2_USER_DATA_FOLDER = mkdtempSync(resolve(tmpdir(), 'qing-installed-browser-'))
const host = spawn(resolve(executable), [], { cwd: dirname(resolve(executable)), env, windowsHide: true, stdio: 'ignore' })
let spawnFailure
host.once('error', error => { spawnFailure = error })
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
async function targetWhere(predicate) {
  const deadline = Date.now() + 20000
  let lastTargets = []
  while (Date.now() < deadline) {
    if (spawnFailure) throw spawnFailure
    if (host.exitCode !== null) throw new Error(`Installed host exited: ${host.exitCode}`)
    const values = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(1500) }).then(r => r.json()).catch(() => [])
    lastTargets = values.map(({ title, url }) => ({ title, url }))
    const found = values.find(predicate)
    if (found) return found
    await delay(150)
  }
  throw new Error('Installed WebView did not become ready: ' + JSON.stringify(lastTargets))
}
async function evaluate(target, expression) {
  const socket = new WebSocket(target.webSocketDebuggerUrl)
  try {
    await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }) })
    return await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error('Installed IPC timed out')), 10000)
      socket.addEventListener('message', event => {
        const value = JSON.parse(event.data)
        if (value.id !== 1) return
        clearTimeout(timeout)
        if (value.error || value.result?.exceptionDetails) reject(new Error(JSON.stringify(value.error ?? value.result.exceptionDetails)))
        else resolve(value.result.result?.value)
      })
      socket.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }))
    })
  } finally { socket.close() }
}
try {
  const main = await targetWhere(t => t.title.includes('QingToolbox') && !t.url.includes('surface='))
  const invoke = (command, args = {}) => evaluate(main, `window.__TAURI_INTERNALS__.invoke(${JSON.stringify(command)},${JSON.stringify(args)})`)
  const info = await invoke('get_host_info')
  if (info.backend !== 'rust' || info.version !== expectedHost) throw new Error('Wrong installed host version/backend')
  const settings = await invoke('get_settings')
  const startup = await invoke('get_startup_registration_status')
  if (startup.registered !== settings.launchAtLogin || startup.canRepair) throw new Error('Installed login-start registration does not match preference')
  const modules = (await invoke('list_modules')).payload.modules
  const launcher = modules.find(module => module.id === 'qing.launcher')
  if (!launcher?.valid || launcher.version !== expectedLauncher) throw new Error('Installed Launcher missing, invalid or shadowed by old version')
  let runtime = await invoke('get_module_runtime', { moduleId: 'qing.launcher' })
  if (!['loaded', 'running', 'deactivated'].includes(runtime.state)) await invoke('start_module', { moduleId: 'qing.launcher' })
  for (let i = 0; i < 50; i++) {
    runtime = await invoke('get_module_runtime', { moduleId: 'qing.launcher' })
    if (['loaded', 'running', 'deactivated'].includes(runtime.state)) break
    if (runtime.state === 'failed') throw new Error(runtime.lastError)
    await delay(100)
  }
  await invoke('open_module', { moduleId: 'qing.launcher' })
  console.log('Installed host and Launcher backend:', JSON.stringify({ info, launcherVersion: launcher.version, runtime, startup }))
  const window = await targetWhere(t => t.url.includes('qing.launcher') || t.title.includes('Qing Launcher'))
  await delay(500)
  const state = await evaluate(window, `window.__TAURI_INTERNALS__.invoke('invoke_module_window',{method:'getState',payload:{}})`)
  const surface = await evaluate(window, `!!document.querySelector('.launcher-shell') && !document.querySelector('.loading-card') && !document.querySelector('.error')`)
  if (!surface) throw new Error('Installed Launcher did not render successfully')
  const result = { host: info, launcherVersion: launcher.version, launcherRuntime: runtime.state, launcherItems: state.items.length, launcherFolders: state.folders.length, loginStartup: startup, invalidModules: modules.filter(m => !m.valid).map(m => ({ id: m.id, issues: m.issues })), checkedAt: new Date().toISOString() }
  writeFileSync(resolve(report), JSON.stringify(result, null, 2))
  console.log(JSON.stringify(result, null, 2))
} finally {
  if (host.pid && host.exitCode === null) {
    await new Promise(resolve => spawn('taskkill.exe', ['/PID', String(host.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' }).once('exit', resolve))
  }
}
