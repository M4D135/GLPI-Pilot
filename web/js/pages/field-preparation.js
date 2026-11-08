import {el,replaceChildren,button,toolbar,card,badge,empty,formatDate,formatNumber,confirm} from '../ui.js';
import {today,addDays} from '../calendar-layout.js';
import {createFieldFilters} from './field-preparation-tools.js';
import {renderMaterialEditor} from './field-preparation-editor.js';
import {renderCatalogueEditor} from './field-preparation-catalogue.js';
import {safeFieldReport,printFieldReport,clearFieldPrint} from './field-preparation-report.js';

const readiness={unspecified:'Matériel non renseigné',none:'Aucun matériel nécessaire','to-prepare':'À préparer',partial:'Préparation partielle',ready:'Prête',done:'Effectuée',cancelled:'Annulée'};
const unit={piece:'pièce(s)',metre:'mètre(s)',lot:'lot(s)'};
const normalize=value=>String(value||'').normalize('NFD').replace(/\p{Diacritic}/gu,'').toLocaleLowerCase('fr');
const field=(label,node)=>el('label',{class:'field'},el('span',{class:'field-label',text:label}),node);
const options=(values,value)=>el('select',{value},...values.map(([key,label])=>el('option',{value:key,text:label,selected:key===value})));
const message=text=>el('p',{class:'field-help',text});

