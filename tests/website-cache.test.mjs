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

async function client({rowLimit=50000,rows=[]}={}){
 let now=Date.UTC(2026,9,20,12),user={uid:'u'},notify,fail=false;
 const queries=[];let overviews=0;
 class Clock extends Date{constructor(...args){super(...(args.length?args:[now]));}static now(){return now;}}
 const context=vm.createContext({window:{},location:{origin:'https://website.invalid'},Date:Clock,Map,Set,JSON,URL,Response,
  testModel:{normalizeActivity:d=>d.data,timeline:(rows,start,end)=>({count:rows.length,start,end}),overview:(rows,start,end)=>{overviews++;return{count:rows.length,start,end};}},
  CloudAuth:{ready:Promise.resolve(),requireUser:()=>user,onUser:fn=>{notify=fn;},documentData(){},async firestore(path,{body}){queries.push({path,...body.structuredQuery});if(fail)throw Error('Quota exceeded');return rows.map(data=>({document:{name:data.id,data}}));}}});
 const source=(await readFile(new URL('../site/app/cloud-bridge.js',import.meta.url),'utf8')).replace("import('./model.mjs')",'Promise.resolve(testModel)').replace('MAX_ROWS=50000',`MAX_ROWS=${rowLimit}`);
 vm.runInContext(source,context);
 return{queries,overviews:()=>overviews,request:path=>context.window.SoftTraceWeb.request(path),invalidate:()=>context.window.SoftTraceWeb.invalidate(),advance:ms=>{now+=ms;},fail:value=>{fail=value;},switchAccount:()=>{user={uid:'other'};notify(user);}};
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

test('range snapshots use LRU eviction and an oversized result is returned without retaining it',async()=>{
 const c=await client(),day=n=>`/api/day?start=2026-09-${String(n).padStart(2,'0')}T00:00:00Z&end=2026-09-${String(n+1).padStart(2,'0')}T00:00:00Z`;
 for(let i=1;i<=12;i++)await c.request(day(i));
 await c.request(day(1));await c.request(day(13));assert.equal(c.queries.length,26);
 await c.request(day(1));assert.equal(c.queries.length,26);
 await c.request(day(2));assert.equal(c.queries.length,28);
 const large=await client({rowLimit:2,rows:[1,2,3].map(id=>({id:String(id),startUtc:'2026-09-01T01:00:00Z',endUtc:'2026-09-01T02:00:00Z'}))});
 assert.equal((await(await large.request(day(1))).json()).count,3);
 assert.equal((await(await large.request(day(1))).json()).count,3);assert.equal(large.queries.length,4);
});

test('overview aggregation is reused until refresh or account change',async()=>{
 const c=await client(),path='/api/overview?start=2026-10-20T00:00:00Z&end=2026-10-21T00:00:00Z';
 const first=await(await c.request(path)).json();c.advance(15000);
 const second=await(await c.request(path)).json();assert.equal(first.dataVersion,second.dataVersion);assert.equal(c.overviews(),1);
 c.invalidate();const refreshed=await(await c.request(path)).json();assert.notEqual(first.dataVersion,refreshed.dataVersion);assert.equal(c.overviews(),2);
 c.switchAccount();const switched=await(await c.request(path)).json();assert.notEqual(refreshed.dataVersion,switched.dataVersion);assert.equal(c.overviews(),3);
});

test('unchanged overview skips chart rendering while updating status time; view revisions redraw',async()=>{
 const source=await readFile(new URL('../site/app/overview.js',import.meta.url),'utf8');
 const load=source.slice(source.indexOf('  async function load(status,revision){'),source.indexOf("  $('period-chart').onclick="));
 const elements=new Map(),element=id=>{if(!elements.has(id))elements.set(id,{});return elements.get(id);};element('device').value='a';
 let renders=0,revision=0;
 const context=vm.createContext({$:element,URLSearchParams,Intl,Date,JSON,Event,renderedVersion:'',setMode(){},range:()=>({mode:'week',start:new Date('2026-10-19'),end:new Date('2026-10-26')}),
  api:async()=>({dataVersion:'1',activeSeconds:1,activeDays:1,apps:[],days:[],patterns:{}}),client:{revision:()=>revision},duration:String,
  document:{dispatchEvent(){}},renderTrend(){renders++;},renderSoftware(){},window:{}});
 vm.runInContext(load,context);
 await context.load({features:{overview:true},nowUtc:'2026-10-20T01:00:00Z'},0);const before=element('account-updated').textContent;
 await context.load({features:{overview:true},nowUtc:'2026-10-20T02:00:00Z'},0);assert.equal(renders,1);assert.notEqual(element('account-updated').textContent,before);
 revision++;await context.load({features:{overview:true},nowUtc:'2026-10-20T02:00:00Z'},revision);assert.equal(renders,2);
});
