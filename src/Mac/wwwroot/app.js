"use strict";
const $ = id => document.getElementById(id);
const audio = $("audio"), grid = $("grid"), menu = $("menu");
const fields = [
  ["title","Title",230],["artistDisplay","Track artist",155],["artist","Album artist",150],
  ["album","Album",160],["genre","Genre",100],["disc","Disc",48],["number","#",52],
  ["year","Year",58],["length","Time",65],["filePath","File path",300],["format","Format",78],["bitrate","kbps",62]
];
let columns = new Set(["title","artistDisplay","album","length"]), source=[], visible=[], lists=[], queue=[];
let selection=new Set(), anchor=-1, cursor=-1, section="Songs", listId="", sort="title", direction=1, playing=null, queueAt=-1;
let connected=false, songCount=0, songRequest=0, scrollTimer, reconnecting, opening;
const songPageSize=128, songPages=new Map(), pendingSongs=new Map();
let missingSongs=new Set(), connectionMessage="", retryOffset;
const songErrors=new Map();
let songSort="title", songDirection=1, sortSaved=Promise.resolve();
let glassWanted=false, visualizerBackground="Off", visualizerStyles=[];
let themes=[], currentTheme="", spectrumInk="#16696B";
let eqMode="Auto", showBars=true, ctx, filters, preamp, analyser, fft, frame=0, lastFrame=0, sequence=0, eqSeq=0, clipEnd=null;
const collator=new Intl.Collator(undefined,{numeric:true,sensitivity:"base"});
const time=s=>Math.floor(Math.max(0,s||0)/60)+":"+String(Math.floor(Math.max(0,s||0))%60).padStart(2,"0");
async function call(action, params={}, signal) {
  const res=await fetch("/command",{method:"POST",headers:{"Content-Type":"application/json","X-Mixamp":"app"},signal,body:JSON.stringify({action,...params})});
  const data=await res.json();
  if(!res.ok) throw Error(data.error||"Request failed");
  return data;
}
function run(fn){return async (...args)=>{try{await fn(...args);}catch(e){if(e.message!=="Canceled" && e.name!=="AbortError")$("status").textContent=e.message;}};}
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
const playbackGroups=[...document.querySelectorAll("header .transport,header .deck,header .volume")];
function layoutPlaybackGlass(){
  if(!document.documentElement.classList.contains("liquid"))return;
  const boxes=playbackGroups.map(group=>{
    const r=group.getBoundingClientRect();
    return [r.left,r.top,r.width,r.height,innerHeight-r.bottom];
  });
  window.external.sendMessage("playback-glass:"+JSON.stringify(boxes));
}
const playbackLayout=new ResizeObserver(layoutPlaybackGlass);
for(const group of playbackGroups)playbackLayout.observe(group);
window.addEventListener("resize",layoutPlaybackGlass);
function syncGlass(){
  const on=glassWanted&&!reducedGlass.matches;
  if(window.external?.sendMessage)window.external.sendMessage((on?"glass:":glassWanted?"muted:":"theme:")+currentTheme);
  else document.documentElement.classList.toggle("glass",on);
}
reducedGlass.addEventListener("change",()=>{if(currentTheme)syncGlass();});
async function toggleGlass(){
  const on=!glassWanted;
  await call("setting",{id:"glass",value:on?"on":"off"});
  glassWanted=on;syncGlass();
}
async function chooseTheme(name){
  await call("setting",{id:"theme",value:name});
  applyTheme(themes.find(entry=>entry.name===name));
}
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
  const backgrounds=document.createElement("details"), caption=document.createElement("summary");
  caption.textContent="Background visualizer";backgrounds.append(caption);
  for(const style of visualizerStyles){
    const choice=document.createElement("button");
    choice.textContent=style;choice.setAttribute("role","menuitemradio");
    choice.setAttribute("aria-checked",String(style===visualizerBackground));
    choice.onclick=run(async()=>{menu.hidden=true;$("view").focus();await chooseBackground(style);});
    backgrounds.append(choice);
  }
  menu.replaceChildren(folder,backgrounds,font,glass);menu.hidden=false;
  const position=()=>{menu.style.left=Math.max(2,Math.min(anchor.left,innerWidth-menu.offsetWidth-4))+"px";
    menu.style.top=Math.max(2,Math.min(anchor.bottom+2,innerHeight-menu.offsetHeight-4))+"px";};
  backgrounds.ontoggle=folder.ontoggle=position;position();heading.focus();
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
  for(const [key,label] of shown){const b=document.createElement("button");b.textContent=label+(sort===key?(direction===1?" ↑":" ↓"):"");b.onclick=run(async()=>{
      direction=sort===key?-direction:1;sort=key;
      if(section==="Songs" && !listId){
        songSort=sort;songDirection=direction;
        const value=songSort+"|"+songDirection;
        sortSaved=sortSaved.catch(()=>{}).then(()=>call("setting",{id:"songSort",value}));
        await Promise.all([sortSaved,firstSongs()]);
      }else if(listId)await firstSongs();else refreshView();
    });$("head").append(b);}
}
$("head").oncontextmenu=e=>{e.preventDefault();popup(fields.map(([key,label])=>[(columns.has(key)?"✓ ":"")+label,async()=>{
  if(columns.has(key)&&columns.size>1)columns.delete(key);else columns.add(key);
  await call("setting",{id:"columns",value:[...columns].join("|")});headers();render();
}]),e.clientX,e.clientY);};
function searchWords(value){return value.trim().toLowerCase().split(/\s+/u).filter(Boolean);}
function sortedRows(){
  const words=searchWords($("search").value), key=sort==="length"?"duration":sort;
  const rows=words.length?source.filter(t=>words.every(w=>[t.title,t.artistDisplay,t.artist,t.album,t.genre,t.filePath].some(s=>s?.toLowerCase().includes(w)))):source.slice();
  if(sort)rows.sort((a,b)=>direction*(typeof a[key]==="number"?a[key]-b[key]:collator.compare(a[key]||"",b[key]||"")));
  return rows;
}
function refreshView(){
  visible=sortedRows();resetRows();
}
function ranged(){return inSongs() || !!listId;}
function rowCount(){return ranged()?songCount:visible.length;}
function resetRows(){
  selection.clear();anchor=cursor=-1;grid.scrollTop=0;$("spacer").style.height=rowCount()*26+"px";headers();render();
}
let playlistView="",rangeQuery="";
let radioView=0, radioQueue="", stationName="", sonicQueue=false;
function inQueue(){return section==="Up Next" && !listId;}
function show(name,rows,id=""){
  if(playlistView)void call("closePlaylist",{id:playlistView}).catch(()=>{});
  playlistView="";clearTimeout(searchDelay);
  ++radioView;songCount=0;resetSongRequests();
  const browsing=name==="Radio" && !id;
  $("radioPane").hidden=!browsing;grid.hidden=browsing;$("search").hidden=browsing;
  $("radio").classList.toggle("active",browsing);
  section=name;listId=id;source=rows;const songs=name==="Songs" && !id;sort=songs?songSort:"";direction=songs?songDirection:1;$("queueBack").hidden=!(inQueue()&&stationName);$("heading").textContent=inQueue()&&stationName?stationName+" · Up Next":name;$("search").value="";
  $("songs").classList.toggle("active",name==="Songs");$("queue").classList.toggle("active",name==="Up Next");
  for(const b of $("lists").children)b.classList.toggle("active",b.dataset.id===id);refreshView();
}
function inSongs(){return section==="Songs" && !listId;}
function resetSongRequests(){
  ++songRequest;
  for(const entry of pendingSongs.values())entry.controller.abort();
  clearTimeout(scrollTimer);scrollTimer=undefined;
  pendingSongs.clear();songPages.clear();songErrors.clear();missingSongs.clear();
}
async function firstSongs(){
  rangeQuery=$("search").value;
  resetSongRequests();songCount=0;source=visible=[];resetRows();
  if(connected && (inSongs() || playlistView))await displaySongPage(0);
}
async function songPage(offset,viewportOnly=false,revalidate=false){
  if(!revalidate && songPages.has(offset))return songPages.get(offset);
  const existing=pendingSongs.get(offset);
  if(existing){
    if(!viewportOnly)existing.viewportOnly=false;
    return existing.pending;
  }
  songErrors.delete(offset);
  const request=songRequest,controller=new AbortController();
  const params=listId ? {offset,id:playlistView,extra:sort,descending:direction<0,value:rangeQuery} :
    {offset,id:songSort,extra:String(songDirection),value:rangeQuery};
  const pending=call(listId?"playlist":"songs",params,controller.signal).then(page=>{
    if(request!==songRequest || controller.signal.aborted)return [];
    const end=offset+page.tracks.length;
    songCount=page.total??(page.more?Math.max(songCount,end+1):end);
    for(const t of page.tracks)t.filePath=t.parts?.[0]?.file??"";
    songPages.set(offset,page.tracks);
    // Keep a small window around the viewport. Explicit selections can be fetched again.
    if(songPages.size>5){
      const top=grid.scrollTop/26;
      const oldest=[...songPages.keys()].filter(k=>k!==offset).sort((a,b)=>Math.abs(b-top)-Math.abs(a-top))[0];
      songPages.delete(oldest);
    }
    $("spacer").style.height=rowCount()*26+"px";
    if(!opening && !playing)$("status").textContent=page.total==null?"Connected to Plex":page.total.toLocaleString()+" tracks";
    render();return page.tracks;
  }).catch(error=>{
    if(controller.signal.aborted || request!==songRequest)return [];
    songErrors.set(offset,error.message);
    throw error;
  }).finally(()=>{
    if(pendingSongs.get(offset)?.controller===controller){
      pendingSongs.delete(offset);
      updateBrowseActivity();
    }
  });
  pendingSongs.set(offset,{pending,controller,viewportOnly});
  updateBrowseActivity();
  return pending;
}
async function displaySongPage(offset,viewportOnly=false,revalidate=false){
  try{await songPage(offset,viewportOnly,revalidate);}
  catch{ /* The range error and retry control are displayed in the library footer. */ }
}
function updateBrowseActivity(){
  let message="";
  retryOffset=undefined;
  if(ranged()){
    const relevant=offset=>missingSongs.has(offset) || offset===0 && grid.scrollTop<songPageSize*26;
    retryOffset=[...songErrors.keys()].find(relevant);
    if(connectionMessage && inSongs())message=connectionMessage;
    else if(retryOffset!==undefined)message=(listId?"Couldn’t load playlist. ":"Couldn’t load tracks. ")+songErrors.get(retryOffset);
    else if(connected && (missingSongs.size || pendingSongs.has(0))){
      if(!missingSongs.size)message=listId?($("search").value?"Searching playlist…":"Loading playlist…"):songCount?"Refreshing library…":"Loading tracks…";
      else{
        const first=Math.min(...missingSongs),last=Math.min(songCount,Math.max(...missingSongs)+songPageSize);
        message="Loading tracks "+(first+1).toLocaleString()+"–"+last.toLocaleString()+"…";
      }
    }
  }
  if($("browseMessage").textContent!==message)$("browseMessage").textContent=message;
  $("retrySongs").hidden=retryOffset===undefined || (!!connectionMessage && inSongs());
}
$("retrySongs").onclick=()=>{if(retryOffset!==undefined)void displaySongPage(retryOffset,false,true);};
function openSongs(){show("Songs",[]);return firstSongs();}
function trackAt(index){return ranged()?songPages.get(index-index%songPageSize)?.[index%songPageSize]:visible[index];}
function render(){
  const start=Math.max(0,Math.floor(grid.scrollTop/26)-3), end=Math.min(rowCount(),start+Math.ceil(grid.clientHeight/26)+8);
  const frag=document.createDocumentFragment();const shown=fields.filter(f=>columns.has(f[0]));
  const missing=new Set();
  for(let i=start;i<end;i++){
    const t=trackAt(i),row=document.createElement("div");row.className="row"+(selection.has(i)?" selected":"");if(t)row.dataset.index=i;
    else{row.classList.add("pending");missing.add(i-i%songPageSize);frag.append(row);continue;}
    for(const [key] of shown){const cell=document.createElement("span");cell.textContent=key==="filePath"?(t.parts?.[0]?.file??""):(t[key]??"");row.append(cell);}
    frag.append(row);
  }
  for(const [offset,entry] of pendingSongs){
    if(entry.viewportOnly && (offset>=end || offset+songPageSize<=start)){
      pendingSongs.delete(offset);
      entry.controller.abort();
    }
  }
  missingSongs=missing;
  for(const offset of songErrors.keys()){
    if(!missing.has(offset) && offset!==0)songErrors.delete(offset);
  }
  // Service the current viewport even while wheel events keep arriving.
  if(connected && scrollTimer===undefined && [...missing].some(offset=>!pendingSongs.has(offset) && !songErrors.has(offset))){
    scrollTimer=setTimeout(()=>{
      scrollTimer=undefined;
      for(const offset of missingSongs){
        if(!pendingSongs.has(offset) && !songErrors.has(offset))void displaySongPage(offset,true);
      }
    },45);
  }
  updateBrowseActivity();
  $("rows").style.transform="translateY("+start*26+"px)";$("rows").replaceChildren(frag);
}
let scrollFrame;
grid.onscroll=()=>{
  if(scrollFrame===undefined)scrollFrame=requestAnimationFrame(()=>{scrollFrame=undefined;render();});
};
new ResizeObserver(render).observe(grid);let searchDelay;
$("search").oninput=()=>{
  clearTimeout(searchDelay);
  searchDelay=setTimeout(run(()=>ranged()?firstSongs():refreshView()),180);
};
function indexAt(e){const row=e.target.closest(".row");return row?.hasAttribute("data-index")?Number(row.dataset.index):-1;}
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
async function chosen(){
  const request=songRequest, tracks=[];
  for(const i of [...selection].sort((a,b)=>a-b)){
    const t=trackAt(i)??(await songPage(i-i%songPageSize))[i%songPageSize];
    if(t && !t.id)throw Error("This playlist entry is unavailable.");
    if(!t || request!==songRequest)throw Error("The library view changed. Select the tracks again.");
    tracks.push(t);
  }
  if(!tracks.length)throw Error("Select tracks first.");
  return tracks;
}
async function one(){const t=await chosen();if(t.length!==1)throw Error("Select one track.");return t[0];}
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
  if(castDevice)await castControl("stop");
  const seq=++sequence;
  opening?.abort();
  const controller=new AbortController(),signal=controller.signal;
  opening=controller;
  audio.pause();audio.removeAttribute("src");audio.load();playing=null;clipEnd=null;
  $("status").textContent="Opening "+track.title+"…";
  const timer=setTimeout(()=>controller.abort("timeout"),25000);
  const aborted=new Promise((_,reject)=>signal.addEventListener("abort",()=>reject(new Error("Canceled")),{once:true}));
  async function open(){
    initAudio();
    const resume=ctx?.resume();
    if(reconnecting && !connected)await reconnecting;
    signal.throwIfAborted();
    if(!connected)throw Error("Reconnect to Plex to play this track.");
    const data=await call("prepare",{id:track.id},signal);
    signal.throwIfAborted();
    playing=data.track;clipEnd=data.cue.end==null?null:data.cue.end/1000;
    audio.onloadedmetadata=()=>{if(seq===sequence && data.cue.start!=null)audio.currentTime=data.cue.start/1000;};
    audio.src=data.url;audio.volume=Number($("volume").value);
    for(const [id,value] of [["now",playing.title],["by",playing.artistDisplay],["album",playing.album]]){
      $(id).textContent=$(id).title=value||"";
    }
    showCover(data.artwork);
    if(navigator.mediaSession && window.MediaMetadata)
      navigator.mediaSession.metadata=new MediaMetadata({title:playing.title,artist:playing.artistDisplay,album:playing.album});
    await Promise.all([audio.play(),resume]);
    if(seq===sequence && !signal.aborted){
      $("status").textContent="Direct play";
      run(()=>setTone())();
    }
  }
  try{await Promise.race([open(),aborted]);}
  catch(e){
    if(seq!==sequence)return;
    controller.abort();
    audio.pause();audio.removeAttribute("src");audio.load();playing=null;clipEnd=null;updateTransport(false);
    throw signal.reason==="timeout"?Error("Plex took too long to start this track. Press Play to retry."):e;
  }finally{clearTimeout(timer);if(opening===controller)opening=null;}
}
async function startSelection(){const tracks=await chosen();if(inQueue()){queueAt=queue.indexOf(tracks[0]);}
  else{radioQueue=stationName="";queue=tracks.slice();queueAt=0;}await start(tracks[0]);}
