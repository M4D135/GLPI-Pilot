import {api} from './bridge.js';
import {el,button,icon,badge,dialog,input,empty,showError,extractionStatus} from './ui.js';
import {pages as foundation} from './pages/foundation.js';
import {pages as records} from './pages/records.js';
import {pages as configuration} from './pages/settings.js';
import {pages as automation} from './pages/automation.js';
import {pages as analytics} from './pages/analytics.js';
import {pages as workplace} from './pages/workplace.js';
import {pages as terrain} from './pages/terrain.js';

const pages=[...foundation,...records,...analytics,...workplace,...terrain,...automation,...configuration];
const registry=new Map(pages.map(page=>[page.id,page]));
const state={settings:null,session:{loaded:false},version:'',capabilities:[]};
let current='home',params={},controller=null,cleanup=null,revision=0,logOpen=false,navRequest=0;
let navCollapsed=false,navToggle;
const narrowNavigation=matchMedia('(max-width:800px)');
let modalEditor=false,dirtyEditor=false,lastEditor=false;
function reportEditor(){const open=modalEditor||dirtyEditor||!!document.activeElement?.matches('form input,form textarea,form select');if(open===lastEditor||!api.available)return;lastEditor=open;api.call('host.editor',{open}).catch(error=>notify(error.message,'danger'));}
const logs=[];let root,content,navigation,statusLabel,sourceLabel,pageTitle,groupTitle,sidebar,overlay,cancel,progress,logPanel;
const descriptions={home:'Votre espace de pilotage GLPI, de la collecte aux interventions.',collect:'Retrouvez vos recherches GLPI et collectez leurs données.',records:'Explorez le relevé, ses colonnes et les tickets associés.',quality:'Contrôlez la couverture et l’interprétation avant de décider.',mapping:'Reliez les colonnes aux rôles métier et vérifiez les valeurs interprétées.',sources:'Conservez une recette distincte pour chaque recherche GLPI.',settings:'Adaptez les connexions, les exports et votre espace de travail.',automation:'Programmez les collectes et composez des recettes explicites.',alerts:'Suivez les dépassements de seuil et leurs objets contributeurs.',operations:'Retrouvez les résultats des traitements et les diagnostics.'};
function notify(message,tone='success'){if(typeof message==='object')message=message.message||JSON.stringify(message);const toast=el('div',{class:`toast ${tone}`,role:tone==='danger'?'alert':'status'},el('span',{text:message}),button('Fermer',()=>toast.remove(),{icon:'close'}));document.querySelector('#notifications').append(toast);setTimeout(()=>toast.remove(),tone==='danger'?15000:7000);}
function applyAppearance(){const settings=state.settings;if(!settings)return;const p=settings.interface||{};const dark=matchMedia('(prefers-color-scheme:dark)').matches;document.documentElement.dataset.theme=p.followWindowsTheme?(dark?'dark':'light'):settings.theme;document.documentElement.dataset.density=p.density||'balanced';document.documentElement.dataset.accent=p.accent||'blue';document.documentElement.dataset.contrast=String(p.highContrast||false);document.documentElement.dataset.motion=p.reduceMotion||(p.followWindowsMotion&&matchMedia('(prefers-reduced-motion:reduce)').matches)?'reduce':'normal';document.documentElement.dataset.vision=p.colorVision||'standard';document.body.classList.toggle('focus-mode',!!p.focusMode);syncNavigation();}
async function appearance(change){await api.call('app.appearance',change);}
let zoomTarget=null,zoomDelay;
function changeZoom(delta){if(!state.settings)return;zoomTarget=delta===0?100:Math.min(150,Math.max(75,(zoomTarget??state.settings.interfaceZoomPercent)+delta));clearTimeout(zoomDelay);zoomDelay=setTimeout(async()=>{const target=zoomTarget;try{await appearance({zoom:target});}catch(error){notify(error.message,'danger');}finally{if(zoomTarget===target)zoomTarget=null;}},80);}

