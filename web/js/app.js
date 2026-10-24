import {api} from './bridge.js';
import {el,button,icon,badge,empty,showError,extractionStatus} from './ui.js';
import {pages as foundation} from './pages/foundation.js';
import {pages as records} from './pages/records.js';
import {pages as configuration} from './pages/settings.js';
import {pages as automation} from './pages/automation.js';
import {pages as analytics} from './pages/analytics.js';
import {pages as business} from './pages/analytics-business.js';
import {pages as workplace} from './pages/workplace.js';
import {pages as terrain} from './pages/terrain.js';
import {pages as library} from './pages/library.js';
import {navigationModel,navigationLabel,navigationTarget,openNavigationEditor} from './navigation.js';
import {pages as pilotWorkspace} from './pages/pilot-workspace.js';
import {pages as dataWorkspace} from './pages/data-workspace.js';
import {initializeDomeniaMotion} from './domenia-motion.js';
import {createCommandPalette} from './command-palette.js';
import {importLocalTestFile,localFileTestSummary} from './local-file-test.js';

const pages=[...foundation,...library,...records,...business,...analytics,...workplace,...terrain,...automation,...configuration,...pilotWorkspace,...dataWorkspace];
const registry=new Map(pages.map(page=>[page.id,page]));
const state={settings:null,session:{loaded:false},version:'',capabilities:[]};
let current='home',params={},controller=null,cleanup=null,cleanupPending=null,rendering=null,changingContext=false,revision=0,logOpen=false,navRequest=0;
let navCollapsed=false,navToggle;
const narrowNavigation=matchMedia('(max-width:800px)');
let modalEditor=false,dirtyEditor=false,lastEditor=false;
function reportEditor(){const open=modalEditor||dirtyEditor||!!document.activeElement?.matches('form input,form textarea,form select');if(open===lastEditor||!api.available)return;lastEditor=open;api.call('host.editor',{open}).catch(error=>notify(error.message,'danger'));}
const logs=[];let root,content,navigation,statusLabel,sourceLabel,pageTitle,groupTitle,sidebar,overlay,cancel,progress,logPanel,testBanner,testFileName,testFileDetails,testExit;
const descriptions={home:'Vos priorités, alertes et interventions du jour.',pilot:'Météo, activité et décisions sur un même périmètre.',data:'Acquérir, vérifier et conserver les données utiles.',collect:'Retrouvez vos recherches GLPI et collectez leurs données.',library:'Conservez les tickets et leurs contenus, puis retrouvez des solutions avec leurs sources.',records:'Explorez le relevé, ses colonnes et les tickets associés.',quality:'Contrôlez la couverture et l’interprétation avant de décider.',mapping:'Reliez les colonnes aux rôles métier et vérifiez les valeurs interprétées.',sources:'Conservez une recette distincte pour chaque recherche GLPI.',settings:'Adaptez les connexions, les exports et votre espace de travail.',automation:'Programmez les collectes et composez des recettes explicites.',alerts:'Suivez les dépassements de seuil et leurs objets contributeurs.',operations:'Retrouvez les résultats des traitements et les diagnostics.','terrain-map':'Localisez les tickets des équipes et techniciens choisis et préparez vos interventions.'};
function notify(message,tone='success'){if(typeof message==='object')message=message.message||JSON.stringify(message);const toast=el('div',{class:`toast ${tone}`,role:tone==='danger'?'alert':'status'},el('span',{text:message}),button('Fermer',()=>toast.remove(),{icon:'close'}));document.querySelector('#notifications').append(toast);setTimeout(()=>toast.remove(),tone==='danger'?15000:7000);}
function applyAppearance(){const settings=state.settings;if(!settings)return;const p=settings.interface||{};const dark=matchMedia('(prefers-color-scheme:dark)').matches;document.documentElement.dataset.theme=p.followWindowsTheme?(dark?'dark':'light'):settings.theme;document.documentElement.dataset.density=p.density||'balanced';document.documentElement.dataset.accent=p.accent||'blue';document.documentElement.dataset.contrast=String(p.highContrast||false);const policy=p.reduceMotion?'reduce':p.followWindowsMotion===false?'full':'system';document.documentElement.dataset.motionPolicy=policy;document.documentElement.dataset.motion=policy==='reduce'||(policy==='system'&&matchMedia('(prefers-reduced-motion:reduce)').matches)?'reduce':'normal';document.documentElement.dataset.vision=p.colorVision||'standard';document.body.classList.toggle('focus-mode',!!p.focusMode);syncNavigation();}
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
 for(const node of root.querySelectorAll('.topbar,.workspace,.statusbar'))node.inert=open||(changingContext&&!node.matches('.statusbar'));
 sidebar.inert=changingContext;
 if(open){sidebar.setAttribute('role','dialog');sidebar.setAttribute('aria-modal','true');}
 else{sidebar.removeAttribute('role');sidebar.removeAttribute('aria-modal');}
}
function closeNavigation(restoreFocus=false){const wasOpen=sidebar?.classList.contains('open');sidebar?.classList.remove('open');syncNavigation();if(wasOpen&&restoreFocus)navToggle?.focus();}
async function toggleNavigation(){
 if(document.body.classList.contains('focus-mode')){navCollapsed=false;await appearance({preferences:{...state.settings.interface,focusMode:false}});if(!narrowNavigation.matches){syncNavigation();return;}}
 if(narrowNavigation.matches){
  sidebar.classList.toggle('open');syncNavigation();
  if(sidebar.classList.contains('open')){
   const currentItem=navigation.querySelector('[aria-current=page]');
   const target=[currentItem,...navigation.querySelectorAll('button:not(:disabled),a[href]')]
    .find(node=>node&&!node.disabled&&node.getClientRects().length>0);
   target?.focus();
  }
 }
 else{navCollapsed=!navCollapsed;syncNavigation();}
}
function buildNavigation(){
 const scroll=navigation.scrollTop,focused=document.activeElement?.closest('.nav-item')?.dataset.page;
 const items=navigationModel(pages,state.settings.interface||{});
 navigation.replaceChildren(el('div',{class:'nav-space-list'},...items.map(page=>el('button',{
  type:'button',class:'nav-item',dataset:{page:page.id},title:page.description,'aria-label':page.label,
  'aria-current':page.id===current?'page':undefined,onClick:()=>navigate(page.id)
 },icon(page.icon||'layers'),el('span',{class:'nav-space-copy'},el('span',{class:'nav-label',text:page.label}),el('small',{class:'nav-space-description',text:page.description})),
 page.pinned?el('span',{class:'nav-space-pin',text:'★','aria-label':'Épinglé'}):null))));
 updatePageContext();navigation.scrollTop=scroll;
 if(focused&&!narrowNavigation.matches)[...navigation.querySelectorAll('.nav-item')].find(item=>item.dataset.page===focused)?.focus({preventScroll:true});
 syncNavigation();
}
function updatePageContext(){
 const page=registry.get(current);if(!page)return;
 if(groupTitle)groupTitle.textContent=state.session.testMode?'Votre simulation':'Votre espace de travail';
 const groupCrumb=document.querySelector('.breadcrumb-home');if(groupCrumb)groupCrumb.textContent='GLPI Pilot';
 const pageCrumb=document.querySelector('#breadcrumb-page');if(pageCrumb)pageCrumb.textContent=navigationLabel(page);
}

