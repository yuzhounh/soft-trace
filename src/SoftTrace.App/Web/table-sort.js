'use strict';
(() => {
  const collator=new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'});
  function next(state,key){
    const direction=state.key!==key?1:state.direction===1?-1:0;
    return {key:direction?key:null,direction};
  }
  function order(items,state,value,defaultCompare){
    return [...items].sort((a,b)=>{
      if(!state.direction)return defaultCompare(a,b);
      const left=value(a,state.key),right=value(b,state.key);
      const result=typeof left==='number'&&typeof right==='number'?left-right:collator.compare(String(left??''),String(right??''));
      return state.direction*result||defaultCompare(a,b);
    });
  }
  function bind(table,keys,onChange,defaultLabel){
    let state={key:null,direction:0};
    const headers=[...table.querySelectorAll('thead th')];
    const labels=headers.map(header=>header.textContent.trim());
    function update(){
      headers.forEach((header,index)=>{
        const direction=state.key===keys[index]?state.direction:0,button=header.querySelector('button');
        header.setAttribute('aria-sort',direction===1?'ascending':direction===-1?'descending':'none');
        const current=direction===1?'正序':direction===-1?'逆序':`默认排序：${defaultLabel}`;
        const action=direction===1?'逆序':direction===-1?'默认排序':'正序';
        button.title=`${current}；点击切换为${action}`;
        button.setAttribute('aria-label',`${labels[index]}，${current}，点击切换为${action}`);
        button.innerHTML=`<span>${labels[index]}</span>${direction?`<svg viewBox="0 0 16 16" aria-hidden="true"><path d="${direction===1?'M8 13V3m-4 4 4-4 4 4':'M8 3v10m-4-4 4 4 4-4'}"/></svg>`:''}`;
      });
    }
    headers.forEach((header,index)=>{
      const button=document.createElement('button');button.type='button';button.className='table-sort';
      header.replaceChildren(button);
      button.onclick=()=>{state=next(state,keys[index]);update();onChange();};
    });
    update();
    return {order:(items,value,defaultCompare)=>order(items,state,value,defaultCompare)};
  }
  window.softTraceTableSort={next,order,bind};
})();
