import {api} from './bridge.js';
import {el,button,icon,badge,empty,showError,extractionStatus,confirm} from './ui.js';
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
import {pages as interventionsWorkspace} from './pages/interventions-workspace.js';
import {openIdentityPicker} from './identity-picker.js';
import {offerDeploymentConfiguration} from './pages/settings-deployment.js';
import {themes,themePreference,resolvedTheme,themeChange,themeQueries} from './theme.js';
import {workspaceContextKey,readWorkspaceView,saveWorkspaceView} from './work-context.js';

const pages=[...foundation,...library,...records,...business,...analytics,...workplace,...terrain,...automation,...configuration,...pilotWorkspace,...dataWorkspace,...interventionsWorkspace];
const registry=new Map(pages.map(page=>[page.id,page]));
const state={settings:null,session:{loaded:false},version:'',capabilities:[]};
let current='home',params={},controller=null,cleanup=null,cleanupPending=null,rendering=null,refreshing=null,changingContext=false,revision=0,logOpen=false,navRequest=0;
let navCollapsed=false,navToggle,navFold,railTip;
let identityTrigger,identityPickerOpen=false,identityRead=0,identityTimer;
const narrowNavigation=matchMedia('(max-width:800px)');
let modalEditor=false,dirtyEditor=false,calendarEditor=false,lastEditor=false;
function reportEditor(){const open=modalEditor||dirtyEditor||calendarEditor||!!document.activeElement?.matches('form input,form textarea,form select');if(open===lastEditor||!api.available)return;lastEditor=open;api.call('host.editor',{open}).catch(error=>notify(error.message,'danger'));}
const logs=[];let sourceSelector,refreshButton,sourceBusy=false;let root,content,navigation,statusLabel,sourceLabel,pageTitle,groupTitle,sidebar,overlay,cancel,progress,logPanel,testBanner,testFileName,testFileDetails,testExit;
const descriptions={interventions:'Planifiez les interventions, coordonnez les participants et préparez les visites.',home:'Vos priorités, alertes et interventions du jour.',pilot:'Météo, activité et décisions sur un même périmètre.',data:'Acquérir, vérifier et conserver les données utiles.',collect:'Retrouvez vos recherches GLPI et collectez leurs données.',library:'Conservez les tickets et leurs contenus, puis retrouvez des solutions avec leurs sources.',records:'Explorez le relevé, ses colonnes et les tickets associés.',quality:'Contrôlez la couverture et l’interprétation avant de décider.',mapping:'Reliez les colonnes aux rôles métier et vérifiez les valeurs interprétées.',sources:'Conservez une recette distincte pour chaque recherche GLPI.',settings:'Adaptez les connexions, les exports et votre espace de travail.',automation:'Programmez les collectes et composez des recettes explicites.',alerts:'Suivez les dépassements de seuil et leurs objets contributeurs.',operations:'Retrouvez les résultats des traitements et les diagnostics.','terrain-map':'Localisez les tickets des équipes et techniciens choisis et préparez vos interventions.'};
function notify(message,tone='success'){if(typeof message==='object')message=message.message||JSON.stringify(message);const toast=el('div',{class:`toast ${tone}`,role:tone==='danger'?'alert':'status'},el('span',{text:message}),button('Fermer',()=>toast.remove(),{icon:'close'}));document.querySelector('#notifications').append(toast);setTimeout(()=>toast.remove(),tone==='danger'?15000:7000);}
function applyAppearance(){const settings=state.settings;if(!settings)return;const p=settings.interface||{};const preference=themePreference(settings),theme=resolvedTheme(preference);document.documentElement.dataset.theme=theme;document.documentElement.dataset.themePref=preference;syncThemeSwitch();document.documentElement.dataset.density=p.density||'balanced';document.documentElement.dataset.accent=p.accent||'blue';document.documentElement.dataset.contrast=String(theme==='contrast'||!!p.highContrast);const policy=p.reduceMotion?'reduce':p.followWindowsMotion===false?'full':'system';document.documentElement.dataset.motionPolicy=policy;document.documentElement.dataset.motion=policy==='reduce'||(policy==='system'&&matchMedia('(prefers-reduced-motion:reduce)').matches)?'reduce':'normal';document.documentElement.dataset.vision=p.colorVision||'standard';document.body.classList.toggle('focus-mode',!!p.focusMode);syncNavigation();}
async function appearance(change){await api.call('app.appearance',change);}
// The four theme buttons of the top bar show the stored choice; "Suivre Windows" stays pressed whatever Windows resolves to.
function syncThemeSwitch(){const preference=themePreference(state.settings);for(const item of document.querySelectorAll('.theme-switch button'))item.setAttribute('aria-pressed',String(item.dataset.set===preference));}
async function setTheme(preference){if(themePreference(state.settings)===preference)return;try{await appearance(themeChange(preference,state.settings.interface));}catch(error){if(error.name!=='AbortError')notify('Le thème n’a pas été enregistré : '+error.message,'danger');}}
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
 if(navFold){const text=navCollapsed?'Déplier le menu':'Replier le menu';navFold.setAttribute('aria-expanded',String(!navCollapsed));navFold.setAttribute('aria-label',text);navFold.dataset.tip=`${text} (Ctrl B)`;navFold.querySelector('.nav-fold-label').textContent=text;}
 if(!navCollapsed||mobile||focus)hideRailTip();
 overlay.classList.toggle('open',open);
 for(const node of root.querySelectorAll('.topbar,.workspace,.statusbar'))node.inert=open||(changingContext&&!node.matches('.statusbar'));
 sidebar.inert=changingContext;
 if(open){sidebar.setAttribute('role','dialog');sidebar.setAttribute('aria-modal','true');}
 else{sidebar.removeAttribute('role');sidebar.removeAttribute('aria-modal');}
}
function closeNavigation(restoreFocus=false){const wasOpen=sidebar?.classList.contains('open');sidebar?.classList.remove('open');syncNavigation();if(wasOpen&&restoreFocus)navToggle?.focus();}
async function toggleNavigation(){
 // The layout may change while the settings call runs: only a click made and still in the compact layout opens the drawer.
 if(document.body.classList.contains('focus-mode')){const compact=narrowNavigation.matches;await appearance({preferences:{...state.settings.interface,focusMode:false}});if(!compact||!narrowNavigation.matches){syncNavigation();return;}}
 if(narrowNavigation.matches){
  sidebar.classList.toggle('open');syncNavigation();
  if(sidebar.classList.contains('open')){
   const currentItem=navigation.querySelector('[aria-current=page]');
   const target=[currentItem,...navigation.querySelectorAll('button:not(:disabled),a[href]')]
    .find(node=>node&&!node.disabled&&node.getClientRects().length>0);
   target?.focus();
  }
 }
 else setNavigationCollapsed(!navCollapsed);
}
// Folded rail labels, as in Domenia and Pesto: shown beside the icon after a short delay on hover or keyboard focus.
// One fixed element, so the scrolling menu never clips it.
function showRailTip(target){
 if(!target?.dataset.tip||!navCollapsed||narrowNavigation.matches||document.body.classList.contains('focus-mode'))return hideRailTip();
 const box=target.getBoundingClientRect();railTip.textContent=target.dataset.tip;
 railTip.style.left=`${Math.round(sidebar.getBoundingClientRect().right+8)}px`;railTip.style.top=`${Math.round(box.top+box.height/2)}px`;railTip.classList.add('visible');
}
function hideRailTip(){railTip?.classList.remove('visible');}
// Like Domenia and Pesto: the content slides to its new place instead of jumping when the rail changes width.
function slideColumn(change){
 const movers=[...root.querySelector('.shell').children].filter(node=>node!==sidebar&&node.getClientRects().length);
 const before=movers.map(node=>node.getBoundingClientRect().left);change();
 if(document.documentElement.dataset.motion==='reduce'||typeof Element.prototype.animate!=='function')return;
 movers.forEach((node,index)=>{const dx=before[index]-node.getBoundingClientRect().left;if(Math.abs(dx)>=1)node.animate([{transform:`translateX(${dx}px)`},{transform:'translateX(0)'}],{duration:520,easing:'cubic-bezier(.16,1,.3,1)'});});
}
function setNavigationCollapsed(collapsed){
 if(collapsed===navCollapsed)return;
 slideColumn(()=>{navCollapsed=collapsed;syncNavigation();});
 appearance({preferences:{...state.settings.interface,navigationCollapsed:collapsed}}).catch(error=>notify(error.message,'danger'));
}
function buildNavigation(){
 const scroll=navigation.scrollTop,focused=document.activeElement?.closest('.nav-item')?.dataset.page;
 const items=navigationModel(pages,state.settings.interface||{});
 const item=page=>el('button',{
  type:'button',class:'nav-item',dataset:{page:page.id,tip:page.label},'aria-label':page.label,
  'aria-current':page.id===current?'page':undefined,onClick:()=>navigate(page.id)
 },icon(page.icon||'layers'),el('span',{class:'nav-space-copy'},el('span',{class:'nav-label',text:page.label}),el('small',{class:'nav-space-description',text:page.description})),
 page.pinned?el('span',{class:'nav-space-pin',text:'★','aria-label':'Épinglé'}):null);
 navigation.replaceChildren(el('div',{class:'nav-space-list nav-primary','aria-label':'Espaces de travail'},...items.filter(page=>!page.utility).map(item)),
  el('div',{class:'nav-space-list nav-utilities','aria-label':'Données et réglages'},...items.filter(page=>page.utility).map(item)));
 updatePageContext();navigation.scrollTop=scroll;
 if(focused&&!narrowNavigation.matches)[...navigation.querySelectorAll('.nav-item')].find(item=>item.dataset.page===focused)?.focus({preventScroll:true});
 syncNavigation();
}
function updatePageContext(){
 const page=registry.get(current);if(!page)return;
 if(groupTitle)groupTitle.textContent='Espace de travail';
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
function sourceMode(session=state.session){
 if(session.demo||session.testMode)return 'simulation';
 if(session.cachedSource||session.historical)return 'cache';
 if(session.fileSource||session.sourceKind==='file')return 'csv';
 return 'live';
}
async function collectLive(){
 if(sourceBusy)return;
 sourceBusy=true;refreshButton&&(refreshButton.disabled=true);
 try{
  const result=await api.call('collect.run',{});
  notify('Collecte GLPI terminée. Les données affichées ont été actualisées.');
  await navigate('pilot');
  return result;
 }catch(error){
  if(error.name!=='AbortError')notify('Collecte GLPI impossible : '+error.message+' · Le relevé précédent reste consultable.','danger');
 }finally{sourceBusy=false;refreshButton&&(refreshButton.disabled=false);sessionChanged();}
}
async function selectDataSource(mode){
 if(mode==='cache'){
  if(state.session.cachedSource){await navigate('pilot');return;}
  try{await sessionApi.call('cache.restore');await navigate('pilot');}
  catch(error){if(error.name!=='AbortError')notify('Copie locale indisponible : '+error.message,'danger');}
 }else if(mode==='live'){
  await collectLive();
 }else if(mode==='csv'){
  await navigate('data');
  notify('Import CSV / Excel disponible dans « Autres façons de travailler ».','info');
 }else{
  // The demonstration is opened, reset and closed in Réglages, as in Domenia, FILPRO and PESTO.
  await navigate('settings',{section:'demo'});
 }
}
function sessionChanged(){
 const session=state.session,status=extractionStatus(session);
 const label=session.demo?'Démonstration':session.testMode?'Simulation':session.cachedSource?'Cache local':session.historical?'Archive':status.label;
 sourceLabel.textContent=session.loaded?`${label} · ${new Intl.NumberFormat('fr-FR').format(session.rows)} lignes`:'Aucun relevé';
 sourceLabel.title=sourceLabel.textContent;sourceLabel.className=`badge source-status ${session.demo?'warning':session.loaded?status.tone:''}`;
 if(sourceSelector){
  const mode=sourceMode(session);
  sourceSelector.value=mode;
  sourceSelector.title=mode==='cache'?'Copie datée : utiliser Live pour récupérer un nouveau relevé':mode==='live'?'Dernier relevé GLPI ; une collecte est nécessaire pour vérifier les changements':mode==='csv'?'Source fichier sélectionnée':'Mode démonstration';
  sourceSelector.disabled=sourceBusy;
 }
 if(refreshButton){refreshButton.title='Actualiser cet espace';refreshButton.dataset.actionDetail=sourceMode(session)==='live'?'Collecte GLPI depuis les écrans compatibles':'Rafraîchissement de la vue, sans collecte';}
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
async function replaceContext(operation,args,options,prepared){
 options.signal?.throwIfAborted();
 const ownedPageSignal=controller?.signal;
 if(changingContext)throw new Error('Un changement de contexte est déjà en cours.');
 changingContext=true;syncNavigation();
 const previous={page:current,params:{...params,...cleanup?.identityParams?.()},scroll:document.querySelector('.workspace')?.scrollTop||0};
 let flushed=false;
 try{
  // Readers and editors must finish against their original stores. File selection
  // is a separate preparation, so cancellation never disposes the current page.
  if(rendering)await rendering.catch(()=>{});
  await flushCurrentPage();flushed=true;prepared?.(previous);controller?.abort();++navRequest;dirtyEditor=false;reportEditor();
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
sessionApi.call=(operation,args={},options={})=>operation==='app.demo'||operation==='records.test.exit'||operation==='records.file.close'||operation==='cache.restore'||operation==='deployment.apply'||(['records.import','records.test.import'].includes(operation)&&args.token&&!args.cancelled&&!args.prepareOnly)
 ?replaceContext(operation,args,options):api.call(operation,args,options);
function paintIdentity(){
 if(!identityTrigger||!state.settings)return;
 const key=state.settings.dashboard.personalTechnician||'',label=state.identity?.key===key?state.identity.label:'';
 identityTrigger.dataset.selected=String(!!key);
 identityTrigger.title=key?`Changer mon identité${label?' · '+label:''}`:'Choisir mon identité pour mes tickets et mes interventions';
 identityTrigger.setAttribute('aria-label',key?`Mon identité${label?' : '+label:''}`:'Choisir mon identité');
 identityTrigger.replaceChildren(icon('users'),el('span',{class:'identity-full',text:key?label||'Mon identité':'Choisir mon identité'}),el('span',{class:'identity-short',text:key?'Moi':'Identité'}));
}
function refreshIdentity(){
 if(!identityTrigger)return;
 paintIdentity();clearTimeout(identityTimer);const generation=++identityRead;
 identityTimer=setTimeout(async()=>{
  const context=workspaceContextKey(state),key=state.settings?.dashboard?.personalTechnician||'';
  if(!key){state.identity=null;paintIdentity();window.dispatchEvent(new CustomEvent('pilot:identity-display'));return;}
  try{
   const result=await api.call('identity.get',{currentOnly:true});
   if(generation!==identityRead||workspaceContextKey(state)!==context)return;
   state.identity=result.current;paintIdentity();window.dispatchEvent(new CustomEvent('pilot:identity-display'));
  }catch(error){if(generation===identityRead&&error.name!=='AbortError')identityTrigger.title='Mon identité · '+error.message;}
 },0);
}
async function chooseIdentity(options={}){
 if(identityPickerOpen||changingContext||!state.settings)return null;
 if([...document.querySelectorAll('dialog[open]')].some(node=>!node.classList.contains('command-palette'))){notify('Fermez le panneau ouvert avant de choisir votre identité.','info');return null;}
 identityPickerOpen=true;closeNavigation(true);
 let resume=null;
 try{
  if(rendering)await rendering.catch(()=>{});
  const identityApi=Object.create(sessionApi);
  identityApi.call=async(operation,args={},requestOptions={})=>{
   if(!['identity.set','identity.create','identity.clear'].includes(operation))return sessionApi.call(operation,args,requestOptions);
   const unsaved=!['records','interventions'].includes(current)&&[...content.querySelectorAll('.form-grid')].some(form=>form.pilotHasUnsavedChanges?form.pilotHasUnsavedChanges():dirtyEditor);
   if(unsaved&&!await confirm('Changer d’identité ?', 'Les modifications non appliquées de cet écran seront abandonnées. Pour les conserver, restez ici et appliquez-les avant de changer d’identité.','Continuer le changement'))
    throw new DOMException('Vos modifications restent ouvertes.','AbortError');
   const previous={page:current,params:{...params,...cleanup?.identityParams?.()},scroll:document.querySelector('.workspace')?.scrollTop||0};
   const result=await replaceContext(operation,args,requestOptions,rollback=>{
    previous.views=['home','calendar','map','scope'].map(name=>[name,readWorkspaceView(state,name,null)]).filter(([,value])=>value!==null);
    const calendar=previous.page==='interventions'?previous.views.find(([name])=>name==='calendar')?.[1]:null;
    if(calendar)Object.assign(previous.params,{date:calendar.date,view:calendar.view,tab:calendar.tab,mine:calendar.mine,technicianKeys:calendar.technicians,teamKeys:calendar.teams,ticketMap:calendar.mapMode==='tickets'});
    rollback.params={...previous.params};
   });resume=previous;return result;
  };
  const result=await openIdentityPicker({api:identityApi,state});
  if(!result||!resume)return null;
  state.identity=result.key?{key:result.key,label:result.label}:null;paintIdentity();
  for(const [name,value] of resume.views||[])saveWorkspaceView(state,name,value);
  const nextParams={...resume.params,...(result.key?options.params:{})};delete nextParams.identity;
  if(nextParams.revision!=null)nextParams.revision=result.revision;
  await navigate(resume.page,nextParams,{preserveScroll:true});
  for(const key of Object.keys(options.params||{}))delete params[key];
  const workspace=document.querySelector('.workspace');if(workspace)workspace.scrollTop=resume.scroll;
  identityTrigger?.focus({preventScroll:true});
  notify(result.key?`Identité enregistrée : ${result.label}.`:'Votre identité personnelle a été retirée.');
  return result;
 }catch(error){if(error.name!=='AbortError')notify(error.message,'danger');return null;}
 finally{identityPickerOpen=false;}
}
async function exitLocalTest(){const session=await sessionApi.call('records.test.exit');state.session=session;sessionChanged();notify('Simulation terminée. Vos données et réglages habituels sont restaurés.');await navigate(session.loaded?'pilot-weather':'home');}
function openLog(){logOpen=!logOpen;if(!logOpen){logPanel?.remove();return;}logPanel=el('section',{class:'log-panel','aria-label':'Journal de la session'},el('div',{class:'card-heading'},el('h2',{text:'Journal de la session'}),button('Fermer',openLog,{icon:'close'})),el('div',{class:'log-lines',text:logs.length?logs.join('\n'):'Aucun message pour cette session.'}));document.body.append(logPanel);}
async function refreshCurrentPage(expectedSignal=null){
 const owner=controller,request=navRequest;
 if(changingContext||cleanupPending||owner?.signal.aborted||(expectedSignal&&expectedSignal!==owner?.signal))return;
 if(refreshing?.owner===owner)return refreshing.pending;
 const refresh={owner,pending:null};refreshing=refresh;
 refresh.pending=(async()=>{
  if(rendering)await rendering.catch(()=>{});
  if(changingContext||cleanupPending||controller!==owner||navRequest!==request||owner?.signal.aborted)return;
  if(typeof cleanup?.refresh==='function')await cleanup.refresh();
  else await navigate(current,params,{preserveScroll:true});
 })().finally(()=>{if(refreshing===refresh)refreshing=null;});
 return refresh.pending;
}
async function navigate(id,nextParams={},options={}){
 const target=navigationTarget(id,nextParams);id=target.id;nextParams=target.params;
 if(changingContext&&!options.restoreContext)return;
 if(!registry.has(id)){notify('Cet espace n’est pas disponible.','danger');return;}
 const request=++navRequest;try{await flushCurrentPage();}catch(error){if(error.name!=='AbortError')notify('La sauvegarde n’a pas abouti : '+error.message,'danger');return;}if(request!==navRequest)return;controller?.abort();dirtyEditor=false;reportEditor();cleanup=null;controller=new AbortController();const signal=controller.signal;const local=++revision;current=id;params=nextParams;const page=registry.get(id);closeNavigation(true);buildNavigation();pageTitle.textContent=navigationLabel(page);updatePageContext();document.title=`${page.title} · GLPI Pilot`;const desc=document.querySelector('#page-description');desc.textContent=descriptions[id]||'';desc.hidden=!descriptions[id];content.setAttribute('aria-busy','true');
 const pageRoot=el('div',{class:`page-content${options.preserveScroll?'':' page-enter'}`,dataset:{page:id,workspace:id}});content.replaceChildren(pageRoot);if(!options.preserveScroll)document.querySelector('.workspace').scrollTop=0;document.scrollingElement.scrollTop=0;
 try{
  const pending=Promise.resolve(page.render({root:pageRoot,api:sessionApi,state,signal,navigate,notify,chooseIdentity,refresh:()=>refreshCurrentPage(signal),params:nextParams,pages,exitSimulation:exitLocalTest}));rendering=pending;
  const done=await pending;if(rendering===pending)rendering=null;if(signal.aborted){if(typeof done==='function')await done();return;}cleanup=done;
 }
 catch(error){if(!signal.aborted&&error.name!=='AbortError')showError(pageRoot,error);}
 finally{if(local===revision){rendering=null;content.removeAttribute('aria-busy');}}
}
const palette=createCommandPalette({api:sessionApi,navigate,getPageSignal:()=>controller?.signal,getState:()=>state,notify,getCommands:()=>({
 ticketLookup:state.session.capabilities?.ticketDetails!==false,
 pages:navigationModel(pages,state.settings?.interface||{},true).map(page=>({id:`page:${page.id}`,label:page.label,keywords:page.description,icon:page.icon,
  active:page.id===current,run:()=>navigate(page.id)})),
 actions:[
  {id:'action:identity',label:'Choisir ou changer mon identité',icon:'users',keywords:'moi mon profil nom prénom mes tickets interventions',run:()=>chooseIdentity()},
  {id:'action:fold-navigation',label:navCollapsed?'Déplier le menu':'Replier le menu',icon:'panel',keywords:'menu navigation barre latérale Ctrl B',run:()=>toggleNavigation()},
  {id:'action:navigation',label:'Personnaliser la navigation',icon:'settings',keywords:'menu groupes favoris',run:customizeNavigation},
  {id:'action:resolution',label:'Trouver une solution',icon:'spark',keywords:'bibliothèque tickets résolus similaires',run:()=>navigate('library',{view:'resolution'})},
  {id:'action:team',label:'Espace d’équipe',icon:'users',keywords:'partage notes contributions',run:()=>navigate('library',{view:'team'})},
  {id:'action:connection',label:'Stockage et synchronisation',icon:'folder',keywords:'bibliothèque base locale centrale connexion',run:()=>navigate('library',{view:'connection'})},
  {id:'action:test-file',label:'Importer une source Excel ou CSV',icon:'folder',keywords:'Excel CSV XLSX XLSM TSV fichier source secours hors ligne',run:()=>importLocalTestFile({api:sessionApi,state,navigate,notify})},
  ...(state.session.testMode?[{id:'action:test-exit',label:'Quitter la simulation',icon:'close',run:exitLocalTest}]:[]),
  {id:'action:demo',label:'Démonstration',icon:'spark',keywords:'données tickets fictifs découvrir essayer réglages',run:()=>navigate('settings',{section:'demo'})},
  ...themes.map(theme=>({id:`action:theme-${theme.id}`,label:theme.label,icon:theme.icon,keywords:'thème apparence couleurs clair sombre contraste Windows',run:()=>setTheme(theme.id)})),
  {id:'action:collect',label:state.session.testMode?'Simuler une collecte':'Collecter la source actuelle',icon:'refresh',run:async()=>{await api.call('collect.run');await navigate('pilot-weather');}}
 ]
})});
function commandPalette(){closeNavigation(true);palette.toggle();}
async function start(){
 root=document.querySelector('#app');
 if(!api.available){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('Ouvrez GLPI-Pilot.exe','Cette interface locale utilise le moteur C# de l’application Windows. Le dossier web seul ne peut pas collecter ni modifier des données.')));return;}
 try{Object.assign(state,await api.call('app.bootstrap'));navCollapsed=state.settings.interface?.navigationCollapsed===true;navFold=el('button',{type:'button',class:'nav-fold','aria-controls':'app-sidebar','aria-keyshortcuts':'Control+B',onClick:()=>setNavigationCollapsed(!navCollapsed)},icon('panel'),el('span',{class:'nav-fold-label',text:'Replier le menu'}),el('kbd',{class:'nav-fold-kbd','aria-hidden':'true',text:'Ctrl B'}));railTip=el('div',{class:'rail-tip',role:'tooltip','aria-hidden':'true'});applyAppearance();root.className='';root.removeAttribute('role');navigation=el('nav',{class:'nav','aria-label':'Navigation principale'});sidebar=el('aside',{class:'sidebar',id:'app-sidebar','aria-label':'Navigation principale'},el('div',{class:'brand'},el('div',{class:'brand-mark','aria-hidden':'true'},el('img',{class:'brand-symbol',src:'brand/glpi-pilot-icon.png',alt:'',width:36,height:36,draggable:false})),el('div',{class:'brand-copy'},el('div',{class:'brand-name',text:'GLPI Pilot'}),el('div',{class:'brand-subtitle',text:state.session.administration?'Administration · responsables':'Tickets & interventions'})),button('Fermer',()=>closeNavigation(true),{icon:'close',attributes:{class:'button sidebar-close','aria-label':'Fermer la navigation'}})),navigation,button('Personnaliser',customizeNavigation,{icon:'settings',attributes:{class:'button nav-customize','aria-label':'Personnaliser la navigation','data-tip':'Personnaliser la navigation'}}),el('div',{class:'sidebar-foot'},el('span',{},el('span',{class:'local-dot'}),'Interface locale'),el('span',{text:`v${state.version}`})),navFold);
 sourceLabel=badge('');pageTitle=el('h1',{text:'Accueil'});groupTitle=el('small',{text:'Espace de travail'});content=el('div',{id:'content',tabIndex:-1});statusLabel=el('span',{class:'status-text'});progress=el('progress',{hidden:true,'aria-label':'Traitement en cours'});cancel=button('Annuler',async()=>{api.cancelAll();await api.call('operations.cancel');},{icon:'close'});cancel.hidden=true;overlay=el('div',{class:'nav-overlay','aria-hidden':'true',onClick:()=>closeNavigation(true)});
 navToggle=button('Navigation',toggleNavigation,{icon:'menu',title:'Replier la navigation',attributes:{class:'button nav-toggle','aria-label':'Navigation','aria-controls':'app-sidebar','aria-expanded':'true'}});
 identityTrigger=el('button',{type:'button',class:'button identity-trigger',id:'identity-trigger',onClick:()=>chooseIdentity()});paintIdentity();
 sourceSelector=el('select',{class:'source-mode-picker','aria-label':'Source des données',title:'Choisir la source affichée'},
  el('option',{value:'live',text:'GLPI Live · collecter'}),
  el('option',{value:'cache',text:'Cache local'}),
  el('option',{value:'csv',text:'CSV / Excel'}),
  el('option',{value:'simulation',text:'Démonstration'}));
 sourceSelector.addEventListener('change',async()=>{const requested=sourceSelector.value;await selectDataSource(requested);sessionChanged();});
 refreshButton=button('Actualiser',()=>sourceMode()==='live'&&['home','data','pilot','pilot-weather'].includes(current)?collectLive():refreshCurrentPage(),{icon:'refresh',title:'Actualiser cet espace',attributes:{'aria-label':'Actualiser'}});
 const topbar=el('header',{class:'topbar'},el('div',{class:'breadcrumb'},navToggle,el('span',{class:'breadcrumb-home',text:'GLPI Pilot'}),el('span',{class:'breadcrumb-separator',text:'/','aria-hidden':'true'}),el('strong',{id:'breadcrumb-page',text:'GLPI Pilot'})),el('div',{class:'top-actions'},sourceLabel,sourceSelector,identityTrigger,el('button',{type:'button',class:'search-command',onClick:commandPalette,title:'Rechercher un écran, une action ou un ticket (Ctrl K)','aria-label':'Rechercher un écran, une action ou un ticket'},icon('search'),el('span',{text:'Rechercher'}),el('kbd',{text:'Ctrl K'})),el('div',{class:'segmented theme-switch',role:'group','aria-label':'Thème'},...themes.map(theme=>el('button',{type:'button','data-set':theme.id,'aria-pressed':'false','aria-label':theme.label,title:theme.label,onClick:()=>setTheme(theme.id)},icon(theme.icon))))));
 testFileName=el('span',{class:'local-test-file-name'});testFileDetails=el('span',{class:'local-test-details'});testExit=button('Quitter la simulation',exitLocalTest,{icon:'close'});
 testBanner=el('aside',{class:'local-test-banner',hidden:true,'aria-label':'Mode simulation'},icon('folder'),el('div',{class:'local-test-copy'},el('strong',{text:'Démonstration · données fictives'}),testFileName,testFileDetails),testExit);
 root.replaceChildren(el('div',{class:'shell'},sidebar,topbar,el('main',{class:'workspace'},testBanner,el('div',{class:'page-heading'},el('div',{},groupTitle,pageTitle,el('p',{id:'page-description'})),refreshButton),content),el('footer',{class:'statusbar'},el('span',{class:'local-dot'}),statusLabel,progress,cancel,button('Journal',openLog,{icon:'log'}),button('−',()=>changeZoom(-5),{title:'Réduire le zoom',attributes:{'aria-label':'Réduire le zoom'}}),button(`${state.settings.interfaceZoomPercent} %`,()=>changeZoom(0),{title:'Revenir à 100 %',attributes:{id:'zoom-label','aria-label':'Réinitialiser le zoom'}}),button('+',()=>changeZoom(5),{title:'Augmenter le zoom',attributes:{'aria-label':'Augmenter le zoom'}}))),overlay,railTip);
 sidebar.addEventListener('pointerover',event=>showRailTip(event.target.closest('[data-tip]')));sidebar.addEventListener('pointerleave',hideRailTip);
 sidebar.addEventListener('focusin',event=>{const target=event.target.closest('[data-tip]');if(target?.matches(':focus-visible'))showRailTip(target);else hideRailTip();});sidebar.addEventListener('focusout',hideRailTip);sidebar.addEventListener('click',hideRailTip);navigation.addEventListener('scroll',hideRailTip,{passive:true});
 buildNavigation();sessionChanged();syncThemeSwitch();refreshIdentity();await navigate('home');await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));await api.call('app.ready');document.body.dataset.ready='true';
 if(state.deployment?.offer?.available)offerDeploymentConfiguration({api:sessionApi,state,navigate,signal:controller.signal,root:content,notify},state.deployment);
 }catch(error){root.className='';root.replaceChildren(el('main',{class:'workspace'},empty('L’interface n’a pas pu démarrer','Le diagnostic de démarrage est disponible dans le dossier de données de GLPI Pilot.'),el('p',{class:'notice danger',text:error.message})));}
}
api.on('settings.changed',data=>{state.settings=data.settings;applyAppearance();if(navigation)buildNavigation();refreshIdentity();const zoom=document.querySelector('#zoom-label');if(zoom)zoom.querySelector('span').textContent=`${state.settings.interfaceZoomPercent} %`;});
api.on('session.changed',data=>{state.session=data;if(sourceLabel)sessionChanged();if(identityTrigger)refreshIdentity();});
api.on('operation.state',data=>{if(!cancel)return;const background=['app.bootstrap','app.ready','host.editor','identity.get','workplace.reminders.check','automation.tick','operations.cancel'];const busy=data.busy&&!background.includes(data.operation);cancel.hidden=!busy;progress.hidden=!busy;root.dataset.busy=String(busy);if(busy)statusLabel.textContent='Traitement en cours…';else sessionChanged();});
api.on('operation.progress',data=>{if(statusLabel)statusLabel.textContent=data.message||`${data.rows||0} lignes collectées`;});
api.on('operation.phase',data=>{if(statusLabel)statusLabel.textContent=`Collecte · ${data.phase}`;});
api.on('operation.log',data=>{logs.push(data.message);if(logs.length>500)logs.shift();const lines=logPanel?.querySelector('.log-lines');if(lines){lines.textContent=logs.join('\n');lines.scrollTop=lines.scrollHeight;}});
api.on('app.beforeClose',async data=>{try{await api.call('operations.cancel');await flushCurrentPage();api.cancelAll();await api.call('app.closeReady',{token:data.token,allow:true});}catch(error){if(error.name!=='AbortError')notify('La fenêtre reste ouverte : '+error.message,'danger');await api.call('app.closeReady',{token:data.token,allow:false}).catch(()=>{});}});
api.on('app.error',data=>notify(data.message,'danger'));api.on('app.navigate',data=>navigate(data.page,data.params||{}));
api.on('workplace.reminders.due',data=>{const day=navigation?.querySelector('[data-page=home]');if(day)day.title=data.count>0?`Accueil · ${data.count} rappel(s) à consulter`:'Accueil';});
window.addEventListener('pilot:calendar-editor',event=>{calendarEditor=!!event.detail?.open||!!event.detail?.dirty;reportEditor();});
window.addEventListener('pilot:editor',event=>{modalEditor=!!event.detail;reportEditor();});
window.addEventListener('focusin',reportEditor);window.addEventListener('focusout',()=>queueMicrotask(reportEditor));
window.addEventListener('input',event=>{if(event.target.closest('.form-grid')){dirtyEditor=true;reportEditor();}});
window.addEventListener('keydown',event=>{
 if(!state.settings||!navigation||event.defaultPrevented)return;
 if((event.ctrlKey||event.metaKey)&&!event.altKey&&event.key.toLowerCase()==='k'){
  event.preventDefault();if(!event.repeat)commandPalette();return;
 }
 if(document.querySelector('dialog[open]'))return;
 if(event.ctrlKey&&!event.altKey&&!event.shiftKey&&event.key.toLowerCase()==='b'){event.preventDefault();if(!event.repeat&&!changingContext)toggleNavigation();return;}
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
for(const query of themeQueries)query.addEventListener('change',applyAppearance);matchMedia('(prefers-reduced-motion:reduce)').addEventListener('change',applyAppearance);
narrowNavigation.addEventListener('change',()=>closeNavigation(true));
initializeDomeniaMotion();
start();