function customizeNavigation(){
 closeNavigation(true);
 openNavigationEditor({pages,preferences:state.settings.interface||{},save:async preferences=>{
  await appearance({preferences:{...state.settings.interface,...preferences}});
  notify('Votre menu a été enregistré.');
 }});
}
function sessionChanged(){
 const session=state.session,status=extractionStatus(session);
 const label=session.testMode?'Simulation':session.demo?'Démonstration':session.historical?'Archive':status.label;
 sourceLabel.textContent=session.loaded?`${label} · ${new Intl.NumberFormat('fr-FR').format(session.rows)} lignes`:'Aucun relevé';
 sourceLabel.title=sourceLabel.textContent;sourceLabel.className=`badge source-status ${session.demo?'warning':session.loaded?status.tone:''}`;
 statusLabel.textContent=session.status||'Prêt';
 if(testBanner){testBanner.hidden=!session.testMode;testFileName.textContent=session.testFileName||'Données de démonstration';testFileName.title=session.testFileName||'';testFileDetails.textContent=localFileTestSummary(session);testExit.title='Fermer cet espace de simulation et retrouver vos données et réglages habituels';}
}
async function flushCurrentPage(){
 if(cleanupPending)return cleanupPending;
 const done=cleanup;if(typeof done!=='function')return;
 const pending=Promise.resolve().then(()=>done());cleanupPending=pending;
 try{await pending;if(cleanup===done)cleanup=null;}
 finally{if(cleanupPending===pending)cleanupPending=null;}
}
async function replaceContext(operation,args,options){
 options.signal?.throwIfAborted();
 const ownedPageSignal=controller?.signal;
 if(changingContext)throw new Error('Un changement de contexte est déjà en cours.');
 changingContext=true;syncNavigation();
 const previous={page:current,params,scroll:document.querySelector('.workspace')?.scrollTop||0};
 let flushed=false;
 try{
  // Readers and editors must finish against their original stores. File selection
  // is a separate preparation, so cancellation never disposes the current page.
  if(rendering)await rendering.catch(()=>{});
  await flushCurrentPage();flushed=true;controller?.abort();++navRequest;dirtyEditor=false;reportEditor();
  // The operation replaces its own page lifetime. Cancel old page reads, but
  // do not cancel the authorized transition with that same owned signal.
  const requestOptions=options.signal===ownedPageSignal?{...options,signal:undefined}:options;
  return await api.call(operation,args,requestOptions);
 }
 catch(error){
  if(flushed){await navigate(previous.page,previous.params,{preserveScroll:true,restoreContext:true});const workspace=document.querySelector('.workspace');if(workspace)workspace.scrollTop=previous.scroll;}
  throw error;
 }
 finally{changingContext=false;syncNavigation();}
}
const sessionApi=Object.create(api);
sessionApi.call=(operation,args={},options={})=>operation==='app.demo'||operation==='records.test.exit'||(operation==='records.test.import'&&args.token&&!args.cancelled&&!args.prepareOnly)
 ?replaceContext(operation,args,options):api.call(operation,args,options);
