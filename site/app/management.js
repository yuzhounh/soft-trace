'use strict';
(() => {
  const $=id=>document.getElementById(id),client=window.softTrace,settings=$('settings-dialog');
  function account() {
    const user=CloudAuth.user;
    $('account-name').textContent=user?.displayName||'Google 账户';
    $('account-email').textContent=user?.email||'';
    $('account-login').hidden=!!user;
    $('account-logout').hidden=!user;
    $('account-avatar').hidden=!user?.photoURL;
    $('account-avatar-fallback').hidden=!!user?.photoURL;
    if(user?.photoURL)$('account-avatar').src=user.photoURL;
  }
  function populate(status) {
    if(!status)return;
    $('account-sync').textContent=status.sync.message;
    $('account-last-sync').textContent=status.sync.lastSyncUtc?'最近读取：'+new Date(status.sync.lastSyncUtc).toLocaleString('zh-CN',{hour12:false}):'';
    $('settings-devices').innerHTML=status.devices.map(d=>'<div class="device-name-row"><strong>'+client.escape(d.name)+'</strong><small>已同步设备</small></div>').join('')||'<p>当前账户尚无同步设备记录。</p>';
  }
  $('manage').onclick=async()=>{settings.showModal();account();try{populate(await client.api('/api/status'));}catch(error){$('account-sync').textContent=error.message;}};
  document.querySelectorAll('[data-close-dialog]').forEach(button=>button.onclick=()=>$(button.dataset.closeDialog).close());
  document.querySelectorAll('[data-settings-tab]').forEach(button=>button.onclick=()=>{
    document.querySelectorAll('[data-settings-tab]').forEach(b=>b.setAttribute('aria-selected',String(b===button)));
    document.querySelectorAll('[data-settings-panel]').forEach(panel=>panel.hidden=panel.dataset.settingsPanel!==button.dataset.settingsTab);
  });
  $('account-logout').onclick=()=>CloudAuth.logout();
  $('account-login').onclick=()=>CloudAuth.login();
  $('account-sync-now').onclick=async()=>{
    const button=$('account-sync-now');button.disabled=true;
    try{window.SoftTraceWeb.invalidate();await client.refresh();populate(await client.api('/api/status'));}
    catch(error){$('account-sync').textContent=error.message;}
    finally{button.disabled=false;}
  };
  document.addEventListener('softtrace-status',event=>{account();populate(event.detail);});
  CloudAuth.onUser(()=>account());
})();
