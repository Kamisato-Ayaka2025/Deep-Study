'use strict';
let current=null, restoring=false, saveTimer=null, scrollInitialized=false;
const element=(tag,text,cls)=>{const el=document.createElement(tag);if(text!==undefined)el.textContent=text;if(cls)el.className=cls;return el;};
function message(action,data){if(current)window.chrome.webview.postMessage({action,lessonHash:current.identity.lessonHash,...data});}
function math(text,target,displayMode=false){try{katex.render(text,target,{displayMode,throwOnError:true,trust:false,strict:'error',maxExpand:500,maxSize:20});return true;}catch{target.textContent=text;return false;}}
function renderCode(text,language=''){
 const pre=element('pre');pre.className='code-block';
 const plain=()=>{pre.textContent=text;return pre;};
 if(text.length>65536||!['','c','cpp','c++','cc','cxx','h','hpp'].includes(language.toLowerCase()))return plain();
 try{
  const keywords=new Set('alignas alignof auto bool break case catch char class const constexpr continue default delete do double else enum explicit extern false float for friend if inline int long namespace new noexcept nullptr operator private protected public register reinterpret_cast return short signed sizeof static static_cast struct switch template this throw true try typedef typename union unsigned using virtual void volatile wchar_t while'.split(' '));
  const tokens=[];let i=0;
  const emit=(end,kind)=>{tokens.push([text.slice(i,end),kind]);i=end;};
  while(i<text.length){
   if(tokens.length>=8192)return plain();
   const start=i,c=text[i],next=text[i+1];let end=i+1;
   if(c==='/'&&next==='/'){end=i+2;while(end<text.length&&!'\r\n'.includes(text[end]))end++;emit(end,'comment');}
   else if(c==='/'&&next==='*'){end=i+2;while(end<text.length&&!(text[end-1]==='*'&&text[end]==='/'))end++;emit(Math.min(end+1,text.length),'comment');}
   else if(c==='"'||c==="'"){while(end<text.length){if(text[end]==='\\'){end=Math.min(end+2,text.length);continue;}if(text[end++]===c)break;}emit(end,'string');}
   else if(/[A-Za-z_]/.test(c)){while(end<text.length&&/[A-Za-z_0-9]/.test(text[end]))end++;emit(end,keywords.has(text.slice(i,end))?'keyword':'');}
   else if(/[0-9]/.test(c)){while(end<text.length&&/[A-Za-z_0-9.]/.test(text[end]))end++;emit(end,'number');}
   else {while(end<text.length&&!/[A-Za-z_0-9/"']/.test(text[end]))end++;emit(end,'');}
   if(i<=start)throw new Error('scanner stalled');
  }
  if(tokens.map(t=>t[0]).join('')!==text)return plain();
  for(const [value,kind] of tokens)pre.append(kind?element('span',value,'token-'+kind):document.createTextNode(value));
  return pre;
 }catch{return plain();}
}
function rich(text,tag='span'){
 const target=element(tag);let last=0;
 // Parse only bounded local formatting; model text never becomes HTML. Code spans are literal.
 const re=/`([^`\n]+)`|\*\*([^*]+)\*\*|\$\$([^$]+)\$\$|\$([^$\n]+)\$|\\\(([^\n]*?)\\\)|\\\[([\s\S]*?)\\\]/g;
 for(const m of text.matchAll(re)){
  target.append(document.createTextNode(text.slice(last,m.index)));
  if(m[1]!==undefined)target.append(element('code',m[1]));
  else if(m[2]!==undefined)target.append(rich(m[2],'strong'));
  else {const display=m[3]!==undefined||m[6]!==undefined;const formula=element('span',undefined,display?'display-math':'inline-math');math(m[3]??m[4]??m[5]??m[6],formula,display);target.append(formula);}
  last=m.index+m[0].length;
 }
 target.append(document.createTextNode(text.slice(last)));return target;
}
function paragraph(text){
 const target=element('div');const rawLines=text.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g)??[];const lines=rawLines.map(l=>l.replace(/(?:\r\n|\r|\n)$/,''));let pending=[],list=null;
 const flush=()=>{if(pending.length){target.append(rich(pending.join('\n'),'p'));pending=[];}list=null;};
 for(let i=0;i<lines.length;i++){
  const line=lines[i];
  const fence=line.match(/^\s*```([^\s`]*)[^\r\n]*$/);
  if(fence){flush();const code=[];while(++i<lines.length&&!/^\s*```\s*$/.test(lines[i]))code.push(rawLines[i]);const value=code.join('');target.append(renderCode(i<lines.length?value.replace(/(?:\r\n|\r|\n)$/,''):value,fence[1]));continue;}
  // Only pipe-delimited tables with an explicit separator; every cell still uses
  // the same safe text/inline-math renderer, never HTML or remote resources.
  const cells=value=>value.trim().replace(/^\|/,'').replace(/\|$/,'').split('|').map(s=>s.trim());
  if(line.trim().startsWith('|')&&lines[i+1]?.trim().startsWith('|')&&
     cells(lines[i+1]).every(s=>/^:?-{3,}:?$/.test(s))&&cells(line).length===cells(lines[i+1]).length){
   flush();const table=element('table'),head=element('tr');const width=cells(line).length;
   cells(line).forEach(s=>head.append(rich(s,'th')));const thead=element('thead');thead.append(head);table.append(thead);
   const body=element('tbody');i++;
   while(i+1<lines.length&&lines[i+1].trim().startsWith('|')&&cells(lines[i+1]).length===width){
    const row=element('tr');cells(lines[++i]).forEach(s=>row.append(rich(s,'td')));body.append(row);
   }
   table.append(body);const scroll=element('div',undefined,'table-scroll');scroll.append(table);target.append(scroll);continue;
  }
  const item=line.match(/^\s*(?:([-+*])|\d+[.)])\s+(.+)$/);
  if(item){if(pending.length)flush();const tag=item[1]?'ul':'ol';if(!list||list.tagName.toLowerCase()!==tag){list=element(tag);target.append(list);}list.append(rich(item[2],'li'));continue;}
  const heading=line.match(/^#{1,6}\s+(.+)$/),quote=line.match(/^>\s?(.*)$/);
  if(heading||quote){flush();target.append(rich(heading?heading[1]:quote[1],heading?'h2':'blockquote'));}
  else if(!line.trim())flush();else{list=null;pending.push(line);}
 }
 flush();return target;
}
function mathBlock(text,target){
 // A model may put prose with inline formulas in a math block. Fall back to safe
 // document formatting instead of displaying the whole paragraph as a red TeX error.
 const trimmed=text.trim();
 const wrapped=trimmed.match(/^\$\$([\s\S]*)\$\$$/)??trimmed.match(/^\\\[([\s\S]*)\\\]$/);
 if(!math(wrapped?wrapped[1]:trimmed,target,true))target.replaceChildren(paragraph(text));
}
function render(reader){
 const same=current?.identity.lessonHash===reader.identity.lessonHash;
 const previous=same&&scrollInitialized?window.scrollY:null;
 if(!same)scrollInitialized=false;
 current=reader;restoring=true;document.getElementById('title').textContent='课程正文';
 document.getElementById('state').textContent=reader.completedAt?'已完成 · 再次作答不会撤回完成记录':reader.canAnswer?'可测验 · 每题至少答对一次即可完成':'历史版本 · 仅可阅读';
 const content=document.getElementById('content');content.replaceChildren();
 for(const block of reader.blocks){const section=element('section',undefined,block.kind);section.id=block.id;section.dataset.block=block.id;
  if(block.kind==='question'){
   const q=reader.questions.find(q=>q.id===block.questionId);const prompt=paragraph(q.prompt);prompt.className='question-prompt';section.append(prompt);const form=element('form');form.dataset.question=q.id;
   for(const option of q.options){const label=element('label'),input=element('input');input.type='radio';input.name=q.id;input.value=option.id;input.disabled=!reader.canAnswer;label.append(input,element('b',(option.displayLabel??'')+'. ','option-label'),rich(option.text));form.append(label);}
   const button=element('button','提交答案');button.type='submit';button.disabled=!reader.canAnswer;form.append(button);form.addEventListener('submit',e=>{e.preventDefault();const picked=form.querySelector('input:checked');if(picked)message('answer',{questionId:q.id,optionId:picked.value,requestId:crypto.randomUUID()});});section.append(form);
   if(q.answer){const correct=q.options.find(o=>o.id===q.answer.correctOptionId);const answer=paragraph('正确选项：'+(correct?.displayLabel??'')+' · '+(correct?.text??'')+'\n'+q.answer.explanation);answer.className='answer';section.append(answer);}
  }else if(block.kind==='heading')section.append(rich(block.text,'h2'));
  else if(block.kind==='code')section.append(renderCode(block.text));
  else if(block.kind==='math')mathBlock(block.text,section);
  else if(block.kind==='diagram'){const graph=element('div',undefined,'diagram');const parts=block.text.split(/\s*(?:->|→)\s*/).slice(0,20);parts.forEach((part,i)=>{if(i)graph.append(element('b','→'));graph.append(element('span',part));});section.append(graph);}
  else section.append(paragraph(block.text));content.append(section);
 }
 document.getElementById('sources').textContent='固定版本资料：\n'+reader.sources.map(s=>s.key+' · '+s.reference.locator.sourceLabel+' / '+(s.reference.locator.heading||'')+' · '+s.reference.versionId).join('\n');
 requestAnimationFrame(()=>{if(previous!==null)window.scrollTo(0,previous);else if(reader.position)document.getElementById(reader.position.blockId)?.scrollIntoView();else window.scrollTo(0,0);scrollInitialized=true;setTimeout(()=>restoring=false,150);});
}
window.chrome.webview.addEventListener('message',e=>{if(e.data.type==='lesson')render(e.data.value);else if(e.data.type==='clear'){current=null;scrollInitialized=false;clearTimeout(saveTimer);document.getElementById('content').replaceChildren();document.getElementById('sources').textContent='';document.getElementById('title').textContent='课程正文';document.getElementById('state').textContent='正文未生成，请查看左侧课程目标并点击生成。';}});
document.getElementById('quiz').addEventListener('click',()=>document.querySelector('.question')?.scrollIntoView());
window.addEventListener('scroll',()=>{if(restoring||!current?.canAnswer)return;clearTimeout(saveTimer);saveTimer=setTimeout(()=>{const blocks=[...document.querySelectorAll('section')];const first=blocks.find(b=>b.getBoundingClientRect().bottom>20);if(first)message('position',{blockId:first.id,offset:0,requestId:crypto.randomUUID()});},300);});
window.chrome.webview.postMessage({action:'ready'});
