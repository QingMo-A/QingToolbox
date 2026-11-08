import { bounds, plot, type Item } from './model'
function path(ctx: CanvasRenderingContext2D, points: Item['points'], smooth = false) {
  if (!points.length) return
  ctx.beginPath();ctx.moveTo(points[0].x, points[0].y)
  if (points.length === 1) { ctx.lineTo(points[0].x + .01, points[0].y);ctx.stroke();return }
  if (smooth && points.length > 2) {
    for (let i=1;i<points.length-1;i++) ctx.quadraticCurveTo(points[i].x,points[i].y,(points[i].x+points[i+1].x)/2,(points[i].y+points[i+1].y)/2)
    const end=points[points.length-1];ctx.lineTo(end.x,end.y)
  } else for(const p of points.slice(1)) ctx.lineTo(p.x,p.y)
  ctx.stroke()
}
export function render(ctx: CanvasRenderingContext2D, items: Item[], selected: number | null, dark = false) {
  for (const item of items) {
    ctx.save();ctx.lineCap='round';ctx.lineJoin='round';ctx.strokeStyle=item.color;ctx.fillStyle=item.color;ctx.lineWidth=item.width
    const r=bounds(item)
    if (item.kind==='stroke') path(ctx,item.points,true)
    if (item.kind==='rect') ctx.strokeRect(r.x,r.y,r.w,r.h)
    if (item.kind==='ellipse') {ctx.beginPath();ctx.ellipse(r.x+r.w/2,r.y+r.h/2,Math.max(.1,r.w/2),Math.max(.1,r.h/2),0,0,Math.PI*2);ctx.stroke()}
    if (item.kind==='text') { const size=item.fontSize??30;ctx.font=`500 ${size}px ${getComputedStyle(document.body).fontFamily}`;ctx.textBaseline='top';(item.text??'').split('\n').forEach((line,i)=>ctx.fillText(line,r.x,r.y+i*size*1.3)) }
    if (item.kind==='graph') {
      ctx.fillStyle=dark?'rgba(20,28,44,.9)':'rgba(255,255,255,.92)';ctx.fillRect(r.x,r.y,r.w,r.h)
      ctx.strokeStyle=dark?'#60718a':'#8b9bb1';ctx.lineWidth=1;ctx.strokeRect(r.x,r.y,r.w,r.h)
      const [xmin,xmax,ymin,ymax]=item.range??[-10,10,-5,5]
      const x=(v:number)=>r.x+(v-xmin)/(xmax-xmin)*r.w,y=(v:number)=>r.y+r.h-(v-ymin)/(ymax-ymin)*r.h
      ctx.save();ctx.beginPath();ctx.rect(r.x,r.y,r.w,r.h);ctx.clip()
      if(item.grid){ctx.strokeStyle=dark?'#303f57':'#e2e8f1';ctx.beginPath();for(let i=1;i<10;i++){ctx.moveTo(r.x+r.w*i/10,r.y);ctx.lineTo(r.x+r.w*i/10,r.y+r.h);ctx.moveTo(r.x,r.y+r.h*i/10);ctx.lineTo(r.x+r.w,r.y+r.h*i/10)}ctx.stroke()}
      ctx.strokeStyle=dark?'#8b9bb1':'#74859b';ctx.beginPath();if(xmin<=0&&xmax>=0){ctx.moveTo(x(0),r.y);ctx.lineTo(x(0),r.y+r.h)}if(ymin<=0&&ymax>=0){ctx.moveTo(r.x,y(0));ctx.lineTo(r.x+r.w,y(0))}ctx.stroke()
      ctx.strokeStyle=item.color;ctx.lineWidth=item.width;ctx.beginPath();let continuing=false
      for (const p of plot(item)) {if(!p){continuing=false;continue}if(continuing)ctx.lineTo(p.x,p.y);else ctx.moveTo(p.x,p.y);continuing=true}ctx.stroke();ctx.restore()
      ctx.fillStyle=dark?'#dce7f7':'#334155';ctx.font='12px "Segoe UI",sans-serif';ctx.textBaseline='top';ctx.fillText(`y = ${item.expression}`,r.x+8,r.y+8,Math.max(20,r.w-16));ctx.textBaseline='bottom';ctx.fillText(`${xmin} ≤ x ≤ ${xmax}    ${ymin} ≤ y ≤ ${ymax}`,r.x+8,r.y+r.h-6,Math.max(20,r.w-16))
    }
    if (item.id===selected) {ctx.strokeStyle='#3478f6';ctx.lineWidth=1;ctx.setLineDash([5,4]);ctx.strokeRect(r.x-6,r.y-6,r.w+12,r.h+12);ctx.setLineDash([]);ctx.fillStyle='#3478f6';ctx.fillRect(r.x+r.w+2,r.y+r.h+2,7,7)}
    ctx.restore()
  }
}
