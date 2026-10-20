import {test} from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
test('current time and device changes reuse a range; refresh stays within its date bounds',async()=>{
 let now=Date.UTC(2026,9,20,12),requests=0;
 class Clock extends Date{constructor(...args){super(...(args.length?args:[now]));}static now(){return now;}}
 const context=vm.createContext({window:{},location:{origin:'https://website.invalid'},Date:Clock,Map,Set,JSON,URL,Response,
  testModel:{normalizeActivity:d=>d.data,timeline:rows=>({count:rows.length}),overview:rows=>({count:rows.length})},
  CloudAuth:{ready:Promise.resolve(),requireUser:()=>({uid:'u'}),onUser(){},documentData(){},
   async firestore(path,{body}){requests++;const filters=body.structuredQuery.where.compositeFilter.filters,field=filters[0].fieldFilter.field.fieldPath;
    assert.ok(['endUtc','stateEndUtc'].includes(field));assert.equal(filters[0].fieldFilter.value.timestampValue,'2026-10-20T00:00:00.000Z');
    assert.equal(filters[1].fieldFilter.field.fieldPath,'startUtc');assert.ok(filters[1].fieldFilter.value.timestampValue<='2026-10-21T00:00:00.000Z');
    return field!=='stateEndUtc'?[{document:{name:'record',data:{id:'record',deviceId:'a',startUtc:'2026-10-20T01:00:00Z',endUtc:'2026-10-20T02:00:00Z'}}}]:[];}}});
 const source=(await readFile(new URL('../site/app/cloud-bridge.js',import.meta.url),'utf8')).replace("import('./model.mjs')",'Promise.resolve(testModel)');
 vm.runInContext(source,context);
 const a=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await a.json()).count,1);now+=10000;
 const b=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=b');
 assert.equal((await b.json()).count,0);assert.equal(requests,2);now+=180000;
 const c=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await c.json()).count,1);assert.equal(requests,4);
 context.window.SoftTraceWeb.invalidate();
 const d=await context.window.SoftTraceWeb.request('/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z&device=a');
 assert.equal((await d.json()).count,1);assert.equal(requests,6);
});

async function client(){
 let now=Date.UTC(2026,9,20,12),user={uid:'u'},notify,fail=false;
 const queries=[];
 class Clock extends Date{constructor(...args){super(...(args.length?args:[now]));}static now(){return now;}}
 const context=vm.createContext({window:{},location:{origin:'https://website.invalid'},Date:Clock,Map,Set,JSON,URL,Response,
  testModel:{normalizeActivity:d=>d.data,timeline:(rows,start,end)=>({count:rows.length,start,end}),overview:(rows,start,end)=>({count:rows.length,start,end})},
  CloudAuth:{ready:Promise.resolve(),requireUser:()=>user,onUser:fn=>{notify=fn;},documentData(){},async firestore(path,{body}){queries.push({path,...body.structuredQuery});if(fail)throw Error('Quota exceeded');return[];}}});
 const source=(await readFile(new URL('../site/app/cloud-bridge.js',import.meta.url),'utf8')).replace("import('./model.mjs')",'Promise.resolve(testModel)');
 vm.runInContext(source,context);
 return{queries,request:path=>context.window.SoftTraceWeb.request(path),invalidate:()=>context.window.SoftTraceWeb.invalidate(),advance:ms=>{now+=ms;},fail:value=>{fail=value;},switchAccount:()=>{user={uid:'other'};notify(user);}};
}

test('missing range defaults to today; all-time requires an explicit selection',async()=>{
 const c=await client(),response=await c.request('/api/day');assert.equal(response.status,200);
 const data=await response.json(),today=new Date(Date.UTC(2026,9,20,12));today.setHours(0,0,0,0);
 assert.equal(data.start,today.toISOString());assert.equal(c.queries.length,2);
 for(const query of c.queries)assert.equal(query.where.compositeFilter.filters[0].fieldFilter.value.timestampValue,today.toISOString());
 await c.request('/api/overview');assert.equal(c.queries.length,2);
 await c.request('/api/overview?all=1');assert.equal(c.queries.length,3);
 assert.equal(c.queries.at(-1).where.fieldFilter.field.fieldPath,'startUtc');
 assert.equal(c.queries.at(-1).where.fieldFilter.op,'LESS_THAN');
});

test('overlapping ranges fetch only missing intervals and concurrent identical requests share reads',async()=>{
 const c=await client(),day='/api/day?start=2026-10-19T00:00:00Z&end=2026-10-20T00:00:00Z';
 await Promise.all([c.request(day),c.request(day)]);assert.equal(c.queries.length,2);
 await c.request('/api/overview?start=2026-10-18T00:00:00Z&end=2026-10-20T00:00:00Z');assert.equal(c.queries.length,4);
 for(const query of c.queries.slice(2)){
  const filters=query.where.compositeFilter.filters;
  assert.equal(filters[0].fieldFilter.value.timestampValue,'2026-10-18T00:00:00.000Z');
  assert.equal(filters[1].fieldFilter.value.timestampValue,'2026-10-19T00:00:00.000Z');
 }
 await c.request(day);assert.equal(c.queries.length,4);
 c.switchAccount();await c.request(day);assert.equal(c.queries.length,6);assert.ok(c.queries.slice(4).every(q=>q.path.startsWith('users/other:')));
});

test('a partially loaded current day is refreshed after midnight; failed loads can be retried',async()=>{
 const c=await client(),day='/api/day?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z';
 await c.request(day);assert.equal(c.queries.length,2);
 c.advance(24*3600000);await c.request(day);assert.equal(c.queries.length,4);
 c.invalidate();c.fail(true);assert.equal((await c.request(day)).status,400);
 c.fail(false);assert.equal((await c.request(day)).status,200);
});
