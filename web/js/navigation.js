import {el,button,icon,dialog,select} from './ui.js';

export const navigationGroups = [
  {key:'pinned',label:'Épinglés',icon:'spark',pages:[]},
  {key:'workspace',label:'Mon espace',icon:'home',pages:['home','day','alerts']},
  {key:'pilot',label:'Pilotage GLPI',icon:'chart',pages:['pilot-weather','pilot-summary','pilot-groups','pilot-categories','pilot-oldest','pilot-followup','pilot-participants','pilot-sites','pilot-settings']},
  {key:'collect',label:'Collecter et vérifier',icon:'download',pages:['collect','sources','records','quality','mapping','library']},
  {key:'analyze',label:'Analyser',icon:'chart',pages:['dashboard','queries','history','populations','assistant']},
  {key:'work',label:'Tickets et équipe',icon:'ticket',pages:['tickets','enrichment','technicians','planning']},
  {key:'terrain',label:'Terrain',icon:'map',pages:['tours','terrain-reference','terrain-review']},
  {key:'admin',label:'Réglages et suivi',icon:'settings',pages:['automation','operations','settings','updates','help']}
];
const labels={home:'Accueil',day:'Mon espace',alerts:'Alertes',collect:'Collecte GLPI',sources:'Sources',records:'Relevé',quality:'Qualité',mapping:'Correspondances',library:'Bibliothèque',dashboard:'Mes tableaux',queries:'Requêtes',history:'Historique',populations:'Effectifs des sites',assistant:'Assistant',tickets:'Tickets',enrichment:'Lectures ciblées',technicians:'Techniciens',planning:'Répartition',tours:'Tournées','terrain-reference':'Lieux et sites','terrain-review':'Localisations',automation:'Automatisations',operations:'Journal',settings:'Réglages',updates:'Mises à jour',help:'Aide','pilot-weather':'Météo','pilot-summary':'Pilotage','pilot-groups':'Groupes','pilot-categories':'Catégories','pilot-oldest':'Plus anciens','pilot-followup':'Suivi agents','pilot-participants':'Agents actifs','pilot-sites':'Sites','pilot-settings':'Paramètres'};
const protectedPages=new Set(['home','settings']);
const defaultHiddenPages=['assistant','enrichment','populations'];
const hiddenPages=preferences=>new Set([...(preferences.hiddenNavigation||[]),...((preferences.navigationDefaultsVersion||0)<1?defaultHiddenPages:[])]);
const defaultGroup=id=>navigationGroups.find(group=>group.pages.includes(id))?.key||'admin';
const unique=values=>[...new Set(values)];
export const navigationLabel=page=>labels[page.id]||page.title;
export const navigationVisible=(id,preferences={})=>protectedPages.has(id)||!hiddenPages(preferences).has(id);
export function navigationModel(pages,preferences={},includeHidden=false) {
  const groupKeys=navigationGroups.map(group=>group.key);
  const orderedGroups=unique((preferences.navigationGroupOrder||[]).filter(key=>groupKeys.includes(key)));
  // A group introduced by an upgrade joins its usual neighbours. Existing
  // groups keep their relative order, including an explicitly moved pilot group.
  for(const key of groupKeys) {
    if(orderedGroups.includes(key))continue;
    const previous=groupKeys.slice(0,groupKeys.indexOf(key)).reverse().find(value=>orderedGroups.includes(value));
    orderedGroups.splice(previous?orderedGroups.indexOf(previous)+1:0,0,key);
  }
  const pageIds=new Set(pages.map(page=>page.id));
  const order=unique([...(preferences.navigationOrder||[]).filter(id=>pageIds.has(id)),...navigationGroups.flatMap(group=>group.pages),...pageIds]);
  const hidden=hiddenPages(preferences),pinned=new Set(preferences.pinnedNavigation||[]);
  const groupFor=page=>pinned.has(page.id)?'pinned':groupKeys.includes(preferences.navigationGroups?.[page.id])&&preferences.navigationGroups[page.id]!=='pinned'?preferences.navigationGroups[page.id]:defaultGroup(page.id);
  return orderedGroups.map(key=>({...navigationGroups.find(group=>group.key===key),pages:pages.filter(page=>groupFor(page)===key&&(includeHidden||!hidden.has(page.id)||protectedPages.has(page.id))).sort((a,b)=>order.indexOf(a.id)-order.indexOf(b.id))}));
}

