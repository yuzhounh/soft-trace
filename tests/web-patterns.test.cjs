const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
function dashboard(){
  const nodes=new Map();
  const get=id=>{
    if(!nodes.has(id))nodes.set(id,{hidden:false,value:'year',textContent:'',innerHTML:'',attributes:{},
      setAttribute(name,value){this.attributes[name]=value;},focus(){this.focused=true;}});
    return nodes.get(id);
  };
  const tabs=['hourly','weekday','academic'].map(kind=>Object.assign(get(`pattern-tab-${kind}`),{dataset:{patternTab:kind}}));
  const document={getElementById:get,querySelectorAll:()=>tabs,
    querySelector:selector=>tabs.find(tab=>selector.includes(`"${tab.dataset.patternTab}"`))};
  const window={softTrace:{escape:value=>String(value).replaceAll('<','&lt;')}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../src/SoftTrace.App/Web/patterns.js'),'utf8'),{document,window});
  return {api:window.softTracePatterns,get,tabs};
}
function summary(){
  return {recordedDays:2,missingDays:5,timeZone:'Asia/Shanghai',
    hours:Array.from({length:24},(_,hour)=>({hour,activeSeconds:hour===9?3600:0,averageMinutes:hour===9?30:null,recordedDays:hour===9?2:0})),
    weekdays:Array.from({length:7},(_,weekday)=>({weekday,label:`周${weekday}`,averageHours:weekday<2?1:null,recordedDays:weekday<2?1:0})),
    academicPeriods:['spring','autumn','winter','summer','unknown'].map((kind,index)=>({kind,label:kind,averageHours:index<2?1:null,recordedDays:index<2?1:0})),
    calendarBasis:'按固定月份分组',calendarSources:[]};
}
test('analysis availability follows actual range and recorded coverage',()=>{
  const {api}=dashboard(),data=summary();
  const eligible=(mode,days)=>Array.from(api.eligibility(data,mode,days));
  assert.deepEqual(eligible('day',1),[]);
  assert.deepEqual(eligible('custom',2),['hourly']);
  assert.deepEqual(eligible('week',6),['hourly']);
  assert.deepEqual(eligible('week',7),['hourly','weekday']);
  assert.deepEqual(eligible('quarter',90),['hourly','weekday']);
  assert.deepEqual(eligible('year',200),['hourly','weekday','academic']);
  assert.deepEqual(eligible('custom',364),['hourly','weekday']);
  assert.deepEqual(eligible('all',365),['hourly','weekday','academic']);
  data.recordedDays=1;
  assert.deepEqual(eligible('all',500),[]);
});
test('shorter range removes unavailable tabs and restores hourly content',()=>{
  const {api,get,tabs}=dashboard(),data=summary();
  api.render(data,'year',365);tabs[2].onclick();
  assert.match(get('pattern-content').innerHTML,/寒假：2 月/);
  assert.doesNotMatch(get('pattern-content').innerHTML,/信阳|xynu|官方校历/);
  get('view-mode').value='custom';api.render(data,'custom',2);
  assert.equal(tabs[2].hidden,true);assert.equal(tabs[1].hidden,true);
  assert.equal(tabs[0].attributes['aria-selected'],'true');
  assert.match(get('pattern-content').innerHTML,/该小时有记录 0 天/);
  assert.match(get('pattern-hour-detail').textContent,/09:00–10:00/);
  assert.equal(get('usage-patterns').hidden,false);
  api.render(undefined,'custom',2);assert.equal(get('usage-patterns').hidden,true);
});
test('unknown calendar days cannot enable academic comparison',()=>{
  const {api}=dashboard(),data=summary();
  data.academicPeriods.forEach(period=>period.recordedDays=period.kind==='spring'||period.kind==='unknown'?1:0);
  assert.deepEqual(Array.from(api.eligibility(data,'year',365)),['hourly','weekday']);
});

test('rare overnight use shares the daily denominator and cannot dominate the peak',()=>{
  const {api,get,tabs}=dashboard(),data=summary();
  data.recordedDays=100;
  Object.assign(data.hours[3],{activeSeconds:7*49.4*60,recordedDays:7,averageMinutes:49.4});
  Object.assign(data.hours[9],{activeSeconds:100*30*60,recordedDays:100,averageMinutes:30});
  api.render(data,'year',365);
  assert.match(get('pattern-hour-detail').textContent,/09:00–10:00：日均 30 分钟/);
  assert.match(get('pattern-content').innerHTML,/03:00–04:00：日均 3.5 分钟；按 100 个有记录日平均/);
  for(const tab of tabs){
    tab.onclick();
    if(tab.dataset.patternTab==='academic')assert.match(get('pattern-description').textContent,/各组有记录日/);
    else{assert.match(get('pattern-description').textContent,/当天有任何记录/);assert.match(get('pattern-description').textContent,/按 0 计入/);assert.match(get('pattern-description').textContent,/整天无记录的日期不参与/);}
  }
});