export async function renderFieldPreparation(ctx){
 const {root,api,signal,state,navigate,notify}=ctx;
 let disposed=false,model=null,modal=null,offset=0,loading=0,selectionGeneration=0;
 const selected=new Map(),zone=Intl.DateTimeFormat().resolvedOptions().timeZone||'Europe/Paris';
 const scope=options([['all','Préparations de l’équipe connues'],['mine','Mes préparations']],ctx.params?.mine?'mine':'all');
 scope.setAttribute('aria-label','Périmètre');
 const search=el('input',{type:'search',placeholder:'Ticket, site ou matériel…',maxLength:300});
 const from=el('input',{type:'date'}),to=el('input',{type:'date'}),closed=el('input',{type:'checkbox'});
 const shell=el('section',{class:'field-workspace','aria-label':'Préparation du matériel'});
 const status=el('div',{class:'field-status',role:'status'}),errors=el('div',{class:'field-errors','aria-live':'polite'}),notices=el('div',{class:'field-notices'});
 const metrics=el('div',{class:'field-metrics'}),rows=el('div',{class:'field-plan-list'}),materials=el('div',{class:'field-material-summary'});
 const pagination=el('div',{class:'field-pagination'}),selectionLabel=el('span',{class:'field-selection-label'});
 const call=(operation,args={},localSignal=signal)=>api.call(operation,args,{signal:localSignal});
 const problem=(error,host=errors)=>{if(error?.name==='AbortError'||disposed)return;replaceChildren(host,el('div',{class:'notice danger',role:'alert',text:error?.message||String(error)}));};
 const run=fn=>async()=>{try{await fn();}catch(error){problem(error,modal?.errors||errors);}};
 const filters=createFieldFilters({call,run,state,params:ctx.params,onTour:tourId=>showReport({tourId,timeZoneId:zone,title:'Fiches de tournée',archive:filters.archive()}),onSelectAll:selectAll,onChange:async()=>{offset=0;await load(true);}});
 const preview=button('Aperçu et impression',run(()=>showReport()),{icon:'log',attributes:{'data-field-action':'preview'}});
 const exportButton=button('Exporter les fiches',run(async()=>{const result=await call('field.export',reportArgs());if(!result.cancelled)notify('Les fiches HTML ont été exportées.');}),{icon:'download',attributes:{'data-field-action':'export'}});
 const selectionBar=toolbar(selectionLabel,preview,exportButton);selectionBar.classList.add('field-selection-bar');
 function updateSelection(){selectionBar.hidden=!selected.size;selectionLabel.textContent=`${selected.size} fiche(s) sélectionnée(s)`;preview.disabled=!selected.size;exportButton.disabled=!selected.size;}
 function reportArgs(){return{selections:[...selected].map(([id,expectedRevision])=>({id,expectedRevision})),timeZoneId:zone,title:'Préparation terrain',archive:filters.archive()};}
 function request(){
  if(!!from.value!==!!to.value)throw new Error('Indiquez le début et la fin de période, ou laissez les deux dates vides.');
  if(from.value&&to.value<from.value)throw new Error('La date de fin doit être égale ou postérieure au début.');
  return{...filters.read(),scope:scope.value,search:search.value,includeClosed:closed.checked,offset,limit:30,...(from.value?{from:from.value,to:addDays(to.value,1),timeZoneId:zone}:{})};
 }
 async function load(clear=false){
  const version=++loading;++selectionGeneration;shell.setAttribute('aria-busy','true');errors.replaceChildren();
  try{
   await filters.load();if(disposed||version!==loading)return;
   const args=request();
   const result=await call('field.get',args);if(disposed||version!==loading)return;
   const scroller=root.closest('.workspace')||document.scrollingElement,scrollTop=scroller.scrollTop;
   model=result;if(clear)selected.clear();
   let removed=0;
   for(const row of model.rows||[])if(selected.has(row.id)&&(selected.get(row.id)!==row.revision||row.conflicts>0)){selected.delete(row.id);removed++;}
   paint();rows.inert=selectionBar.inert=false;scroller.scrollTop=scrollTop;
   if(removed)notify(`${removed} sélection(s) retirée(s) : la préparation a changé. Vérifiez-la avant de la sélectionner à nouveau.`,'info');
  }catch(error){if(!disposed&&version===loading)throw error;}finally{if(version===loading)shell.removeAttribute('aria-busy');}
 }
 async function selectAll(){
  const generation=++selectionGeneration,version=loading,args=request();
  const response=await call('field.get',{...args,offset:0,limit:200,selectAll:true});
  if(disposed||generation!==selectionGeneration||version!==loading||JSON.stringify(request())!==JSON.stringify(args))return;
  selected.clear();for(const item of response.selections||[])selected.set(item.id,item.expectedRevision);paint();
 }
 function paint(){
  const summary=model.summary||{},sync=model.sync||{};notices.replaceChildren();
  replaceChildren(status,badge(sync.enabled?'Espace partagé':'Sur ce poste',sync.enabled?'info':''),
   el('span',{text:sync.label||'Préparations enregistrées'}),
   sync.enabled&&sync.lastSuccess?el('span',{text:'Dernière synchronisation : '+formatDate(sync.lastSuccess)}):null,
   sync.enabled&&sync.pending?badge(`${sync.pending} modification(s) en attente`,'warning'):null,
   sync.conflicts?badge(`${sync.conflicts} préparation(s) en conflit`,'warning'):null);
  replaceChildren(metrics,...[[summary.planCount||0,'Préparations comptées',''],[summary.unspecifiedCount||0,'À renseigner','unspecified'],[summary.toPrepareCount||0,'À préparer','to-prepare'],[summary.partlyPreparedCount||0,'Partiellement préparées','partial'],[summary.readyCount||0,'Prêtes','ready'],[summary.noMaterialsCount||0,'Sans matériel nécessaire','none']].map(([value,label,readiness])=>
   el('button',{type:'button',class:'field-metric','aria-label':`${label} : ${formatNumber(value)}. Filtrer`,onClick:run(async()=>{filters.readiness(readiness);offset=0;await load(true);})},el('strong',{text:formatNumber(value)}),el('span',{text:label}))));
  for(const warning of summary.warnings||[])notices.append(el('div',{class:'notice warning',text:warning}));
  if(model.identityRequired)notices.append(el('div',{class:'notice info'},el('span',{text:'Choisissez votre identité pour retrouver vos préparations.'}),
   ctx.chooseIdentity?button('Choisir mon identité',run(()=>ctx.chooseIdentity({params:{...ctx.params,mine:true}})),{icon:'users'}):null));
  rows.replaceChildren();
  if(!model.rows?.length)rows.append(empty('Aucune préparation enregistrée dans ce périmètre','Choisissez un ticket ou une visite. Une tournée n’est pas obligatoire.'));
  for(const row of model.rows||[]){
   const check=el('input',{type:'checkbox',checked:selected.has(row.id),disabled:row.conflicts>0,'aria-label':'Sélectionner '+row.title,'data-field-select':row.id});
   check.addEventListener('change',()=>{++selectionGeneration;if(check.checked&&selected.size>=200){check.checked=false;notify('Sélectionnez au maximum 200 fiches.','info');return;}if(check.checked)selected.set(row.id,row.revision);else selected.delete(row.id);updateSelection();});
   const tone=row.conflicts||row.excludedFromTotals?'warning':row.readiness==='ready'||row.readiness==='none'?'success':'';
   rows.append(el('article',{class:'field-plan-card','data-field-plan':row.id},check,
    el('div',{class:'field-plan-copy'},el('h3',{text:row.title}),
     el('p',{text:[row.siteName||'Site non renseigné',row.plannedStart?formatDate(row.plannedStart):'Sans créneau enregistré'].join(' · ')}),
     message((row.participantLabels||[]).join(' · ')||'Participants non renseignés'),
     el('div',{class:'field-plan-badges'},badge(readiness[row.readiness]||row.readiness,tone),row.excludedFromTotals?badge('Hors totaux','warning'):null,
      row.pending&&model.sync?.enabled?badge('À synchroniser','warning'):null),
     message('Contexte observé : '+formatDate(row.observedAtUtc)),row.sourceStatus&&row.sourceStatus!=='current'?message(row.sourceMessage):null),
    el('div',{class:'field-plan-actions'},button('Ouvrir',run(()=>openPreparation({id:row.id})),{icon:'layers',attributes:{'data-field-open':row.id}}),
     row.conflicts?button('Voir les versions',run(()=>showVersions(row.id)),{icon:'layers'}):null)));
  }
  const totals=summary.materials||[];
  replaceChildren(materials,el('h2',{text:'À emporter · besoins enregistrés'}),message(model.coverage||''));
  if(totals.length){
   const body=el('tbody');
   for(const item of totals)body.append(el('tr',{},el('th',{scope:'row'},el('strong',{text:item.label}),item.code?el('small',{text:item.code}):null),
    el('td',{text:unit[item.unit]||item.unit}),el('td',{text:formatNumber(item.quantity)}),el('td',{text:formatNumber(item.preparedQuantity)}),el('td',{text:formatNumber(item.remainingQuantity)})));
   materials.append(el('div',{class:'field-table-scroll'},el('table',{},el('thead',{},el('tr',{},...['Référence','Unité','Prévu','Préparé','Restant'].map(text=>el('th',{scope:'col',text})))),body)));
  }else materials.append(message('Aucune ligne de matériel dans les préparations comptées. Un matériel non renseigné ne signifie pas « aucun matériel nécessaire ».'));
  replaceChildren(pagination,button('Précédent',run(async()=>{offset=Math.max(0,offset-30);await load(false);}),{disabled:offset===0}),
   el('span',{text:`${model.total?offset+1:0}–${Math.min(offset+30,model.total)} sur ${model.total} préparation(s)`}),
   button('Suivant',run(async()=>{offset+=30;await load(false);}),{disabled:offset+30>=model.total}));
  updateSelection();
 }
 function reportEditor(){window.dispatchEvent(new CustomEvent('pilot:calendar-editor',{detail:{open:!!modal,dirty:!!modal?.dirty}}));}
 function destroyDialog(){
  if(!modal)return;const owned=modal;modal=null;owned.controller.abort();owned.dialog.close();owned.dialog.remove();clearFieldPrint();reportEditor();
  if(owned.token&&!signal?.aborted)call('field.close',{token:owned.token}).catch(()=>{});
  if(owned.focus?.isConnected)owned.focus.focus({preventScroll:true});
 }
 async function closeDialog(force=false){
  if(modal?.dirty&&!force&&!await confirm('Fermer sans enregistrer ?','Votre saisie de préparation ou de catalogue serait abandonnée.','Abandonner la saisie'))return false;
  destroyDialog();return true;
 }
 async function openDialog(title){
  if(!await closeDialog())return null;
  const owned={dialog:el('dialog',{class:'field-modal'}),controller:new AbortController(),dirty:false,token:null,focus:document.activeElement,
   errors:el('div',{class:'field-errors','aria-live':'polite'}),body:el('div',{class:'field-modal-body'})};
  owned.dialog.append(el('header',{class:'field-modal-heading'},el('h2',{text:title}),button('Fermer',run(()=>closeDialog()),{icon:'close','attributes':{'aria-label':'Fermer le panneau'}})),owned.errors,owned.body);
  owned.dialog.addEventListener('cancel',event=>{event.preventDefault();run(()=>closeDialog())();});
  modal=owned;document.body.append(owned.dialog);owned.dialog.showModal();reportEditor();return owned;
 }
 function editorContext(owned){return{root:owned.body,api,signal:owned.controller.signal,state,
  markDirty:()=>{if(modal===owned){owned.dirty=true;reportEditor();}},
  onError:error=>problem(error,owned.errors),
  onCancel:()=>closeDialog(),
  onSaved:async()=>{
   // The save changed the revision. The previous rows must not accept a new selection
   // until a fresh list is actually painted, including when the first refresh fails.
   owned.dirty=false;rows.inert=selectionBar.inert=true;
   const opener=owned.focus?.dataset.fieldOpen;
   try{await Promise.all([closeDialog(true),load(false)]);}
   catch(error){
    if(error?.name!=='AbortError'&&!disposed)problem(new Error('La préparation est enregistrée. La liste n’a pas pu être actualisée. Utilisez « Actualiser » pour retrouver la fiche.'));
    return;
   }
   if(disposed)return;
   if(opener&&!rows.inert&&document.activeElement===document.body)
    rows.querySelector(`[data-field-open="${CSS.escape(opener)}"]`)?.focus({preventScroll:true});
   notify('La préparation a été enregistrée.');
  }};}
 async function openPreparation(args){
  const owned=await openDialog('Préparer l’intervention');if(!owned)return;
  owned.body.append(message('Lecture de la préparation…'));
  try{
   const value=await call('field.open',args,owned.controller.signal);if(modal!==owned)return;
   owned.token=value.token;owned.body.replaceChildren();renderMaterialEditor({...editorContext(owned),model:value});
  }catch(error){problem(error,owned.errors);if(modal===owned)replaceChildren(owned.body,button('Réessayer',run(()=>openPreparation(args)),{icon:'refresh'}));}
 }
 async function openCatalogue(){
  const owned=await openDialog('Catalogue du matériel');if(!owned)return;
  try{
   const value=await call('field.catalogue.get',{},owned.controller.signal);if(modal!==owned)return;
   if(value.conflicts)owned.body.append(button('Comparer les versions du catalogue',run(()=>showVersions(value.id)),{icon:'layers'}));
   renderCatalogueEditor({...editorContext(owned),model:value,onSaved:async()=>{owned.dirty=false;await closeDialog(true);notify('Le catalogue a été enregistré. Les anciennes fiches conservent leurs libellés.');}});
  }catch(error){problem(error,owned.errors);}
 }
 async function pickSource(kind){
  const owned=await openDialog(kind==='ticket'?'Choisir un ticket à préparer':'Choisir une visite à préparer');if(!owned)return;
  const query=el('input',{type:'search',maxLength:300,placeholder:'Rechercher…','aria-label':'Rechercher une source'});
  const first=el('input',{type:'date',value:from.value||today(zone)}),last=el('input',{type:'date',value:to.value||addDays(first.value,6)});
  const results=el('div',{class:'field-source-list'});let sequence=0,start=0;
  async function refresh(){
   const current=++sequence;owned.errors.replaceChildren();const range={from:first.value,to:last.value?addDays(last.value,1):'',timeZoneId:zone};
   if(!range.from||!range.to||range.to<=range.from)throw new Error('Choisissez une période valide, avec une fin égale ou postérieure au début.');
   const data=await call(kind==='ticket'?'calendar.tickets':'calendar.get',kind==='ticket'?{search:query.value,offset:start,limit:30}:range,owned.controller.signal);
   if(modal!==owned||current!==sequence)return;
   const entries=kind==='ticket'?data.rows:(data.occurrences||[]).filter(row=>row.kind!=='unavailability'&&normalize(row.title+' '+row.location).includes(normalize(query.value)));
   results.replaceChildren();
   if(!entries.length)results.append(empty('Aucune source correspondante',kind==='ticket'?'Chargez un relevé ou la démonstration, puis recommencez.':'Créez une intervention dans le planning ou élargissez la période.'));
   for(const row of entries.slice(0,200))results.append(el('div',{class:'field-source-row'},el('div',{},el('strong',{text:kind==='ticket'?`${row.id} · ${row.title}`:row.title}),message(kind==='ticket'?row.location||'Localisation non renseignée':`${row.startLocal||''} · ${row.location||'Localisation non renseignée'}`)),
    button('Préparer',run(()=>openPreparation({...range,revision:data.revision,...(kind==='ticket'?{ticketKey:row.key}:{occurrenceKey:row.key})})),{primary:true,attributes:{'data-field-source':row.key}})));
   if(kind==='ticket')results.append(toolbar(button('Précédent',run(async()=>{start=Math.max(0,start-30);await refresh();}),{disabled:start===0}),message(`${data.total} ticket(s) correspondant(s)`),button('Suivant',run(async()=>{start+=30;await refresh();}),{disabled:start+30>=data.total})));
   else if(entries.length>200)results.append(message('Plus de 200 visites correspondent. Affinez la recherche ou la période ; aucune visite masquée n’est sélectionnée automatiquement.'));
  }
  owned.body.append(toolbar(field('Rechercher',query),...(kind==='ticket'?[]:[field('Du',first),field('Au, inclus',last)]),button('Rechercher',run(async()=>{start=0;await refresh();}),{icon:'search'})),results);
  try{await refresh();}catch(error){problem(error,owned.errors);}
 }
 async function showReport(args=reportArgs()){
  const owned=await openDialog('Fiches d’intervention');if(!owned)return;
  try{
   const result=await call('field.report',args,owned.controller.signal);if(modal!==owned)return;
   const report=safeFieldReport(result.html);
   owned.body.append(message('Fiches rapprochées du planning et des données connues du poste. Les archives non vérifiées sont signalées et exclues de la liste de départ. Aucune collecte GLPI n’est déclenchée.'),
    toolbar(button('Imprimer ces fiches',()=>printFieldReport(report),{primary:true,icon:'log',attributes:{'data-field-action':'print'}}),
     button('Exporter en HTML',run(async()=>{const saved=await call('field.export',args,owned.controller.signal);if(!saved.cancelled)notify('Les fiches ont été exportées.');}),{icon:'download'})),report);
  }catch(error){problem(error,owned.errors);}
 }
 async function showVersions(id,offset=0){
  const owned=await openDialog('Comparer et résoudre les versions');if(!owned)return;
  try{
   const value=await call('field.versions',{id,offset},owned.controller.signal);if(modal!==owned)return;
   owned.body.append(message('Choisissez explicitement une version après comparaison. Les deux versions restent archivées ; les autres conflits ne sont pas effacés.'));
   async function resolve(version,choice){
    if(!await confirm('Confirmer cette version ?',choice==='incoming'?'La version reçue remplace le contenu affiché comme courant. Pour un catalogue, les références absentes sont conservées archivées.':'La version actuellement enregistrée est conservée. La version reçue reste dans l’historique.','Confirmer le choix'))return;
    await call('field.conflict.resolve',{id,conflictId:version.id,choice,expectedRevision:value.expectedRevision,confirmed:true},owned.controller.signal);
    owned.dirty=false;await closeDialog(true);await load();notify('Le choix a été enregistré et sera partagé à la prochaine synchronisation.');
   }
   for(const version of value.items||[])owned.body.append(card(formatDate(version.recordedAt),
    ...[['Version actuellement enregistrée',value.current||version.current],['Version reçue',version.incoming]].map(([label,item])=>el('section',{},el('h3',{text:label}),
     message(`${item.author||'Auteur non renseigné'} · ${formatDate(item.updatedAt)}`),el('pre',{class:'field-version-data',text:JSON.stringify(item.payload,null,2)}))),
    toolbar(button('Conserver la version actuelle',run(()=>resolve(version,'current')),{icon:'check'}),button('Choisir la version reçue',run(()=>resolve(version,'incoming')),{icon:'layers'}))));
   if(!value.total)owned.body.append(message('Aucune version en conflit.'));
   owned.body.append(toolbar(button('Versions précédentes',run(()=>showVersions(id,Math.max(0,offset-5))),{disabled:offset===0}),
    message(`${offset+value.items.length} sur ${value.total} version(s)`),button('Versions suivantes',run(()=>showVersions(id,offset+value.items.length)),{disabled:offset+value.items.length>=value.total})));
  }catch(error){problem(error,owned.errors);}
 }
 shell.append(el('header',{class:'field-heading'},el('div',{},el('h2',{text:'Préparer, emporter, intervenir'}),message('Du matériel prévu pour une intervention. Pas de gestion de stock.')),
  toolbar(button('Préparer un ticket',run(()=>pickSource('ticket')),{primary:true,icon:'ticket',attributes:{'data-field-action':'ticket'}}),
   button('Préparer une visite',run(()=>pickSource('occurrence')),{icon:'calendar',attributes:{'data-field-action':'visit'}}),button('Catalogue',run(openCatalogue),{icon:'layers',attributes:{'data-field-action':'catalogue'}}))),
  toolbar(field('Périmètre',scope),field('Rechercher',search),field('Du',from),field('Au, inclus',to),
   button('Appliquer',run(async()=>{offset=0;await load(true);}),{icon:'search'}),button('Actualiser',run(()=>load(false)),{icon:'refresh'})),
  filters.element,status,errors,notices,metrics,
  toolbar(filters.selectAll,filters.tourReport,button('Effacer les filtres',run(async()=>{search.value='';from.value='';to.value='';closed.checked=false;scope.value='all';filters.reset();offset=0;await load(true);}),{attributes:{'data-field-action':'reset-filters'}})),
  selectionBar,message('Les sélections inchangées sont conservées lors d’une actualisation. Un changement de filtre les réinitialise. Les préparations sans créneau restent incluses dans la période.'),rows,pagination,materials);
 filters.element.querySelector('.reading-options-body').append(el('label',{class:'field-check'},closed,el('span',{text:'Inclure les fiches terminées ou annulées'})));
 scope.addEventListener('change',run(async()=>{offset=0;await load(true);}));
 closed.addEventListener('change',run(async()=>{offset=0;await load(true);}));
 search.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.isComposing){event.preventDefault();void run(async()=>{offset=0;await load(true);})();}});
 if(ctx.params?.fieldReturnTour&&ctx.params?.fieldTourId)shell.prepend(toolbar(button('Retour à la tournée',()=>navigate('tours',{tourId:ctx.params.fieldTourId,terrainPanel:'planning'}),{icon:'route'})));
 root.append(shell);updateSelection();
 const onAbort=()=>{disposed=true;destroyDialog();clearFieldPrint();};signal?.addEventListener('abort',onAbort,{once:true});
 try{
  await load();
  if(ctx.params?.fieldPlanId)await openPreparation({id:ctx.params.fieldPlanId});
  else if(ctx.params?.fieldTicketKey)await openPreparation({ticketKey:ctx.params.fieldTicketKey,revision:model.revision});
  else if(ctx.params?.fieldOccurrenceKey)await openPreparation({occurrenceKey:ctx.params.fieldOccurrenceKey,revision:model.revision});
  else if(ctx.params?.fieldShowReport&&ctx.params?.fieldTourId)await showReport({tourId:ctx.params.fieldTourId,timeZoneId:zone,title:'Fiches de tournée',archive:false});
 }catch(error){problem(error);}
 const cleanup=async()=>{if(!await closeDialog())throw new DOMException('La saisie reste ouverte.','AbortError');disposed=true;signal?.removeEventListener('abort',onAbort);clearFieldPrint();shell.remove();};
 cleanup.refresh=async()=>{if(modal){notify('Fermez la préparation ouverte avant d’actualiser.','info');return;}await load();};
 cleanup.identityParams=()=>({...ctx.params,mine:scope.value==='mine'});
 return cleanup;
}