async function advance(step){if(castDevice)return castControl(step>0?"next":"previous");const n=queueAt+step;
  if(n===queue.length && radioQueue && !sonicQueue && queue.length){
    const id=radioQueue, last=queue.at(-1).queueItem;
    const more=await call("continueRadio",{id,value:last});
    if(id!==radioQueue || n!==queueAt+step)return;
    const anchor=more.findIndex(t=>t.queueItem===last), seen=new Set(queue.map(t=>t.queueItem));
    if(anchor>=0) queue.push(...more.slice(anchor+1).filter(t=>t.queueItem&&!seen.has(t.queueItem)));
    refreshQueue();
  }if(n<0||n>=queue.length){if(step>0)audio.pause();return;}queueAt=n;await start(queue[n]);}
function refreshQueue(){
  if(!inQueue())return;
  if(sort||$("search").value){refreshView();return;}
  visible=queue.slice();$("spacer").style.height=rowCount()*26+"px";render();
}
async function addQueue(){const t=await chosen();queue.push(...t);$("status").textContent="Added "+t.length+" to Up Next";refreshQueue();}
async function resumeAudio(){
  if(castDevice)return castControl("play");
  if(playing && audio.error)return start(playing);
  if(!playing){
    if(selection.size)return startSelection();
    if(queue.length){queueAt=Math.max(0,queueAt);return start(queue[queueAt]);}
    return;
  }
  await ctx?.resume();await audio.play();
}
function toggleAudio(){if(castDevice)return castControl("toggle");if(audio.paused)return resumeAudio();audio.pause();}
$("prev").onclick=run(()=>advance(-1));$("next").onclick=run(()=>advance(1));$("play").onclick=run(toggleAudio);
if(navigator.mediaSession){
  const handlers={play:resumeAudio,pause:()=>castDevice?castControl("pause"):audio.pause(),previoustrack:()=>advance(-1),nexttrack:()=>advance(1)};
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
audio.ontimeupdate=()=>{if(castDevice)return;$("clock").textContent=time(audio.currentTime);$("seek").max=isFinite(audio.duration)?audio.duration:0;$("seek").value=audio.currentTime;
  $("seek").style.setProperty("--fill",(audio.duration>0 ? 100*audio.currentTime/audio.duration : 0)+"%");
  if(clipEnd!==null&&audio.currentTime>=clipEnd){clipEnd=null;audio.pause();run(()=>advance(1))();}};
audio.onended=run(()=>{if(castDevice)return;updateTransport(false);return advance(1);});
audio.onwaiting=()=>{if(playing && !opening && !audio.paused)$("status").textContent="Buffering audio…";};
audio.onerror=()=>{
  if(castDevice || !audio.error || !audio.getAttribute("src"))return;
  updateTransport(false);
  $("status").textContent=audio.error.code===2?"Audio stream interrupted. Press Play to retry.":
    audio.error.code===3?"Couldn’t decode this track.":"Couldn’t open this audio stream.";
};
async function chooseBackground(style){
  await call("setting",{id:"visualizerBackground",value:style});
  visualizerBackground=style;$("visualizerBackground").hidden=style==="Off";
  restartVisualizer();
}
function restartVisualizer(){
  cancelAnimationFrame(frame);
  if(!audio.paused&&(showBars||visualizerBackground!=="Off"))frame=requestAnimationFrame(bars);
}
function drawBands(canvas,style){
  const scale=window.devicePixelRatio||1;
  const w=Math.max(1,Math.round(canvas.clientWidth*scale)),h=Math.max(1,Math.round(canvas.clientHeight*scale));
  if(canvas.width!==w||canvas.height!==h){canvas.width=w;canvas.height=h;}
  const c=canvas.getContext("2d");c.clearRect(0,0,w,h);c.fillStyle=spectrumInk;
  const mirror=style==="Mirrored peaks",bands=24,levels=8,slot=w/bands,step=h/(mirror?2*levels:levels);
  for(let i=0;i<bands;i++){
    const bin=Math.max(1,Math.round((fft.length-1)**(i/(bands-1))));
    const lit=Math.round(fft[bin]*levels/255);
    const left=Math.round(i*slot), width=Math.max(1,Math.round((i+.78)*slot)-left);
    for(let j=0;j<lit;j++){
      const top=Math.round((mirror?h/2:h)-(j+1)*step),height=Math.max(1,Math.round(step*.8));
      c.globalAlpha=.54+.42*(j+1)/levels;
      if(style==="Dot matrix"){
        c.beginPath();c.arc(left+slot/2,top+step/2,Math.min(slot,step)*.35,0,Math.PI*2);c.fill();
      }else{
        c.fillRect(left,top,width,height);
        if(mirror)c.fillRect(left,h-top-height,width,height);
      }
    }
  }
}
function bars(ts){
  if(audio.paused||(!showBars&&visualizerBackground==="Off")||!analyser)return;
  if(ts-lastFrame>=50){
    lastFrame=ts;analyser.getByteFrequencyData(fft);
    if(showBars)drawBands($("bars"),"Pixel spectrum");
    if(visualizerBackground!=="Off")drawBands($("visualizerBackground"),visualizerBackground);
  }
  frame=requestAnimationFrame(bars);
}
audio.onplaying=()=>{if($("status").textContent==="Buffering audio…")$("status").textContent="Direct play";updateTransport(true);cancelAnimationFrame(frame);frame=requestAnimationFrame(bars);};
audio.onpause=()=>{if(!castDevice)updateTransport(false);cancelAnimationFrame(frame);for(const c of [$("bars"),$("visualizerBackground")])c.getContext("2d").clearRect(0,0,c.width,c.height);};
async function playlist(p){
  show(p.name,[],p.id);
  const version=radioView;
  const view=await call("openPlaylist",{id:p.id});
  if(version!==radioView){await call("closePlaylist",{id:view});return;}
  playlistView=view;
  await firstSongs();
}
async function loadLists(){lists=await call("playlists");$("lists").replaceChildren();for(const p of lists){
  const b=document.createElement("button");b.textContent=p.name;b.dataset.id=p.id;b.onclick=run(()=>playlist(p));b.oncontextmenu=e=>{e.preventDefault();e.stopPropagation();popup([["New playlist",newList],["Remove playlist",async()=>{await ask("Remove "+p.name+"?");await call("removeList",{id:p.id});await reload();}]],e.clientX,e.clientY);};$("lists").append(b);}}
async function newList(){const value=await ask("New playlist",{value:""});await call("newList",{value});$("folders").open=true;await loadLists();}
async function addList(){const ids=(await chosen()).map(t=>t.id);await loadLists();
  let id=await ask("Add to playlist",{choices:[...lists,{id:"new",name:"New playlist…"}]});
  if(id==="new"){const value=await ask("New playlist",{value:""});id=await call("newList",{value});await loadLists();}
  await call("addList",{id,ids});$("status").textContent="Playlist updated";}
$("folders").oncontextmenu=e=>{e.preventDefault();popup([["New playlist",newList]],e.clientX,e.clientY);};
$("songs").onclick=run(openSongs);$("queue").onclick=()=>show("Up Next",queue);
let castDevice="";
function setCastIcon(){
  const button=$("cast");
  button.classList.toggle("casting",!!castDevice);
  button.title=castDevice?"Casting to "+castDevice:"Cast to a device";
  button.setAttribute("aria-label",button.title);
}
let castTimer,castGeneration=0;
function clearCastDevice(){
  ++castGeneration;clearTimeout(castTimer);
  if(castDevice){
    $("now").textContent=playing?.title||"Ready to play";$("by").textContent=playing?.artistDisplay||"";$("album").textContent=playing?.album||"";
    $("clock").textContent=time(audio.currentTime);updateTransport(!audio.paused);
  }
  castDevice="";setCastIcon();$("seek").disabled=$("volume").disabled=false;
}
function renderCast(state){
  if(!state.device){clearCastDevice();$("status").textContent="Casting ended";return;}
  updateTransport(state.playing);$("clock").textContent=time(state.seconds);
  if(state.song){$("now").textContent=state.song.title;$("by").textContent=state.song.artist;$("album").textContent=state.song.album;}
}
function watchCast(){
  const generation=++castGeneration;clearTimeout(castTimer);
  async function refresh(){
    try{const state=await call("castState");if(castGeneration===generation)renderCast(state);}
    finally{if(castGeneration===generation)castTimer=setTimeout(()=>run(refresh)(),2000);}
  }
  run(refresh)();
}
async function castControl(action){
  const state=await call("castControl",{id:action});
  if(action!=="stop")renderCast(state);
  if(action==="stop"){clearCastDevice();$("status").textContent="Casting stopped";}
}
async function castQueue(){
  if(!connected)throw Error("Connect to Plex to cast music.");
  const tracks=queue.length?queue.slice(Math.max(0,queueAt)):await chosen();
  if(!tracks.length)throw Error("Choose a track to cast.");
  $("status").textContent="Looking for Google Cast devices…";
  const {devices:choices,instance}=await call("castDevices");
  if(!choices.length)throw Error("No Google Cast devices found. Mixamp can’t cast to Echo speakers over Wi-Fi.");
  const id=await ask("Play on Google Cast",{choices});
  const seconds=playing?.id===tracks[0].id?audio.currentTime:0;
  const songs=tracks.map(t=>({id:t.id,title:t.title,artist:t.artistDisplay,album:t.album,format:t.parts?.[0]?.container||t.format}));
  $("status").textContent="Connecting to speaker…";
  castDevice=await call("castStart",{id,songs,seconds,extra:instance});
  audio.pause();$("seek").disabled=$("volume").disabled=true;watchCast();setCastIcon();$("status").textContent="Playing on "+castDevice;
}
tool("cast",()=>[
  ["Google Cast…",castQueue],
  ["AirPlay via Mac Sound output…",()=>call("soundOutput")],
  ...(castDevice?[[castDevice,null],["Resume on speaker",()=>castControl("play")],["Pause on speaker",()=>castControl("pause")],
    ["Previous on speaker",()=>castControl("previous")],["Next on speaker",()=>castControl("next")],["Stop casting",()=>castControl("stop")]]:[])
]);
function radioButton(label, action) {
  const button=document.createElement("button");
  button.textContent=label;
  button.onclick=run(action);
  return button;
}
async function browseRadio() {
  show("Radio",[]);
  const view=radioView, pane=$("radioPane");
  pane.classList.remove("builder");
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
  const track=await one(),view=++radioView;
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
  pane.classList.add("builder");
  pane.replaceChildren(back,title);
  let choices;
  try { choices=await call("radioChoices",{id}); }
  catch(error) { if(view===radioView)title.textContent=error.message; return; }
  if(view!==radioView)return;
  title.textContent=station.name;
  if(!choices.length){pane.append("No choices in this library.");return;}
  const mix=station.kind==="artist"||station.kind==="album", picked=new Map();
  const search=document.createElement("input"), list=document.createElement("div"), seeds=document.createElement("div");
  const actions=document.createElement("div"), count=document.createElement("small");
  search.type="search";search.placeholder="Search "+(mix?station.kind+"s":"stations");
  search.setAttribute("aria-label",search.placeholder);
  list.className="radioChoices";
  list.setAttribute("role","radiogroup");list.setAttribute("aria-label",station.name+" choices");
  let selected;
  const play=radioButton(mix?"Play mix":"Play station",()=>startStation(id,mix?[...picked.keys()]:[selected.id],view,station.name));
  const add=radioButton("Add selection",()=>{
    if(!selected || picked.size>=100)return;
    picked.set(selected.id,selected.name);renderChoices();
  });
  function update(){add.disabled=!selected||picked.size>=100;play.disabled=mix?!picked.size:!selected;}
  function renderChoices(){
    const query=search.value.trim(),words=searchWords(query);
    const matches=choices.filter(c=>!picked.has(c.id)&&words.every(w=>c.name.toLowerCase().includes(w)));
    selected=null;
    list.replaceChildren(...matches.slice(0,250).map(choice=>{
      const row=document.createElement("label"), input=document.createElement("input");
      input.type="radio";input.name="radioChoice";input.value=choice.id;
      input.onchange=()=>{selected=choice;update();};
      row.append(input,document.createTextNode(choice.name));return row;
    }));
    list.scrollTop=0;list.hidden=!matches.length;
    count.textContent=matches.length>250?"Showing 250 of "+matches.length.toLocaleString()+" · Search to narrow":
      matches.length?"":query?"No matches":"All choices selected";
    seeds.replaceChildren(...[...picked].map(([key,name])=>radioButton(name+"  ×",()=>{
      picked.delete(key);renderChoices();
    })));
    update();
  }
  search.oninput=renderChoices;
  seeds.className="seeds";actions.className="radioActions";
  if(mix)actions.append(add);
  actions.append(play);pane.append(search,seeds,count,list,actions);
  renderChoices();search.focus();
}
$("radio").onclick=$("queueBack").onclick=run(browseRadio);
$("folders").ontoggle=run(async()=>{if(connected && $("folders").open)await loadLists();});
async function reload(){connected=true;connectionMessage="";await openSongs();if($("folders").open)await loadLists();}
async function apply(plan){await ask(plan.title,{lines:plan.lines});await call("apply",{id:plan.id});await reload();}
async function review(action){
  const items=await chosen(),ids=items.map(t=>t.id);let value="",extra="";
  if(action==="rename"){const t=await one();value=await ask("Rename file",{value:t.filePath.split("/").pop()});}
  if(action==="organize"){value=await ask("Artist folders",{choices:[{id:"album",name:"Album artist"},{id:"track",name:"Track artist"},{id:"custom",name:"One artist folder"}]});
    if(value==="custom")extra=await ask("Artist folder name",{value:items[0].artistDisplay});}
  await apply(await call("review",{id:action,ids,value,extra}));
}
async function historyAction(action){await apply(await call(action));}
async function editMetadata(){
  const tracks=[...new Map((await chosen()).map(t=>[t.id,t])).values()];
  if(tracks.length>1)await ask(`Edit ${tracks.length} tracks?`,{lines:[`Any field you change will apply to all ${tracks.length} tracks.`]});
  const data=await call("metadata",{ids:tracks.map(t=>t.id)});
  const dlg=$("review"),body=$("reviewBody"),rows=[];
  dlg.querySelector("h2").textContent=tracks.length===1?"Edit · "+tracks[0].title:"Edit "+tracks.length+" tracks";
  body.replaceChildren();
  const help=document.createElement("p");help.textContent="Enter one tag per line.";body.append(help);
  const target=document.createElement("select");target.setAttribute("aria-label","Save target");
  target.add(new Option("Plex library","plex"));target.add(new Option("Mapped audio file tags","files"));
  target.options[1].disabled=!data.fileTags;body.append(target);
  const fields=document.createElement("div");body.append(fields);
  const fileFields=[["TITLE","Title","title"],["ARTIST","Track artist","artistDisplay"],["ALBUM","Album","album"],
    ["ALBUMARTIST","Album artist","artist"],["GENRE","Genre","genre"],["DATE","Year","year"],
    ["DISCNUMBER","Disc number","disc"],["TRACKNUMBER","Track number","number"]].map(([key,label,prop])=>{
      const first=tracks[0][prop];return {key,label,value:tracks.every(t=>t[prop]===first)?String(first??""):null};
    });
  const changed=r=>r.input.value!==r.initial;
  function updateSave(){$("confirm").disabled=!rows.some(changed);}
  function draw(){
    fields.replaceChildren();rows.length=0;
    for(const field of target.value==="files"?fileFields:data.fields){
      const row=document.createElement("div"),label=document.createElement("label");
      label.textContent=field.label;
      const input=document.createElement(field.tags?"textarea":"input");
      input.value=field.value??"";input.placeholder=field.value===null?"Mixed values":"";
      input.id="metadata-"+field.key;label.htmlFor=input.id;input.oninput=updateSave;
      row.className="metadataField";row.append(label,input);fields.append(row);rows.push({field,input,initial:input.value});
    }
    updateSave();
  }
  target.onchange=draw;draw();
  dlg.showModal();
  const ok=await new Promise(resolve=>{
    const close=value=>{dlg.oncancel=null;dlg.close();resolve(value);};
    $("confirm").textContent="Save changes";$("confirm").onclick=()=>close(true);$("cancel").onclick=()=>close(false);
    dlg.oncancel=e=>{e.preventDefault();close(false);};
  });
  $("confirm").textContent="Continue";$("confirm").disabled=false;
  const patch=Object.fromEntries(rows.filter(changed).map(r=>[r.field.key,r.input.value]));
  if(ok&&Object.keys(patch).length){await call(target.value==="files"?"saveFileMetadata":"saveMetadata",{id:data.id,value:JSON.stringify(patch)});await reload();}
}
function edits(){return [["Undo",()=>historyAction("undo")],["Redo",()=>historyAction("redo")],["Edit…",editMetadata],["Rename track title",async()=>{const t=await one();await call("title",{id:t.id,value:await ask("Track title",{value:t.title})});await reload();}],
  ["Edit file tags",async()=>{const t=await one();const tags={TITLE:"title",ARTIST:"artistDisplay",ALBUM:"album",ALBUMARTIST:"artist",GENRE:"genre"};
    const key=await ask("File tag",{choices:Object.keys(tags).map(k=>({id:k,name:k}))});
    const value=await ask("Edit "+key.toLowerCase(),{value:t[tags[key]]||""});await call("editFile",{id:t.id,value:key,extra:value});await reload();}],
  ["Edit playback range",async()=>{const t=await one(),c=await call("readCue",{id:t.id});
    const from=await ask("Start time (blank for beginning)",{value:c.start==null?"":time(c.start/1000)});
    const to=await ask("Stop time (blank for end)",{value:c.end==null?"":time(c.end/1000)});
    const keep=(text,ms)=>ms!=null&&text===time(ms/1000)?text+"."+String(ms%1000).padStart(3,"0"):text;
    await call("cue",{id:t.id,value:keep(from,c.start),extra:keep(to,c.end)});}],
  ["Rename file",()=>review("rename")],["Organize files by artist",()=>review("organize")],["Delete tracks from server",()=>review("delete")],
  ...(listId?[["Remove from playlist",async()=>{for(const t of await chosen())await call("removeEntry",{id:listId,value:t.playlistItem});await playlist({id:listId,name:section});}]]:[])];}
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
async function addPlexLibrary(fresh=false){
  if(connecting)return;
  connecting=true;$("addLibrary").disabled=true;
  try{
    $("status").textContent="Connecting to Plex… Complete sign-in in your browser if prompted.";
    const servers=await call("connect",{value:fresh===true?"fresh":""});
    if(!servers.length)throw Error("This Plex account has no available servers.");
    $("status").textContent="Choose your Plex server.";
    const id=await ask("Choose your Plex server",{choices:servers});
    $("status").textContent="Finding music libraries…";
    const libraries=await call("server",{id});
    if(!libraries.length)throw Error("This server has no music libraries available to your account.");
    const library=await ask("Choose your music library",{choices:libraries});
    $("status").textContent="Loading your music library…";
    const result=await call("library",{id:library});clearCastDevice();$("reconnect").hidden=true;await reload();
    if(result.warning)$("status").textContent=result.warning;
  }catch(error){
    if(error.message!=="Canceled")throw error;
    $("status").textContent="Library selection canceled.";
  }finally{
    connecting=false;$("addLibrary").disabled=false;
  }
}
$("reconnect").onclick=run(async()=>{
  if(connecting)return;
  connectionMessage="Reconnecting to Plex…";updateBrowseActivity();
  connecting=true;$("reconnect").disabled=$("addLibrary").disabled=true;
  try{
    $("status").textContent="Reconnecting to your Plex library…";
    const result=await call("reconnect");clearCastDevice();
    $("reconnect").hidden=true;await reload();
    if(result.warning)$("status").textContent=result.warning;
  }catch(error){connectionMessage="Couldn’t reconnect to Plex.";throw error;}
  finally{updateBrowseActivity();connecting=false;$("reconnect").disabled=$("addLibrary").disabled=false;}
});
$("addLibrary").onclick=run(addPlexLibrary);
tool("library",()=>[["Add Plex Library",addPlexLibrary],["Sign in with Plex…",()=>addPlexLibrary(true)],["Sync",async()=>{if(!connected)throw Error("Connect to Plex first.");await reload();}],
  ["Share mapping",async()=>{const value=await ask("Server music root",{value:"/music"});const extra=await ask("Mounted music folder",{value:"/Volumes/"});await call("mapping",{value,extra});}],
  ["New playlist",newList],[(showBars?"✓ ":"")+"Now Playing bars",async()=>{showBars=!showBars;$("bars").hidden=!showBars;await call("setting",{id:"bars",value:showBars?"on":"off"});restartVisualizer();}],
  ["Demo",async()=>{await call("demo");clearCastDevice();$("reconnect").hidden=true;await reload();}]]);
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
      {label:"Background visualizer",children:visualizerStyles.map(style=>item(style,()=>chooseBackground(style)))},
      item("Glass appearance",toggleGlass)
    ]},
    {label:"EQ",children:actions(toolEntries.get("eq")())}
  ];
  function checks(){
    const options=[...groups[0].children,...groups[2].children.flatMap(e=>e.children??[e]),...groups[3].children];
    const active=new Set([currentTheme,visualizerBackground,eqMode.startsWith("Custom|")?"Custom…":eqMode]);
    if(document.documentElement.classList.contains("ubuntuFont"))active.add("Use Ubuntu font");
    if(glassWanted)active.add("Glass appearance");
    if(showBars)active.add("Now Playing bars");
    bridge.sendMessage("checks:"+JSON.stringify(options.filter(e=>active.has(e.label)).map(e=>e.id)));
  }
  bridge.receiveMessage(run(async msg=>{
    if(msg.startsWith("glass-active:")){
      const root=document.documentElement;
      root.classList.toggle("glass",msg!=="glass-active:off");
      root.classList.toggle("liquid",msg==="glass-active:liquid" && currentTheme!=="Bitstream 98");
      if(root.classList.contains("liquid"))layoutPlaybackGlass();
      else if(msg==="glass-active:liquid")bridge.sendMessage("playback-glass:"+JSON.stringify(playbackGroups.map(()=>[0,0,0,0,0])));
      return;
    }
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
    if(key==="2")await browseRadio();else{if(key==="1")await openSongs();else show("Up Next",queue);grid.focus();}
    return;
  }
  if(key===" "&&!mod&&!e.altKey&&!e.shiftKey){
    e.preventDefault();if(!e.repeat)await toggleAudio();return;
  }
  if(mod && (key==="ArrowLeft"||key==="ArrowRight")){
    e.preventDefault();if(!e.repeat)await advance(key==="ArrowRight"?1:-1);return;
  }
  if(mod && key.toLowerCase()==="z"){e.preventDefault();await historyAction(e.shiftKey?"redo":"undo");return;}
  if(target!==grid || !rowCount())return;
  if(key==="Enter"){e.preventDefault();if(!e.repeat)await startSelection();return;}
  const page=Math.max(1,Math.floor((grid.clientHeight-28)/26));
  const positions={ArrowDown:cursor+1,ArrowUp:cursor-1,PageDown:cursor+page,PageUp:cursor-page,Home:0,End:rowCount()-1};
  if(!(key in positions))return;
  e.preventDefault();
  const i=Math.max(0,Math.min(positions[key],rowCount()-1));
  select(i,{shiftKey:e.shiftKey});
  if(i*26<grid.scrollTop)grid.scrollTop=i*26;
  else if((i+1)*26+28>grid.scrollTop+grid.clientHeight)grid.scrollTop=(i+1)*26+28-grid.clientHeight;
});
run(async()=>{const config=await call("config");themes=config.themes;glassWanted=config.glass;
  visualizerStyles=config.visualizerStyles;visualizerBackground=config.visualizerBackground;$("visualizerBackground").hidden=visualizerBackground==="Off";
  document.documentElement.classList.toggle("ubuntuFont",config.ubuntuFont);applyTheme(themes.find(t=>t.name===config.theme));$("view").disabled=false;eqMode=config.eq;showBars=config.bars;$("bars").hidden=!showBars;
  if(config.columns){const saved=config.columns.split("|").filter(k=>fields.some(f=>f[0]===k));if(saved.length)columns=new Set(saved);}
  const [key,order]=(config.songSort??"").split("|");
  if(fields.some(f=>f[0]===key) && (order==="1"||order==="-1")){songSort=key;songDirection=Number(order);}
  installNativeMenus();show("Songs",[]);
  const cached=await call("launchSongs",{id:songSort,extra:String(songDirection)});
  if(cached && inSongs() && !$("search").value){
    songCount=cached.total??cached.tracks.length+(cached.more?1:0);
    songPages.set(0,cached.tracks);
    $("spacer").style.height=rowCount()*26+"px";
    $("status").textContent="Reconnecting to Plex…";
    render();
  }
  connectionMessage="Reconnecting to Plex…";updateBrowseActivity();
  reconnecting=call("resume").then(resumed=>{
    connected=!!resumed && !resumed.needsSignIn;
    connectionMessage="";
    if(!connected && (cached || resumed.needsSignIn)){
      connectionMessage="Reconnect to load tracks.";
      $("reconnect").hidden=false;$("status").textContent="Reconnect to browse your Plex library.";
    }
  }).catch(error=>{connectionMessage="Couldn’t reconnect to Plex."; $("reconnect").hidden=false;throw error;});
  try{await reconnecting;}finally{reconnecting=null;updateBrowseActivity();}
  if(connected && inSongs()){
    const cachedSelection=[...selection].map(i=>[i,trackAt(i)?.id]);
    const generation=songRequest;
    render();
    await displaySongPage(0,false,true);
    if(generation===songRequest){
      for(const [i,id] of cachedSelection)if(id && trackAt(i)?.id!==id)selection.delete(i);
      render();
    }
  }
  if(connected && $("folders").open)await loadLists();
})();
