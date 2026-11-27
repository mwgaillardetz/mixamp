"use strict";
const $ = id => document.getElementById(id);
const audio = $("audio"), grid = $("grid"), menu = $("menu");
const fields = [
  ["title","Title",230],["artistDisplay","Track artist",155],["artist","Album artist",150],
  ["album","Album",160],["genre","Genre",100],["disc","Disc",48],["number","#",52],
  ["year","Year",58],["length","Time",65],["filePath","File path",300],["format","Format",78],["bitrate","kbps",62]
];
let columns = new Set(["title","artistDisplay","album","length"]), source=[], visible=[], catalog=[], lists=[], queue=[];
let selection=new Set(), anchor=-1, cursor=-1, section="Songs", listId="", sort="title", direction=1, playing=null, queueAt=-1;
let glassWanted=false;
let themes=[], currentTheme="", spectrumInk="#16696B";
let eqMode="Auto", showBars=true, ctx, filters, preamp, analyser, fft, frame=0, lastFrame=0, sequence=0, eqSeq=0, clipEnd=null;
const collator=new Intl.Collator(undefined,{numeric:true,sensitivity:"base"});
const time=s=>Math.floor(Math.max(0,s||0)/60)+":"+String(Math.floor(Math.max(0,s||0))%60).padStart(2,"0");
async function call(action, params={}) {
  const res=await fetch("/command",{method:"POST",headers:{"Content-Type":"application/json","X-Mixamp":"app"},body:JSON.stringify({action,...params})});
  const data=await res.json();
  if(!res.ok) throw Error(data.error||"Request failed");
  return data;
}
function run(fn){return async (...args)=>{try{await fn(...args);}catch(e){if(e.message!=="Canceled")$("status").textContent=e.message;}};}
function popup(entries,x,y){
  menu.replaceChildren();
  for(const [label,fn] of entries){const b=document.createElement("button");b.textContent=label;b.disabled=!fn;b.onclick=run(async()=>{menu.hidden=true;await fn();});menu.append(b);}
  menu.hidden=false;menu.style.left=Math.max(2,Math.min(x,innerWidth-menu.offsetWidth-4))+"px";
  menu.style.top=Math.max(2,Math.min(y,innerHeight-menu.offsetHeight-4))+"px";menu.querySelector("button")?.focus();
}
document.addEventListener("pointerdown",e=>{if(!menu.contains(e.target))menu.hidden=true;});
document.addEventListener("keydown",e=>{if(e.key==="Escape"&&!menu.hidden){e.preventDefault();menu.hidden=true;}});
const toolEntries=new Map();
function tool(id,entries){toolEntries.set(id,entries);$(id).onclick=e=>{const r=e.currentTarget.getBoundingClientRect();popup(entries(),r.left,r.bottom+2);};}
function applyTheme(entry) {
  if(!entry)return;
  currentTheme=entry.name;
  $("themePicker").value=entry.name;
  const root=document.documentElement;
  root.dataset.theme=entry.name;
  for(const [key,value] of Object.entries(entry.palette))root.style.setProperty("--"+key,value);
  root.style.colorScheme=["Silver Halide","Cyanotype"].includes(entry.name)?"light":"dark";
  syncGlass();
  const classic=entry.name==="Bitstream 98", seek=$("seek");
  const seekParent=document.querySelector(classic?"header":".deck");
  if(seek.parentElement!==seekParent)seekParent.append(seek);
  spectrumInk=entry.palette.accent;document.dispatchEvent(new Event("mixamp-theme"));
}
const reducedGlass=matchMedia("(prefers-reduced-transparency: reduce), (forced-colors: active)");
function syncGlass(){
  const on=glassWanted&&!reducedGlass.matches;
  if(window.external?.sendMessage)window.external.sendMessage((on?"glass:":"theme:")+currentTheme);
  else document.documentElement.classList.toggle("glass",on);
}
reducedGlass.addEventListener("change",()=>{if(currentTheme)syncGlass();});
async function toggleGlass(){
  const on=!glassWanted;
  await call("setting",{id:"glass",value:on?"on":"off"});
  glassWanted=on;syncGlass();
}
async function chooseTheme(name){
  const picker=$("themePicker");
  try{
    await call("setting",{id:"theme",value:name});
    applyTheme(themes.find(entry=>entry.name===name));
  }finally{picker.value=currentTheme;}
}
$("themePicker").onchange=run(e=>chooseTheme(e.target.value));
$("view").onclick=()=>{
  const anchor=$("view").getBoundingClientRect();
  const folder=document.createElement("details"), heading=document.createElement("summary");
  heading.textContent="Themes";heading.className="themeFolder";folder.append(heading);
  for(const entry of themes){
    const choice=document.createElement("button");
    choice.className="themeChoice";choice.setAttribute("role","menuitemradio");
    const active=entry.name===currentTheme;
    choice.setAttribute("aria-checked",String(active));choice.textContent=(active?"✓　":"　　")+entry.name;
    choice.title=entry.description;
    choice.onclick=run(async()=>{
      menu.hidden=true;$("view").focus();
      await chooseTheme(entry.name);
    });
    folder.append(choice);
  }
  heading.onkeydown=e=>{
    if(e.key==="ArrowRight"||e.key==="ArrowLeft"){e.preventDefault();folder.open=e.key==="ArrowRight";}
  };
  const font=document.createElement("button"), root=document.documentElement;
  const enabled=root.classList.contains("ubuntuFont");
  font.setAttribute("role","menuitemcheckbox");font.setAttribute("aria-checked",String(enabled));
  font.textContent="Use Ubuntu font";
  font.onclick=run(async()=>{
    menu.hidden=true;$("view").focus();
    await call("setting",{id:"font",value:enabled?"theme":"ubuntu"});
    root.classList.toggle("ubuntuFont",!enabled);
  });
  const glass=document.createElement("button");glass.setAttribute("role","menuitemcheckbox");
  glass.setAttribute("aria-checked",String(glassWanted));glass.textContent="Glass appearance";
  glass.onclick=run(async()=>{menu.hidden=true;$("view").focus();await toggleGlass();});
  menu.replaceChildren(folder,font,glass);menu.hidden=false;
  const position=()=>{menu.style.left=Math.max(2,Math.min(anchor.left,innerWidth-menu.offsetWidth-4))+"px";
    menu.style.top=Math.max(2,Math.min(anchor.bottom+2,innerHeight-menu.offsetHeight-4))+"px";};
  folder.ontoggle=position;position();heading.focus();
};
function ask(title,{value,choices,lines}={}){
  const dlg=$("review"),body=$("reviewBody"); body.replaceChildren();dlg.querySelector("h2").textContent=title;
  let control;
  if(choices){if(!choices.length)throw Error("No entries available.");control=document.createElement("select");
    for(const c of choices)control.add(new Option(c.name,c.id));body.append(control);}
  else if(value!==undefined){control=document.createElement("input");control.value=value;body.append(control);}
  else body.textContent=(lines||[]).join("\n\n");
  dlg.showModal();control?.focus();if(control?.select)control.select();
  return new Promise((resolve,reject)=>{
    const close=ok=>{dlg.oncancel=null;dlg.close();ok?resolve(control?control.value:true):reject(Error("Canceled"));};
    $("confirm").onclick=()=>close(true);$("cancel").onclick=()=>close(false);dlg.oncancel=e=>{e.preventDefault();close(false);};
    if(control)control.onkeydown=e=>{if(e.key==="Enter")close(true);};
  });
}
function headers(){
  const shown=fields.filter(f=>columns.has(f[0]));grid.style.setProperty("--cols",shown.map(f=>"minmax("+f[2]+"px,"+(f[0]==="title"?2:1)+"fr)").join(" "));
  grid.style.setProperty("--width",shown.reduce((n,f)=>n+f[2],0)+"px");$("head").replaceChildren();
  for(const [key,label] of shown){const b=document.createElement("button");b.textContent=label+(sort===key?(direction===1?" ↑":" ↓"):"");b.onclick=()=>{direction=sort===key?-direction:1;sort=key;refreshView();};$("head").append(b);}
}
$("head").oncontextmenu=e=>{e.preventDefault();popup(fields.map(([key,label])=>[(columns.has(key)?"✓ ":"")+label,async()=>{
  if(columns.has(key)&&columns.size>1)columns.delete(key);else columns.add(key);
  await call("setting",{id:"columns",value:[...columns].join("|")});headers();render();
}]),e.clientX,e.clientY);};
function refreshView(){
  const query=$("search").value.toLocaleLowerCase().trim(), key=sort==="length"?"duration":sort;
  visible=query?source.filter(t=>[t.title,t.artistDisplay,t.album,t.genre,t.filePath].some(s=>s?.toLocaleLowerCase().includes(query))):source.slice();
  if(sort)visible.sort((a,b)=>direction*(typeof a[key]==="number"?a[key]-b[key]:collator.compare(a[key]||"",b[key]||"")));
  selection.clear();anchor=cursor=-1;grid.scrollTop=0;$("spacer").style.height=visible.length*26+"px";headers();render();
}
let radioView=0, radioQueue="", stationName="", sonicQueue=false;
function inQueue(){return section==="Up Next" && !listId;}
function show(name,rows,id=""){
  ++radioView;
  const browsing=name==="Radio" && !id;
  $("radioPane").hidden=!browsing;grid.hidden=browsing;$("search").hidden=browsing;
  $("radio").classList.toggle("active",browsing);
  section=name;listId=id;source=rows;sort=name==="Songs"?"title":"";direction=1;$("queueBack").hidden=!(inQueue()&&stationName);$("heading").textContent=inQueue()&&stationName?stationName+" · Up Next":name;$("search").value="";
  $("songs").classList.toggle("active",name==="Songs");$("queue").classList.toggle("active",name==="Up Next");
  for(const b of $("lists").children)b.classList.toggle("active",b.dataset.id===id);refreshView();
}
function render(){
  const start=Math.max(0,Math.floor(grid.scrollTop/26)-3), end=Math.min(visible.length,start+Math.ceil(grid.clientHeight/26)+8);
  const frag=document.createDocumentFragment();const shown=fields.filter(f=>columns.has(f[0]));
  for(let i=start;i<end;i++){
    const t=visible[i],row=document.createElement("div");row.className="row"+(selection.has(i)?" selected":"");row.dataset.index=i;
    for(const [key] of shown){const cell=document.createElement("span");cell.textContent=t[key]??"";row.append(cell);}
    frag.append(row);
  }
  $("rows").style.transform="translateY("+start*26+"px)";$("rows").replaceChildren(frag);
}
grid.onscroll=render;new ResizeObserver(render).observe(grid);let searchDelay;
$("search").oninput=()=>{clearTimeout(searchDelay);searchDelay=setTimeout(refreshView,80);};
function indexAt(e){const row=e.target.closest(".row");return row?Number(row.dataset.index):-1;}
function select(i,e){
  cursor=i;
  if(e.shiftKey&&anchor>=0){selection.clear();for(let j=Math.min(i,anchor);j<=Math.max(i,anchor);j++)selection.add(j);}
  else if(e.metaKey||e.ctrlKey){if(selection.has(i))selection.delete(i);else selection.add(i);anchor=i;}
  else{selection=new Set([i]);anchor=i;}
  for(const row of $("rows").children)row.classList.toggle("selected",selection.has(Number(row.dataset.index)));
}
grid.onclick=e=>{const i=indexAt(e);if(i>=0){grid.focus({preventScroll:true});select(i,e);}};
grid.ondblclick=run(async e=>{const i=indexAt(e);if(i>=0){select(i,{});await startSelection();}});
grid.oncontextmenu=e=>{const i=indexAt(e);if(i<0)return;e.preventDefault();if(!selection.has(i))select(i,e);
  popup([["Play",startSelection],["Sonically similar radio",selection.size===1?sonicRadio:null],["Add to queue",addQueue],["Add to playlist",addList],...edits()],e.clientX,e.clientY);};
