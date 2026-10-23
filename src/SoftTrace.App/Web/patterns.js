'use strict';
(() => {
  const $=id=>document.getElementById(id),esc=window.softTrace.escape;
  let summary=null,available=[],active='hourly';
  const number=value=>Number(value).toLocaleString('zh-CN',{maximumFractionDigits:1});
  const hasValue=value=>value!==null&&value!==undefined&&Number.isFinite(value);
  const hourLabel=hour=>`${String(hour).padStart(2,'0')}:00–${String(hour+1).padStart(2,'0')}:00`;
  const average=value=>hasValue(value)?`${number(value)} 小时`:'无记录';
  // Derive from totals so refreshed resources also correct older local servers.
  const hourAverage=hour=>summary.recordedDays>0?hour.activeSeconds/summary.recordedDays/60:null;
  const hourDetail=hour=>`${hourLabel(hour.hour)}：${hasValue(hourAverage(hour))?`日均 ${number(hourAverage(hour))} 分钟；按 ${summary.recordedDays} 个有记录日平均，该小时有记录 ${hour.recordedDays} 天`:'无记录'}`;
  function eligibility(data,mode,days){
    if(!data||['day','yesterday'].includes(mode)||days<2||data.recordedDays<2)return [];
    const result=['hourly'];
    if(days>=7&&data.weekdays.filter(day=>day.recordedDays>0).length>=2)result.push('weekday');
    const yearView=['year','12months'].includes(mode)||(['all','custom'].includes(mode)&&days>=365);
    if(yearView&&data.academicPeriods.filter(period=>period.kind!=='unknown'&&period.recordedDays>0).length>=2)result.push('academic');
    return result;
  }
  function render(data,mode,days){
    summary=data;available=eligibility(data,mode,days);
    $('usage-patterns').hidden=available.length===0;
    if(!available.length)return;
    if(!available.includes(active))active=available[0];
    $('patterns-coverage').textContent=`${data.recordedDays} 个有记录日 · ${data.missingDays} 天无记录 · ${data.timeZone}`;
    for(const button of document.querySelectorAll('[data-pattern-tab]')){
      button.hidden=!available.includes(button.dataset.patternTab);
      button.setAttribute('aria-selected',String(button.dataset.patternTab===active));
      button.tabIndex=button.dataset.patternTab===active?0:-1;
    }
    $('pattern-content').setAttribute('aria-labelledby',`pattern-tab-${active}`);
    if(active==='hourly')renderHours();
    else if(active==='weekday')renderWeekdays();
    else renderAcademic();
  }
  function renderHours(){
    const maximum=Math.max(1,...summary.hours.map(hour=>hourAverage(hour)||0));
    $('pattern-description').textContent='24 个小时统一按当天有任何记录的日期计算日均分钟数；这些日期中未使用的小时按 0 计入，整天无记录的日期不参与。跨小时记录拆分，空闲和锁屏不计入使用时长。';
    $('pattern-content').innerHTML=`<div class="pattern-hour-scroll"><div class="pattern-hour-chart"><div class="pattern-axis"><span>${number(maximum)} 分钟</span><span>${number(maximum/2)}</span><span>0</span></div><div class="pattern-hours">${summary.hours.map(hour=>{
      const value=hourAverage(hour);
      const title=hourDetail(hour);
      return `<button type="button" class="pattern-hour${hasValue(value)?'':' missing'}" data-pattern-hour="${hour.hour}" title="${esc(title)}" aria-label="${esc(title)}"><span class="pattern-column"><i style="height:${hasValue(value)?Math.max(0,value/maximum*100):0}%"></i><em>${hasValue(value)?number(value):'—'}</em></span><span class="pattern-hour-label">${String(hour.hour).padStart(2,'0')}</span></button>`;
    }).join('')}</div></div></div><p id="pattern-hour-detail" class="pattern-detail" aria-live="polite"></p>`;
    const peak=summary.hours.filter(hour=>hourAverage(hour)>0).sort((a,b)=>hourAverage(b)-hourAverage(a))[0];
    $('pattern-hour-detail').textContent=peak?`使用较多的时段：${hourDetail(peak)}。`:'该范围内未记录到实际使用时长。';
  }
  function rows(items){
    const maximum=Math.max(1,...items.map(item=>item.averageHours||0));
    return `<div class="pattern-comparisons">${items.map(item=>`<div class="pattern-comparison"><span>${esc(item.label)}</span><span class="pattern-track"><i style="width:${hasValue(item.averageHours)?Math.max(0,item.averageHours/maximum*100):0}%"></i></span><strong>${average(item.averageHours)}</strong><small>${item.recordedDays} 个记录日</small></div>`).join('')}</div>`;
  }
  function renderWeekdays(){
    $('pattern-description').textContent='按各星期中当天有任何记录的日期计算日均小时数；有记录但未实际使用的日期按 0 计入，整天无记录的日期不参与。没有记录的星期显示“无记录”。';
    $('pattern-content').innerHTML=rows(summary.weekdays);
  }
  function renderAcademic(){
    if(!summary.calendarBasis?.includes('固定月份')){$('pattern-description').textContent='月份分组需更新本机程序后生效。';$('pattern-content').innerHTML='';return;}
    $('pattern-description').textContent='按固定月份分组，并按各组有记录日计算日均使用时长。';
    const groups=summary.academicPeriods.filter(period=>period.kind!=='unknown');
    $('pattern-content').innerHTML=rows(groups)+'<p class="pattern-detail">寒假：2 月；暑假：7–8 月；春季学期：3–6 月；秋季学期：9 月至次年 1 月。</p>';
  }
  for(const button of document.querySelectorAll('[data-pattern-tab]')){
    button.onclick=()=>{active=button.dataset.patternTab;render(summary,$('view-mode').value,window.softTracePatterns.span);};
    button.onkeydown=event=>{
      const index=available.indexOf(button.dataset.patternTab);
      const next={ArrowRight:(index+1)%available.length,ArrowLeft:(index+available.length-1)%available.length,Home:0,End:available.length-1}[event.key];
      if(next===undefined)return;
      event.preventDefault();active=available[next];render(summary,$('view-mode').value,window.softTracePatterns.span);
      document.querySelector(`[data-pattern-tab="${active}"]`).focus();
    };
  }
  $('pattern-content').onclick=event=>{
    const button=event.target.closest('[data-pattern-hour]');if(!button)return;
    const hour=summary.hours[Number(button.dataset.patternHour)];
    $('pattern-hour-detail').textContent=`${hourDetail(hour)}。`;
  };
  window.softTracePatterns={span:0,eligibility,render(data,mode,days){this.span=days;render(data,mode,days);}};
})();
