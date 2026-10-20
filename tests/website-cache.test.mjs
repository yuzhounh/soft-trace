import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
test('advancing current time and switching devices reuse one selected-range read',async()=>{
 let now=Date.UTC(2026,9,20,12),requests=0;
 class Clock extends Date{constructor(...args){super(...(args.length?args:[now]));}static now(){return now;}}
 const context=vm.createContext({window:{},location:{origin:'https://website.invalid'},Date:Clock,Map,Set,JSON,URL,Response,
  testModel:{normalizeActivity:d=>d.data,timeline:rows=>({count:rows.length}),overview:rows=>({count:rows.length})},
  CloudAuth:{ready:Promise.resolve(),requireUser:()=>({uid:'u'}),onUser(){},documentData(){},
   async firestore(path,{body}){requests++;const field=body.structuredQuery.where.fieldFilter?.field.fieldPath||body.structuredQuery.where.compositeFilter.filters[0].fieldFilter.field.fieldPath;return field!=='stateEndUtc'?[{document:{name:'record',data:{id:'record',deviceId:'a',startUtc:'2026-10-20T01:00:00Z',endUtc:'2026-10-20T02:00:00Z'}}}]:[];}}});
 const source=(await readFile(new URL('../site/app/cloud-bridge.js',import.meta.url),'utf8')).replace("import('./model.mjs')",'Promise.resolve(testModel)');
 vm.runInContext(source,context);
 const a=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await a.json()).count,1);now+=10000;
 const b=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=b');
 assert.equal((await b.json()).count,0);assert.equal(requests,2);now+=180000;
 const c=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await c.json()).count,1);assert.equal(requests,3);
 context.window.SoftTraceWeb.invalidate();
 const d=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await d.json()).count,1);assert.equal(requests,4);
});
