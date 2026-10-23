const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');

function setup(){
  const window={};
  const document={createElement:()=>({attrs:{},setAttribute(key,value){this.attrs[key]=value;}})};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../src/SoftTrace.App/Web/table-sort.js'),'utf8'),{window,document});
  return window.softTraceTableSort;
}

test('column clicks cycle default, ascending, descending and default; a new column starts ascending',()=>{
  const api=setup();let state={key:null,direction:0};
  state=api.next(state,'start');assert.equal(state.direction,1);
  state=api.next(state,'start');assert.equal(state.direction,-1);
  state=api.next(state,'start');assert.equal(state.direction,0);assert.equal(state.key,null);
  state=api.next(state,'start');state=api.next(state,'duration');
  assert.equal(state.key,'duration');assert.equal(state.direction,1);
});

test('detail default is newest start first even when its duration is shorter',()=>{
  const api=setup(),rows=[{start:1,seconds:100},{start:10,seconds:2},{start:3,seconds:50}];
  const defaults=(a,b)=>b.start-a.start;
  const ordered=api.order(rows,{direction:0},(row,key)=>row[key],defaults);
  assert.deepEqual(Array.from(ordered,row=>row.start),[10,3,1]);
  assert.deepEqual(Array.from(api.order(rows,{key:'seconds',direction:1},(row,key)=>row[key],defaults),row=>row.seconds),[2,50,100]);
  assert.deepEqual(Array.from(rows,row=>row.start),[1,10,3]);
});

test('software durations sort numerically and sorting preserves chart membership and original rank',()=>{
  const api=setup(),apps=[{seconds:100,name:'App 10'},{seconds:50,name:'App 2'},{seconds:2,name:'App 1'}];
  const ranks=new Map(apps.map((app,index)=>[app,index]));
  const ordered=api.order(apps,{key:'seconds',direction:1},(app,key)=>app[key],(a,b)=>b.seconds-a.seconds);
  assert.deepEqual(Array.from(ordered,app=>app.seconds),[2,50,100]);
  assert.deepEqual(Array.from(ordered,app=>ranks.get(app)),[2,1,0]);
  assert.deepEqual(apps.map(app=>app.seconds),[100,50,2]);
  assert.deepEqual(Array.from(api.order(apps,{key:'name',direction:1},(app,key)=>app[key],()=>0),app=>app.name),['App 1','App 2','App 10']);
});

test('default has no icon; explicit sorts show one arrow and reset other columns',()=>{
  const api=setup(),headers=['开始','持续时间'].map(textContent=>({textContent,attrs:{},
    setAttribute(key,value){this.attrs[key]=value;},replaceChildren(button){this.button=button;},querySelector(){return this.button;}}));
  let renders=0;
  const controller=api.bind({querySelectorAll:()=>headers},['start','seconds'],()=>renders++,'开始时间逆序');
  assert.ok(headers.every(header=>!header.button.innerHTML.includes('<svg')));
  headers[0].button.onclick();assert.equal(headers[0].attrs['aria-sort'],'ascending');assert.match(headers[0].button.innerHTML,/<svg/);
  headers[0].button.onclick();assert.equal(headers[0].attrs['aria-sort'],'descending');
  headers[0].button.onclick();assert.ok(!headers[0].button.innerHTML.includes('<svg'));
  headers[0].button.onclick();headers[1].button.onclick();
  assert.ok(!headers[0].button.innerHTML.includes('<svg'));assert.equal(headers[1].attrs['aria-sort'],'ascending');
  const rows=[{seconds:10},{seconds:2},{seconds:5}];
  assert.deepEqual(Array.from(controller.order(rows,(row,key)=>row[key],()=>0),row=>row.seconds),[2,5,10]);
  assert.equal(renders,5);
});