async function exitLocalTest(){const session=await sessionApi.call('records.test.exit');state.session=session;sessionChanged();notify('Simulation terminée. Vos données et réglages habituels sont restaurés.');await navigate(session.loaded?'pilot-weather':'home');}
function openLog(){logOpen=!logOpen;if(!logOpen){logPanel?.remove();return;}logPanel=el('section',{class:'log-panel','aria-label':'Journal de la session'},el('div',{class:'card-heading'},el('h2',{text:'Journal de la session'}),button('Fermer',openLog,{icon:'close'})),el('div',{class:'log-lines',text:logs.length?logs.join('\n'):'Aucun message pour cette session.'}));document.body.append(logPanel);}
async function navigate(id,nextParams={},options={}){
 const target=navigationTarget(id,nextParams);id=target.id;nextParams=target.params;
 if(changingContext&&!options.restoreContext)return;
 if(!registry.has(id)){notify('Cet espace n’est pas disponible.','danger');return;}
 const request=++navRequest;try{await flushCurrentPage();}catch(error){notify('La sauvegarde n’a pas abouti : '+error.message,'danger');return;}if(request!==navRequest)return;controller?.abort();dirtyEditor=false;reportEditor();cleanup=null;controller=new AbortController();const signal=controller.signal;const local=++revision;current=id;params=nextParams;const page=registry.get(id);closeNavigation(true);buildNavigation();pageTitle.textContent=navigationLabel(page);updatePageContext();document.title=`${page.title} · GLPI Pilot`;const desc=document.querySelector('#page-description');desc.textContent=descriptions[id]||'';desc.hidden=!descriptions[id];content.setAttribute('aria-busy','true');
 const pageRoot=el('div',{class:`page-content${options.preserveScroll?'':' page-enter'}`,dataset:{page:id,workspace:id}});content.replaceChildren(pageRoot);if(!options.preserveScroll)document.querySelector('.workspace').scrollTop=0;
 try{
  const pending=Promise.resolve(page.render({root:pageRoot,api:sessionApi,state,signal,navigate,notify,refresh:()=>navigate(current,params,{preserveScroll:true}),params:nextParams,pages}));rendering=pending;
  const done=await pending;if(rendering===pending)rendering=null;if(signal.aborted){if(typeof done==='function')await done();return;}cleanup=done;
 }
 catch(error){if(!signal.aborted&&error.name!=='AbortError')showError(pageRoot,error);}
 finally{if(local===revision){rendering=null;content.removeAttribute('aria-busy');}}
}
const palette=createCommandPalette({api:sessionApi,navigate,getPageSignal:()=>controller?.signal,getState:()=>state,notify,getCommands:()=>({
 ticketLookup:true,
 pages:navigationModel(pages,state.settings?.interface||{},true).map(page=>({id:`page:${page.id}`,label:page.label,keywords:page.description,icon:page.icon,
  active:page.id===current,run:()=>navigate(page.id)})),
 actions:[
  {id:'action:navigation',label:'Personnaliser la navigation',icon:'settings',keywords:'menu groupes favoris',run:customizeNavigation},
  {id:'action:resolution',label:'Trouver une solution',icon:'spark',keywords:'bibliothèque tickets résolus similaires',run:()=>navigate('library',{view:'resolution'})},
  {id:'action:team',label:'Espace d’équipe',icon:'users',keywords:'partage notes contributions',run:()=>navigate('library',{view:'team'})},
  {id:'action:connection',label:'Stockage et synchronisation',icon:'folder',keywords:'bibliothèque base locale centrale connexion',run:()=>navigate('library',{view:'connection'})},
  {id:'action:test-file',label:'Simuler avec un fichier Excel ou CSV',icon:'folder',keywords:'Excel CSV XLSX XLSM TSV local hors ligne simulation test',run:()=>importLocalTestFile({api:sessionApi,state,navigate,notify})},
  ...(state.session.testMode?[{id:'action:test-exit',label:'Quitter la simulation',icon:'close',run:exitLocalTest}]:[]),
  {id:'action:demo',label:'Explorer la simulation de démonstration',icon:'spark',run:async()=>{await sessionApi.call('app.demo');await navigate('pilot-weather');}},
  {id:'action:collect',label:state.session.testMode?'Simuler une collecte':'Collecter la source actuelle',icon:'refresh',run:async()=>{await api.call('collect.run');await navigate('pilot-weather');}}
 ]
})});
function commandPalette(){closeNavigation(true);palette.toggle();}
async function start(){
 root=document.querySelector('#app');
 if(!api.available){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('Ouvrez GLPI-Pilot.exe','Cette interface locale utilise le moteur C# de l’application Windows. Le dossier web seul ne peut pas collecter ni modifier des données.')));return;}
 try{Object.assign(state,await api.call('app.bootstrap'));applyAppearance();root.className='';root.removeAttribute('role');navigation=el('nav',{class:'nav','aria-label':'Navigation principale'});sidebar=el('aside',{class:'sidebar',id:'app-sidebar','aria-label':'Navigation principale'},el('div',{class:'brand'},el('div',{class:'brand-mark',text:'GP','aria-hidden':'true'}),el('div',{class:'brand-copy'},el('div',{class:'brand-name',text:'GLPI Pilot'}),el('div',{class:'brand-subtitle',text:'Votre espace de pilotage'})),button('Fermer',()=>closeNavigation(true),{icon:'close',attributes:{class:'button sidebar-close','aria-label':'Fermer la navigation'}})),navigation,button('Personnaliser',customizeNavigation,{icon:'settings',title:'Personnaliser la navigation',attributes:{class:'button nav-customize','aria-label':'Personnaliser la navigation'}}),el('div',{class:'sidebar-foot'},el('span',{},el('span',{class:'local-dot'}),'Interface locale'),el('span',{text:`v${state.version}`})));
 sourceLabel=badge('');pageTitle=el('h1',{text:'Accueil'});groupTitle=el('small',{text:'Espace de travail'});content=el('div',{id:'content',tabIndex:-1});statusLabel=el('span',{class:'status-text'});progress=el('progress',{hidden:true,'aria-label':'Traitement en cours'});cancel=button('Annuler',async()=>{api.cancelAll();await api.call('operations.cancel');},{icon:'close'});cancel.hidden=true;overlay=el('div',{class:'nav-overlay','aria-hidden':'true',onClick:()=>closeNavigation(true)});
 navToggle=button('Navigation',toggleNavigation,{icon:'menu',title:'Replier la navigation',attributes:{class:'button nav-toggle','aria-label':'Navigation','aria-controls':'app-sidebar','aria-expanded':'true'}});
 const topbar=el('header',{class:'topbar'},el('div',{class:'breadcrumb'},navToggle,el('span',{class:'breadcrumb-home',text:'GLPI Pilot'}),el('span',{class:'breadcrumb-separator',text:'/','aria-hidden':'true'}),el('strong',{id:'breadcrumb-page',text:'GLPI Pilot'})),el('div',{class:'top-actions'},sourceLabel,el('button',{type:'button',class:'search-command',onClick:commandPalette,title:'Rechercher un écran, une action ou un ticket (Ctrl K)','aria-label':'Rechercher un écran, une action ou un ticket'},icon('search'),el('span',{text:'Rechercher'}),el('kbd',{text:'Ctrl K'})),button('Thème',()=>appearance({theme:document.documentElement.dataset.theme==='dark'?'light':'dark',preferences:{...state.settings.interface,followWindowsTheme:false}}),{icon:'sun',title:'Changer de thème',attributes:{'aria-label':'Changer de thème'}})));
 testFileName=el('span',{class:'local-test-file-name'});testFileDetails=el('span',{class:'local-test-details'});testExit=button('Quitter la simulation',exitLocalTest,{icon:'close'});
 testBanner=el('aside',{class:'local-test-banner',hidden:true,'aria-label':'Mode simulation'},icon('folder'),el('div',{class:'local-test-copy'},el('strong',{text:'Simulation · toutes les rubriques'}),testFileName,testFileDetails),testExit);
 root.replaceChildren(el('div',{class:'shell'},sidebar,topbar,el('main',{class:'workspace'},testBanner,el('div',{class:'page-heading'},el('div',{},groupTitle,pageTitle,el('p',{id:'page-description'})),button('Actualiser',()=>navigate(current,params),{icon:'refresh',title:'Actualiser cet espace',attributes:{'aria-label':'Actualiser'}})),content),el('footer',{class:'statusbar'},el('span',{class:'local-dot'}),statusLabel,progress,cancel,button('Journal',openLog,{icon:'log'}),button('−',()=>changeZoom(-5),{title:'Réduire le zoom',attributes:{'aria-label':'Réduire le zoom'}}),button(`${state.settings.interfaceZoomPercent} %`,()=>changeZoom(0),{title:'Revenir à 100 %',attributes:{id:'zoom-label','aria-label':'Réinitialiser le zoom'}}),button('+',()=>changeZoom(5),{title:'Augmenter le zoom',attributes:{'aria-label':'Augmenter le zoom'}}))),overlay);
 buildNavigation();sessionChanged();await navigate('home');await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));await api.call('app.ready');document.body.dataset.ready='true';
 }catch(error){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('L’interface n’a pas pu démarrer','Le diagnostic de démarrage est disponible dans le dossier de données de GLPI Pilot.'),el('p',{class:'notice danger',text:error.message})));}
}
api.on('settings.changed',data=>{state.settings=data.settings;applyAppearance();if(navigation)buildNavigation();const zoom=document.querySelector('#zoom-label');if(zoom)zoom.querySelector('span').textContent=`${state.settings.interfaceZoomPercent} %`;});
api.on('session.changed',data=>{state.session=data;if(sourceLabel)sessionChanged();});
api.on('operation.state',data=>{if(!cancel)return;const background=['app.bootstrap','app.ready','host.editor','workplace.reminders.check','automation.tick','operations.cancel'];const busy=data.busy&&!background.includes(data.operation);cancel.hidden=!busy;progress.hidden=!busy;root.dataset.busy=String(busy);if(busy)statusLabel.textContent='Traitement en cours…';else sessionChanged();});
api.on('operation.progress',data=>{if(statusLabel)statusLabel.textContent=data.message||`${data.rows||0} lignes collectées`;});
api.on('operation.phase',data=>{if(statusLabel)statusLabel.textContent=`Collecte · ${data.phase}`;});
api.on('operation.log',data=>{logs.push(data.message);if(logs.length>500)logs.shift();const lines=logPanel?.querySelector('.log-lines');if(lines){lines.textContent=logs.join('\n');lines.scrollTop=lines.scrollHeight;}});
api.on('app.beforeClose',async data=>{try{await api.call('operations.cancel');await flushCurrentPage();api.cancelAll();await api.call('app.closeReady',{token:data.token,allow:true});}catch(error){notify('La fenêtre reste ouverte : '+error.message,'danger');await api.call('app.closeReady',{token:data.token,allow:false}).catch(()=>{});}});
api.on('app.error',data=>notify(data.message,'danger'));api.on('app.navigate',data=>navigate(data.page,data.params||{}));
api.on('workplace.reminders.due',data=>{const day=navigation?.querySelector('[data-page=home]');if(day)day.title=data.count>0?`Accueil · ${data.count} rappel(s) à consulter`:'Accueil';});
window.addEventListener('pilot:editor',event=>{modalEditor=!!event.detail;reportEditor();});
window.addEventListener('focusin',reportEditor);window.addEventListener('focusout',()=>queueMicrotask(reportEditor));
window.addEventListener('input',event=>{if(event.target.closest('.form-grid')){dirtyEditor=true;reportEditor();}});
window.addEventListener('keydown',event=>{
 if(!state.settings||!navigation||event.defaultPrevented)return;
 if((event.ctrlKey||event.metaKey)&&!event.altKey&&event.key.toLowerCase()==='k'){
  event.preventDefault();if(!event.repeat)commandPalette();return;
 }
 if(document.querySelector('dialog[open]'))return;
 if(narrowNavigation.matches&&sidebar.classList.contains('open')&&event.key==='Tab'){
  const items=[...sidebar.querySelectorAll('button:not(:disabled),a[href]')].filter(item=>item.getClientRects().length);
  const first=items[0],last=items.at(-1);
  if(event.shiftKey&&document.activeElement===first){event.preventDefault();last?.focus();}
  else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first?.focus();}
  return;
 }
 const typing=event.target.matches('input,textarea,select,[contenteditable=true]');
 if(event.key==='F11'){event.preventDefault();closeNavigation();appearance({preferences:{...state.settings.interface,focusMode:!state.settings.interface.focusMode}});}
 else if(event.ctrlKey&&['+','=','-','0'].includes(event.key)){event.preventDefault();changeZoom(event.key==='0'?0:event.key==='-'?-5:5);}
 else if(!typing&&event.key==='Escape'&&!document.querySelector('dialog[open]')){event.preventDefault();closeNavigation(true);}
});
window.addEventListener('wheel',event=>{if(event.ctrlKey){event.preventDefault();changeZoom(event.deltaY>0?-5:5);}},{passive:false});
matchMedia('(prefers-color-scheme:dark)').addEventListener('change',applyAppearance);matchMedia('(prefers-reduced-motion:reduce)').addEventListener('change',applyAppearance);
narrowNavigation.addEventListener('change',()=>closeNavigation(true));
initializeDomeniaMotion();
start();
