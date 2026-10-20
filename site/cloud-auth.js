'use strict';
(() => {
  const root = new URL('.', document.currentScript.src);
  let user = null, auth, config;
  const listeners = new Set();
  let resolveReady, rejectReady;
  const ready = new Promise((yes,no)=>{resolveReady=yes;rejectReady=no;});
  const friendly = error => ({
    'auth/popup-blocked':'登录窗口被浏览器拦截，请允许弹窗后重试。',
    'auth/popup-closed-by-user':'登录已取消。',
    'auth/unauthorized-domain':'此网站的登录域名尚未授权，请联系维护者。',
    'auth/network-request-failed':'无法连接 Google 登录服务，请检查网络后重试。'
  }[error.code] || error.message || '操作未完成，请重试。');
  function notify(next) {
    user=next;
    const gate=document.getElementById('auth-gate');if(gate){document.body.classList.toggle('auth-wait',!next);gate.hidden=!!next;}
    const message=document.getElementById('auth-message');if(message?.textContent==='正在读取登录状态…')message.textContent=next?'':'使用与原设备相同的 Google 账户。';
    document.querySelectorAll('[data-cloud-email]').forEach(el=>{el.textContent=next?.email||'';});
    document.querySelectorAll('[data-cloud-login]').forEach(el=>{el.textContent=el.dataset.cloudLabel||(next?'进入我的数据':'Google 登录');});
    for(const fn of listeners)fn(next);
  }
  async function login() {
    await ready;
    if(user)return user;
    const provider=new firebase.auth.GoogleAuthProvider();
    provider.setCustomParameters({prompt:'select_account'});
    return (await auth.signInWithPopup(provider)).user;
  }
  async function logout(){await ready;await auth.signOut();location.replace(root.href);}
  function requireUser(){if(!user)throw new Error('请先登录 Google 账户。');return user;}
  async function firestore(path,{method='GET',body,optional=false}={}) {
    await ready;const current=requireUser(),uid=current.uid;
    if(!(path.startsWith('users/'+uid+'/')||path==='users/'+uid+':runQuery')||path.split(/[/?]/).some(p=>p==='..'||p==='.'))throw new Error('禁止跨账户读取。');
    if(method!=='GET'&&!(method==='POST'&&path.endsWith(':runQuery')))throw new Error('网页仅支持读取云端记录。');
    const token=await current.getIdToken();
    const response=await fetch('https://firestore.googleapis.com/v1/projects/'+config.projectId+'/databases/(default)/documents/'+path,{
      method,headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{}),signal:AbortSignal.timeout(30000)
    });
    if(user?.uid!==uid)throw new Error('账户已切换，请重新加载。');
    if(optional&&response.status===404)return null;
    const data=await response.json();
    if(!response.ok)throw new Error(data.error?.status==='PERMISSION_DENIED'?'当前账户无权读取这些云端记录。':data.error?.message||'云端读取失败。');
    return data;
  }
  async function collection(path) {
    const result=[];let pageToken='';
    do {const separator=path.includes('?')?'&':'?';const data=await firestore(path+separator+'pageSize=500'+(pageToken?'&pageToken='+encodeURIComponent(pageToken):''));result.push(...(data.documents||[]));pageToken=data.nextPageToken||'';}while(pageToken);
    return result;
  }
  function decode(value) {
    if(!value)return null;
    if('stringValue'in value)return value.stringValue;
    if('timestampValue'in value)return value.timestampValue;
    if('integerValue'in value)return Number(value.integerValue);
    if('doubleValue'in value)return value.doubleValue;
    if('booleanValue'in value)return value.booleanValue;
    if('nullValue'in value)return null;
    if(value.arrayValue)return(value.arrayValue.values||[]).map(decode);
    if(value.mapValue)return Object.fromEntries(Object.entries(value.mapValue.fields||{}).map(([k,v])=>[k,decode(v)]));
    return null;
  }
  function documentData(doc){return Object.fromEntries(Object.entries(doc?.fields||{}).map(([k,v])=>[k,decode(v)]));}
  window.CloudAuth={root,ready,login,logout,firestore,collection,documentData,requireUser,friendly,
    get user(){return user;},onUser(fn){listeners.add(fn);ready.then(()=>fn(user)).catch(()=>{});return()=>listeners.delete(fn);}};
  (async()=>{
    config=await (await fetch(new URL('firebase-client.json',root))).json();
    firebase.initializeApp(config);
    auth=firebase.auth();
    await auth.setPersistence(firebase.auth.Auth.Persistence.LOCAL);
    let initial=true;
    auth.onAuthStateChanged(next=>{notify(next);if(initial){initial=false;resolveReady(next);}},error=>{rejectReady(error);});
    document.querySelectorAll('[data-cloud-login]').forEach(button=>button.addEventListener('click',async()=>{
      button.disabled=true;
      try{await login();location.assign(new URL('app/',root).href);}
      catch(error){const out=document.getElementById('auth-message');if(out)out.textContent=friendly(error);}
      finally{button.disabled=false;}
    }));
    document.querySelectorAll('[data-cloud-logout]').forEach(button=>button.addEventListener('click',()=>logout()));
  })().catch(error=>{rejectReady(error);const out=document.getElementById('auth-message');if(out)out.textContent=friendly(error);});
})();
