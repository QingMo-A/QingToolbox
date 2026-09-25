export async function verifyLauncherDrag(page, { evaluate, mouse, delay, waitFor }) {
  await evaluate(page, `(() => {
    const original = window.__TAURI_INTERNALS__.invoke;
    window.__smokeOriginalInvoke = original;
    window.__smokeState = { sortMode:'custom', items:['a','b','c'].map(id=>({id,name:'Test '+id,iconKey:null,source:'custom',lastLaunchedAt:null})), folders:[], customOrder:['a','b','c'], recent:[], hotkey:{ctrl:true,alt:true,shift:false,win:false,virtualKey:76,keyLabel:'L'},hotkeyStatus:'HostManaged',active:true };
    window.__smokeLaunches = 0;
    window.__TAURI_INTERNALS__.invoke = async (cmd,args) => {
      if (cmd === 'hide_module_window') return;
      if (cmd !== 'invoke_module_window') return original(cmd,args);
      if (args.method === 'setCustomOrder') { window.__smokeState.customOrder = args.payload.ids; window.__smokeState.items.sort((a,b)=>args.payload.ids.indexOf(a.id)-args.payload.ids.indexOf(b.id)); }
      if (args.method === 'launchItem') window.__smokeLaunches++;
      return structuredClone(window.__smokeState);
    };
    document.querySelector('.refresh').click(); return true;
  })()`)
  try { await waitFor(() => evaluate(page, `document.querySelectorAll('.launcher-tile').length === 3`), 5000) }
  catch (e) { throw new Error('Launcher fixture failed: '+JSON.stringify(await evaluate(page, `({ text:document.body.innerText,invoke:Object.getOwnPropertyDescriptor(window.__TAURI_INTERNALS__,'invoke'),internal:Object.getOwnPropertyDescriptor(window,'__TAURI_INTERNALS__'),overridden:window.__TAURI_INTERNALS__.invoke!==window.__smokeOriginalInvoke })`))) }
  const rect = await evaluate(page, `(() => { const r=document.querySelector('[data-id="a"]').getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+44,width:r.width+10}; })()`)
  await mouse(page, 'mousePressed', rect.x, rect.y)
  await mouse(page, 'mouseReleased', rect.x, rect.y)
  if (await evaluate(page, 'window.__smokeLaunches') !== 1) throw new Error('Launcher click was swallowed by drag capture')
  await mouse(page, 'mousePressed', rect.x, rect.y)
  await mouse(page, 'mouseMoved', rect.x+12, rect.y)
  await waitFor(async () => {
    const value = await evaluate(page, `({ghost:!!document.querySelector('.drag-ghost'),hidden:getComputedStyle(document.querySelector('[data-id="a"]')).opacity,b:document.querySelector('[data-id="b"]').getBoundingClientRect().x})`)
    return value.ghost && value.hidden === '0' && value.b <= rect.x
  }, 3000)
  const lifted = await evaluate(page, `({ghost:!!document.querySelector('.drag-ghost'),hidden:getComputedStyle(document.querySelector('[data-id="a"]')).opacity,b:document.querySelector('[data-id="b"]').getBoundingClientRect().x})`)
  if (!lifted.ghost || lifted.hidden !== '0' || lifted.b > rect.x) throw new Error('Launcher lift/compaction failed: '+JSON.stringify({lifted,rect}))
  // A fast pass must not trigger a reorder; dwell creates the final gap.
  await mouse(page, 'mouseMoved', rect.x+rect.width, rect.y)
  await delay(30)
  await mouse(page, 'mouseMoved', rect.x+rect.width*2, rect.y)
  await delay(450)
  const before = await evaluate(page, `['b','c'].map(id=>document.querySelector('[data-id="'+id+'"]').getBoundingClientRect().x)`)
  await mouse(page, 'mouseReleased', rect.x+rect.width*2, rect.y)
  await delay(60)
  const after = await evaluate(page, `({order:window.__smokeState.customOrder, positions:['b','c'].map(id=>document.querySelector('[data-id="'+id+'"]').getBoundingClientRect().x),launches:window.__smokeLaunches,ghost:!!document.querySelector('.drag-ghost')})`)
  if (after.order.join(',') !== 'b,c,a' || after.launches !== 1 || after.ghost) throw new Error('Launcher end-slot drop or click suppression failed')
  if (before.some((x,i)=>Math.abs(x-after.positions[i])>1)) throw new Error('Launcher repeated its shift animation on drop')
  // Move the last icon into the middle, hold inside the full opened cell.
  await mouse(page, 'mousePressed', rect.x+rect.width*2, rect.y)
  await mouse(page, 'mouseMoved', rect.x+rect.width, rect.y)
  await delay(450)
  await mouse(page, 'mouseMoved', rect.x+rect.width+rect.width*.38, rect.y+40)
  await delay(160)
  await mouse(page, 'mouseReleased', rect.x+rect.width+rect.width*.38, rect.y+40)
  if (await evaluate(page, `window.__smokeState.customOrder.join(',')`) !== 'b,a,c') throw new Error('Launcher middle gap hysteresis failed')
  await evaluate(page, `window.__TAURI_INTERNALS__.invoke = window.__smokeOriginalInvoke; true`)
  console.log('Launcher click, lift, compaction, end/middle insertion, gap hold and drop continuity passed.')
}
