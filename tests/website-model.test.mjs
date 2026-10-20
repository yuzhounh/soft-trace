import {test} from 'node:test';
import assert from 'node:assert/strict';
import {timeline,overview,normalizeActivity} from '../site/app/model.mjs';
const entry=(id,a,b,kind='application')=>({id,kind,appName:'Editor',processName:'editor',startUtc:a,endUtc:b,source:'test'});
test('daily totals count overlapping software once and retain unsampled gaps',()=>{
 const a='2026-10-20T00:00:00Z',b='2026-10-20T04:00:00Z';
 const result=timeline([entry('a','2026-10-20T00:00:00Z','2026-10-20T02:00:00Z'),entry('b','2026-10-20T01:00:00Z','2026-10-20T03:00:00Z')],a,b);
 assert.equal(result.activeSeconds,10800);assert.equal(result.states.at(-1).kind,'unknown');assert.equal(result.entries.length,2);
});
test('state rows use their real state end, including a midnight crossing',()=>{
 const e=normalizeActivity({name:'users/u/activities/x'},()=>({kind:'idle',startUtc:'2026-10-19T23:30:00Z',endUtc:'2026-10-19T23:30:00Z',stateStartUtc:'2026-10-19T23:30:00Z',stateEndUtc:'2026-10-20T00:30:00Z'}));
 assert.equal(timeline([e],'2026-10-20T00:00:00Z','2026-10-20T01:00:00Z').idleSeconds,1800);
});
test('periods retain empty dates without counting them as recorded days',()=>{
 const start=new Date(2026,9,20),end=new Date(2026,9,22),stop=new Date(2026,9,20,1);
 const result=overview([entry('x',start.toISOString(),stop.toISOString())],start,end);
 assert.equal(result.days.length,2);assert.equal(result.activeDays,1);assert.equal(result.patterns.recordedDays,1);assert.equal(result.patterns.missingDays,1);assert.equal(result.patterns.hours.reduce((n,h)=>n+h.activeSeconds,0),3600);
});
