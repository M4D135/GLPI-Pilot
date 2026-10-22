const icons = { home:'M3 10 12 3l9 7v10H6V10m3 10v-7h6v7', chart:'M4 20V4m0 16h17M8 16v-5m5 5V7m5 9v-8', table:'M3 4h18v16H3zM3 9h18M9 4v16', ticket:'M4 4h16v5a3 3 0 0 0 0 6v5H4v-5a3 3 0 0 0 0-6zM12 7v2m0 3v2m0 3v1', calendar:'M4 5h16v16H4zM7 2v6m10-6v6M4 10h16', users:'M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M2 21v-3a7 7 0 0 1 14 0v3m1-17a4 4 0 0 1 0 7m2 3a6 6 0 0 1 3 4v3', map:'m3 5 6-2 6 2 6-2v16l-6 2-6-2-6 2zM9 3v16m6-14v16', route:'M6 4a2 2 0 1 0 0 4 2 2 0 0 0 0-4m12 12a2 2 0 1 0 0 4 2 2 0 0 0 0-4M8 6h7a4 4 0 0 1 0 8H9a4 4 0 0 0 0 8h7', settings:'m12 3 2 3 4-1 1 4 3 3-3 2 1 4-4 1-4 3-2-3-4 1-1-4-3-3 3-2-1-4 4-1zM12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8', search:'M11 3a8 8 0 1 0 0 16 8 8 0 0 0 0-16m6 14 5 5', download:'M12 3v12m-5-5 5 5 5-5M4 15v6h16v-6', refresh:'M20 8a8 8 0 0 0-14-3L3 8m0-5v5h5m-4 8a8 8 0 0 0 14 3l3-3m0 5v-5h-5', clock:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18m0 4v6l4 2', shield:'m12 2 9 4v6c0 5-9 10-9 10S3 17 3 12V6zM8 12l3 3 5-6', layers:'m12 3 10 6-10 6L2 9zm-10 11 10 6 10-6m-20 5 10 6 10-6', code:'m8 5-6 7 6 7m8-14 6 7-6 7M14 3l-4 18', bell:'M6 16V9a6 6 0 0 1 12 0v7l3 3H3zm4 6h4', folder:'M3 5h7l2 3h9v13H3z', plus:'M12 4v16M4 12h16', check:'m4 12 5 5L20 6', close:'m5 5 14 14M19 5 5 19', menu:'M3 5h18M3 12h18M3 19h18', sun:'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M12 1v3m0 16v3M1 12h3m16 0h3M4 4l2 2m12 12 2 2M4 20l2-2M18 6l2-2', arrow:'M4 12h16m-6-6 6 6-6 6', help:'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M9 9a3 3 0 1 1 5 2c-2 1-2 2-2 3m0 3v.1', log:'M5 3h14v18H5zM8 7h8m-8 5h8m-8 5h5', spark:'m12 2 3 7 7 3-7 3-3 7-3-7-7-3 7-3z' };
export function icon(name='layers') { const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'); for(const [k,v] of Object.entries({viewBox:'0 0 24 24',fill:'none',stroke:'currentColor','stroke-width':'1.7','stroke-linecap':'round','stroke-linejoin':'round','aria-hidden':'true',class:'icon'})) svg.setAttribute(k,v); const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d',icons[name]||icons.layers);svg.append(path);return svg; }
function contentNodes(children) {
  const nodes=[];
  const visit=child=>{if(Array.isArray(child))child.forEach(visit);else if(child!==null&&child!==undefined&&child!==false)nodes.push(child instanceof Node?child:document.createTextNode(String(child)));};
  children.forEach(visit);return nodes;
}
// Native DOM replacement stringifies null/undefined. Keep optional UI children
// consistent with el(), without changing literal user strings or numeric zero.
export function replaceChildren(parent,...children) { parent.replaceChildren(...contentNodes(children));return parent; }
export function el(tag,attrs={},...children) {
  const node=document.createElement(tag); attrs ||= {};
  for(const [key,value] of Object.entries(attrs)) {
    if(value===undefined||value===null) continue;
    if(key==='text') node.textContent=String(value);
    else if(key==='class'||key==='className') node.className=value;
    else if(key==='style'&&typeof value==='object') Object.assign(node.style,value);
    else if(key.startsWith('on')&&typeof value==='function') node.addEventListener(key.slice(2).toLowerCase(),value);
    else if(key==='dataset') Object.assign(node.dataset,value);
    else if(key==='innerHTML'||key==='outerHTML'||key==='srcdoc') throw new Error('Le contenu HTML brut est interdit.');
    else if(key in node&&!key.includes('-')) { try {node[key]=value;} catch {node.setAttribute(key,String(value));} }
    else node.setAttribute(key,String(value));
  }
  node.append(...contentNodes(children));return node;
}
export function errorMessage(error) { return error?.message || String(error||'Erreur inconnue'); }
export function showError(root,error) { if(error?.name==='AbortError'||error?.code==='CANCELLED')return; const notice=el('div',{class:'notice danger',role:'alert'},icon('shield'),el('div',{},el('strong',{text:'L’action n’a pas abouti'}),el('p',{text:errorMessage(error)}))); (root||document.querySelector('main')).prepend(notice);return notice; }
export function button(label,handler,options={}) {
  const result=el('button',{type:'button',class:`button ${options.primary?'primary':''} ${options.danger?'danger':''}`,disabled:!!options.disabled,title:options.title||'',...options.attributes}, options.icon?icon(options.icon):null,el('span',{text:label}));
  if(handler)result.addEventListener('click',async event=>{const disabled=result.disabled;result.disabled=true;result.setAttribute('aria-busy','true');try{await handler(event);}catch(error){if(error?.name!=='AbortError'&&error?.code!=='CANCELLED'){showError(result.closest('dialog,.card,main')||document.querySelector('main'),error);window.dispatchEvent(new CustomEvent('pilot:error',{detail:error}));}}finally{result.disabled=disabled;result.removeAttribute('aria-busy');}});
  return result;
}
export const card=(title,...children)=>el('section',{class:'card'},title?el('div',{class:'card-heading'},el('h2',{text:title})):null,...children);
export const toolbar=(...children)=>el('div',{class:'toolbar'},...children);
export const empty=(title,detail='')=>el('div',{class:'empty'},icon('layers'),el('h3',{text:title}),detail?el('p',{text:detail}):null);
export const badge=(text,tone='')=>el('span',{class:`badge ${tone}`,text});
export function extractionStatus(session={}) {
 const quality=session.quality;
 const expected=Number.isInteger(session.expectedTotal)&&session.expectedTotal>=0?session.expectedTotal:null;
 const legacyComplete=!quality&&session.complete===true&&(expected===null||session.rows===expected);
 const receptionComplete=legacyComplete||(quality?.receptionComplete===true&&quality.sourceRows===session.rows&&(quality.expectedRows??null)===expected&&(expected===null||session.rows===expected));
 const receipt=expected!==null&&session.rows<expected?'Réception partielle':legacyComplete?'Relevé complet':!quality?'Réception non documentée':receptionComplete?'Réception complète':'Réception non confirmée';
 const concordance=quality?.representationVerified===true?'Concordance vérifiée sur l’échantillon':quality?.representationVerified===false?'Concordance à vérifier':'Contrôle de concordance indisponible';
 const identity=quality?.sampleIdentityVerified===true?'Identifiants rapprochés sur l’échantillon':quality?.sampleIdentityVerified===false?'Identifiants à vérifier':'Contrôle des identifiants indisponible';
 const attention=quality?.scopeStable===false?'La recherche a changé ou son contexte final n’a pas pu être confirmé.':quality?.sampleIdentityVerified===false?'Des identifiants de l’échantillon manquent dans le relevé.':quality?.finalTotalVerified===false?'Le total GLPI a changé pendant la collecte.':!receptionComplete?'Le nombre de lignes reçues ou la portée de la collecte reste à vérifier.':'';
 const label=attention&&receptionComplete?'Collecte à vérifier':receipt;
 const details=[
  `Lignes chargées : ${formatNumber(session.rows)}.`,expected===null?'Total attendu indisponible.':`Total attendu : ${formatNumber(expected)}.`,
  ...(!quality?[legacyComplete?'Le relevé est complet ; le détail des contrôles de réception n’est pas disponible.':'Les observations de réception ne sont pas disponibles pour ce relevé.']:[
  quality.sourceRows==null?'Nombre de lignes reçues non documenté.':quality.sourceRows!==session.rows?'Les observations de réception ne correspondent pas au nombre de lignes chargées.':'',
  (quality.expectedRows??null)!==expected?'Les observations du total attendu ne correspondent pas au relevé chargé.':'',
  quality.allPagesRequested==null?'Contrôle de portée indisponible.':quality.allPagesRequested===false?'Toutes les pages n’ont pas été demandées.':'',
  quality.rowCountVerified==null?'Contrôle du nombre de lignes indisponible.':quality.rowCountVerified===false?'Nombre de lignes à vérifier.':'',
  quality.finalTotalVerified==null?'Contrôle du total final indisponible.':quality.finalTotalVerified===false?'Total final à vérifier.':''
  ])
 ].filter(Boolean).join(' ');
 return{label,tone:attention?'warning':legacyComplete?'info':'success',receipt,receptionComplete,concordance,identity,details,attention};
}
export function extractionNotice(session,onDetails,{detailed=false}={}) {
 if(!session?.loaded||session.demo)return null;
 const status=extractionStatus(session);
 if(!detailed)return el('section',{class:status.attention?'notice warning':'receipt-summary','aria-label':'Bilan de la collecte'},
  toolbar(badge(status.receipt,status.receptionComplete?'success':'warning'),
   status.attention?el('span',{text:status.attention}):null,onDetails?button('Qualité du relevé',onDetails):null));
 return el('section',{class:`notice ${status.tone==='warning'?'warning':''}`,'aria-label':'Réception et concordance du relevé'},icon('shield'),el('div',{},
  toolbar(badge(status.receipt,status.receptionComplete?'success':'warning'),badge(status.concordance,session.quality?.representationVerified===true?'':'warning'),onDetails?button('Détail des contrôles',onDetails):null),
  el('p',{class:'help',text:status.details}),el('p',{class:'help',text:status.identity}),
  status.attention?el('p',{class:'help',text:status.attention}):null,
  !session.complete?el('p',{class:'help',text:'Les données sont disponibles. Les comparaisons historiques restent limitées aux relevés dont les contrôles concordent.'}):null));
}
export const input=(value='',options={})=>el(options.multiline?'textarea':'input',{type:'text',value:value??'',...options});
export function select(options,value='') {return el('select',{},...options.map(option=>el('option',{value:typeof option==='object'?option.value:option,selected:String(typeof option==='object'?option.value:option)===String(value),text:typeof option==='object'?option.label:option})));}
let fieldId=0;
export function field(label,control,help='') {
 const id=control.id||`pilot-field-${++fieldId}`;control.id=id;
 const caption=el('label',{class:'field-label',htmlFor:id,text:label});
 let description=null;if(help){description=el('span',{class:'help',id:`${id}-help`,text:help});control.setAttribute('aria-describedby',description.id);}
 return el('div',{class:'field'},caption,control,description);
}
export function form(fields,values={}) {
 const controls=new Map();const element=el('form',{class:'form-grid',onSubmit:e=>e.preventDefault()});
 for(const descriptor of fields){const value=values[descriptor.key]??descriptor.value??(descriptor.type==='checkbox'?false:'');let control;
  if(descriptor.type==='select')control=select(descriptor.options||[],value);
  else if(descriptor.type==='textarea')control=el('textarea',{value,rows:descriptor.rows||4});
  else control=input(value,{type:descriptor.type||'text',min:descriptor.min,max:descriptor.max,step:descriptor.step});
  if(descriptor.type==='checkbox'){control.checked=!!value;control.value='true';}
  control.name=descriptor.key;control.required=!!descriptor.required;control.disabled=!!descriptor.disabled;
  if(descriptor.placeholder)control.placeholder=descriptor.placeholder;if(descriptor.maxLength)control.maxLength=descriptor.maxLength;
  controls.set(descriptor.key,{control,descriptor});element.append(field(descriptor.label,control,descriptor.help||''));
 }
 return {element,controls,read(){if(!element.reportValidity())throw new Error('Vérifiez les champs signalés.');const result={};for(const [key,{control,descriptor}]of controls){if(descriptor.type==='checkbox')result[key]=control.checked;else if(descriptor.type==='number')result[key]=control.value===''?null:Number(control.value);else result[key]=control.value;}return result;}};
}
export function table(rows,columns,options={}) {
 const wrapper=el('div',{class:'table-scroll',tabIndex:0,'aria-label':options.label||'Tableau de données'});const grid=el('table',{class:'data-table'});const body=el('tbody');
 const header=el('tr',{},...columns.map(column=>el('th',{scope:'col',style:column.width?{width:typeof column.width==='number'?`${column.width}px`:column.width}:{}},column.header instanceof Node?column.header:column.label??column.key)));
 grid.append(el('thead',{},header),body);wrapper.append(grid);const list=rows.slice(0,Math.min(options.maxRows||500,2000));
 if(!list.length){body.append(el('tr',{},el('td',{colSpan:Math.max(1,columns.length)},empty(options.emptyText||'Aucun résultat'))));return wrapper;}
 let active=-1; const key=row=>typeof options.key==='function'?options.key(row):row[options.key||'id'];
 const selectRow=(index,event)=>{if(index<0||index>=list.length)return;active=index;[...body.rows].forEach((r,i)=>{r.classList.toggle('selected',i===index);r.setAttribute('aria-selected',String(i===index));});options.onSelect?.(list[index],event);};
 list.forEach((row,index)=>{const selected=options.selected instanceof Set?options.selected.has(key(row)):options.selected!=null&&key(row)===options.selected;
  const tr=el('tr',{class:selected?'selected':'',tabIndex:options.onSelect?0:undefined,'aria-selected':selected,onClick:event=>{if(!event.target.closest('button,a,input,select,textarea'))selectRow(index,event);},onKeydown:event=>{if(event.target!==tr)return;if(event.key==='Enter'||event.key===' '){event.preventDefault();selectRow(index,event);}if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();const next=Math.max(0,Math.min(list.length-1,index+(event.key==='ArrowDown'?1:-1)));body.rows[next].focus();selectRow(next,event);}}},...columns.map(column=>el('td',{title:column.render?undefined:String(row[column.key]??'')},column.render?column.render(row,index):row[column.key]??'')));body.append(tr);
 });
 if(rows.length>list.length)wrapper.append(el('p',{class:'help',text:`${formatNumber(list.length)} lignes affichées sur ${formatNumber(rows.length)}. Affinez le filtre.`}));return wrapper;
}
let dialogCount=0;
export function dialog(title,content,actions=[]) {
 const previous=document.activeElement;const node=el('dialog',{class:'modal','aria-label':title});const close=(value)=>{if(!node.isConnected)return;node.close();node.remove();dialogCount--;window.dispatchEvent(new CustomEvent('pilot:editor',{detail:dialogCount>0}));previous?.focus?.();node.dispatchEvent(new CustomEvent('pilot:closed',{detail:value}));};
 const heading=el('div',{class:'modal-heading'},el('h2',{text:title}),button('Fermer',()=>close(),{icon:'close'}));
 node.append(heading,el('div',{class:'modal-body'},content),el('div',{class:'modal-actions'},...actions.map(action=>button(action.label,()=>action.action?.(close),action))));
 node.addEventListener('cancel',event=>{event.preventDefault();close();});document.body.append(node);dialogCount++;node.showModal();window.dispatchEvent(new CustomEvent('pilot:editor',{detail:true}));return{close,element:node};
}
export function confirm(title,message,confirmLabel='Confirmer') {return new Promise(resolve=>{const modal=dialog(title,el('p',{text:message}),[{label:'Annuler',action:close=>close(false)},{label:confirmLabel,primary:true,action:close=>close(true)}]);modal.element.addEventListener('pilot:closed',event=>resolve(event.detail===true),{once:true});});}
export const formatNumber=(value,options={})=>new Intl.NumberFormat('fr-FR',options).format(Number(value)||0);
export function formatDate(value){if(!value)return'—';const date=new Date(value);return Number.isNaN(date.getTime())?String(value):new Intl.DateTimeFormat('fr-FR',{dateStyle:'short',timeStyle:'short'}).format(date);}
