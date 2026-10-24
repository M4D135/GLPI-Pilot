import {el,button,icon,dialog} from './ui.js';

export const navigationSpaces=[
 {id:'home',label:'Accueil',icon:'home',description:'Priorités, alertes et rendez-vous du jour'},
 {id:'pilot',label:'Pilotage',icon:'chart',description:'Météo, activité et analyses de votre périmètre'},
 {id:'records',label:'Tickets',icon:'ticket',description:'Liste de travail, fiches et actions'},
 {id:'terrain-map',label:'Terrain',icon:'map',description:'Carte des tickets et préparation des interventions'},
 {id:'data',label:'Données',icon:'folder',description:'Importer, collecter, vérifier et conserver'},
 {id:'settings',label:'Réglages',icon:'settings',description:'Préférences et administration de votre installation'}
];
const spaces=new Map(navigationSpaces.map(space=>[space.id,space]));
const protectedPages=new Set(['home','settings']);
const optionalTools=new Set(['assistant','enrichment','populations']);
const aliases={
 day:['home',{panel:'priorities'}],alerts:['home',{panel:'alerts'}],
 'pilot-weather':['pilot',{focusSection:'weather'}],'pilot-summary':['pilot',{focusSection:'flow'}],
 'pilot-groups':['pilot',{focusSection:'teams'}],'pilot-categories':['pilot',{focusSection:'analysis'}],
 'pilot-oldest':['pilot',{focusSection:'attention'}],'pilot-followup':['pilot',{focusSection:'people'}],
 'pilot-participants':['pilot',{focusSection:'people'}],dashboard:['pilot',{panel:'compositions'}],
 queries:['pilot',{panel:'queries'}],history:['pilot',{panel:'history'}],technicians:['pilot',{panel:'technicians'}],
 planning:['pilot',{panel:'planning'}],assistant:['pilot',{panel:'assistant'}],
 tickets:['records',{}],collect:['data',{panel:'collect'}],sources:['data',{panel:'sources'}],library:['data',{panel:'library'}],
 quality:['data',{panel:'quality'}],mapping:['data',{panel:'mapping'}],enrichment:['data',{panel:'enrichment'}],
 'terrain-reference':['terrain-map',{panel:'reference'}],'terrain-review':['terrain-map',{panel:'review'}],
 'pilot-sites':['terrain-map',{panel:'sites'}],populations:['terrain-map',{panel:'populations'}],tours:['terrain-map',{panel:'tours'}],
 automation:['settings',{panel:'automation'}],updates:['settings',{panel:'updates'}],help:['settings',{panel:'help'}],
 operations:['settings',{panel:'operations'}],'pilot-settings':['settings',{panel:'pilot-settings'}]
};
export function navigationTarget(id,params={}){
 const alias=aliases[id];if(!alias)return {id,params};
 const defaults=id==='tours'&&(params.tourId||params.openDraft)?{panel:'planning'}:id==='tickets'&&params.rowIndex==null&&!params.ticketKey?{resumeTicket:true}:id==='day'&&params.section?{panel:params.section==='list'?'priorities':params.section}:alias[1];
 return {id:alias[0],params:{...defaults,...params}};
}
export const navigationSpaceFor=id=>navigationTarget(id).id;
export const navigationLabel=page=>spaces.get(navigationSpaceFor(page.id))?.label||page.title;
export const navigationVisible=(id,preferences={})=>protectedPages.has(id)||!(preferences.hiddenNavigation||[]).includes(optionalTools.has(id)?id:navigationSpaceFor(id));
export function navigationModel(pages,preferences={},includeHidden=false){
 const registry=new Map(pages.map(page=>[page.id,page]));
 const order=[...new Set([...(preferences.navigationOrder||[]).map(navigationSpaceFor),...spaces.keys()])];
 const pinned=new Set((preferences.pinnedNavigation||[]).map(navigationSpaceFor));
 return navigationSpaces.filter(space=>registry.has(space.id)&&(includeHidden||navigationVisible(space.id,preferences)))
  .map(space=>({...registry.get(space.id),...space,pinned:pinned.has(space.id)}))
  .sort((a,b)=>Number(b.pinned)-Number(a.pinned)||order.indexOf(a.id)-order.indexOf(b.id));
}

