import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
test('account settings initialize after the deferred authentication scripts',async()=>{
 const nodes=new Map(),events=new Map();
 const node=id=>{if(!nodes.has(id))nodes.set(id,{});return nodes.get(id);};
 const context=vm.createContext({window:{softTrace:{}},document:{getElementById:node,querySelectorAll:()=>[],addEventListener:(name,fn)=>events.set(name,fn)}});
 vm.runInContext(await readFile(new URL('../site/app/management.js',import.meta.url),'utf8'),context);
 context.CloudAuth={user:{displayName:'Reader',email:'test@example.invalid'},onUser:fn=>fn()};
 events.get('DOMContentLoaded')();
 assert.equal(node('account-email').textContent,'test@example.invalid');
 assert.equal(node('account-login').hidden,true);
});
