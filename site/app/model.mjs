// Cloud presentation model. Input is only the signed-in user's Firestore records.
export function timeline(observations,start,end) {
  start=+new Date(start);end=+new Date(end);
  const entries=observations.map(e=>({...e,startUtc:new Date(Math.max(start,+new Date(e.startUtc))).toISOString(),endUtc:new Date(Math.min(end,+new Date(e.endUtc))).toISOString()}))
    .filter(e=>+new Date(e.endUtc)>+new Date(e.startUtc)).sort((a,b)=>+new Date(a.startUtc)-+new Date(b.startUtc));
  const states=[];let cursor=start;
  const add=e=>{const last=states.at(-1);if(last&&last.kind===e.kind&&last.endUtc===e.startUtc){last.endUtc=e.endUtc;last.isOpen=e.isOpen;}else states.push(e);};
  const gap=(a,b)=>add({id:'gap-'+a,kind:'unknown',appName:'未记录',processName:'',windowTitle:null,startUtc:new Date(a).toISOString(),endUtc:new Date(b).toISOString(),isOpen:false,source:'gap'});
  for(const entry of entries){const a=+new Date(entry.startUtc),b=+new Date(entry.endUtc);if(a>cursor)gap(cursor,a);if(b<=cursor)continue;const kind=entry.kind==='application'?'active':entry.kind;add({...entry,kind,appName:kind==='active'?'使用中':entry.appName,windowTitle:null,startUtc:new Date(Math.max(cursor,a)).toISOString()});cursor=b;}
  if(cursor<end)gap(cursor,end);
  const seconds=e=>(+new Date(e.endUtc)-+new Date(e.startUtc))/1000,apps=entries.filter(e=>e.kind==='application');
  return{startUtc:new Date(start).toISOString(),endUtc:new Date(end).toISOString(),entries,states,
    activeSeconds:states.filter(e=>e.kind==='active').reduce((n,e)=>n+seconds(e),0),
    idleSeconds:states.filter(e=>e.kind==='idle').reduce((n,e)=>n+seconds(e),0),
    firstActivityUtc:apps.length?new Date(Math.min(...apps.map(e=>+new Date(e.startUtc)))).toISOString():null,
    lastActivityUtc:apps.length?new Date(Math.max(...apps.map(e=>+new Date(e.endUtc)))).toISOString():null};
}
const dateKey=d=>d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');
const semesters=[['2024-08-29','2025-01-18','autumn'],['2025-01-18','2025-02-13','winter'],['2025-02-13','2025-07-05','spring'],['2025-07-05','2025-08-27','summer'],['2025-08-27','2026-01-17','autumn'],['2026-01-17','2026-02-25','winter'],['2026-02-25','2026-07-10','spring'],['2026-07-10','2026-08-26','summer'],['2026-08-26','2027-01-25','autumn'],['2027-01-25','2027-02-17','winter'],['2027-02-17','2027-07-19','spring']];
export function overview(entries,start,end) {
  end=new Date(end);start=start?new Date(start):new Date(Math.min(+end,...entries.map(e=>+new Date(e.startUtc))));
  if(!Number.isFinite(+start)||!Number.isFinite(+end))throw Error('日期无效');
  if(!entries.length&&!start)start=new Date(end);
  const first=new Date(start);first.setHours(0,0,0,0);
  const days=[],hourDays=[],clipped=timeline(entries,start,end).entries,buckets=new Map();
  for(const entry of clipped){const cursor=new Date(entry.startUtc);cursor.setHours(0,0,0,0);for(;cursor<new Date(entry.endUtc);cursor.setDate(cursor.getDate()+1)){const key=dateKey(cursor);if(!buckets.has(key))buckets.set(key,[]);buckets.get(key).push(entry);}}
  for(let cursor=new Date(first);cursor<end;cursor.setDate(cursor.getDate()+1)){
    const next=new Date(cursor);next.setDate(next.getDate()+1);
    const day=timeline(buckets.get(dateKey(cursor))||[],Math.max(+cursor,+start),Math.min(+next,+end));
    days.push({date:dateKey(cursor),activeSeconds:day.activeSeconds,idleSeconds:day.idleSeconds});
    const active=Array(24).fill(0),recorded=Array(24).fill(0);
    for(const state of day.states){if(['unknown','fault','unavailable','disconnected'].includes(state.kind))continue;for(let at=+new Date(state.startUtc),stop=+new Date(state.endUtc);at<stop;){const local=new Date(at),hour=local.getHours(),boundary=new Date(local);boundary.setMinutes(0,0,0);boundary.setHours(hour+1);const until=Math.min(stop,+boundary);if(until<=at)break;recorded[hour]+=(until-at)/1000;if(state.kind==='active')active[hour]+=(until-at)/1000;at=until;}}
    hourDays.push({date:dateKey(cursor),weekday:(cursor.getDay()+6)%7,active,recorded,recordedSeconds:recorded.reduce((a,b)=>a+b,0),activeSeconds:active.reduce((a,b)=>a+b,0)});
  }
  const apps=new Map();
  for(const e of clipped.filter(e=>e.kind==='application')){const key=e.appName+'\0'+e.processName;const app=apps.get(key)||{appName:e.appName,processName:e.processName,seconds:0,segmentCount:0};app.seconds+=(+new Date(e.endUtc)-+new Date(e.startUtc))/1000;app.segmentCount++;apps.set(key,app);}
  const observed=hourDays.filter(d=>d.recordedSeconds>0),count=observed.length;
  const hours=Array.from({length:24},(_,hour)=>{const activeSeconds=observed.reduce((n,d)=>n+d.active[hour],0);return{hour,activeSeconds,recordedSeconds:observed.reduce((n,d)=>n+d.recorded[hour],0),recordedDays:observed.filter(d=>d.recorded[hour]>0).length,averageMinutes:count?activeSeconds/count/60:null};});
  const weekdays=['周一','周二','周三','周四','周五','周六','周日'].map((label,weekday)=>{const ds=observed.filter(d=>d.weekday===weekday),activeSeconds=ds.reduce((n,d)=>n+d.activeSeconds,0);return{weekday,label,activeSeconds,recordedDays:ds.length,averageHours:ds.length?activeSeconds/ds.length/3600:null};});
  const academicPeriods=[['spring','春季学期'],['autumn','秋季学期'],['winter','寒假轮休期'],['summer','暑假轮休期'],['unknown','待补校历']].map(([kind,label])=>{const ds=observed.filter(d=>(semesters.find(([a,b])=>d.date>=a&&d.date<b)?.[2]||'unknown')===kind),activeSeconds=ds.reduce((n,d)=>n+d.activeSeconds,0);return{kind,label,activeSeconds,recordedDays:ds.length,averageHours:ds.length?activeSeconds/ds.length/3600:null};});
  return{startUtc:start.toISOString(),endUtc:end.toISOString(),days,apps:[...apps.values()].sort((a,b)=>b.seconds-a.seconds),activeSeconds:days.reduce((n,d)=>n+d.activeSeconds,0),idleSeconds:days.reduce((n,d)=>n+d.idleSeconds,0),activeDays:days.filter(d=>d.activeSeconds>0).length,segmentCount:clipped.filter(e=>e.kind==='application').length,
    patterns:{hours,weekdays,academicPeriods,recordedDays:count,missingDays:days.length-count,recordedSeconds:observed.reduce((n,d)=>n+d.recordedSeconds,0),timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,calendarBasis:'信阳师范大学谭山校区教职工报到与轮休日期；未核对日期单列。',calendarSources:[{academicYear:'2024—2025',url:'https://www.xynu.edu.cn/info/1059/16121.htm'},{academicYear:'2025—2026',url:'https://www.xynu.edu.cn/info/1059/1067.htm'},{academicYear:'2026—2027',url:'https://www.xynu.edu.cn/info/1059/7920.htm'}]}};
}
export function normalizeActivity(doc,decode) {
  const e=decode(doc),kind=e.kind||'application',start=kind==='application'?e.startUtc:e.stateStartUtc||e.startUtc,end=kind==='application'?e.endUtc:e.stateEndUtc||e.endUtc;
  if(!start||!end||!Number.isFinite(+new Date(start))||!Number.isFinite(+new Date(end)))return null;
  return{id:e.syncId||doc.name.split('/').at(-1),kind,deviceId:e.deviceId,deviceName:e.deviceName,appName:e.appName||e.processName||'',processName:e.processName||'',windowTitle:e.windowTitle||'',startUtc:start,endUtc:end,isOpen:!!e.isOpen,source:e.source||'cloud'};
}
