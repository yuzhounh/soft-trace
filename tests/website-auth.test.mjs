import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
test('the website client rejects cross-account and mutation requests before network access',async()=>{
  let calls=0;
  const user={uid:'owner-a',email:'test@example.invalid',getIdToken:async()=>'test-token'};
  const auth={setPersistence:async()=>{},onAuthStateChanged(fn){fn(user);}};
  const authFunction=()=>auth;authFunction.Auth={Persistence:{LOCAL:'local'}};
  const document={currentScript:{src:'https://website.invalid/cloud-auth.js'},body:{classList:{toggle(){}}},getElementById(){return null;},querySelectorAll(){return[];}};
  const context=vm.createContext({URL,Promise,Set,Map,AbortSignal,document,window:{},firebase:{initializeApp(){},auth:authFunction},fetch:async url=>{calls++;return{json:async()=>({projectId:'test'}),ok:true,status:200};}});
  vm.runInContext(await readFile(new URL('../site/cloud-auth.js',import.meta.url),'utf8'),context);
  const api=context.window.CloudAuth;await api.ready;
  const baseline=calls;
  await assert.rejects(api.firestore('users/owner-b/activities'),/跨账户/);
  await assert.rejects(api.firestore('users/owner-a/activities',{method:'PATCH',body:{}}),/仅支持读取/);
  assert.equal(calls,baseline);
  await api.firestore('users/owner-a/devices');assert.equal(calls,baseline+1);
});
