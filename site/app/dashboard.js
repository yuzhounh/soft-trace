'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const hashParts = location.hash.slice(1).split('/');
  const token = hashParts[0] || sessionStorage.getItem('softtrace-token') || '';
  if (token) sessionStorage.setItem('softtrace-token', token);
  if (location.hash) history.replaceState(null, '', location.pathname);
  const savedTheme = localStorage.getItem('softtrace-theme');
  document.documentElement.dataset.theme = savedTheme || (matchMedia('(prefers-color-scheme:dark)').matches ? 'dark' : 'light');
  const state = { day: null, status: null, rows: [], filtered: [], app: '', selected: '', timelineSelected: '', timeRange: null, drag: null, loading: false, revision: 0 };
  const colors = ['#6579cb','#6b9d91','#bd8b62','#a981b1','#659bb7','#ad9957','#c17d83','#89966a'];
  const labels = {application:'软件使用',active:'使用中',idle:'空闲',locked:'已锁屏',sleep:'睡眠',paused:'暂停记录',fault:'采集故障',unavailable:'无法识别前台窗口',disconnected:'会话已断开',unknown:'未记录'};
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const localDate = d => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  const clock = value => value ? new Date(value).toLocaleTimeString('zh-CN', {hour12:false}) : '—';
  const seconds = e => Math.max(0, (new Date(e.endUtc)-new Date(e.startUtc))/1000);
  const duration = s => { s = Math.floor(s); const h=Math.floor(s/3600),m=Math.floor(s%3600/60); return h ? `${h} 小时 ${m} 分` : m ? `${m} 分 ${s%60} 秒` : `${s} 秒`; };
  const color = name => colors[[...name].reduce((n,c)=>(n*31+c.charCodeAt(0))>>>0,0)%colors.length];
  const appKey = e => `${e.appName}\u0000${e.processName}`;
  const rowSort=window.softTraceTableSort.bind(document.querySelector('.details table'),
    ['start','end','duration','app','process','title'],()=>{renderRows();$('rows').closest('.table-wrap').scrollTop=0;},'开始时间逆序');
  const showError = message => { $('error').textContent = message; $('error').hidden = !message; };
  async function request(path, method='GET', body) {
    const headers={'X-SoftTrace-Token':token};
    if(body && !(body instanceof FormData)){headers['Content-Type']='application/json';body=JSON.stringify(body);}
    const response = await window.SoftTraceWeb.request(path,{method,headers,body,cache:'no-store'});
    if(!response.ok) { const body=await response.json().catch(()=>({})); throw new Error(response.status===403 ? '请从 SoftTrace 托盘菜单重新打开页面。' : body.error || '无法连接本机采集程序，请确认程序仍在运行。'); }
    return response;
  }
  async function api(path, method='GET', body) { return (await request(path,method,body)).json().catch(()=>null); }
  async function load(initial=false) {
    if (state.loading||state.drag) return;
    state.loading=true;
    const revision=state.revision;
    try {
      const status=await api('/api/status'); state.status=status;
      document.dispatchEvent(new CustomEvent('softtrace-status',{detail:status}));
      const current=$('device').value || localStorage.getItem('softtrace-web-device-'+CloudAuth.requireUser().uid) || status.localDeviceId;
      $('device').innerHTML=status.devices.map(d=>`<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('');
      $('device').value=current;
      if (!$('device').value) $('device').value=status.localDeviceId;      for(const option of $('view-mode').options)option.disabled=!['day','yesterday'].includes(option.value)&&!status.features?.overview;
      $('view-mode').title=status.features?.overview?'选择时间范围':'周、月、季度、年和全部时间视图需要更新托盘程序后启用';
      if(!window.softTrace.overview.isDay()){
        await window.softTrace.overview.load(status,revision);
        return;
      }      $('idle-rule').textContent='以设备已同步的空闲状态记录为准';
      const start=new Date(`${$('date').value}T00:00:00`),end=new Date(start); end.setDate(end.getDate()+1);
      const device=$('device').value;
      const query=new URLSearchParams({start:start.toISOString(),end:end.toISOString(),device});
      const day=await api(`/api/day?${query}`);
      if(revision!==state.revision) return;
      state.day=day;
      state.rows=[...day.entries,...day.states.filter(e=>e.kind==='unknown')].sort((a,b)=>new Date(a.startUtc)-new Date(b.startUtc));
      $('active').textContent=duration(day.activeSeconds); $('idle').textContent=duration(day.idleSeconds);
      $('first').textContent=clock(day.firstActivityUtc); $('last').textContent=clock(day.lastActivityUtc);
      $('day-label').textContent=`${start.toLocaleDateString('zh-CN',{year:'numeric',month:'long',day:'numeric',weekday:'long'})} · ${Intl.DateTimeFormat().resolvedOptions().timeZone} · 点击片段查看明细`;
      $('account-updated').textContent=`数据更新于 ${clock(status.nowUtc)}`;
      $('next').disabled=$('date').value>=localDate(new Date());
      document.dispatchEvent(new Event('softtrace-dates'));
      showError(status.capture.error ? `本机采集异常：${status.capture.error}` : '');
      renderTimeline(); renderApps(); renderRows();
    } catch(error) { showError(error.message); }
    finally { state.loading=false; if(revision!==state.revision) load(); }
  }
  function renderTimeline() {
    const day=state.day;if(!day||state.drag)return;
    $('timeline-tooltip').hidden=true;
    let start=new Date(`${$('date').value}T00:00:00`).getTime(),end=new Date(start);end.setDate(end.getDate()+1);end=+end;
    const known=day.entries;
    if(state.timeRange){start=state.timeRange.start;end=state.timeRange.end;}
    else if($('zoom').value==='activity'&&known.length) {
      start=Math.max(start,Math.floor(Math.min(...known.map(e=>+new Date(e.startUtc)))/3600000)*3600000);
      end=Math.min(end,Math.ceil(Math.max(...known.map(e=>+new Date(e.endUtc)))/3600000)*3600000);
      if(end<=start)end=start+3600000;
    }
    const span=end-start;
    state.timelineStart=start;state.timelineEnd=end;
    $('axis').innerHTML=Array.from({length:9},(_,i)=>`<span class="tick" style="left:${i/8*100}%">${new Date(start+span*i/8).toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false})}</span>`).join('');
    function segments(entries) {
      return entries.filter(e=>+new Date(e.endUtc)>start&&+new Date(e.startUtc)<end).map(e=>{
        const left=Math.max(start,+new Date(e.startUtc)),right=Math.min(end,+new Date(e.endUtc));
        const text=`${clock(e.startUtc)} – ${clock(e.endUtc)} · ${e.appName} · ${duration(seconds(e))}${e.windowTitle?' · '+e.windowTitle:''}`;
        const bg=e.kind==='application'?`background:${color(appKey(e))};`:'';
        return `<button class="segment ${esc(e.kind)}${e.id===state.selected||e.id===state.timelineSelected?' selected':''}" data-id="${esc(e.id)}" style="${bg}left:${(left-start)/span*100}%;width:${(right-left)/span*100}%" aria-label="${esc(text)}"></button>`;
      }).join('');
    }
    $('state-track').innerHTML=segments(day.states);$('app-track').innerHTML=segments(day.entries.filter(e=>e.kind==='application'));
    $('timeline-reset').disabled=!state.timeRange&&$('zoom').value==='day';
    updateRangeStatus();
  }
  function updateRangeStatus(start=state.timeRange?.start,end=state.timeRange?.end,preview=false){
    const label=$('timeline-range');label.hidden=start==null||end==null;
    const dayEnd=new Date(`${$('date').value}T00:00:00`);dayEnd.setDate(dayEnd.getDate()+1);const format=time=>time===+dayEnd?'24:00:00':clock(time);
    if(!label.hidden)label.textContent=`${preview?'正在选择':'已选择'} ${format(start)} – ${format(end)} · ${duration((end-start)/1000)}`;
  }
  function clearTimeRange(zoom='day'){state.timeRange=null;state.selected='';state.timelineSelected='';$('zoom').value=zoom;$('zoom-range').hidden=true;$('zoom-range').disabled=true;$('selection').textContent='选择任意时间片段，可在下方定位它的记录。';}
  function cancelTimelineDrag(){
    const drag=state.drag;state.drag=null;
    if(drag?.track.hasPointerCapture(drag.pointerId))drag.track.releasePointerCapture(drag.pointerId);
    document.querySelectorAll('.range-preview').forEach(element=>element.remove());
    document.querySelector('.timeline-panel').classList.remove('is-selecting');updateRangeStatus();
  }
  function dragTime(drag,x){return Math.round((drag.axisStart+Math.max(0,Math.min(1,(x-drag.rect.left)/drag.rect.width))*(drag.axisEnd-drag.axisStart))/1000)*1000;}
  for(const id of ['state-track','app-track','axis']){
    const track=$(id);
    track.onpointerdown=event=>{
      if(event.button!==0||!event.isPrimary||!state.day||state.drag)return;
      const rect=track.getBoundingClientRect();
      state.drag={track,pointerId:event.pointerId,startX:event.clientX,rect,axisStart:state.timelineStart,axisEnd:state.timelineEnd,entryId:event.target.closest('[data-id]')?.dataset.id,dragging:false};
      state.drag.start=dragTime(state.drag,event.clientX);track.setPointerCapture(event.pointerId);
    };
    track.addEventListener('pointermove',event=>{
      const drag=state.drag;if(!drag||drag.pointerId!==event.pointerId)return;
      if(!drag.dragging&&Math.abs(event.clientX-drag.startX)<5)return;
      drag.dragging=true;event.preventDefault();$('timeline-tooltip').hidden=true;
      const current=dragTime(drag,event.clientX),start=Math.min(current,drag.start),end=Math.max(current,drag.start);
      document.querySelector('.timeline-panel').classList.add('is-selecting');
      for(const target of [$('state-track'),$('app-track')]){
        let overlay=target.querySelector('.range-preview');if(!overlay){overlay=document.createElement('div');overlay.className='range-preview';target.append(overlay);}
        overlay.style.left=`${(start-drag.axisStart)/(drag.axisEnd-drag.axisStart)*100}%`;overlay.style.width=`${(end-start)/(drag.axisEnd-drag.axisStart)*100}%`;
      }
      updateRangeStatus(start,end,true);
    });
    track.onpointerup=event=>{
      const drag=state.drag;if(!drag||drag.pointerId!==event.pointerId)return;
      const current=dragTime(drag,event.clientX),start=Math.min(current,drag.start),end=Math.max(current,drag.start);
      cancelTimelineDrag();
      if(drag.dragging){
        if(end-start>=1000){state.timeRange={start,end};$('zoom-range').hidden=false;$('zoom-range').disabled=false;$('zoom').value='range';state.selected='';state.timelineSelected='';$('selection').textContent='下方仅显示与选区重叠的记录，并保留原始起止时间。';renderTimeline();renderRows();}
      }else if(drag.entryId)select(drag.entryId,id);
    };
    track.onpointercancel=()=>cancelTimelineDrag();track.onlostpointercapture=()=>{if(state.drag?.track===track)cancelTimelineDrag();};
  }
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&state.drag){event.preventDefault();cancelTimelineDrag();}});
  function renderApps() {
    if(!state.day)return;
    const apps=new Map();
    for(const e of state.day.entries.filter(e=>e.kind==='application')){const key=appKey(e);const app=apps.get(key)||{appName:e.appName,processName:e.processName,seconds:0};app.seconds+=seconds(e);apps.set(key,app);}
    window.softTrace.overview.renderSoftware({apps:[...apps.values()].sort((a,b)=>b.seconds-a.seconds)});
  }
  function renderRows() {
    const search=$('search').value.trim().toLocaleLowerCase(),kind=$('kind').value;
    state.filtered=state.rows.filter(e=>(!state.timeRange||(+new Date(e.endUtc)>state.timeRange.start&&+new Date(e.startUtc)<state.timeRange.end))&&(!state.app||(e.kind==='application'&&appKey(e)===state.app))&&
      (kind==='all'||(kind==='states'?e.kind!=='application':e.kind===kind))&&
      (!search||`${e.appName} ${e.processName} ${e.windowTitle||''} ${labels[e.kind]||''}`.toLocaleLowerCase().includes(search)));
    state.filtered=rowSort.order(state.filtered,(entry,key)=>{
      switch(key){
        case 'start':return +new Date(entry.startUtc);
        case 'end':return +new Date(entry.endUtc);
        case 'duration':return seconds(entry);
        case 'app':return entry.appName;
        case 'process':return entry.kind==='application'?entry.processName:'—';
        case 'title':return entry.windowTitle||(entry.kind==='application'?'未记录窗口标题':entry.kind==='unknown'?'此时段没有可用记录':labels[entry.kind]||'');
      }
    },(a,b)=>new Date(b.startUtc)-new Date(a.startUtc));
    const visible=state.filtered;
    $('count').textContent=`${state.filtered.length.toLocaleString()} 段`;
    $('rows').innerHTML=visible.map(e=>`<tr tabindex="0" data-id="${esc(e.id)}" class="${e.id===state.selected?'selected':''}"><td>${clock(e.startUtc)}</td><td>${clock(e.endUtc)}</td><td>${duration(seconds(e))}</td><td title="${esc(e.appName)}"><span class="app-cell"><i class="dot ${esc(e.kind)}"${e.kind==='application'?` style="background:${color(appKey(e))}"`:''}></i><span class="app-label">${esc(e.appName)}</span></span></td><td class="process-cell" title="${esc(e.processName)}">${e.kind==='application'?esc(e.processName):'—'}</td><td class="title-cell" title="${esc(e.windowTitle)}">${esc(e.windowTitle|| (e.kind==='application'?'未记录窗口标题':e.kind==='unknown'?'此时段没有可用记录':labels[e.kind]||''))}</td></tr>`).join('')||'<tr><td colspan="6" class="empty">没有符合条件的记录。可以清除筛选或切换日期。</td></tr>';
  }
  function select(id,source='timeline') {
    const e=state.rows.find(e=>e.id===id)||state.day?.states.find(e=>e.id===id);if(!e)return;
    state.selected=id;state.app='';$('search').value='';$('kind').value='all';
    state.timelineSelected=source==='rows'&&e.kind!=='application'?(state.day.states.find(s=>s.kind===e.kind&&new Date(s.startUtc)<=new Date(e.startUtc)&&new Date(s.endUtc)>=new Date(e.endUtc))?.id||id):id;
    renderRows();
    let index=state.filtered.findIndex(row=>row.id===id);
    if(index<0)index=state.filtered.findIndex(row=>new Date(row.endUtc)>new Date(e.startUtc)&&new Date(row.startUtc)<new Date(e.endUtc));
    if(index>=0){state.selected=state.filtered[index].id;renderRows();}
    $('selection').textContent=`${clock(e.startUtc)} – ${clock(e.endUtc)} · ${e.appName} · ${duration(seconds(e))}${e.windowTitle?' · '+e.windowTitle:''}`;
    renderTimeline();renderApps();
    const target=source==='rows'?($(e.kind==='application'?'app-track':'state-track').querySelector('.selected')||$('state-track')):$('rows').querySelector('.selected');
    target?.scrollIntoView({block:'center',inline:'nearest',behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth'});
  }
  const resetFilters=()=>{cancelTimelineDrag();clearTimeRange();state.app='';state.selected='';state.timelineSelected='';$('search').value='';$('kind').value='all';$('selection').textContent='选择任意时间片段，可在下方定位它的记录。';};
  const changeDay=()=>{state.revision++;resetFilters();load();};
  const shift=delta=>{const date=new Date(`${$('date').value}T12:00:00`),mode=$('view-mode').value;
    if(mode==='all'||mode==='custom')return;
    if(mode==='yesterday')$('view-mode').value='day';
    if(mode==='week'||mode==='7days')date.setDate(date.getDate()+7*delta);
    else if(mode==='30days')date.setDate(date.getDate()+30*delta);
    else if(mode==='3months'||mode==='12months'){const day=date.getDate();date.setDate(1);date.setMonth(date.getMonth()+delta*(mode==='3months'?3:12));date.setDate(Math.min(day,new Date(date.getFullYear(),date.getMonth()+1,0).getDate()));}
    else if(mode==='month'||mode==='quarter')date.setMonth(date.getMonth()+delta*(mode==='quarter'?3:1),1);
    else if(mode==='year')date.setFullYear(date.getFullYear()+delta,0,1);
    else date.setDate(date.getDate()+delta);
    $('date').value=localDate(date)>localDate(new Date())?localDate(new Date()):localDate(date);window.softTrace.overview.setMode();changeDay();};
  $('view-mode').value='day';$('date').value=localDate(new Date());$('date').max=$('date').value;
  $('range-to').value=$('date').value;const initialFrom=new Date();initialFrom.setDate(initialFrom.getDate()-6);$('range-from').value=localDate(initialFrom);
  $('previous').onclick=()=>shift(-1);$('next').onclick=()=>shift(1);
  $('today').onclick=()=>{if($('view-mode').value==='yesterday')$('view-mode').value='day';$('date').value=localDate(new Date());window.softTrace.overview.setMode();changeDay();};
  $('date').onchange=()=>{if($('view-mode').value==='yesterday')$('view-mode').value='day';window.softTrace.overview.setMode();changeDay();};$('device').onchange=()=>{localStorage.setItem('softtrace-web-device-'+CloudAuth.requireUser().uid,$('device').value);changeDay();};$('refresh').onclick=()=>{window.SoftTraceWeb.invalidate();load();};
  $('zoom').onchange=()=>{if($('zoom').value!=='range')clearTimeRange($('zoom').value);renderTimeline();renderRows();};
  $('timeline-reset').onclick=()=>{cancelTimelineDrag();clearTimeRange();renderTimeline();renderRows();};
  for(const id of ['range-from','range-to'])$(id).onchange=()=>{document.dispatchEvent(new Event('softtrace-dates'));changeDay();};
  $('view-mode').onchange=()=>{const mode=$('view-mode').value;if(mode==='yesterday'){const yesterday=new Date();yesterday.setDate(yesterday.getDate()-1);$('date').value=localDate(yesterday);}else if(['7days','30days','3months','12months'].includes(mode))$('date').value=localDate(new Date());window.softTrace.overview.setMode();document.dispatchEvent(new Event('softtrace-dates'));changeDay();};
  $('search').oninput=renderRows;$('kind').onchange=renderRows;
  $('clear').onclick=()=>{resetFilters();renderRows();renderApps();renderTimeline();};
  for(const id of ['state-track','app-track','rows']){$(id).onclick=event=>{if(id!=='rows'&&event.detail!==0)return;const target=event.target.closest('[data-id]');if(target)select(target.dataset.id,id);};}
  $('rows').onkeydown=event=>{if(event.key==='Enter'||event.key===' '){const row=event.target.closest('[data-id]');if(row){event.preventDefault();select(row.dataset.id,'rows');}}};
  $('jump').onclick=()=>{const value=$('jump-time').value;if(!value)return;const t=+new Date(`${$('date').value}T${value}`);const entry=state.rows.find(e=>+new Date(e.startUtc)<=t&&+new Date(e.endUtc)>t);if(entry){if(state.timeRange&&(t<state.timeRange.start||t>=state.timeRange.end))clearTimeRange();select(entry.id);}else $('selection').textContent='该时间尚无已保存的记录。';};  $('back-top').onclick=()=>window.scrollTo({top:0,behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth'});
  function showTooltip(entry,x,y) {
    const tip=$('timeline-tooltip');
    if(!entry){tip.hidden=true;return;}
    tip.textContent=`${entry.appName}\n${clock(entry.startUtc)} – ${clock(entry.endUtc)} · ${duration(seconds(entry))}${entry.windowTitle?'\n'+entry.windowTitle:''}`;
    tip.hidden=false;
    tip.style.left=`${Math.max(12,Math.min(x+14,innerWidth-tip.offsetWidth-12))}px`;
    tip.style.top=`${y+16+tip.offsetHeight>innerHeight?Math.max(12,y-tip.offsetHeight-12):y+16}px`;
  }
  for(const id of ['app-track','state-track']) {
    const track=$(id);
    track.onpointermove=event=>{
      if(!state.day||state.drag?.dragging)return;
      const rect=track.getBoundingClientRect(),time=state.timelineStart+(event.clientX-rect.left)/rect.width*(state.timelineEnd-state.timelineStart);
      const entries=id==='app-track'?state.day.entries.filter(e=>e.kind==='application'):state.day.states;
      showTooltip(entries.find(e=>+new Date(e.startUtc)<=time&&+new Date(e.endUtc)>time),event.clientX,event.clientY);
    };
    track.onpointerleave=()=>{$('timeline-tooltip').hidden=true;};
    track.onfocusin=event=>{const button=event.target.closest('[data-id]');if(!button||!state.day)return;const rect=button.getBoundingClientRect();const entries=id==='app-track'?state.day.entries:state.day.states;showTooltip(entries.find(e=>e.id===button.dataset.id),rect.left,rect.bottom);};
    track.onfocusout=()=>{$('timeline-tooltip').hidden=true;};
  }
  window.softTrace={api,request,refresh:load,localDate,duration,escape:esc,color,getStatus:()=>state.status,
    selectedApp:()=>state.app,filterApp:key=>{state.app=state.app===key?'':key;$('kind').value=state.app?'application':'all';renderRows();renderApps();},
    revision:()=>state.revision,reloadView:changeDay,openDay:date=>{$('view-mode').value='day';$('date').value=date;window.softTrace.overview.setMode();changeDay();},
    openSettings:hashParts[1]==='settings'};
  function updateThemeLabel(){document.querySelectorAll('button[data-theme]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.theme===document.documentElement.dataset.theme)));}
  document.querySelectorAll('button[data-theme]').forEach(button=>button.onclick=()=>{const next=button.dataset.theme;document.documentElement.dataset.theme=next;localStorage.setItem('softtrace-theme',next);updateThemeLabel();});
  updateThemeLabel();
  document.addEventListener('DOMContentLoaded',()=>load(true),{once:true});setInterval(()=>{if(!document.hidden)load();},15000);document.addEventListener('visibilitychange',()=>{if(!document.hidden)load();});
})();
