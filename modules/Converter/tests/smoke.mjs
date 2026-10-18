// Only generated fixtures and a fresh module-data directory are touched.
import { spawn, execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { inflateRawSync } from 'node:zlib'
import assert from 'node:assert/strict'

const root = resolve(import.meta.dirname, '..')
const binary = resolve(process.argv[2] ?? resolve(root, 'target/debug/qing-convert-module.exe'))
const profile = mkdtempSync(resolve(tmpdir(), 'qing-convert-test-'))
const output = resolve(profile, '结果'); mkdirSync(output)
const child = spawn(binary, [], { stdio: ['pipe','pipe','pipe'], windowsHide:true, env:{...process.env,QINGTOOLBOX_MODULE_ID:'qing.convert',QINGTOOLBOX_MODULE_NONCE:'fixture-nonce',QINGTOOLBOX_MODULE_DIRECTORY:root,QINGTOOLBOX_MODULE_DATA_DIR:profile} })
let seq = 0, buffer = ''
const requests = new Map()
child.stdout.on('data', chunk => {
  buffer += chunk.toString()
  while (buffer.includes('\n')) {
    const end = buffer.indexOf('\n'), line = buffer.slice(0,end); buffer = buffer.slice(end+1)
    const frame = JSON.parse(line), pending = requests.get(frame.requestId)
    if (!pending) continue
    requests.delete(frame.requestId); clearTimeout(pending.timer)
    frame.error ? pending.reject(Error(JSON.stringify(frame.error))) : pending.done(frame.payload)
  }
})
child.stderr.on('data', () => {})
child.once('exit', () => { for (const entry of requests.values()) { clearTimeout(entry.timer); entry.reject(Error('Module exited before replying')) }; requests.clear() })
function send(messageType, payload) {
  const requestId = `fixture-${++seq}`
  return new Promise((done,reject) => {
    const timer = setTimeout(() => { requests.delete(requestId); reject(Error('Module request timed out')) },15000)
    requests.set(requestId,{done,reject,timer})
    child.stdin.write(JSON.stringify({protocolVersion:1,messageType,requestId,payload})+'\n')
  })
}
const invoke = (method,payload={}) => send('module.invoke.request',{method,payload})
const pause = ms => new Promise(done => setTimeout(done,ms))
async function finished(timeout=30000) {
  const until = Date.now()+timeout
  while (Date.now()<until) { const s=await invoke('getState'); if (!s.busy) return s; await pause(120) }
  await invoke('cancel')
  throw Error('Conversion fixture timed out; cancellation requested')
}
async function convert(paths,target) {
  await invoke('clearFiles'); await invoke('addFiles',{paths,target}); await invoke('startConversion',{target,outputDirectory:output}); return finished(target === 'word-pdf' ? 100000 : 30000)
}
try {
  const hello = await send('module.hello.request',{moduleId:'qing.convert',nonce:'fixture-nonce'})
  assert.equal(hello.lifecycleVersion,1)
  await send('module.lifecycle.request',{active:true})
  await send('module.lifecycle.request',{active:false})
  const bitmap = resolve(profile,'图片 & 测试.bmp'); makeBmp(bitmap)
  let result = await convert([bitmap],'png')
  assert.equal(result.files[0].status,'succeeded')
  const png = result.files[0].outputPath
  assert.equal(readFileSync(png).subarray(1,4).toString(),'PNG')
  const original = readFileSync(png)
  result = await convert([bitmap],'png')
  assert.equal(result.files[0].status,'succeeded'); assert.match(result.files[0].outputPath,/\(1\)\.png$/)
  assert.deepEqual(readFileSync(png),original)
  for (const target of ['jpg','webp','bmp']) { const r = await convert([png],target); assert.equal(r.files[0].status,'succeeded') }
  const broken = resolve(profile,'broken.png'); writeFileSync(broken,'not an image')
  result = await convert([broken,png],'jpg')
  assert.equal(result.files[0].status,'failed'); assert.equal(result.files[1].status,'succeeded')
  const pdf = resolve(profile,'两页文本.pdf'); makePdf(pdf,['Qing Convert fixture','Second page of text'])
  result = await convert([pdf],'pdf-word')
  assert.equal(result.files[0].status,'succeeded')
  const xml = zipEntry(readFileSync(result.files[0].outputPath),'word/document.xml')
  assert.match(xml,/Qing Convert fixture/); assert.match(xml,/Second page of text/); assert.match(xml,/w:type="page"/)
  const empty = resolve(profile,'扫描件示例.pdf'); makePdf(empty,[''])
  result = await convert([empty],'pdf-word')
  assert.equal(result.files[0].status,'failed'); assert.match(result.files[0].message,/OCR/)
  const cancellationFiles = Array.from({length:10},(_,i)=>{const path=resolve(profile,`cancel-${i}.bmp`);makeBmp(path);return path})
  await invoke('clearFiles'); await invoke('addFiles',{paths:cancellationFiles,target:'png'})
  await invoke('startConversion',{target:'png',outputDirectory:output}); await invoke('cancel')
  result = await finished(); assert.ok(result.files.some(v=>v.status==='cancelled')); assert.equal(result.busy,false)
  const state = await invoke('getState')
  let wordTest = 'not installed / skipped'
  if (state.engines.wordAvailable && !process.argv.includes('--skip-word')) {
    const running = execFileSync('powershell.exe',['-NoProfile','-Command',"@(Get-Process -Name WINWORD -ErrorAction SilentlyContinue).Count"],{encoding:'utf8',windowsHide:true}).trim()
    if (running === '0') {
      const word = resolve(profile,'文档 & 测试.docx'); makeDocx(word)
      result = await convert([word],'word-pdf')
      assert.equal(result.files[0].status,'succeeded',result.files[0].message)
      const exported = result.files[0].outputPath
      assert.equal(readFileSync(exported).subarray(0,5).toString(),'%PDF-')
      result = await convert([exported],'pdf-word')
      assert.equal(result.files[0].status,'succeeded',result.files[0].message)
      const text = zipEntry(readFileSync(result.files[0].outputPath),'word/document.xml')
      assert.match(text,/中文/)
      wordTest = 'DOCX → PDF → editable DOCX, Chinese text passed'
    } else { wordTest = 'skipped: user Word instance is running' }
  }
  console.log(JSON.stringify({profile,images:'PNG/JPG/WebP/BMP passed',queue:'failure isolation, no overwrite and cancellation passed',pdfWord:'text/page breaks passed, scan rejected',wordPdf:wordTest},null,2))
} finally {
  if (child.exitCode === null) { try { await send('module.shutdown.request',{}) } catch {} child.stdin.end(); await new Promise(done => { if (child.exitCode !== null) done(); else child.once('exit',done) }) }
}

function makeBmp(path) {
  const data=Buffer.alloc(54+16*16*3); data.write('BM');data.writeUInt32LE(data.length,2);data.writeUInt32LE(54,10);data.writeUInt32LE(40,14);data.writeInt32LE(16,18);data.writeInt32LE(16,22);data.writeUInt16LE(1,26);data.writeUInt16LE(24,28)
  for(let i=54;i<data.length;i+=3){data[i]=180;data[i+1]=140;data[i+2]=30};writeFileSync(path,data)
}
function makePdf(path,texts) {
  const pages=texts.map((_,i)=>`${4+i*2} 0 R`).join(' ')
  const objects=['<< /Type /Catalog /Pages 2 0 R >>',`<< /Type /Pages /Kids [${pages}] /Count ${texts.length} >>`,'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
  for(const text of texts){const stream=`BT /F1 12 Tf 50 700 Td (${text.replace(/[()\\]/g,'\\$&')}) Tj ET`;objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents ${objects.length+2} 0 R >>`,`<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}\nendstream`)}
  let pdf='%PDF-1.4\n',offsets=[0];for(let i=0;i<objects.length;i++){offsets.push(Buffer.byteLength(pdf));pdf+=`${i+1} 0 obj\n${objects[i]}\nendobj\n`}
  const xref=Buffer.byteLength(pdf);pdf+=`xref\n0 ${offsets.length}\n0000000000 65535 f \n${offsets.slice(1).map(v=>`${String(v).padStart(10,'0')} 00000 n \n`).join('')}trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;writeFileSync(path,pdf)
}
function crc32(buffer){let c=0xffffffff;for(const byte of buffer){c^=byte;for(let n=0;n<8;n++)c=(c>>>1)^((c&1)?0xedb88320:0)}return(c^0xffffffff)>>>0}
function makeDocx(path) {
  const files={
    '[Content_Types].xml':'<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
    '_rels/.rels':'<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>',
    'word/document.xml':'<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Qing Convert 中文文档测试</w:t></w:r></w:p><w:sectPr><w:pgSz w:w="11906" w:h="16838"/></w:sectPr></w:body></w:document>',
  }
  const locals=[],centrals=[];let offset=0
  for(const [name,content]of Object.entries(files)){const n=Buffer.from(name),data=Buffer.from(content),crc=crc32(data),l=Buffer.alloc(30),c=Buffer.alloc(46);l.writeUInt32LE(0x04034b50);l.writeUInt16LE(20,4);l.writeUInt32LE(crc,14);l.writeUInt32LE(data.length,18);l.writeUInt32LE(data.length,22);l.writeUInt16LE(n.length,26);c.writeUInt32LE(0x02014b50);c.writeUInt16LE(20,4);c.writeUInt16LE(20,6);c.writeUInt32LE(crc,16);c.writeUInt32LE(data.length,20);c.writeUInt32LE(data.length,24);c.writeUInt16LE(n.length,28);c.writeUInt32LE(offset,42);locals.push(l,n,data);centrals.push(c,n);offset+=l.length+n.length+data.length}
  const central=Buffer.concat(centrals),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(Object.keys(files).length,8);end.writeUInt16LE(Object.keys(files).length,10);end.writeUInt32LE(central.length,12);end.writeUInt32LE(offset,16);writeFileSync(path,Buffer.concat([...locals,central,end]))
}
function zipEntry(zip,name) {
  const end=zip.lastIndexOf(Buffer.from([0x50,0x4b,0x05,0x06]));let pos=zip.readUInt32LE(end+16)
  for(let n=0;n<zip.readUInt16LE(end+10);n++){const length=zip.readUInt16LE(pos+28),file=zip.subarray(pos+46,pos+46+length).toString();if(file===name){const local=zip.readUInt32LE(pos+42),start=local+30+zip.readUInt16LE(local+26)+zip.readUInt16LE(local+28),data=zip.subarray(start,start+zip.readUInt32LE(pos+20));return(zip.readUInt16LE(pos+10)===0?data:inflateRawSync(data)).toString()}pos+=46+length+zip.readUInt16LE(pos+30)+zip.readUInt16LE(pos+32)}throw Error('DOCX entry missing')
}
