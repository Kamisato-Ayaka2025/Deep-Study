'use strict';
// Declarative diagrams only: never evaluate model scripts or insert model HTML/SVG.
function lessonDiagram(text){
 const figure=document.createElement('figure');figure.className='lesson-figure';
 const fallback=()=>{figure.replaceChildren();const pre=document.createElement('pre');pre.className='diagram-fallback';pre.textContent=text;figure.append(pre);return figure;};
 const ns='http://www.w3.org/2000/svg';
 const node=(tag,attrs={},label)=>{const el=document.createElementNS(ns,tag);for(const [k,v] of Object.entries(attrs))el.setAttribute(k,String(v));if(label!==undefined)el.textContent=label;return el;};
 const validText=(s,max=100)=>typeof s==='string'&&s.length<=max;
 try{
  let data;
  if(text.trim().startsWith('{'))data=JSON.parse(text);
  else {const nodes=text.split(/\s*(?:->|→)\s*/);if(nodes.length<2)return fallback();data={type:'flow',nodes,edges:nodes.slice(1).map((_,i)=>[i,i+1]),title:''};}
  if(!data||!validText(data.title??'',200))return fallback();
  const title=data.title??'';let svg;
  if(data.type==='flow'){
   if(!Array.isArray(data.nodes)||data.nodes.length<1||data.nodes.length>20||!data.nodes.every(s=>validText(s,48))||!Array.isArray(data.edges)||data.edges.length>40)return fallback();
   if(!data.edges.every(e=>Array.isArray(e)&&e.length>=2&&e.length<=3&&Number.isInteger(e[0])&&Number.isInteger(e[1])&&e[0]!==e[1]&&e[0]>=0&&e[1]>=0&&e[0]<data.nodes.length&&e[1]<data.nodes.length&&validText(e[2]??'',24)))return fallback();
   const cols=Math.min(3,data.nodes.length),width=cols*300,height=Math.ceil(data.nodes.length/cols)*150;
   svg=node('svg',{viewBox:`0 0 ${width} ${height}`,role:'img','aria-label':title||'流程示意图'});
   const pos=i=>({x:(i%cols)*300+150,y:Math.floor(i/cols)*150+70});
   for(const [from,to,label] of data.edges){
    const a=pos(from),b=pos(to);if(from===to)continue;
    const dx=b.x-a.x,dy=b.y-a.y,len=Math.hypot(dx,dy),ux=dx/len,uy=dy/len;
    const inset=Math.min(Math.abs(ux)>0.8?115:48,len/3);const x=b.x-ux*inset,y=b.y-uy*inset;
    svg.append(node('line',{x1:a.x+ux*inset,y1:a.y+uy*inset,x2:x,y2:y,stroke:'#5364ad','stroke-width':2}));
    svg.append(node('polygon',{points:`${x},${y} ${x-ux*10-uy*5},${y-uy*10+ux*5} ${x-ux*10+uy*5},${y-uy*10-ux*5}`,fill:'#5364ad'}));
    if(label)svg.append(node('text',{x:(a.x+b.x)/2,y:(a.y+b.y)/2-8,'text-anchor':'middle',fill:'#39446d','font-size':13},label));
   }
   data.nodes.forEach((label,i)=>{const p=pos(i);svg.append(node('rect',{x:p.x-108,y:p.y-42,width:216,height:84,rx:12,fill:'#eef1ff',stroke:'#7382d8'}));
    const t=node('text',{x:p.x,y:p.y-12,'text-anchor':'middle',fill:'#1b2540','font-size':16});
    const parts=Array.from(label);for(let offset=0;offset<parts.length;offset+=16)t.append(node('tspan',{x:p.x,dy:offset===0?0:20},parts.slice(offset,offset+16).join('')));svg.append(t);});
  }else if(data.type==='bar'||data.type==='line'){
   if(!Array.isArray(data.labels)||!Array.isArray(data.values)||data.labels.length<1||data.labels.length>12||data.labels.length!==data.values.length||!data.labels.every(s=>validText(s,24))||!data.values.every(v=>Number.isFinite(v)&&Math.abs(v)<=1e9)||!validText(data.unit??'',30))return fallback();
   svg=node('svg',{viewBox:'0 0 900 430',role:'img','aria-label':title||'数据示意图'});
   const low=Math.min(0,...data.values),high=Math.max(1,...data.values),scale=v=>330-(v-low)/(high-low)*280;
   const x=i=>80+(i+0.5)*760/data.values.length,base=scale(0);
   svg.append(node('line',{x1:65,y1:base,x2:860,y2:base,stroke:'#5364ad'}));
   if(data.type==='line')svg.append(node('polyline',{points:data.values.map((v,i)=>`${x(i)},${scale(v)}`).join(' '),fill:'none',stroke:'#5364e8','stroke-width':3}));
   data.values.forEach((v,i)=>{const y=scale(v);
    if(data.type==='bar')svg.append(node('rect',{x:x(i)-Math.min(28,260/data.values.length),y:Math.min(base,y),width:Math.min(56,520/data.values.length),height:Math.max(1,Math.abs(base-y)),fill:'#6474ec',rx:3}));
    else svg.append(node('circle',{cx:x(i),cy:y,r:5,fill:'#5364e8'}));
    svg.append(node('text',{x:x(i),y:y-10,'text-anchor':'middle','font-size':14,fill:'#1b2540'},String(v)));
    svg.append(node('text',{x:x(i),y:360,transform:`rotate(20 ${x(i)} 360)`,'text-anchor':'middle','font-size':12,fill:'#1b2540'},data.labels[i]));
   });
   if(data.unit)svg.append(node('text',{x:20,y:25,'font-size':14,fill:'#1b2540'},data.unit));
  }else return fallback();
  figure.append(svg);if(title){const caption=document.createElement('figcaption');caption.textContent=title;figure.append(caption);}return figure;
 }catch{return fallback();}
}