function chosen(){const tracks=[...selection].sort((a,b)=>a-b).map(i=>visible[i]).filter(Boolean);if(!tracks.length)throw Error("Select tracks first.");return tracks;}
function one(){const t=chosen();if(t.length!==1)throw Error("Select one track.");return t[0];}
function initAudio(){
  const Audio=window.AudioContext||window.webkitAudioContext;
  if(ctx||!Audio)return;
  ctx=new Audio();const src=ctx.createMediaElementSource(audio);
  filters=[120,1000,6500].map(f=>{const b=ctx.createBiquadFilter();b.type="peaking";b.frequency.value=f;b.Q.value=.9;return b;});
  preamp=ctx.createGain();analyser=ctx.createAnalyser();analyser.fftSize=1024;fft=new Uint8Array(analyser.frequencyBinCount);
  src.connect(filters[0]);filters[0].connect(filters[1]);filters[1].connect(filters[2]);filters[2].connect(preamp);preamp.connect(analyser);analyser.connect(ctx.destination);
}
async function setTone(){
  const n=++eqSeq,p=await call("eq",{value:eqMode,extra:playing?.genre||""});
  if(n!==eqSeq)return;
  if(ctx)[p.low,p.mid,p.high].forEach((db,i)=>filters[i].gain.setTargetAtTime(db,ctx.currentTime,.04));
  if(ctx)preamp.gain.setTargetAtTime(p.headroom,ctx.currentTime,.04);
  $("eq").title=eqMode.startsWith("Custom|")?"Custom":eqMode+" · "+p.name;
}
function showCover(url){
  const slot=$("cover");
  if(url && slot.firstElementChild?.getAttribute("src")===url)return;
  slot.replaceChildren();slot.hidden=true;
  if(!url)return;
  const img=new Image();
  img.alt="";img.onload=()=>{if(img.parentNode===slot)slot.hidden=false;};
  img.onerror=()=>img.remove();slot.append(img);img.src=url;
}
async function start(track){
  const seq=++sequence;initAudio();await ctx?.resume();if(seq!==sequence)return;
  audio.pause();$("status").textContent="Opening "+track.title+"…";
  const data=await call("prepare",{id:track.id});if(seq!==sequence)return;
  playing=data.track;clipEnd=data.cue.end==null?null:data.cue.end/1000;await setTone();if(seq!==sequence)return;
  audio.src=data.url;audio.volume=Number($("volume").value);audio.onloadedmetadata=()=>{if(data.cue.start!=null)audio.currentTime=data.cue.start/1000;};
  for(const [id,value] of [["now",playing.title],["by",playing.artistDisplay],["album",playing.album]]){
    $(id).textContent=$(id).title=value||"";
  }
  showCover(data.artwork);
  if(navigator.mediaSession && window.MediaMetadata)
    navigator.mediaSession.metadata=new MediaMetadata({title:playing.title,artist:playing.artistDisplay,album:playing.album});
  await audio.play();$("status").textContent="Direct play";
}
async function startSelection(){const tracks=chosen();if(inQueue()){queueAt=queue.indexOf(tracks[0]);}
  else{radioQueue=stationName="";queue=tracks.slice();queueAt=0;}await start(tracks[0]);}
