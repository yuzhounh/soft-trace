'use strict';
(() => {
  const $=id=>document.getElementById(id),calendar=$('calendar');
  const localDate=window.softTrace.localDate;
  let input=null,anchor=null,month=new Date();
  function syncLabels(){document.querySelectorAll('[data-date-label]').forEach(label=>{const value=$(label.dataset.dateLabel).value;label.textContent=value?value.replaceAll('-',' / '):'选择日期';});}
  function draw(){
    const year=month.getFullYear(),m=month.getMonth(),first=new Date(year,m,1),offset=(first.getDay()+6)%7;
    $('calendar-month').textContent=`${year} 年 ${m+1} 月`;
    $('month-next').disabled=year===new Date().getFullYear()&&m>=new Date().getMonth();
    $('calendar-days').innerHTML=Array.from({length:42},(_,i)=>{
      const date=new Date(year,m,1-offset+i),value=localDate(date),future=value>localDate(new Date());
      return `<button data-calendar-date="${value}" class="${date.getMonth()!==m?'outside ':''}${value===input.value?'chosen':''}" aria-label="${date.getFullYear()}年${date.getMonth()+1}月${date.getDate()}日" aria-pressed="${value===input.value}"${future?' disabled':''}>${date.getDate()}</button>`;
    }).join('');
  }
  function choose(value){input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));syncLabels();calendar.hidePopover();anchor.focus();}
  document.addEventListener('click',event=>{
    const button=event.target.closest('[data-date-for]');if(!button||button.disabled)return;
    if(calendar.matches(':popover-open'))calendar.hidePopover();
    anchor=button;input=$(button.dataset.dateFor);month=new Date(`${input.value||localDate(new Date())}T12:00:00`);month.setDate(1);
    (button.closest('dialog')||document.body).appendChild(calendar);
    draw();calendar.showPopover();button.setAttribute('aria-expanded','true');
    const rect=button.getBoundingClientRect();
    calendar.style.left=`${Math.max(12,Math.min(rect.left,innerWidth-calendar.offsetWidth-12))}px`;
    calendar.style.top=`${rect.bottom+8+calendar.offsetHeight>innerHeight?Math.max(12,rect.top-calendar.offsetHeight-8):rect.bottom+8}px`;
    (calendar.querySelector('.chosen:not(:disabled)')||calendar.querySelector('[data-calendar-date]:not(:disabled)')).focus();
  });
  calendar.addEventListener('toggle',event=>{if(anchor&&event.newState==='closed')anchor.setAttribute('aria-expanded','false');});
  $('month-previous').onclick=()=>{month.setMonth(month.getMonth()-1);draw();};
  $('month-next').onclick=()=>{month.setMonth(month.getMonth()+1);draw();};
  $('calendar-days').onclick=event=>{const day=event.target.closest('[data-calendar-date]');if(day&&!day.disabled)choose(day.dataset.calendarDate);};
  $('calendar-today').onclick=()=>choose(localDate(new Date()));
  $('calendar-days').onkeydown=event=>{
    const day=event.target.closest('[data-calendar-date]');if(!day)return;
    const delta={ArrowLeft:-1,ArrowRight:1,ArrowUp:-7,ArrowDown:7}[event.key];if(!delta)return;
    event.preventDefault();const date=new Date(`${day.dataset.calendarDate}T12:00:00`);date.setDate(date.getDate()+delta);
    if(localDate(date)>localDate(new Date()))return;
    if(date.getMonth()!==month.getMonth()||date.getFullYear()!==month.getFullYear()){month=new Date(date.getFullYear(),date.getMonth(),1);draw();}
    calendar.querySelector(`[data-calendar-date="${localDate(date)}"]`)?.focus();
  };
  document.addEventListener('softtrace-dates',syncLabels);syncLabels();
})();
