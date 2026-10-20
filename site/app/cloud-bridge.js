'use strict';
(() => {
  const model=import('./model.mjs');
  let owner='',generation=0,registry=[],registryAt=0,dataAt=0;
  const cache=new Map();
  const me=()=>CloudAuth.requireUser();
  function invalidate(){for(const entry of cache.values())entry.force=true;registryAt=0;}
  CloudAuth.onUser(user=>{if(owner!==(user?.uid||'')){owner=user?.uid||'';generation++;registry=[];cache.clear();dataAt=0;registryAt=0;}if(user)window.softTrace?.refresh(true);});
  function checkOwner(uid,epoch){if(me().uid!==uid||generation!==epoch)throw Error('账户已切换，请重新加载。');}
  async function devices() {
    if(Date.now()-registryAt<120000)return registry;
    const uid=me().uid,epoch=generation,docs=await CloudAuth.collection('users/'+uid+'/devices');
    const next=docs.map(doc=>{const d=CloudAuth.documentData(doc);return{id:d.deviceId||doc.name.split('/').at(-1),name:d.displayName||d.deviceName||d.deviceId||'同步设备'};});
    if(!next.length){const rows=await CloudAuth.firestore('users/'+uid+':runQuery',{method:'POST',body:{structuredQuery:{from:[{collectionId:'activities'}],orderBy:[{field:{fieldPath:'startUtc'},direction:'DESCENDING'}],limit:1}}});const last=rows.find(r=>r.document)?.document;if(last){const d=CloudAuth.documentData(last);next.push({id:d.deviceId,name:d.deviceName||d.deviceId});}}
    checkOwner(uid,epoch);registry=next;registryAt=Date.now();return registry;
  }
  async function query(field,start,end,uid,epoch) {
    const found=[];let cursor=null;
    do{
      checkOwner(uid,epoch);
      const structuredQuery={from:[{collectionId:'activities'}],limit:1000};
      if(start){structuredQuery.where={compositeFilter:{op:'AND',filters:[
        {fieldFilter:{field:{fieldPath:field},op:'GREATER_THAN',value:{timestampValue:start}}},
        {fieldFilter:{field:{fieldPath:'startUtc'},op:'LESS_THAN',value:{timestampValue:end}}}
      ]}};structuredQuery.orderBy=[{field:{fieldPath:field},direction:'ASCENDING'},{field:{fieldPath:'startUtc'},direction:'ASCENDING'},{field:{fieldPath:'__name__'},direction:'ASCENDING'}];}
      else{structuredQuery.where={fieldFilter:{field:{fieldPath:'startUtc'},op:'LESS_THAN',value:{timestampValue:end}}};structuredQuery.orderBy=[{field:{fieldPath:'startUtc'},direction:'ASCENDING'},{field:{fieldPath:'__name__'},direction:'ASCENDING'}];}
      if(cursor)structuredQuery.startAt={values:cursor,before:false};
      const results=await CloudAuth.firestore('users/'+uid+':runQuery',{method:'POST',body:{structuredQuery}});
      checkOwner(uid,epoch);
      const docs=results.flatMap(r=>r.document?[r.document]:[]);found.push(...docs);
      if(docs.length<1000)break;
      const last=docs.at(-1);
      cursor=start?[last.fields[field],last.fields.startUtc,{referenceValue:last.name}]:[last.fields.startUtc,{referenceValue:last.name}];
    }while(true);
    return found;
  }
  async function records(start,end,device,rangeEnd) {
    const uid=me().uid,epoch=generation,key=JSON.stringify([uid,start,rangeEnd]),hit=cache.get(key);
    const select=rows=>rows.filter(e=>!device||e.deviceId===device);
    const fresh=entry=>!entry.force&&((entry.end>=entry.rangeEnd&&entry.rangeEnd<=Date.now())||Date.now()-entry.at<120000);
    if(hit&&fresh(hit)){const rows=await hit.promise;checkOwner(uid,epoch);return select(rows);}
    const from=start?+new Date(start):-Infinity,to=+new Date(end);
    const reusable=[...cache.values()].filter(entry=>fresh(entry)&&entry.start<to&&entry.end>from).sort((a,b)=>a.start-b.start);
    const promise=(async()=>{const m=await model;
      const merged=new Map(),gaps=[];let cursor=from;
      for(const entry of reusable){for(const row of await entry.promise)merged.set(row.id,row);if(entry.start>cursor)gaps.push([cursor,Math.min(entry.start,to)]);cursor=Math.max(cursor,entry.end);if(cursor>=to)break;}
      if(cursor<to)gaps.push([cursor,to]);
      for(const[a,b]of gaps){
        checkOwner(uid,epoch);const lower=Number.isFinite(a)?new Date(a).toISOString():null,upper=new Date(b).toISOString();
        const pages=lower?await Promise.all([query('endUtc',lower,upper,uid,epoch),query('stateEndUtc',lower,upper,uid,epoch)]):[await query(null,null,upper,uid,epoch)];
        for(const doc of new Map(pages.flat().map(d=>[d.name,d])).values()){const entry=m.normalizeActivity(doc,CloudAuth.documentData);if(entry)merged.set(entry.id,entry);}
      }
      checkOwner(uid,epoch);return[...merged.values()].filter(e=>+new Date(e.endUtc)>from&&+new Date(e.startUtc)<to);
    })();cache.set(key,{at:Date.now(),start:from,end:to,rangeEnd:+new Date(rangeEnd),promise});
    try{const rows=await promise;checkOwner(uid,epoch);dataAt=Date.now();return select(rows);}catch(error){if(generation===epoch){if(hit)cache.set(key,hit);else cache.delete(key);}throw error;}
  }
  async function request(path,{method='GET'}={}) {
    await CloudAuth.ready;
    try{
      const user=me(),url=new URL(path,location.origin),m=await model;let value;
      if(url.pathname==='/api/status'){const list=await devices();value={devices:list,localDeviceId:list[0]?.id||'',idleMinutes:3,autoStart:false,features:{overview:true,avatar:true,cloudView:true},capture:{isPaused:false,isIdle:true,error:null},sync:{isSignedIn:true,isSyncing:false,email:user.email,displayName:user.displayName,photoUrl:user.photoURL,lastSyncUtc:new Date(dataAt||registryAt).toISOString(),message:'云端查看 · 采集由原电脑完成',hasError:false},nowUtc:new Date(dataAt||registryAt||Date.now()).toISOString()};}
      else if(url.pathname==='/api/day'||url.pathname==='/api/overview'){
        const today=new Date();today.setHours(0,0,0,0);const tomorrow=new Date(today);tomorrow.setDate(tomorrow.getDate()+1);
        const all=url.pathname==='/api/overview'&&url.searchParams.get('all')==='1';
        const start=all?null:url.searchParams.get('start')||today.toISOString(),rangeEnd=url.searchParams.get('end')||tomorrow.toISOString();
        const end=new Date(Math.min(Date.now(),+new Date(rangeEnd))).toISOString(),device=url.searchParams.get('device');
        if(start&&(!Number.isFinite(+new Date(start))||+new Date(start)>=+new Date(end)))throw Error('请选择有效的日期范围。');
        const rows=await records(start,end,device,rangeEnd);value=url.pathname==='/api/day'?m.timeline(rows,start,end):m.overview(rows,start,end);
      }else if(url.pathname==='/api/account/avatar')value={dataUrl:user.photoURL||''};
      else if(url.pathname==='/api/sync'){invalidate();value={message:'云端记录已刷新',hasError:false};}
      else if(url.pathname==='/api/account/logout'){await CloudAuth.logout();value={message:'已退出'};}
      else if(url.pathname==='/api/account/login'){await CloudAuth.login();value={message:'已登录'};}
      else throw Error('请在电脑上的 Soft Trace 中管理采集和原始记录。');
      return new Response(JSON.stringify(value),{headers:{'Content-Type':'application/json'}});
    }catch(error){return new Response(JSON.stringify({error:error.message}),{status:400,headers:{'Content-Type':'application/json'}});}
  }
  window.SoftTraceWeb={request,invalidate};
})();