async function advance(step){const n=queueAt+step;
  if(n===queue.length && radioQueue && queue.length){
    const id=radioQueue, last=queue.at(-1).queueItem;
    const more=sonicQueue ? await call("continueSonic",{id,value:last,extra:queue.at(-1).id,ids:queue.map(t=>t.id)}) : await call("continueRadio",{id,value:last});
    if(id!==radioQueue || n!==queueAt+step)return;
    const anchor=more.findIndex(t=>t.queueItem===last), seen=new Set(queue.map(t=>t.queueItem));
    if(anchor>=0) queue.push(...more.slice(anchor+1).filter(t=>t.queueItem&&!seen.has(t.queueItem)));
    refreshQueue();
  }if(n<0||n>=queue.length){if(step>0)audio.pause();return;}queueAt=n;await start(queue[n]);}
function refreshQueue(){
  if(!inQueue())return;
  if(sort||$("search").value){refreshView();return;}
  visible=queue.slice();$("spacer").style.height=visible.length*26+"px";render();
}
function addQueue(){const t=chosen();queue.push(...t);$("status").textContent="Added "+t.length+" to Up Next";refreshQueue();}
async function resumeAudio(){
  if(!playing){
    if(selection.size)return startSelection();
    if(queue.length){queueAt=Math.max(0,queueAt);return start(queue[queueAt]);}
    return;
  }
  await ctx?.resume();await audio.play();
}
function toggleAudio(){if(audio.paused)return resumeAudio();audio.pause();}
$("prev").onclick=run(()=>advance(-1));$("next").onclick=run(()=>advance(1));$("play").onclick=run(toggleAudio);
if(navigator.mediaSession){
  const handlers={play:resumeAudio,pause:()=>audio.pause(),previoustrack:()=>advance(-1),nexttrack:()=>advance(1)};
  for(const [action,handler] of Object.entries(handlers)){
    try{navigator.mediaSession.setActionHandler(action,run(handler));}catch(e){if(e.name!=="NotSupportedError")throw e;}
  }
}
function updateTransport(active){
  const action=active?"Pause":"Play", button=$("play");
  button.classList.toggle("playing",active);button.setAttribute("aria-label",action);button.title=action+" (Space)";
  if(navigator.mediaSession)navigator.mediaSession.playbackState=active?"playing":"paused";
}
function setVolume(){
  const slider=$("volume"), percent=Math.round(Number(slider.value)*100)+"%";
  audio.volume=Number(slider.value);slider.style.setProperty("--fill",percent);
  slider.setAttribute("aria-valuetext",percent);slider.title="Volume "+percent;
}
$("volume").oninput=setVolume;setVolume();
$("seek").oninput=()=>{if(playing)audio.currentTime=+$("seek").value;};
audio.ontimeupdate=()=>{$("clock").textContent=time(audio.currentTime);$("seek").max=isFinite(audio.duration)?audio.duration:0;$("seek").value=audio.currentTime;
  $("seek").style.setProperty("--fill",(audio.duration>0 ? 100*audio.currentTime/audio.duration : 0)+"%");
  if(clipEnd!==null&&audio.currentTime>=clipEnd){clipEnd=null;audio.pause();run(()=>advance(1))();}};
