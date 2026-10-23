'use strict';
(() => {
  const $=id=>document.getElementById(id),client=window.softTrace,{api,localDate,duration,escape:esc,color}=client;
  const titles={day:'每日记录',yesterday:'昨日记录',week:'每周概览',month:'每月概览',quarter:'季度概览',year:'年度概览','7days':'近 7 天','30days':'近 30 天','3months':'近一季度','12months':'近一年',all:'全部时间',custom:'自定义范围'};
  const currentLabels={day:'今天',yesterday:'今天',week:'本周',month:'本月',quarter:'本季度',year:'今年','7days':'截至今天','30days':'截至今天','3months':'截至今天','12months':'截至今天',all:'全部时间'};
  const isDay=()=>['day','yesterday'].includes($('view-mode').value);
  let buckets=[],distribution=[],distributionTotal=0,trendMode='day';
  let softwareApps=[],softwarePage=0,softwareContext='';
  const softwareSort=window.softTraceTableSort.bind(document.querySelector('#software-panel table'),
    ['app','process','seconds','share'],()=>{softwarePage=0;renderSoftwareRows();},'使用时长逆序');
  function range(){
    const start=new Date(`${$('date').value}T00:00:00`),mode=$('view-mode').value;
    if(mode==='custom'){
      const from=new Date(`${$('range-from').value}T00:00:00`),end=new Date(`${$('range-to').value}T00:00:00`);
      if(!Number.isFinite(+from)||!Number.isFinite(+end)||end<from)throw new Error('请选择有效的日期范围，结束日期不能早于开始日期。');
      if(localDate(end)>localDate(new Date()))throw new Error('结束日期不能晚于今天。');
      end.setDate(end.getDate()+1);return {start:from,end,mode};
    }
    if(mode==='week')start.setDate(start.getDate()-(start.getDay()+6)%7);
    if(mode==='month')start.setDate(1);
    if(mode==='quarter')start.setMonth(Math.floor(start.getMonth()/3)*3,1);
    if(mode==='year')start.setMonth(0,1);
    const end=new Date(start);
    if(mode==='week')end.setDate(end.getDate()+7);
    else if(mode==='month'||mode==='quarter')end.setMonth(end.getMonth()+(mode==='quarter'?3:1));
    else if(mode==='year')end.setFullYear(end.getFullYear()+1);
    else end.setDate(end.getDate()+1);
    if(mode==='7days'||mode==='30days')start.setDate(start.getDate()-(mode==='7days'?6:29));
    if(mode==='3months'||mode==='12months'){
      const day=start.getDate();start.setDate(1);start.setMonth(start.getMonth()-(mode==='3months'?3:12));start.setDate(Math.min(day,new Date(start.getFullYear(),start.getMonth()+1,0).getDate()));
    }
    return {start,end,mode};
  }
  function setMode(){
    const mode=$('view-mode').value,daily=isDay(),all=mode==='all',custom=mode==='custom';
    document.querySelectorAll('.day-view-only').forEach(element=>element.hidden=!daily);
    $('period-content').hidden=daily;
    if(daily)$('usage-patterns').hidden=true;
    $('page-title').textContent=titles[mode];
    document.title=`SoftTrace · ${titles[mode]}`;
    $('page-description').textContent=daily?'回到某个时刻，看看当时在做什么。':'从更长的时间范围，了解电脑与软件的使用习惯。';
    for(const id of ['previous','next','date-picker','today'])$(id).hidden=all||custom;
    $('custom-range').hidden=!custom;
    $('today').textContent=currentLabels[mode]||'今天';
  }
  function renderTrend(data,mode){
    trendMode=mode;
    const monthly=mode==='year'||data.days.length>100;
    if(monthly){
      const groups=new Map();
      for(const day of data.days){const key=day.date.slice(0,7);const item=groups.get(key)||{date:key+'-01',seconds:0,monthly:true};item.seconds+=day.activeSeconds;groups.set(key,item);}
      if(mode==='year'){
        const selected=range(),today=localDate(new Date());
        buckets=Array.from({length:12},(_,i)=>{const date=new Date(selected.start);date.setMonth(i,1);const key=localDate(date);return {date:key,seconds:groups.get(key.slice(0,7))?.seconds||0,monthly:true,future:key.slice(0,7)>today.slice(0,7)};});
      }else buckets=[...groups.values()];
    }else if(['week','month','quarter'].includes(mode)){
      const selected=range(),days=new Map(data.days.map(day=>[day.date,day.activeSeconds])),today=localDate(new Date());
      buckets=[];
      for(const date=new Date(selected.start);date<selected.end;date.setDate(date.getDate()+1)){
        const key=localDate(date);buckets.push({date:key,seconds:days.get(key)||0,monthly:false,future:key>today});
      }
    }else buckets=data.days.map(day=>({date:day.date,seconds:day.activeSeconds,monthly:false}));
    $('trend-heading').textContent=monthly?'每月使用时长':'每日使用时长';
    $('trend-description').textContent=monthly?'点击月份查看月视图。':'点击日期回到当天时间轴与明细。';
    const max=Math.max(3600,...buckets.map(bucket=>bucket.seconds));
    const ticks=Array.from({length:5},(_,i)=>`<div class="trend-grid-line" style="bottom:${i*25}%"><span>${(max/3600*i/4).toFixed(1)}h</span></div>`).join('');
    const bars=buckets.map((bucket,i)=>{
      const label=bucket.monthly?bucket.date.slice(0,7):bucket.date;
      const title=`${label} · ${bucket.future?'尚未到该日期':duration(bucket.seconds)}`;
      return `<button class="trend-bar" data-bucket="${i}" ${bucket.future?'disabled':''} style="left:${(i+.1)/Math.max(1,buckets.length)*100}%;width:${80/Math.max(1,buckets.length)}%;height:${bucket.future?0:Math.max(bucket.seconds?1:.3,bucket.seconds/max*100)}%" title="${esc(title)}" aria-label="${esc(title)}"></button>`;
    }).join('');
    $('period-chart').innerHTML=`<div class="trend-plot">${ticks}${bars}</div><div class="trend-labels"></div><div class="trend-periods"></div>`;
    $('period-chart').dataset.mode=mode;
    renderTrendLabels();
  }
  function renderTrendLabels(){
    const target=$('period-chart').querySelector('.trend-labels');if(!target||!buckets.length)return;
    const width=target.clientWidth;if(!width)return;
    const week=['week','7days'].includes(trendMode),month=['month','30days'].includes(trendMode),year=trendMode==='year',monthly=buckets[0].monthly;
    const minimum=month?22:year?32:monthly?56:46;
    const required=minimum*buckets.length/width;
    const steps=['quarter','3months'].includes(trendMode)?[7,14,21,28,35,42,56,84]:monthly?[1,2,3,6,12,24,36,60,120]:[1,2,3,5,7,10,14,21,28,35,56,84];
    const step=week?1:steps.find(value=>value>=required)||Math.ceil(required);
    target.dataset.step=step;
    const indices=[];for(let i=0;i<buckets.length;i+=step)indices.push(i);
    target.innerHTML=indices.map(i=>{
      const bucket=buckets[i],date=new Date(`${bucket.date}T00:00:00`);
      const label=monthly?(year?`${date.getMonth()+1}月`:bucket.date.slice(0,7).replace('-','/')):month?String(date.getDate()):`${bucket.date.slice(5).replace('-','/')}${week&&width>=560?' '+['周日','周一','周二','周三','周四','周五','周六'][date.getDay()]:''}`;
      return `<span data-bucket-index="${i}" class="${bucket.future?'future':''}" style="left:${(i+.5)/buckets.length*100}%" title="${esc(bucket.monthly?bucket.date.slice(0,7):bucket.date)}">${esc(label)}</span>`;
    }).join('');
    const groups=[];
    for(let i=0;i<buckets.length;i++){
      const key=buckets[i].date.slice(0,monthly?4:7),last=groups.at(-1);
      if(last?.key===key)last.end=i+1;else groups.push({key,start:i,end:i+1});
    }
    $('period-chart').querySelector('.trend-periods').innerHTML=groups.map(group=>`<span style="left:${group.start/buckets.length*100}%;width:${(group.end-group.start)/buckets.length*100}%" title="${group.key}">${monthly?group.key+'年':group.key.replace('-','年')+'月'}</span>`).join('');
  }
  function highlightDistribution(index,softwareIndex=null){
    const app=softwareIndex===null?null:softwareApps[softwareIndex];
    const item=app?{name:app.appName,seconds:app.seconds}:distribution[index];
    $('period-donut').classList.toggle('has-selection',!!item);
    $('software-stack').classList.toggle('has-selection',!!item);
    document.querySelectorAll('[data-distribution]').forEach(element=>element.classList.toggle('highlight',!!item&&Number(element.dataset.distribution)===index&&(softwareIndex===null||!element.matches('tr')||Number(element.dataset.softwareIndex)===softwareIndex)));
    $('donut-total').textContent=duration(item?item.seconds:distributionTotal);
    $('donut-label').textContent=item?item.name:'总计';
    $('donut-label').title=item?item.name:'';
    $('stack-summary').textContent=item?`${item.name} · ${duration(item.seconds)} · ${distributionTotal?(item.seconds/distributionTotal*100).toFixed(1):'0.0'}%`:`总计 ${duration(distributionTotal)}`;
  }
  function renderSoftware(data){
    const context=[$('view-mode').value,$('date').value,$('device').value,$('range-from').value,$('range-to').value].join('|');
    if(context!==softwareContext){softwarePage=0;softwareContext=context;}
    softwareApps=[...data.apps].sort((a,b)=>b.seconds-a.seconds);
    data={...data,apps:softwareApps};
    const total=data.apps.reduce((sum,app)=>sum+app.seconds,0),key=app=>`${app.appName}\u0000${app.processName}`;
    const legend=data.apps.slice(0,9).map(app=>({name:app.appName,seconds:app.seconds,color:color(key(app))}));
    if(data.apps.length>9)legend.push({name:'Others',seconds:data.apps.slice(9).reduce((sum,app)=>sum+app.seconds,0),color:color('__others__')});
    distribution=legend;distributionTotal=total;
    let position=0;
    $('period-donut').querySelector('svg').innerHTML='<circle class="donut-track" cx="100" cy="100" r="78"/>'+legend.map((app,i)=>{
      const share=total?app.seconds/total*100:0,start=position;position+=share;
      if(!share)return '';
      return `<circle class="donut-arc" data-distribution="${i}" cx="100" cy="100" r="78" pathLength="100" stroke="${app.color}" stroke-dasharray="${share} ${100-share}" stroke-dashoffset="${-start}" transform="rotate(-90 100 100)" tabindex="0" role="img" aria-label="${esc(app.name)} · ${duration(app.seconds)} · ${share.toFixed(1)}%"><title>${esc(app.name)} · ${duration(app.seconds)} · ${share.toFixed(1)}%</title></circle>`;
    }).join('');
    $('software-stack').innerHTML=legend.filter(app=>app.seconds>0).map(app=>{
      const index=legend.indexOf(app),share=total?app.seconds/total*100:0,title=`${app.name} · ${duration(app.seconds)} · ${share.toFixed(1)}%`;
      return `<button type="button" class="stack-segment" data-distribution="${index}" style="flex-grow:${app.seconds};background:${app.color}" title="${esc(title)}" aria-label="${esc(title)}"></button>`;
    }).join('');
    highlightDistribution(-1);
    $('software-total').textContent=`${duration(total)} · ${data.apps.length.toLocaleString()} 个软件`;
    $('software-caption').textContent=isDay()?'点击软件行可筛选上方的逐段明细。':'所选时间范围的软件使用分布与明细。';
    $('software-panel').dataset.mode=$('view-mode').value;
    renderSoftwareRows();
  }
  function renderSoftwareRows(){
    const size=$('software-page-size').value==='all'?Math.max(1,softwareApps.length):Number($('software-page-size').value),pages=Math.max(1,Math.ceil(softwareApps.length/size)),daily=isDay();
    softwarePage=Math.max(0,Math.min(softwarePage,pages-1));
    const ranks=new Map(softwareApps.map((app,index)=>[app,index]));
    const sorted=softwareSort.order(softwareApps,(app,key)=>key==='app'?app.appName:key==='process'?app.processName:app.seconds,(a,b)=>b.seconds-a.seconds);
    $('period-apps').innerHTML=sorted.slice(softwarePage*size,(softwarePage+1)*size).map(app=>{
      const index=ranks.get(app),ringIndex=Math.min(index,9),key=`${app.appName}\u0000${app.processName}`,selected=daily&&client.selectedApp()===key;
      return `<tr tabindex="0" data-software-index="${index}" data-distribution="${ringIndex}" ${daily?`data-software-app="${index}"`:''} class="${selected?'selected':''}"><td title="${esc(app.appName)}"><span class="app-cell"><i class="dot" style="background:${distribution[ringIndex]?.color||color(key)}"></i><span class="app-label">${esc(app.appName)}</span></span></td><td title="${esc(app.processName)}">${esc(app.processName)}</td><td>${duration(app.seconds)}</td><td>${distributionTotal?(app.seconds/distributionTotal*100).toFixed(1):'0.0'}%</td></tr>`;
    }).join('')||'<tr><td colspan="4" class="empty">此范围没有软件使用记录。</td></tr>';
    $('software-page-label').textContent=`第 ${softwarePage+1} / ${pages} 页`;
    $('software-prev').disabled=softwarePage===0;$('software-next').disabled=softwarePage>=pages-1;
    highlightDistribution(-1);
  }
  async function load(status,revision){
    setMode();
    if(!status.features?.overview)throw new Error('请更新托盘程序后使用周期视图。');
    const selected=range(),query=new URLSearchParams({device:$('device').value,zone:Intl.DateTimeFormat().resolvedOptions().timeZone});
    if(selected.mode!=='all'){query.set('start',selected.start.toISOString());query.set('end',selected.end.toISOString());}
    const data=await api(`/api/overview?${query}`);
    if(revision!==client.revision())return;
    $('period-active').textContent=duration(data.activeSeconds);
    $('period-average').textContent=duration(data.activeDays?data.activeSeconds/data.activeDays:0);
    $('period-days').textContent=`${data.activeDays} 天`;
    $('period-app-count').textContent=`${data.apps.length} 个`;
    const quarter=selected.mode==='quarter'?`${selected.start.getFullYear()} 年第 ${Math.floor(selected.start.getMonth()/3)+1} 季度 · `:'';
    $('period-range').textContent=data.days.length?`${quarter}${data.days[0].date} — ${data.days.at(-1).date}`:'暂无记录';
    $('account-updated').textContent=`数据更新于 ${new Date(status.nowUtc).toLocaleTimeString('zh-CN',{hour12:false})}`;
    $('next').disabled=selected.mode==='all'||selected.end>new Date();
    document.dispatchEvent(new Event('softtrace-dates'));
    renderTrend(data,selected.mode);renderSoftware(data);
    window.softTracePatterns?.render(data.patterns,selected.mode,data.days.length);
    $('error').hidden=true;
  }
  $('period-chart').onclick=event=>{
    const button=event.target.closest('[data-bucket]');if(!button)return;
    const bucket=buckets[Number(button.dataset.bucket)];
    if(bucket.monthly){$('view-mode').value='month';$('date').value=bucket.date;setMode();client.reloadView();}
    else client.openDay(bucket.date);
  };
  for(const id of ['period-donut','software-stack','period-apps']){
    const root=$(id);
    for(const name of ['pointerover','focusin'])root.addEventListener(name,event=>{const item=event.target.closest('[data-distribution]');if(item)highlightDistribution(Number(item.dataset.distribution),item.hasAttribute('data-software-index')?Number(item.dataset.softwareIndex):null);});
    for(const name of ['pointerleave','focusout'])root.addEventListener(name,()=>{const focused=document.activeElement?.closest('[data-distribution]');highlightDistribution(focused?Number(focused.dataset.distribution):-1,focused?.hasAttribute('data-software-index')?Number(focused.dataset.softwareIndex):null);});
  }
  new ResizeObserver(renderTrendLabels).observe($('period-chart'));
  $('software-page-size').onchange=()=>{softwarePage=0;renderSoftwareRows();};
  $('software-prev').onclick=()=>{softwarePage--;renderSoftwareRows();};
  $('software-next').onclick=()=>{softwarePage++;renderSoftwareRows();};
  function selectSoftware(event){const row=event.target.closest('[data-software-app]');if(!row)return;const app=softwareApps[Number(row.dataset.softwareApp)];client.filterApp(`${app.appName}\u0000${app.processName}`);}
  $('period-apps').onclick=selectSoftware;
  $('period-apps').onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();selectSoftware(event);}};
  function setSoftwareLayout(layout){
    const stacked=layout==='stacked';$('software-panel').dataset.layout=stacked?'stacked':'donut';
    $('period-donut').hidden=stacked;$('software-stack').hidden=!stacked;$('stack-summary').hidden=!stacked;
    document.querySelectorAll('[data-software-layout]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.softwareLayout===(stacked?'stacked':'donut'))));
    highlightDistribution(-1);
  }
  document.querySelectorAll('[data-software-layout]').forEach(button=>button.onclick=()=>{setSoftwareLayout(button.dataset.softwareLayout);localStorage.setItem('softtrace-software-layout',button.dataset.softwareLayout);});
  setSoftwareLayout(localStorage.getItem('softtrace-software-layout'));
  client.overview={load,setMode,range,isDay,renderSoftware};setMode();
})();