function syncNavigation(){
 if(!sidebar||!navToggle)return;
 const focus=document.body.classList.contains('focus-mode');
 const mobile=narrowNavigation.matches;
 const open=mobile&&!focus&&sidebar.classList.contains('open');
 root.querySelector('.shell')?.classList.toggle('nav-collapsed',navCollapsed&&!mobile&&!focus);
 navToggle.setAttribute('aria-expanded',String(mobile?open:!navCollapsed&&!focus));
 navToggle.title=focus?'Quitter le mode concentration':mobile?(open?'Fermer la navigation':'Ouvrir la navigation'):navCollapsed?'Déplier la navigation':'Replier la navigation';
 overlay.classList.toggle('open',open);
 for(const node of root.querySelectorAll('.topbar,.workspace,.statusbar'))node.inert=open;
 if(open){sidebar.setAttribute('role','dialog');sidebar.setAttribute('aria-modal','true');}
 else{sidebar.removeAttribute('role');sidebar.removeAttribute('aria-modal');}
}
function closeNavigation(restoreFocus=false){const wasOpen=sidebar?.classList.contains('open');sidebar?.classList.remove('open');syncNavigation();if(wasOpen&&restoreFocus)navToggle?.focus();}
async function toggleNavigation(){
 if(document.body.classList.contains('focus-mode')){navCollapsed=false;await appearance({preferences:{...state.settings.interface,focusMode:false}});if(!narrowNavigation.matches){syncNavigation();return;}}
 if(narrowNavigation.matches){sidebar.classList.toggle('open');syncNavigation();if(sidebar.classList.contains('open'))navigation.querySelector('[aria-current=page]')?.focus();}
 else{navCollapsed=!navCollapsed;syncNavigation();}
}
function buildNavigation(){const scroll=navigation.scrollTop;const focused=document.activeElement?.closest('.nav-item')?.dataset.page;navigation.replaceChildren();const prefs=state.settings.interface||{};const order=prefs.navigationOrder||[];const hidden=new Set(prefs.hiddenNavigation||[]);const pinned=new Set(prefs.pinnedNavigation||[]);const available=pages.filter(p=>!hidden.has(p.id)||['home','settings'].includes(p.id));const groups=['Épinglés','Espace de travail','Collecter','Analyser','Intervenir','Terrain','Administrer'];const normalized=page=>['Données','Acquisition'].includes(page.group)?'Collecter':['Pilotage','Analyse'].includes(page.group)?'Analyser':['Travail','Équipe','Tickets','Travailler','Organiser'].includes(page.group)?'Intervenir':['Configuration','Système','Outils','Automatiser','Application'].includes(page.group)?'Administrer':page.group||'Espace de travail';
 for(const group of [...new Set([...groups,...available.map(normalized)])]){const entries=available.filter(p=>group==='Épinglés'?pinned.has(p.id):!pinned.has(p.id)&&normalized(p)===group).sort((a,b)=>{const ai=order.indexOf(a.id),bi=order.indexOf(b.id);return(ai<0?999:ai)-(bi<0?999:bi);});if(!entries.length)continue;navigation.append(el('div',{class:'nav-group',text:group}));for(const page of entries){const item=el('button',{type:'button',class:'nav-item',dataset:{page:page.id},title:page.title,'aria-label':page.title,'aria-current':page.id===current?'page':undefined,onClick:()=>navigate(page.id)},icon(page.icon||'layers'),el('span',{class:'nav-label',text:page.title}));navigation.append(item);}}
 navigation.scrollTop=scroll;
 if(focused&&!narrowNavigation.matches)[...navigation.querySelectorAll('.nav-item')].find(item=>item.dataset.page===focused)?.focus({preventScroll:true});
 syncNavigation();
}
function sessionChanged(){
 const session=state.session,status=extractionStatus(session);
 const label=session.demo?'Démonstration':session.historical?'Archive':status.label;
 sourceLabel.textContent=session.loaded?`${label} · ${new Intl.NumberFormat('fr-FR').format(session.rows)} lignes`:'Aucun relevé';
 sourceLabel.title=sourceLabel.textContent;sourceLabel.className=`badge source-status ${session.demo?'warning':session.loaded?status.tone:''}`;
 statusLabel.textContent=session.status||'Prêt';
}
function openLog(){logOpen=!logOpen;if(!logOpen){logPanel?.remove();return;}logPanel=el('section',{class:'log-panel','aria-label':'Journal de la session'},el('div',{class:'card-heading'},el('h2',{text:'Journal de la session'}),button('Fermer',openLog,{icon:'close'})),el('div',{class:'log-lines',text:logs.length?logs.join('\n'):'Aucun message pour cette session.'}));document.body.append(logPanel);}
async function navigate(id,nextParams={},options={}){
 if(!registry.has(id)){notify('Cet espace n’est pas disponible.','danger');return;}
 const request=++navRequest;if(typeof cleanup==='function'){try{await cleanup();}catch(error){notify('La sauvegarde n’a pas abouti : '+error.message,'danger');return;}}if(request!==navRequest)return;controller?.abort();dirtyEditor=false;reportEditor();cleanup=null;controller=new AbortController();const signal=controller.signal;const local=++revision;current=id;params=nextParams;const page=registry.get(id);closeNavigation(true);buildNavigation();pageTitle.textContent=page.title;const crumb=document.querySelector('#breadcrumb-page');if(crumb)crumb.textContent=page.title;groupTitle.textContent=page.group||'Espace de travail';document.title=`${page.title} · GLPI Pilot`;const desc=document.querySelector('#page-description');desc.textContent=descriptions[id]||'';desc.hidden=!descriptions[id];content.setAttribute('aria-busy','true');
 const pageRoot=el('div',{class:`page-content${options.preserveScroll?'':' page-enter'}`,dataset:{page:id}});content.replaceChildren(pageRoot);
 try{const done=await page.render({root:pageRoot,api,state,signal,navigate,notify,refresh:()=>navigate(current,params,{preserveScroll:true}),params:nextParams,pages});if(signal.aborted){if(typeof done==='function')done();return;}cleanup=done;if(!options.preserveScroll)document.querySelector('.workspace').scrollTop=0;}
 catch(error){if(!signal.aborted&&error.name!=='AbortError')showError(pageRoot,error);}
 finally{if(local===revision)content.removeAttribute('aria-busy');}
}
function commandPalette(){closeNavigation(true);const search=input('',{placeholder:'Chercher un écran ou une action…','aria-label':'Rechercher une commande'});const results=el('div',{class:'command-results'});const commands=[...pages.map(p=>({label:p.title,icon:p.icon,run:()=>navigate(p.id)})),{label:'Charger la démonstration',icon:'spark',run:async()=>{await api.call('app.demo');navigate('dashboard');}},{label:'Importer un relevé CSV ou Excel',icon:'download',run:async()=>{await api.call('records.import');navigate('records');}},{label:'Collecter la source actuelle',icon:'refresh',run:async()=>{await api.call('collect.run');navigate('dashboard');}}];const modal=dialog('Ouvrir un espace ou lancer une action',el('div',{},search,results));const paint=()=>{const q=search.value.toLocaleLowerCase('fr');results.replaceChildren(...commands.filter(c=>c.label.toLocaleLowerCase('fr').includes(q)).slice(0,16).map(c=>button(c.label,async()=>{modal.close();await c.run();},{icon:c.icon})));};search.addEventListener('input',paint);search.addEventListener('keydown',e=>{if(e.key==='ArrowDown'){e.preventDefault();results.querySelector('button')?.focus();}if(e.key==='Enter'){e.preventDefault();results.querySelector('button')?.click();}});paint();search.focus();}
async function start(){
 root=document.querySelector('#app');
 if(!api.available){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('Ouvrez GLPI-Pilot.exe','Cette interface locale utilise le moteur C# de l’application Windows. Le dossier web seul ne peut pas collecter ni modifier des données.')));return;}
 try{Object.assign(state,await api.call('app.bootstrap'));applyAppearance();root.className='';root.removeAttribute('role');navigation=el('nav',{class:'nav','aria-label':'Navigation principale'});sidebar=el('aside',{class:'sidebar',id:'app-sidebar','aria-label':'Navigation principale'},el('div',{class:'brand'},el('div',{class:'brand-mark',text:'GP','aria-hidden':'true'}),el('div',{class:'brand-copy'},el('div',{class:'brand-name',text:'GLPI Pilot'}),el('div',{class:'brand-subtitle',text:'Votre espace de pilotage'})),button('Fermer',()=>closeNavigation(true),{icon:'close',attributes:{class:'button sidebar-close','aria-label':'Fermer la navigation'}})),navigation,el('div',{class:'sidebar-foot'},el('span',{},el('span',{class:'local-dot'}),'Interface locale'),el('span',{text:`v${state.version}`})));
 sourceLabel=badge('');pageTitle=el('h1',{text:'Accueil'});groupTitle=el('small',{text:'Espace de travail'});content=el('div',{id:'content',tabIndex:-1});statusLabel=el('span',{class:'status-text'});progress=el('progress',{hidden:true,'aria-label':'Traitement en cours'});cancel=button('Annuler',async()=>{api.cancelAll();await api.call('operations.cancel');},{icon:'close'});cancel.hidden=true;overlay=el('div',{class:'nav-overlay','aria-hidden':'true',onClick:()=>closeNavigation(true)});
 navToggle=button('Navigation',toggleNavigation,{icon:'menu',title:'Replier la navigation',attributes:{class:'button nav-toggle','aria-label':'Navigation','aria-controls':'app-sidebar','aria-expanded':'true'}});
 const topbar=el('header',{class:'topbar'},el('div',{class:'breadcrumb'},navToggle,el('span',{class:'breadcrumb-home',text:'Mon espace'}),el('span',{class:'breadcrumb-separator',text:'/','aria-hidden':'true'}),el('strong',{id:'breadcrumb-page',text:'GLPI Pilot'})),el('div',{class:'top-actions'},sourceLabel,el('button',{type:'button',class:'search-command',onClick:commandPalette,title:'Rechercher un écran ou une action (Ctrl K)','aria-label':'Rechercher un écran ou une action'},icon('search'),el('span',{text:'Rechercher'}),el('kbd',{text:'Ctrl K'})),button('Thème',()=>appearance({theme:document.documentElement.dataset.theme==='dark'?'light':'dark',preferences:{...state.settings.interface,followWindowsTheme:false}}),{icon:'sun',title:'Changer de thème',attributes:{'aria-label':'Thème'}})));
 root.replaceChildren(el('div',{class:'shell'},sidebar,topbar,el('main',{class:'workspace'},el('div',{class:'page-heading'},el('div',{},groupTitle,pageTitle,el('p',{id:'page-description'})),button('Actualiser',()=>navigate(current,params),{icon:'refresh',title:'Actualiser cet espace',attributes:{'aria-label':'Actualiser'}})),content),el('footer',{class:'statusbar'},el('span',{class:'local-dot'}),statusLabel,progress,cancel,button('Journal',openLog,{icon:'log'}),button('−',()=>changeZoom(-5),{title:'Réduire le zoom',attributes:{'aria-label':'Réduire le zoom'}}),button(`${state.settings.interfaceZoomPercent} %`,()=>changeZoom(0),{title:'Revenir à 100 %',attributes:{id:'zoom-label','aria-label':'Réinitialiser le zoom'}}),button('+',()=>changeZoom(5),{title:'Augmenter le zoom',attributes:{'aria-label':'Augmenter le zoom'}}))),overlay);
 buildNavigation();sessionChanged();await navigate('home');await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));await api.call('app.ready');document.body.dataset.ready='true';
 }catch(error){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('L’interface n’a pas pu démarrer','Le diagnostic de démarrage est disponible dans le dossier de données de GLPI Pilot.'),el('p',{class:'notice danger',text:error.message})));}
}
api.on('settings.changed',data=>{state.settings=data.settings;applyAppearance();if(navigation)buildNavigation();const zoom=document.querySelector('#zoom-label');if(zoom)zoom.querySelector('span').textContent=`${state.settings.interfaceZoomPercent} %`;});
api.on('session.changed',data=>{state.session=data;if(sourceLabel)sessionChanged();});
api.on('operation.state',data=>{if(!cancel)return;const background=['app.bootstrap','app.ready','host.editor','workplace.reminders.check','automation.tick','operations.cancel'];const busy=data.busy&&!background.includes(data.operation);cancel.hidden=!busy;progress.hidden=!busy;root.dataset.busy=String(busy);if(busy)statusLabel.textContent='Traitement en cours…';else sessionChanged();});
api.on('operation.progress',data=>{if(statusLabel)statusLabel.textContent=data.message||`${data.rows||0} lignes collectées`;});
api.on('operation.phase',data=>{if(statusLabel)statusLabel.textContent=`Collecte · ${data.phase}`;});
api.on('operation.log',data=>{logs.push(data.message);if(logs.length>500)logs.shift();const lines=logPanel?.querySelector('.log-lines');if(lines){lines.textContent=logs.join('\n');lines.scrollTop=lines.scrollHeight;}});
api.on('app.beforeClose',async data=>{try{await api.call('operations.cancel');if(typeof cleanup==='function')await cleanup();cleanup=null;api.cancelAll();await api.call('app.closeReady',{token:data.token,allow:true});}catch(error){notify('La fenêtre reste ouverte : '+error.message,'danger');await api.call('app.closeReady',{token:data.token,allow:false}).catch(()=>{});}});
api.on('app.error',data=>notify(data.message,'danger'));api.on('app.navigate',data=>navigate(data.page));
api.on('workplace.reminders.due',data=>{const day=navigation?.querySelector('[data-page=day]');if(day)day.title=data.count>0?`Ma journée · ${data.count} rappel(s) à consulter`:'Ma journée';});
window.addEventListener('pilot:editor',event=>{modalEditor=!!event.detail;reportEditor();});
window.addEventListener('focusin',reportEditor);window.addEventListener('focusout',()=>queueMicrotask(reportEditor));
window.addEventListener('input',event=>{if(event.target.closest('.form-grid')){dirtyEditor=true;reportEditor();}});
window.addEventListener('keydown',event=>{
 if(!state.settings||!navigation)return;
 if(narrowNavigation.matches&&sidebar.classList.contains('open')&&event.key==='Tab'){
  const items=[...sidebar.querySelectorAll('button:not(:disabled),a[href]')].filter(item=>item.getClientRects().length);
  const first=items[0],last=items.at(-1);
  if(event.shiftKey&&document.activeElement===first){event.preventDefault();last?.focus();}
  else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first?.focus();}
  return;
 }
 const typing=event.target.matches('input,textarea,select,[contenteditable=true]');
 if(event.ctrlKey&&event.key.toLowerCase()==='k'){event.preventDefault();commandPalette();}
 else if(event.key==='F11'){event.preventDefault();closeNavigation();appearance({preferences:{...state.settings.interface,focusMode:!state.settings.interface.focusMode}});}
 else if(event.ctrlKey&&['+','=','-','0'].includes(event.key)){event.preventDefault();changeZoom(event.key==='0'?0:event.key==='-'?-5:5);}
 else if(!typing&&event.key==='Escape'&&!document.querySelector('dialog[open]')){event.preventDefault();closeNavigation(true);}
});
window.addEventListener('wheel',event=>{if(event.ctrlKey){event.preventDefault();changeZoom(event.deltaY>0?-5:5);}},{passive:false});
matchMedia('(prefers-color-scheme:dark)').addEventListener('change',applyAppearance);matchMedia('(prefers-reduced-motion:reduce)').addEventListener('change',applyAppearance);
narrowNavigation.addEventListener('change',()=>closeNavigation(true));
start();