audio.onended=run(()=>{updateTransport(false);return advance(1);});
audio.onerror=()=>{updateTransport(false);$("status").textContent="The audio decoder could not open this track.";};
function bars(ts){
  if(audio.paused||!showBars||!analyser)return;
  if(ts-lastFrame>=50){lastFrame=ts;const canvas=$("bars"),scale=window.devicePixelRatio||1;
    const w=Math.max(1,Math.round(canvas.clientWidth*scale)),h=Math.max(1,Math.round(canvas.clientHeight*scale));
    if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
    const c=canvas.getContext("2d");c.clearRect(0,0,w,h);analyser.getByteFrequencyData(fft);c.fillStyle=spectrumInk;
    for(let i=0;i<32;i++){const bin=Math.max(1,Math.floor(Math.pow(fft.length-1,i/31))),height=fft[bin]*h/255;c.fillRect(i*w/32,h-height,Math.min(2*scale,w/32),height);}}
  frame=requestAnimationFrame(bars);
}
audio.onplaying=()=>{updateTransport(true);cancelAnimationFrame(frame);frame=requestAnimationFrame(bars);};
audio.onpause=()=>{updateTransport(false);cancelAnimationFrame(frame);$("bars").getContext("2d").clearRect(0,0,$("bars").width,$("bars").height);};
async function playlist(p){show(p.name,await call("playlist",{id:p.id}),p.id);}
async function loadLists(){lists=await call("playlists");$("lists").replaceChildren();for(const p of lists){
  const b=document.createElement("button");b.textContent=p.name;b.dataset.id=p.id;b.onclick=run(()=>playlist(p));b.oncontextmenu=e=>{e.preventDefault();e.stopPropagation();popup([["New playlist",newList],["Remove playlist",async()=>{await ask("Remove "+p.name+"?");await call("removeList",{id:p.id});await reload();}]],e.clientX,e.clientY);};$("lists").append(b);}}
