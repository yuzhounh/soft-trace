'use strict';
(() => {
  const model=import('./model.mjs');
  let owner='',registry=[],registryAt=0;
  const cache=new Map();
  const me=()=>CloudAuth.requireUser();
  function invalidate(){cache.clear();registryAt=0;}
  CloudAuth.onUser(user=>{if(owner!==user?.uid){owner=user?.uid||'';registry=[];invalidate();}if(user)window.softTrace?.refresh(true);});
  async function devices() {
    if(Date.now()-registryAt<120000)return registry;
    const docs=await CloudAuth.collection('users/'+me().uid+'/devices');
    registry=docs.map(doc=>{const d=CloudAuth.documentData(doc);return{id:d.deviceId||doc.name.split('/').at(-1),name:d.displayName||d.deviceName||d.deviceId||'同步设备'};});
    if(!registry.length){const rows=await CloudAuth.firestore('users/'+me().uid+':runQuery',{method:'POST',body:{structuredQuery:{from:[{collectionId:'activities'}],orderBy:[{field:{fieldPath:'startUtc'},direction:'DESCENDING'}],limit:1}}});const last=rows.find(r=>r.document)?.document;if(last){const d=CloudAuth.documentData(last);registry.push({id:d.deviceId,name:d.deviceName||d.deviceId});}}
    registryAt=Date.now();return registry;
  }
  async function query(field,start,end) {
    const uid=me().uid,found=[];let cursor=null;
    do{
      const structuredQuery={from:[{collectionId:'activities'}],limit:1000};
      if(start){structuredQuery.where={compositeFilter:{op:'AND',filters:[
        {fieldFilter:{field:{fieldPath:field},op:'GREATER_THAN',value:{timestampValue:start}}},
        {fieldFilter:{field:{fieldPath:'startUtc'},op:'LESS_THAN',value:{timestampValue:end}}}
      ]}};structuredQuery.orderBy=[{field:{fieldPath:field},direction:'ASCENDING'},{field:{fieldPath:'startUtc'},direction:'ASCENDING'},{field:{fieldPath:'__name__'},direction:'ASCENDING'}];}
      else structuredQuery.orderBy=[{field:{fieldPath:'__name__'},direction:'ASCENDING'}];
      if(cursor)structuredQuery.startAt={values:cursor,before:false};
      const results=await CloudAuth.firestore('users/'+uid+':runQuery',{method:'POST',body:{structuredQuery}});
      const docs=results.flatMap(r=>r.document?[r.document]:[]);found.push(...docs);
      if(docs.length<1000)break;
      const last=docs.at(-1);
      cursor=start?[last.fields[field],last.fields.startUtc,{referenceValue:last.name}]:[{referenceValue:last.name}];
    }while(true);
    return found;
  }
  async function records(start,end,device) {
    const uid=me().uid,key=JSON.stringify([uid,start,end,device]),hit=cache.get(key);
    if(hit&&Date.now()-hit.at<60000)return hit.promise;
    const promise=(async()=>{const pages=start?await Promise.all([query('endUtc',start,end),query('stateEndUtc',start,end)]):[await query(null,null,end)];
      const unique=new Map(pages.flat().map(doc=>[doc.name,doc])),m=await model;
      return[...unique.values()].map(d=>m.normalizeActivity(d,CloudAuth.documentData)).filter(e=>e&&(!device||e.deviceId===device));
    })();cache.set(key,{at:Date.now(),promise});
    try{return await promise;}catch(error){cache.delete(key);throw error;}
  }
  async function request(path,{method='GET'}={}) {
    await CloudAuth.ready;
    try{
      const user=me(),url=new URL(path,location.origin),m=await model;let value;
      if(url.pathname==='/api/status'){const list=await devices();value={devices:list,localDeviceId:list[0]?.id||'',idleMinutes:3,autoStart:false,features:{overview:true,avatar:true,cloudView:true},capture:{isPaused:false,isIdle:true,error:null},sync:{isSignedIn:true,isSyncing:false,email:user.email,displayName:user.displayName,photoUrl:user.photoURL,lastSyncUtc:new Date(registryAt).toISOString(),message:'云端查看 · 采集由原电脑完成',hasError:false},nowUtc:new Date().toISOString()};}
      else if(url.pathname==='/api/day'||url.pathname==='/api/overview'){
        const start=url.searchParams.get('start'),end=new Date(Math.min(Date.now(),url.searchParams.has('end')?+new Date(url.searchParams.get('end')):Date.now())).toISOString(),device=url.searchParams.get('device');
        const rows=await records(start,end,device);value=url.pathname==='/api/day'?m.timeline(rows,start,end):m.overview(rows,start,end);
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