export function openNavigationEditor({pages,preferences={},save}){
 const draft={navigationDefaultsVersion:2,navigationOrder:[...(preferences.navigationOrder||[])],
  hiddenNavigation:[...(preferences.hiddenNavigation||[])],pinnedNavigation:[...(preferences.pinnedNavigation||[])],
  navigationGroupOrder:[],navigationGroups:{}};
 const list=el('div',{class:'navigation-space-list'}),status=el('p',{class:'navigation-editor-status',role:'status','aria-live':'polite'});
 const body=el('div',{class:'navigation-editor'},el('p',{class:'navigation-editor-intro',text:'Six espaces pour vos usages. Déplacez leurs poignées ou utilisez les flèches ; une étoile place un espace en tête. Les outils et détails restent accessibles depuis leur contexte. Accueil et Réglages restent visibles.'}),
  el('div',{class:'navigation-editor-tools'},button('Tout afficher',()=>{draft.hiddenNavigation=draft.hiddenNavigation.filter(id=>!spaces.has(id));paint('Les six espaces sont visibles.');}),
   button('Réinitialiser le menu',()=>{draft.navigationOrder=[];draft.pinnedNavigation=[];draft.hiddenNavigation=draft.hiddenNavigation.filter(id=>!spaces.has(id));paint('Ordre des six espaces restauré. Enregistrez pour le conserver.');},{icon:'refresh'})),status,list);
 const modal=dialog('Personnaliser la navigation',body,[{label:'Annuler',action:close=>close()},
  {label:'Enregistrer le menu',primary:true,action:async close=>{await save({...draft,navigationOrder:[...draft.navigationOrder],hiddenNavigation:[...draft.hiddenNavigation],pinnedNavigation:[...draft.pinnedNavigation]});close();}}]);
 modal.element.classList.add('navigation-modal');let dragging=null;
 function paint(message='',focus=document.activeElement?.dataset.navigationFocus){
  const scroller=modal.element.querySelector('.modal-body'),scroll=scroller.scrollTop,items=navigationModel(pages,draft,true);
  list.replaceChildren(...items.map((space,index)=>{
   const visible=el('input',{type:'checkbox',checked:navigationVisible(space.id,draft),disabled:protectedPages.has(space.id),'aria-label':'Afficher '+space.label,
    onChange:event=>{draft.hiddenNavigation=draft.hiddenNavigation.filter(id=>id!==space.id);if(!event.target.checked)draft.hiddenNavigation.push(space.id);paint('Visibilité de '+space.label+' modifiée.','visibility:'+space.id);},dataset:{navigationFocus:'visibility:'+space.id}});
   const handle=el('button',{type:'button',class:'button navigation-handle',draggable:true,'aria-label':'Déplacer '+space.label,dataset:{navigationFocus:'page:'+space.id},
    onKeydown:event=>{if(['ArrowUp','ArrowDown'].includes(event.key)){event.preventDefault();step(space.id,event.key==='ArrowUp'?-1:1);}},
    onDragstart:event=>{dragging=space.id;event.dataTransfer.effectAllowed='move';event.dataTransfer.setData('text/plain','GLPI-Pilot navigation');},onDragend:()=>{dragging=null;list.querySelectorAll('.navigation-drop-target').forEach(node=>node.classList.remove('navigation-drop-target'));}},icon('menu'));
   const row=el('div',{class:'navigation-item-row navigation-space-row'+(visible.checked?'':' is-hidden'),dataset:{navPage:space.id}},handle,
    el('label',{class:'navigation-page-visibility'},visible,icon(space.icon),el('span',{class:'navigation-page-name'},el('strong',{text:space.label}),el('small',{text:space.description}))),
    button(space.pinned?'★':'☆',()=>{draft.pinnedNavigation=draft.pinnedNavigation.filter(id=>id!==space.id);if(!space.pinned)draft.pinnedNavigation.push(space.id);paint(space.label+(space.pinned?' retiré des favoris.':' ajouté aux favoris.'),'pin:'+space.id);},{title:(space.pinned?'Désépingler ':'Épingler ')+space.label,attributes:{'aria-label':(space.pinned?'Désépingler ':'Épingler ')+space.label,'aria-pressed':String(space.pinned),dataset:{navigationFocus:'pin:'+space.id}}}),
    el('div',{class:'navigation-steps'},button('↑',()=>step(space.id,-1),{disabled:index===0,attributes:{'aria-label':'Monter '+space.label}}),button('↓',()=>step(space.id,1),{disabled:index===items.length-1,attributes:{'aria-label':'Descendre '+space.label}})));
   row.addEventListener('dragover',event=>{if(!dragging||dragging===space.id)return;event.preventDefault();event.dataTransfer.dropEffect='move';row.classList.add('navigation-drop-target');});
   row.addEventListener('dragleave',event=>{if(!row.contains(event.relatedTarget))row.classList.remove('navigation-drop-target');});
   row.addEventListener('drop',event=>{if(!dragging||dragging===space.id)return;event.preventDefault();const moving=dragging;dragging=null;move(moving,space.id);});
   return row;
  }));status.textContent=message;scroller.scrollTop=scroll;
  const target=[...list.querySelectorAll('[data-navigation-focus]')].find(node=>node.dataset.navigationFocus===focus);target?.focus({preventScroll:true});
 }
 function move(id,before){
  const items=navigationModel(pages,draft,true).map(space=>space.id),order=items.filter(value=>value!==id);
  const index=before?order.indexOf(before):order.length;order.splice(index<0?order.length:index,0,id);
  // A deliberate move wins over the previous pinned rank for this item.
  const targetPinned=before&&draft.pinnedNavigation.includes(before);draft.pinnedNavigation=draft.pinnedNavigation.filter(value=>value!==id);if(targetPinned)draft.pinnedNavigation.push(id);draft.navigationOrder=order;
  paint(navigationLabel({id})+' déplacé.','page:'+id);
 }
 function step(id,direction){const items=navigationModel(pages,draft,true).map(space=>space.id),index=items.indexOf(id);if(index+direction<0||index+direction>=items.length)return;move(id,direction<0?items[index-1]:items[index+2]||null);}
 paint();return modal;
}