async function newList(){const value=await ask("New playlist",{value:""});await call("newList",{value});$("folders").open=true;await loadLists();}
async function addList(){const ids=chosen().map(t=>t.id);await loadLists();
  let id=await ask("Add to playlist",{choices:[...lists,{id:"new",name:"New playlist…"}]});
  if(id==="new"){const value=await ask("New playlist",{value:""});id=await call("newList",{value});await loadLists();}
  await call("addList",{id,ids});$("status").textContent="Playlist updated";}
$("folders").oncontextmenu=e=>{e.preventDefault();popup([["New playlist",newList]],e.clientX,e.clientY);};
$("songs").onclick=()=>show("Songs",catalog);$("queue").onclick=()=>show("Up Next",queue);
function radioButton(label, action) {
  const button=document.createElement("button");
  button.textContent=label;
  button.onclick=run(action);
  return button;
}
async function browseRadio() {
  show("Radio",[]);
  const view=radioView, pane=$("radioPane");
  pane.textContent="Loading stations…";
  let stations;
  try { stations=await call("stations"); }
  catch(error) { if(radioView===view)pane.textContent=error.message; return; }
  if(view!==radioView)return;
  pane.replaceChildren();
  stations.forEach((station,index)=>{
    const id=String(index);
    const button=radioButton(station.name,()=>station.kind==="station"
      ? startStation(id,[],view,station.name) : chooseRadio(station,id));
    button.className="station";
    pane.append(button);
  });
  pane.querySelector(".station")?.focus();
}
async function sonicRadio(){
  const track=one(),view=++radioView;
  await showStation(await call("sonic",{id:track.id}),view,track.title+" Radio",true);
}
async function startStation(id,ids,view,name) {
  await showStation(await call("startRadio",{id,ids}),view,name);
}
async function showStation(result,view,name,sonic=false){
  if(radioView!==view)return;
  if(!result.tracks.length)throw Error("Plex returned an empty station.");
  queue=result.tracks;sonicQueue=sonic;radioQueue=result.id;stationName=name;queueAt=0;
  show("Up Next",queue);grid.focus();select(0,{});
  await start(queue[0]);
}
async function chooseRadio(station,id) {
  const view=++radioView, pane=$("radioPane");
  const back=radioButton("‹  All stations",browseRadio), title=document.createElement("h2");
  title.textContent=station.name+" · Loading…";
  pane.replaceChildren(back,title);
  let choices;
  try { choices=await call("radioChoices",{id}); }
  catch(error) { if(view===radioView)title.textContent=error.message; return; }
  if(view!==radioView)return;
  title.textContent=station.name;
  if(!choices.length){pane.append("No choices in this library.");return;}
  const mix=station.kind==="artist"||station.kind==="album", picked=new Map();
  const search=document.createElement("input"), list=document.createElement("select"), seeds=document.createElement("div");
  search.type="search";search.placeholder="Search "+(mix?station.kind+"s":"stations");
  search.setAttribute("aria-label",search.placeholder);
  list.size=10;list.setAttribute("aria-label",station.name+" choices");
  const play=radioButton(mix?"Play mix":"Play station",()=>startStation(id,mix?[...picked.keys()]:[list.value],view,station.name));
  const add=radioButton("Add selection",()=>{
    if(!list.value||picked.size>=100)return;
    picked.set(list.value,list.selectedOptions[0].textContent);renderChoices();
  });
  function update(){add.disabled=!list.value||picked.size>=100;play.disabled=mix?!picked.size:!list.value;}
  function renderChoices(){
    const query=search.value.trim().toLocaleLowerCase();
    const matches=choices.filter(c=>!picked.has(c.id)&&c.name.toLocaleLowerCase().includes(query));
    // Bound the native select on large libraries. Searching covers every seed.
    list.replaceChildren(...matches.slice(0,250).map(c=>new Option(c.name,c.id)));
    count.textContent=matches.length>250?"Showing 250 of "+matches.length.toLocaleString()+" · Search to narrow": "";
    seeds.replaceChildren(...[...picked].map(([key,name])=>radioButton(name+"  ×",()=>{
      picked.delete(key);renderChoices();
    })));
    update();
  }
  const count=document.createElement("small");
  search.oninput=renderChoices;list.onchange=update;
  seeds.className="seeds";pane.append(search,seeds,count,list);
  if(mix)pane.append(add);
  pane.append(play);renderChoices();search.focus();
}
$("radio").onclick=$("queueBack").onclick=run(browseRadio);
async function reload(){catalog=await call("cached");show("Songs",catalog);await loadLists();$("status").textContent=catalog.length.toLocaleString()+" tracks";}
async function apply(plan){await ask(plan.title,{lines:plan.lines});await call("apply",{id:plan.id});await reload();}
async function review(action){
  const items=chosen(),ids=items.map(t=>t.id);let value="",extra="";
  if(action==="rename"){const t=one();value=await ask("Rename file",{value:t.filePath.split("/").pop()});}
  if(action==="organize"){value=await ask("Artist folders",{choices:[{id:"album",name:"Album artist"},{id:"track",name:"Track artist"},{id:"custom",name:"One artist folder"}]});
    if(value==="custom")extra=await ask("Artist folder name",{value:items[0].artistDisplay});}
  await apply(await call("review",{id:action,ids,value,extra}));
}
async function historyAction(action){await apply(await call(action));}
async function editMetadata(){
  const tracks=chosen(), data=await call("metadata",{ids:tracks.map(t=>t.id)});
  const dlg=$("review"),body=$("reviewBody"),rows=[];
  dlg.querySelector("h2").textContent=tracks.length===1?"Edit · "+tracks[0].title:"Edit "+tracks.length+" tracks";
  body.replaceChildren();
  const help=document.createElement("p");help.textContent="Checked fields apply to every selected track. Enter one tag per line.";body.append(help);
  const target=document.createElement("select");target.setAttribute("aria-label","Save target");
  target.add(new Option("Plex library","plex"));target.add(new Option("Mapped audio file tags","files"));
  target.options[1].disabled=!data.fileTags;body.append(target);
  const fields=document.createElement("div");body.append(fields);
  const fileFields=[["TITLE","Title","title"],["ARTIST","Track artist","artistDisplay"],["ALBUM","Album","album"],
    ["ALBUMARTIST","Album artist","artist"],["GENRE","Genre","genre"],["DATE","Year","year"],
    ["DISCNUMBER","Disc number","disc"],["TRACKNUMBER","Track number","number"]].map(([key,label,prop])=>{
      const first=tracks[0][prop];return {key,label,value:tracks.every(t=>t[prop]===first)?String(first??""):null};
    });
  function draw(){
    fields.replaceChildren();rows.length=0;
    for(const field of target.value==="files"?fileFields:data.fields){
      const row=document.createElement("div"),label=document.createElement("label"),check=document.createElement("input");
      check.type="checkbox";label.append(check,field.label);
      const input=document.createElement(field.tags?"textarea":"input");
      input.value=field.value??"";input.placeholder=field.value===null?"Mixed values":"";
      input.setAttribute("aria-label",field.label);input.oninput=()=>check.checked=true;
      row.className="metadataField";row.append(label,input);fields.append(row);rows.push({field,check,input});
    }
  }
  target.onchange=draw;draw();
  dlg.showModal();
  const ok=await new Promise(resolve=>{
    const close=value=>{dlg.oncancel=null;dlg.close();resolve(value);};
    $("confirm").textContent="Save changes";$("confirm").onclick=()=>close(true);$("cancel").onclick=()=>close(false);
    dlg.oncancel=e=>{e.preventDefault();close(false);};
  });
  $("confirm").textContent="Continue";
  const patch=Object.fromEntries(rows.filter(r=>r.check.checked).map(r=>[r.field.key,r.input.value]));
  if(ok&&Object.keys(patch).length){await call(target.value==="files"?"saveFileMetadata":"saveMetadata",{id:data.id,value:JSON.stringify(patch)});await reload();}
}
function edits(){return [["Undo",()=>historyAction("undo")],["Redo",()=>historyAction("redo")],["Edit…",editMetadata],["Rename track title",async()=>{const t=one();await call("title",{id:t.id,value:await ask("Track title",{value:t.title})});await reload();}],
  ["Edit file tags",async()=>{const t=one();const tags={TITLE:"title",ARTIST:"artistDisplay",ALBUM:"album",ALBUMARTIST:"artist",GENRE:"genre"};
    const key=await ask("File tag",{choices:Object.keys(tags).map(k=>({id:k,name:k}))});
    const value=await ask("Edit "+key.toLowerCase(),{value:t[tags[key]]||""});await call("editFile",{id:t.id,value:key,extra:value});await reload();}],
  ["Edit playback range",async()=>{const t=one(),c=await call("readCue",{id:t.id});
    const from=await ask("Start time (blank for beginning)",{value:c.start==null?"":time(c.start/1000)});
    const to=await ask("Stop time (blank for end)",{value:c.end==null?"":time(c.end/1000)});
    const keep=(text,ms)=>ms!=null&&text===time(ms/1000)?text+"."+String(ms%1000).padStart(3,"0"):text;
    await call("cue",{id:t.id,value:keep(from,c.start),extra:keep(to,c.end)});}],
  ["Rename file",()=>review("rename")],["Organize files by artist",()=>review("organize")],["Delete tracks from server",()=>review("delete")],
  ...(listId?[["Remove from playlist",async()=>{for(const t of chosen())await call("removeEntry",{id:listId,value:t.playlistItem});await playlist({id:listId,name:section});}]]:[])];}