export function openNavigationEditor({pages,preferences={},save}) {
  const draft={
    navigationDefaultsVersion:1,
    navigationOrder:[...(preferences.navigationOrder||[])],
    hiddenNavigation:[...hiddenPages(preferences)].filter(id=>!protectedPages.has(id)),
    pinnedNavigation:[...(preferences.pinnedNavigation||[])],
    navigationGroupOrder:[...(preferences.navigationGroupOrder||[])],
    navigationGroups:{...(preferences.navigationGroups||{})}
  };
  const registry=new Map(pages.map(page=>[page.id,page]));
  const groupsHost=el('div',{class:'navigation-groups'});
  const status=el('p',{class:'navigation-editor-status',role:'status','aria-live':'polite'});
  const body=el('div',{class:'navigation-editor'},
    el('p',{class:'navigation-editor-intro',text:'Organisez vos raccourcis par tâche. Dépliez les groupes du menu selon vos besoins. Les écrans masqués restent accessibles avec Rechercher (Ctrl K). Accueil et Réglages ne peuvent pas être masqués.'}),
    el('div',{class:'navigation-editor-tools'},
      button('Tout afficher',()=>{draft.hiddenNavigation=[];paint('Tous les écrans sont visibles.');}),
      button('Masquer les écrans facultatifs',()=>{draft.hiddenNavigation=pages.filter(page=>!protectedPages.has(page.id)).map(page=>page.id);paint('Seuls Accueil et Réglages restent visibles.');}),
      button('Réinitialiser le menu',()=>{draft.navigationOrder=[];draft.hiddenNavigation=[...defaultHiddenPages];draft.pinnedNavigation=[];draft.navigationGroupOrder=[];draft.navigationGroups={};paint('Disposition par défaut restaurée. Assistant, Lectures ciblées et Effectifs des sites sont masqués. Enregistrez pour la conserver.');},{icon:'refresh'})),
    el('p',{class:'help navigation-editor-help',text:'Glissez les poignées pour déplacer une page ou un groupe. Au clavier, utilisez ↑ / ↓ pour réordonner ; sur une page, ← / → change de groupe. Les boutons et la liste Groupe offrent les mêmes actions.'}),
    status,groupsHost);
  const modal=dialog('Personnaliser la navigation',body,[
    {label:'Annuler',action:close=>close()},
    {label:'Enregistrer le menu',primary:true,action:async close=>{await save({...draft,navigationOrder:[...draft.navigationOrder],hiddenNavigation:[...draft.hiddenNavigation],pinnedNavigation:[...draft.pinnedNavigation],navigationGroupOrder:[...draft.navigationGroupOrder],navigationGroups:{...draft.navigationGroups}});close();}}
  ]);
  modal.element.classList.add('navigation-modal');
  let dragging=null;
  const model=()=>navigationModel(pages,draft,true);
  const groupFor=id=>model().find(group=>group.pages.some(page=>page.id===id));
  function focusKey() {return document.activeElement?.dataset.navigationFocus;}
  function paint(message='',focus=focusKey()) {
    const scroller=modal.element.querySelector('.modal-body'),scroll=scroller.scrollTop;
    const groups=model();
    groupsHost.replaceChildren(...groups.map((group,index)=>groupCard(group,index,groups)));
    status.textContent=message;
    scroller.scrollTop=scroll;
    if(focus) {
      const target=[...groupsHost.querySelectorAll('[data-navigation-focus]')].find(node=>node.dataset.navigationFocus===focus);
      target?.focus({preventScroll:true});
      if(target)requestAnimationFrame(()=>{if(target.isConnected)target.scrollIntoView({block:'nearest',inline:'nearest',behavior:'instant'});});
    }
  }
  function setVisible(ids,visible) {
    const hidden=new Set(draft.hiddenNavigation);
    for(const id of ids)if(!protectedPages.has(id)){if(visible)hidden.delete(id);else hidden.add(id);}
    draft.hiddenNavigation=[...hidden];
  }
  function assign(id,key) {
    draft.pinnedNavigation=draft.pinnedNavigation.filter(value=>value!==id);
    if(key==='pinned')draft.pinnedNavigation.push(id);
    else if(key===defaultGroup(id))delete draft.navigationGroups[id];
    else draft.navigationGroups[id]=key;
  }
  function movePage(id,key,before=null) {
    if(!registry.has(id)||!navigationGroups.some(group=>group.key===key)||id===before)return;
    const target=groupFor(before),currentGroups=model();
    if(before&&target?.key!==key)return;
    const order=currentGroups.flatMap(group=>group.pages.map(page=>page.id)).filter(value=>value!==id);
    const siblings=currentGroups.find(group=>group.key===key).pages.map(page=>page.id).filter(value=>value!==id);
    assign(id,key);
    const index=before?order.indexOf(before):siblings.length?order.indexOf(siblings.at(-1))+1:order.length;
    order.splice(index<0?order.length:index,0,id);draft.navigationOrder=order;
    paint(navigationLabel(registry.get(id))+' déplacé dans '+navigationGroups.find(group=>group.key===key).label+'.','page:'+id);
  }
  function stepPage(id,delta) {
    const group=groupFor(id),index=group.pages.findIndex(page=>page.id===id);
    if(index+delta<0||index+delta>=group.pages.length)return;
    const before=delta<0?group.pages[index-1]?.id:group.pages[index+2]?.id;
    movePage(id,group.key,before||null);
  }
  function moveGroup(key,before=null) {
    if(key===before)return;
    const order=model().map(group=>group.key).filter(value=>value!==key);
    order.splice(before?order.indexOf(before):order.length,0,key);draft.navigationGroupOrder=order;
    paint('Groupe '+navigationGroups.find(group=>group.key===key).label+' déplacé.','group:'+key);
  }
  function stepGroup(key,delta) {
    const groups=model(),index=groups.findIndex(group=>group.key===key);
    if(index+delta<0||index+delta>=groups.length)return;
    moveGroup(key,delta<0?groups[index-1].key:groups[index+2]?.key||null);
  }
  function startDrag(event,kind,id) {
    dragging={kind,id};event.dataTransfer.effectAllowed='move';
    event.dataTransfer.setData('text/plain','GLPI-Pilot navigation');
    event.currentTarget.classList.add('navigation-dragging');
  }
  function clearDrag() {
    dragging=null;
    groupsHost.querySelectorAll('.navigation-drop-target,.navigation-dragging').forEach(node=>node.classList.remove('navigation-drop-target','navigation-dragging'));
  }
  function dropTarget(node,accept,drop) {
    node.addEventListener('dragover',event=>{if(!dragging||!accept(dragging))return;event.preventDefault();event.stopPropagation();event.dataTransfer.dropEffect='move';node.classList.add('navigation-drop-target');});
    node.addEventListener('dragleave',event=>{if(!node.contains(event.relatedTarget))node.classList.remove('navigation-drop-target');});
    node.addEventListener('drop',event=>{if(!dragging||!accept(dragging))return;event.preventDefault();event.stopPropagation();const active=dragging;clearDrag();drop(active);});
  }
  function handle(kind,id,label,onKeydown) {
    return el('button',{type:'button',class:'button navigation-handle',draggable:true,'aria-label':'Déplacer '+label,title:kind==='group'?'Déplacer le groupe · ↑ / ↓':'Déplacer la page · ↑ / ↓ ou ← / →',dataset:{navigationFocus:kind+':'+id},onKeydown,onDragstart:event=>startDrag(event,kind,id),onDragend:clearDrag},icon('menu'));
  }
  const smallButton=(symbol,label,handler,disabled=false)=>button(symbol,handler,{disabled,title:label,attributes:{class:'button navigation-step','aria-label':label}});
  function groupCard(group,index,groups) {
    const optional=group.pages.filter(page=>!protectedPages.has(page.id));
    const visible=group.pages.filter(page=>!draft.hiddenNavigation.includes(page.id)).length;
    const toggle=el('input',{type:'checkbox',checked:optional.length?optional.every(page=>!draft.hiddenNavigation.includes(page.id)):visible>0,disabled:!optional.length,'aria-label':'Afficher le groupe '+group.label,dataset:{navigationFocus:'visibility-group:'+group.key},onChange:event=>{setVisible(optional.map(page=>page.id),event.target.checked);paint('Visibilité du groupe '+group.label+' modifiée.');}});
    toggle.indeterminate=optional.some(page=>draft.hiddenNavigation.includes(page.id))&&optional.some(page=>!draft.hiddenNavigation.includes(page.id));
    const heading=el('div',{class:'navigation-group-heading'},
      handle('group',group.key,'le groupe '+group.label,event=>{if(event.key==='ArrowUp'||event.key==='ArrowDown'){event.preventDefault();stepGroup(group.key,event.key==='ArrowUp'?-1:1);}}),
      icon(group.icon),el('div',{class:'navigation-group-copy'},el('h3',{text:group.label}),el('span',{class:'help',text:visible+' / '+group.pages.length+' visibles'})),
      el('label',{class:'navigation-group-visibility'},toggle,el('span',{text:'Afficher'})),
      el('div',{class:'navigation-steps'},smallButton('↑','Monter le groupe '+group.label,()=>stepGroup(group.key,-1),index===0),smallButton('↓','Descendre le groupe '+group.label,()=>stepGroup(group.key,1),index===groups.length-1)));
    const list=el('div',{class:'navigation-group-pages'},...group.pages.map((page,pageIndex)=>pageRow(page,group,pageIndex)));
    if(!group.pages.length)list.append(el('p',{class:'navigation-empty-group',text:group.key==='pinned'?'Placez ici vos écrans les plus utilisés.':'Déposez une page dans ce groupe.'}));
    const card=el('section',{class:'navigation-group-card',dataset:{navGroup:group.key},'aria-label':group.label},heading,list);
    dropTarget(heading,active=>active.kind==='group',active=>moveGroup(active.id,group.key));
    dropTarget(card,active=>active.kind==='page',active=>movePage(active.id,group.key));
    return card;
  }
  function pageRow(page,group,index) {
    const label=navigationLabel(page),protectedPage=protectedPages.has(page.id);
    const toggle=el('input',{type:'checkbox',checked:protectedPage||!draft.hiddenNavigation.includes(page.id),disabled:protectedPage,'aria-label':'Afficher '+label,dataset:{navigationFocus:'visibility-page:'+page.id},onChange:event=>{setVisible([page.id],event.target.checked);paint('Visibilité de '+label+' modifiée.');}});
    const destination=select(model().map(value=>({value:value.key,label:value.label})),group.key);
    destination.setAttribute('aria-label','Groupe de '+label);destination.dataset.navigationFocus='destination:'+page.id;
    destination.addEventListener('change',()=>movePage(page.id,destination.value));
    const row=el('div',{class:'navigation-item-row'+(draft.hiddenNavigation.includes(page.id)?' is-hidden':''),dataset:{navPage:page.id}},
      handle('page',page.id,label,event=>{
        if(event.key==='ArrowUp'||event.key==='ArrowDown'){event.preventDefault();stepPage(page.id,event.key==='ArrowUp'?-1:1);}
        if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();const groups=model(),position=groups.findIndex(value=>value.key===group.key),next=groups[position+(event.key==='ArrowLeft'?-1:1)];if(next)movePage(page.id,next.key);}
      }),
      el('label',{class:'navigation-page-visibility'},toggle,icon(page.icon||'layers'),el('span',{class:'navigation-page-name'},el('strong',{text:label}),protectedPage?el('small',{text:'Toujours visible'}):page.title!==label?el('small',{text:page.title}):null)),
      destination,el('div',{class:'navigation-steps'},smallButton('↑','Monter '+label,()=>stepPage(page.id,-1),index===0),smallButton('↓','Descendre '+label,()=>stepPage(page.id,1),index===group.pages.length-1)));
    dropTarget(row,active=>active.kind==='page',active=>movePage(active.id,group.key,page.id));
    return row;
  }
  paint();
  return modal;
}
