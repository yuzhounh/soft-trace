'use strict';
(() => {
  const $=id=>document.getElementById(id),client=window.softTrace,{api,escape:esc,localDate,duration}=client;
  const settings=$('settings-dialog');
  let avatarKey='',avatarRevision=0,savedPreferences=null,savingPreferences=false;
  function updateAvatar(status){
    const key=status.sync.isSignedIn?`${status.sync.email}|${status.sync.photoUrl||''}`:'';
    if(key===avatarKey)return;
    avatarKey=key;const revision=++avatarRevision;
    $('account-avatar').hidden=true;$('account-avatar-fallback').toggleAttribute('hidden',false);
    if(!status.sync.isSignedIn||!status.sync.photoUrl||!status.features?.avatar)return;
    api('/api/account/avatar').then(result=>{
      if(revision!==avatarRevision||!result?.dataUrl)return;
      const image=$('account-avatar');image.onload=()=>{if(revision===avatarRevision){image.hidden=false;$('account-avatar-fallback').toggleAttribute('hidden',true);}};
      image.src=result.dataUrl;
    }).catch(()=>{});
  }
  const noticeTimers=new Map();
  function message(panel,text,error=false,waiting=false){
    let notice=panel.querySelector('[data-operation-message]');
    if(!notice){notice=document.createElement('p');notice.dataset.operationMessage='';notice.className='dialog-message';notice.setAttribute('role','status');panel.prepend(notice);}
    clearTimeout(noticeTimers.get(notice));noticeTimers.delete(notice);
    notice.textContent=text;notice.hidden=!text;notice.classList.toggle('error',error);
    if(text&&!waiting)noticeTimers.set(notice,setTimeout(()=>{notice.textContent='';notice.hidden=true;noticeTimers.delete(notice);},error?6000:3000));
  }
  function clearMessages(){
    for(const timer of noticeTimers.values())clearTimeout(timer);
    noticeTimers.clear();
    settings.querySelectorAll('[data-operation-message]').forEach(notice=>{notice.textContent='';notice.hidden=true;});
  }
  function updateAccount(status){
    if(!status)return;
    const sync=status.sync;
    updateAvatar(status);
    $('account-name').textContent=sync.displayName||(sync.isSignedIn?'已登录 Google':'本地记录');
    $('account-email').textContent=sync.email||'尚未登录';
    $('account-sync').textContent=sync.message;
    $('account-last-sync').textContent=sync.lastSyncUtc?`最近成功同步：${new Date(sync.lastSyncUtc).toLocaleString('zh-CN',{hour12:false})}`:'';
    $('account-login').hidden=sync.isSignedIn&&!sync.requiresReauthentication&&!sync.hasError;
    $('account-login').textContent=sync.isSignedIn?'重新授权':'Google 登录';
    for(const id of ['account-login','account-sync-now','account-logout'])$(id).disabled=Boolean($(id).dataset.busy)||sync.isSyncing||(id!=='account-login'&&!sync.isSignedIn);
  }
  function populate(status){
    updateAccount(status);
    if(!status)return;
    for(const id of ['export-device']){
      const selected=$(id).value;
      $(id).innerHTML='<option value="">全部设备</option>'+status.devices.map(d=>`<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('');
      $(id).value=selected;
    }
    const devices=[...status.devices].sort((a,b)=>Number(b.id===status.localDeviceId)-Number(a.id===status.localDeviceId));
    $('settings-devices').innerHTML=devices.map((d,i)=>`<div class="device-name-row"><div class="device-metadata"><label class="device-id-label" for="settings-device-${i}">${d.id===status.localDeviceId?'当前电脑':'同步设备'}</label><span class="device-identity">设备 ID：<code>${esc(d.id)}</code></span></div><div class="device-editor-controls"><input id="settings-device-${i}" data-device-name="${esc(d.id)}" value="${esc(d.name)}" maxlength="80" aria-label="${esc(d.name)}的显示名称"><button data-rename-device="${esc(d.id)}">保存</button></div></div>`).join('');
  }
  async function refreshControls(){const status=await api('/api/status');populate(status);await client.refresh();return status;}
  async function openSettings(){
    settings.showModal();clearMessages();
    const panel=settings.querySelector('[data-settings-panel]:not([hidden])');
    try{const status=await refreshControls();savedPreferences={idleMinutes:status.idleMinutes,autoStart:status.autoStart};$('idle-minutes').value=status.idleMinutes;$('auto-start').checked=status.autoStart;}catch(error){message(panel,error.message,true);}
  }
  $('manage').onclick=openSettings;
  settings.addEventListener('close',clearMessages);
  document.querySelectorAll('[data-close-dialog]').forEach(button=>button.onclick=()=>$(button.dataset.closeDialog).close());
  document.querySelectorAll('[data-settings-tab]').forEach(button=>button.onclick=()=>{
    clearMessages();
    document.querySelectorAll('[data-settings-tab]').forEach(item=>item.setAttribute('aria-selected',String(item===button)));
    document.querySelectorAll('[data-settings-panel]').forEach(panel=>panel.hidden=panel.dataset.settingsPanel!==button.dataset.settingsTab);
  });
  async function run(button,operation){
    const panel=button.closest('[data-settings-panel]');
    const notify=(text,error=false,waiting=false)=>message(panel,text,error,waiting);
    button.dataset.busy='true';button.disabled=true;notify('正在处理…',false,true);
    try{await operation(notify);}catch(error){notify(error.message,true);}
    finally{delete button.dataset.busy;button.disabled=false;updateAccount(client.getStatus());}
  }
  $('account-login').onclick=()=>run($('account-login'),async notify=>{notify('请在打开的 Google 页面选择原账号并完成授权。',false,true);const result=await api('/api/account/login','POST');await refreshControls();notify(result.message,result.hasError);});
  $('account-logout').onclick=()=>{
    if(!confirm('退出当前 Google 账号？本地记录和设备信息会保留。'))return;
    run($('account-logout'),async notify=>{await api('/api/account/logout','POST');await refreshControls();notify('已退出账号，本地记录保留。');});
  };
  $('account-sync-now').onclick=()=>run($('account-sync-now'),async notify=>{const result=await api('/api/sync','POST');await refreshControls();notify(result.message,result.hasError);});
  async function savePreferences(){
    if(savingPreferences||!savedPreferences)return;
    const panel=$('idle-minutes').closest('[data-settings-panel]');
    const idleMinutes=Number($('idle-minutes').value);
    if(!Number.isInteger(idleMinutes)||idleMinutes<1||idleMinutes>60){$('idle-minutes').value=savedPreferences.idleMinutes;message(panel,'空闲阈值需为 1–60 的整数。',true);return;}
    const next={idleMinutes,autoStart:$('auto-start').checked};
    if(next.idleMinutes===savedPreferences.idleMinutes&&next.autoStart===savedPreferences.autoStart)return;
    savingPreferences=true;$('idle-minutes').disabled=true;$('auto-start').disabled=true;
    try{await api('/api/preferences','POST',next);savedPreferences=next;message(panel,'已自动保存。');await client.refresh();}
    catch(error){
      try{const status=await api('/api/status');savedPreferences={idleMinutes:status.idleMinutes,autoStart:status.autoStart};}catch{}
      $('idle-minutes').value=savedPreferences.idleMinutes;$('auto-start').checked=savedPreferences.autoStart;message(panel,error.message,true);
    }finally{savingPreferences=false;$('idle-minutes').disabled=false;$('auto-start').disabled=false;}
  }
  $('idle-minutes').onchange=savePreferences;
  $('idle-minutes').onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();savePreferences();}};
  $('auto-start').onchange=savePreferences;
  $('settings-devices').onclick=event=>{
    const button=event.target.closest('[data-rename-device]');if(!button)return;
    const input=button.parentElement.querySelector('input');
    run(button,async notify=>{const result=await api('/api/device/name','POST',{deviceId:button.dataset.renameDevice,name:input.value});await refreshControls();notify(result.sync?.hasError?'名称已保存在本机，云同步稍后重试。':'设备名称已保存。');});
  };
  function dateQuery(from,to){
    const start=new Date(`${from}T00:00:00`),end=new Date(`${to}T00:00:00`);end.setDate(end.getDate()+1);
    if(!Number.isFinite(+start)||!Number.isFinite(+end)||end<=start)throw new Error('请选择有效的起止日期。');
    return {start:start.toISOString(),end:end.toISOString()};
  }
  $('export-data').onclick=()=>run($('export-data'),async notify=>{
    const scope=$('export-scope').value;
    let dates={};
    if(scope==='day')dates=dateQuery($('date').value,$('date').value);
    if(scope==='view'){const selected=client.overview.range();if(selected.mode!=='all')dates={start:selected.start.toISOString(),end:selected.end.toISOString()};}
    const query=new URLSearchParams({device:$('export-device').value,...dates});
    const response=await client.request(`/api/export?${query}`),blob=await response.blob(),url=URL.createObjectURL(blob);
    const link=document.createElement('a');link.href=url;link.download=`SoftTrace_Export_${localDate(new Date())}.json`;document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);notify('导出文件已生成。');
  });
  function updateImportFileName(){const name=$('import-file').files[0]?.name||'未选择文件';$('import-file-name').textContent=name;$('import-file-name').title=name;}
  $('choose-import-file').onclick=()=>$('import-file').click();
  $('import-file').onchange=updateImportFileName;
  $('import-data').onclick=()=>run($('import-data'),async notify=>{
    const file=$('import-file').files[0];if(!file)throw new Error('请先选择导入文件。');
    if(file.size>256*1024*1024)throw new Error('导入文件不能超过 256 MB。');
    const form=new FormData();form.append('file',file);
    const result=await api('/api/import','POST',form);await refreshControls();
    notify(`读取 ${result.read.toLocaleString()} 段，导入 ${result.imported.toLocaleString()} 段（${duration(result.seconds)}）；重叠跳过 ${duration(result.overlapSeconds)}。`);
    $('import-file').value='';updateImportFileName();
  });
  document.addEventListener('softtrace-status',event=>updateAccount(event.detail));
  updateAccount(client.getStatus());
  if(client.openSettings)openSettings();
})();