async function customEq(){
  const original=eqMode, dlg=$("review"),body=$("reviewBody");
  let values=original.startsWith("Custom|")?original.split("|").slice(1).map(Number):[0,0,0];
  const update=()=>{eqMode="Custom|"+values.join("|");if(ctx){values.forEach((v,i)=>filters[i].gain.setTargetAtTime(v,ctx.currentTime,.04));
    preamp.gain.setTargetAtTime(10**(-values.reduce((n,v)=>n+Math.max(v,0),0)/20),ctx.currentTime,.04);}};
  dlg.querySelector("h2").textContent="Custom equalizer";body.replaceChildren();
  ["Bass · 120 Hz","Midrange · 1 kHz","Treble · 6.5 kHz"].forEach((name,i)=>{
    const label=document.createElement("label"),caption=document.createElement("span"),slider=document.createElement("input");
    slider.type="range";slider.min=-6;slider.max=6;slider.step=.5;slider.value=values[i];
    const title=()=>caption.textContent=name+"    "+values[i]+" dB";title();slider.oninput=()=>{values[i]=Number(slider.value);title();update();};
    label.append(caption,slider);body.append(label);
  });
  dlg.showModal();update();
  const apply=await new Promise(resolve=>{const done=value=>{dlg.oncancel=null;dlg.close();resolve(value);};
    $("confirm").onclick=()=>done(true);$("cancel").onclick=()=>done(false);dlg.oncancel=e=>{e.preventDefault();done(false);};});
  if(apply)await call("setting",{id:"eq",value:eqMode});else eqMode=original;await setTone();
}
$("edit").onclick=run(async e=>{
  const anchor=e.currentTarget.getBoundingClientRect(), history=await call("history");
  const actions=["undo","redo"].map(key=>{
    const title=key==="undo"?"Undo":"Redo";
    return [history[key]?title+" "+history[key]:title,history[key]?()=>historyAction(key):null];
  });
  popup([...actions,...edits().slice(2)],anchor.left,anchor.bottom+2);
});
tool("eq",()=>[...["Auto","Flat","Warm","Clarity","Punch"].map(m=>[(m===eqMode?"✓ ":"")+m,async()=>{eqMode=m;await call("setting",{id:"eq",value:m});await setTone();}]),["Custom…",customEq]]);
let connecting=false;
async function addPlexLibrary(){
  if(connecting)return;
  connecting=true;$("addLibrary").disabled=true;
  try{
    $("status").textContent="Connecting to Plex… Complete sign-in in your browser if prompted.";
    const servers=await call("connect");
    if(!servers.length)throw Error("This Plex account has no available servers.");
    $("status").textContent="Choose your Plex server.";
    const id=await ask("Choose your Plex server",{choices:servers});
    $("status").textContent="Finding music libraries…";
    const libraries=await call("server",{id});
    if(!libraries.length)throw Error("This server has no music libraries available to your account.");
    const library=await ask("Choose your music library",{choices:libraries});
    $("status").textContent="Loading your music library…";
    await call("library",{id:library});await reload();
  }catch(error){
    if(error.message!=="Canceled")throw error;
    $("status").textContent="Library selection canceled.";
  }finally{
    connecting=false;$("addLibrary").disabled=false;
  }
}
$("addLibrary").onclick=run(addPlexLibrary);
tool("library",()=>[["Add Plex Library",addPlexLibrary],["Sync",async()=>{await call("sync");await reload();}],
  ["Share mapping",async()=>{const value=await ask("Server music root",{value:"/music"});const extra=await ask("Mounted music folder",{value:"/Volumes/"});await call("mapping",{value,extra});}],
  ["New playlist",newList],[(showBars?"✓ ":"")+"Now Playing bars",async()=>{showBars=!showBars;$("bars").hidden=!showBars;await call("setting",{id:"bars",value:showBars?"on":"off"});cancelAnimationFrame(frame);if(showBars&&!audio.paused)frame=requestAnimationFrame(bars);}],
  ["Demo",async()=>{await call("demo");await reload();}]]);
function installNativeMenus(){
  const bridge=window.external;
  if(!bridge?.receiveMessage||!bridge?.sendMessage)return;
  const commands=new Map();
  function item(label,action){const id=String(commands.size);commands.set(id,action);return {label,id};}
  function actions(entries){return entries.map(([raw,fn])=>item(raw.replace(/^✓ /,""),fn));}
  const groups=[
    {label:"Library",children:actions(toolEntries.get("library")())},
    {label:"Edit",children:actions(edits().filter(([label])=>label!=="Remove from playlist"))},
    {label:"View",children:[
      {label:"Themes",children:themes.map(t=>item(t.name,()=>chooseTheme(t.name)))},
      item("Use Ubuntu font",async()=>{const root=document.documentElement,enabled=!root.classList.contains("ubuntuFont");
        await call("setting",{id:"font",value:enabled?"ubuntu":"theme"});root.classList.toggle("ubuntuFont",enabled);}),
      item("Glass appearance",toggleGlass)
    ]},
    {label:"EQ",children:actions(toolEntries.get("eq")())}
  ];
  function checks(){
    const options=[...groups[0].children,...groups[2].children[0].children,...groups[2].children.slice(1),...groups[3].children];
    const active=new Set([currentTheme,eqMode.startsWith("Custom|")?"Custom…":eqMode]);
    if(document.documentElement.classList.contains("ubuntuFont"))active.add("Use Ubuntu font");
    if(glassWanted)active.add("Glass appearance");
    if(showBars)active.add("Now Playing bars");
    bridge.sendMessage("checks:"+JSON.stringify(options.filter(e=>active.has(e.label)).map(e=>e.id)));
  }
  bridge.receiveMessage(run(async msg=>{
    if(msg.startsWith("glass-active:")){document.documentElement.classList.toggle("glass",msg==="glass-active:on");return;}
    if(msg==="menus-ready"){
      syncGlass();
      for(const id of ["library","edit","view","eq"])$(id).hidden=true;
      checks();return;
    }
    if(!msg.startsWith("menu:")||$("review").open)return;
    menu.hidden=true;
    const action=commands.get(msg.slice(5));if(action)await action();
    checks();
  }));
  document.addEventListener("mixamp-theme",checks);
  bridge.sendMessage("menus:"+JSON.stringify(groups));
}
document.onkeydown=run(async e=>{
  if(e.defaultPrevented||$("review").open||!menu.hidden)return;
  const mod=e.metaKey||e.ctrlKey, key=e.key;
  const browsing=section==="Radio"&&!listId;
  const back=(key==="Escape" || e.altKey&&key==="ArrowLeft" || e.metaKey&&key==="[");
  if(back && (inQueue()&&stationName || browsing&&$("radioPane").querySelector("h2"))){
    e.preventDefault();await browseRadio();return;
  }
  const target=e.target;
  if(target.closest("input:not([type=range]),select,textarea,[contenteditable=true]"))return;
  if((key==="ArrowDown"||key==="ArrowUp")&&!mod && target.matches(".station, aside button")){
    const items=[...document.querySelectorAll(target.matches(".station")?"#radioPane .station":"aside button")].filter(b=>b.offsetHeight);
    e.preventDefault();items[Math.max(0,Math.min(items.length-1,items.indexOf(target)+(key==="ArrowDown"?1:-1)))]?.focus();
    return;
  }
  if(mod && key.toLowerCase()==="f"){
    const search=browsing?$("radioPane").querySelector("input"):$("search");
    if(search){e.preventDefault();search.focus();search.select();}
    return;
  }
  if(mod && ["1","2","3"].includes(key)){
    e.preventDefault();
    if(key==="2")await browseRadio();else{show(key==="1"?"Songs":"Up Next",key==="1"?catalog:queue);grid.focus();}
    return;
  }
  if(key===" "&&!mod&&!e.altKey&&!e.shiftKey){
    e.preventDefault();if(!e.repeat)await toggleAudio();return;
  }
  if(mod && (key==="ArrowLeft"||key==="ArrowRight")){
    e.preventDefault();if(!e.repeat)await advance(key==="ArrowRight"?1:-1);return;
  }
  if(mod && key.toLowerCase()==="z"){e.preventDefault();await historyAction(e.shiftKey?"redo":"undo");return;}
  if(target!==grid || !visible.length)return;
  if(key==="Enter"){e.preventDefault();if(!e.repeat)await startSelection();return;}
  const page=Math.max(1,Math.floor((grid.clientHeight-28)/26));
  const positions={ArrowDown:cursor+1,ArrowUp:cursor-1,PageDown:cursor+page,PageUp:cursor-page,Home:0,End:visible.length-1};
  if(!(key in positions))return;
  e.preventDefault();
  const i=Math.max(0,Math.min(positions[key],visible.length-1));
  select(i,{shiftKey:e.shiftKey});
  if(i*26<grid.scrollTop)grid.scrollTop=i*26;
  else if((i+1)*26+28>grid.scrollTop+grid.clientHeight)grid.scrollTop=(i+1)*26+28-grid.clientHeight;
});
run(async()=>{const config=await call("config");themes=config.themes;glassWanted=config.glass;
  for(const entry of themes)$("themePicker").add(new Option(entry.name,entry.name));
  document.documentElement.classList.toggle("ubuntuFont",config.ubuntuFont);applyTheme(themes.find(t=>t.name===config.theme));$("themePicker").disabled=false;$("view").disabled=false;eqMode=config.eq;showBars=config.bars;$("bars").hidden=!showBars;
  if(config.columns){const saved=config.columns.split("|").filter(k=>fields.some(f=>f[0]===k));if(saved.length)columns=new Set(saved);}
  installNativeMenus();catalog=await call("cached");show("Songs",catalog);if(await call("resume"))await reload();
})();
setInterval(async()=>{try{const p=await (await fetch("/progress")).text();$("progress").textContent=p==="Ready"?"":p;}catch{}},1200);
