import {el,replaceChildren,button,icon,badge,toolbar,card,empty,formatNumber,formatDate,dialog,field,input,select,form,confirm,showError,isSimulation,extractionStatus} from '../ui.js';
import {importLocalTestFile} from '../local-file-test.js';
import {currentScope,rememberScope,readWorkspaceView,saveWorkspaceView} from '../work-context.js';
import {chooseWorkspaceTeams} from '../workspace-scope.js';
import {teamScopeCatalog,teamScopeSelection} from '../team-scope.js';
import {sourceCacheCard} from '../source-cache.js';

export async function renderHomeWorkspace(ctx){
 const {root,api,state,navigate,notify,signal}=ctx;
 root.classList.add('home-workspace');
 const popups=new Set();let disposed=false,day=null,alerts=null,page=0,overview=null,agenda=null,bootstrap=null;
 const preference=readWorkspaceView(state,'home',{personal:false,calendarMine:false}),shared=currentScope(state);
 const popup=(title,body,actions)=>{const opened=dialog(title,body,actions);popups.add(opened);opened.element.addEventListener('pilot:closed',()=>popups.delete(opened),{once:true});return opened;};
 const safe=handler=>async(...args)=>{if(disposed||signal?.aborted)return;try{return await handler(...args);}catch(error){if(error.name!=='AbortError')notify(error.message,'danger');}};
 const call=(op,args={})=>api.call(op,args,{signal});
 const today=new Intl.DateTimeFormat('fr-FR',{weekday:'long',day:'numeric',month:'long'}).format(new Date());
 const source=state.session,simulation=isSimulation(source),receipt=extractionStatus(source);
 const sourceText=source.loaded?`${formatNumber(source.rows)} lignes · ${source.testFileName||source.title||'Relevé courant'}`:'Aucun relevé chargé';
 root.append(el('header',{class:'home-overview-heading'},el('div',{},el('p',{class:'home-date',text:today}),
  el('h2',{text:'À faire et à prévoir'}),el('div',{class:'home-source-line'},
   source.loaded?badge(receipt.receipt,simulation?'info':receipt.tone):null,el('span',{text:sourceText}))),
  toolbar(button('Planifier',()=>navigate('interventions',{plan:true}),{primary:true,icon:'calendar'}),button('Carte des tickets',()=>navigate('terrain-map'),{icon:'map'}),
   !source.loaded?button('Collecter depuis GLPI',()=>navigate('data'),{icon:'download'}):button('Ouvrir le pilotage',()=>navigate('pilot'),{icon:'chart'}))));
 if(source.loaded&&receipt.attention)root.append(el('p',{class:'notice warning home-source-attention',text:receipt.attention}));
 const cachePosition=document.createComment('cache local');root.append(cachePosition);
 async function loadCache(){
  const cache=await sourceCacheCard(ctx,{compact:true});
  if(disposed||signal?.aborted)return;
  if(cache)cachePosition.replaceWith(cache);else cachePosition.remove();
 }
 const identityChoice=el('section',{class:'home-identity-choice','aria-label':'Votre identité'});
 const chooseIdentity=()=>ctx.chooseIdentity?ctx.chooseIdentity():navigate('interventions',{identity:true});
 function paintIdentity(){
  const key=state.settings.dashboard.personalTechnician||'',name=state.identity?.key===key?state.identity.label:day?.personalName||'';
  replaceChildren(identityChoice,el('span',{class:'identity-avatar','aria-hidden':'true'},icon('users')),
   el('div',{},el('strong',{text:key?name||'Votre identité est enregistrée':'Retrouvez vos tickets et vos interventions'}),
    el('p',{text:key?'Votre choix est mémorisé. Vous pouvez le changer à tout moment.':'Choisissez votre nom une fois pour personnaliser votre espace.'})),
   button(key?'Changer mon identité':'Choisir mon identité',chooseIdentity,{primary:!key,icon:key?'users':'check'}));
 }
 paintIdentity();window.addEventListener('pilot:identity-display',paintIdentity);root.append(identityChoice);
 const scopeControls=el('div',{class:'home-scope-controls toolbar'});
 root.append(scopeControls);
 const counters=el('div',{class:'home-counters','aria-label':'Repères de la journée'});
 const priorityContent=el('div'),alertContent=el('div'),tourContent=el('div');
 const search=input('',{placeholder:'Ticket, titre, ville ou notes','aria-label':'Rechercher dans mes priorités',maxLength:300});
 const scopes=['Aujourd’hui','Mes tickets du relevé','File personnelle','Épinglés','Rappels à traiter','Reportés','Tous les conservés'];
 const scope=select(scopes.map((label,value)=>({value:String(value),label})));scope.setAttribute('aria-label','Périmètre de mes priorités');
 const personalControls=toolbar(search,scope,button('Appliquer',safe(async()=>{page=0;await saveView();await loadDay();}),{icon:'search'}));
 const priorities=card('Priorités',personalControls,priorityContent);
 priorities.classList.add('home-priority-panel');priorities.dataset.homeSection='priorities';
 const signals=card('À surveiller',toolbar(button('Évaluer le relevé actuel',safe(async()=>{await call('alerts.evaluate');await loadAlerts();}),{icon:'refresh',disabled:!source.loaded||(!simulation&&source.historical)}),
  button('Règles de surveillance',()=>editRules(),{icon:'settings',attributes:{dataset:{openPanel:'alert-rules'}}})),alertContent);
 signals.classList.add('home-alert-panel');signals.dataset.homeSection='alerts';
 const calendarScope=select([{value:'all',label:'Toutes les interventions'},{value:'mine',label:'Mes interventions'}],preference.calendarMine?'mine':'all');calendarScope.setAttribute('aria-label','Périmètre des prochaines interventions');calendarScope.addEventListener('change',safe(async()=>{preference.calendarMine=calendarScope.value==='mine';saveWorkspaceView(state,'home',preference);await loadCalendar();}));
 const tours=card('Agenda · 7 prochains jours',toolbar(calendarScope,button('Ouvrir le planning',()=>navigate('interventions'),{icon:'calendar'})),tourContent);tours.dataset.homeSection='tours';
 const tools=el('details',{class:'home-personal-tools'},el('summary',{text:'Organiser mon travail'}),toolbar(
  button('Choisir mon identité',chooseIdentity,{icon:'users'}),
  button('Équipe et charge',()=>navigate('pilot',{panel:'technicians'}),{icon:'users'}),
  button('Modèles de listes',()=>editTemplates(),{icon:'layers'}),button('Rappels personnels',()=>editReminders(),{icon:'bell'}),button('Briefing de session',()=>showBriefing(),{icon:'log'})));
 root.append(counters,el('div',{class:'grid home-columns'},priorities,tours),el('div',{class:'grid home-bottom'},signals,card('Organisation',tools)));
 function anchor(name){root.querySelector(`[data-home-section="${name}"]`)?.scrollIntoView({block:'start',behavior:'instant'});}
 function paintScope(){
  personalControls.hidden=!preference.personal;
  const teams=bootstrap?teamScopeSelection(teamScopeCatalog(bootstrap.focus||[],bootstrap.groups||[]),shared.focusKeys):null;
  replaceChildren(scopeControls,button('Tickets du périmètre',safe(async()=>{preference.personal=false;saveWorkspaceView(state,'home',preference);paintScope();await loadOverview();}),{primary:!preference.personal,icon:'users'}),
   button('Ma file personnelle',safe(async()=>{preference.personal=true;saveWorkspaceView(state,'home',preference);paintScope();if(day)paintDay();await loadOverview();}),{primary:preference.personal,icon:'ticket'}),
   button(teams?.keys.length?`Équipes (${teams.keys.length})`:'Équipes',()=>{if(!bootstrap)return;const modal=chooseWorkspaceTeams(ctx,bootstrap,shared.focusKeys,async values=>{shared.focusKeys=values;rememberScope(state,shared);paintScope();await loadOverview();});popups.add(modal);},{icon:'users',disabled:!bootstrap?.hasSource}),
   el('span',{class:'help',text:teams?.options.length?teams.options.map(item=>item.label).join(' + '):'Toutes les équipes'}));
 }
 function paintCounters(){
  const stats=overview?.metrics||[],get=key=>stats.find(item=>item.key===key)?.value;
  const entries=[['active','Tickets actifs',get('active'),'ticket'],['unassigned','Sans technicien',get('unassigned'),'users'],['overdue','Échéance dépassée',get('overdue'),'bell'],['calendar','Interventions · 7 jours',agenda?.summary?.interventions,'calendar']];
  replaceChildren(counters,...entries.map(([key,label,value,symbol])=>{const node=button('',safe(async()=>{
   if(key==='calendar')return navigate('interventions');if(!overview?.token)return;const result=await call('workplace.overview.rows',{token:overview.token,key});await navigate('records',{indices:result.rows,revision:result.revision});
  }),{disabled:value==null,attributes:{class:'button home-counter metric',dataset:{openPanel:key,metricKind:key,metricKnown:String(value!=null)}}});replaceChildren(node,el('span',{class:'home-counter-emblem','aria-hidden':'true'},icon(symbol)),el('span',{},el('strong',{class:'metric-value',text:value==null?'—':formatNumber(value)}),el('span',{class:'metric-label',text:label})));return node;}));
 }
 async function loadOverview(){
  overview=await call('workplace.overview',{...shared,mine:preference.personal&&!!state.settings.dashboard.personalTechnician});if(disposed)return;
  paintCounters();if(preference.personal){if(day)paintDay();return;}
  replaceChildren(priorityContent,!overview.hasSource?empty('Les priorités apparaîtront avec vos tickets','Collectez vos tickets depuis GLPI pour lire le stock, les affectations et les échéances. Un extrait CSV / Excel peut aussi être utilisé.'):
   el('div',{class:'home-ticket-list'},...overview.priority.map(row=>el('article',{class:'home-ticket-row'},el('div',{class:'home-ticket-main'},
    button(`#${row.id} · ${row.title||'Sans titre'}`,()=>navigate('records',{rowIndex:row.rowIndex,revision:overview.revision}),{attributes:{class:'button home-ticket-title'}}),
    el('div',{class:'home-ticket-meta'},badge(row.status||'Statut inconnu'),...row.reasons.map(reason=>badge(reason,'warning'))),
    row.technician?el('small',{class:'help',text:row.technician}):null),
    button('Planifier',()=>navigate('interventions',{plan:true,rowIndex:row.rowIndex,revision:overview.revision}),{icon:'calendar'})))),
   overview.hasSource&&overview.priority.length===0?empty('Aucun ticket actif dans ce périmètre','Changez les filtres pour examiner un autre périmètre.'):null,
   el('p',{class:'help',text:'Ordre de lecture : échéance dépassée, priorité haute, absence de technicien, puis mise à jour et ancienneté. Les données absentes restent inconnues.'}),
   toolbar(button('Tous les tickets',()=>navigate('records'),{icon:'ticket'}),source.loaded?button(simulation?'Collecter la simulation':'Collecter depuis GLPI',()=>navigate('data'),{icon:'download'}):null,button('Importer CSV / Excel',()=>importLocalTestFile(ctx),{icon:'folder'})));
 }
 async function loadCalendar(){
  const from=new Date(),to=new Date();to.setDate(to.getDate()+7);const date=value=>`${value.getFullYear()}-${String(value.getMonth()+1).padStart(2,'0')}-${String(value.getDate()).padStart(2,'0')}`;
  agenda=await call('calendar.summary',{from:date(from),to:date(to),scope:preference.calendarMine?'mine':'all'});if(disposed)return;
  replaceChildren(tourContent,agenda.identityRequired?el('p',{class:'help',text:'Choisissez votre identité pour afficher uniquement vos interventions.'}):null,
   ...(agenda.upcoming||[]).slice(0,6).map(item=>el('article',{class:'home-agenda-item'},el('div',{class:'home-agenda-time'},el('span',{text:item.allDay?'Journée':item.startLocal?.slice(11,16)||'—'}),el('small',{text:item.startLocal?.slice(5,10).split('-').reverse().join('/')})),
    el('div',{},el('h3',{text:item.title}),el('p',{text:[item.location||item.address||item.siteId,item.participantKeys?.join(' · ')].filter(Boolean).join(' · ')})),
    button('Ouvrir',()=>navigate('interventions',{eventId:item.interventionId,interventionId:item.interventionId,occurrenceKey:item.key,date:item.startLocal?.slice(0,10)}),{icon:'calendar'}))),
   !agenda.upcoming?.length?empty('Aucune intervention à venir','Planifiez un passage lié à un ticket ou créez une intervention libre.'):null,
   ...(agenda.issues||[]).filter(issue=>issue.severity==='warning'||issue.severity==='error').slice(0,2).map(issue=>el('p',{class:'notice warning',text:issue.message})),
   toolbar(button('Nouvelle intervention',()=>navigate('interventions',{plan:true}),{icon:'plus'}),button('Choisir mon identité',chooseIdentity,{icon:'users'})));
  paintCounters();
 }
 async function saveView(){await call('workplace.preferences.save',{daySearch:search.value,dayScope:Number(scope.value)});}
 async function loadDay(initial=false){
  const args=initial?{page:0,pageSize:8}:{page,pageSize:8,search:search.value,scope:Number(scope.value)};
  day=await call('workplace.day.get',args);if(disposed)return;
  if(initial){search.value=day.search||'';scope.value=String(day.scope||0);}
  paintIdentity();if(preference.personal)paintDay();paintCounters();
 }
 function openTicket(row){return navigate('records',row.saved?{ticketKey:row.ticketKey}:{rowIndex:row.rowIndex,revision:day.revision});}
 function paintDay(){
  const items=day.rows.map(row=>el('article',{class:'home-ticket-row'},
   el('div',{class:'home-ticket-main'},button(`#${row.id} · ${row.title||'Sans titre'}`,()=>openTicket(row),{attributes:{class:'button home-ticket-title'}}),
    el('div',{class:'home-ticket-meta'},badge(row.status||'État non vérifié'),row.city?el('span',{text:row.city}):null,
     row.reminderDue?badge('Rappel à traiter','warning'):null,row.pinned?badge('Épinglé','info'):null,!row.inSource?badge('Absent du relevé · à vérifier','warning'):null)),
   el('div',{class:'home-ticket-side'},row.reminderAt?el('small',{text:formatDate(row.reminderAt)}):row.due?el('small',{text:'Échéance '+formatDate(row.due)}):null,
    row.queueOrder!=null?toolbar(button('↑',safe(async()=>{await call('workplace.queue.move',{ticketKey:row.ticketKey,direction:-1});await loadDay();}),{title:'Monter dans ma file'}),button('↓',safe(async()=>{await call('workplace.queue.move',{ticketKey:row.ticketKey,direction:1});await loadDay();}),{title:'Descendre dans ma file'})):null)));
  const personalName=day.personalName||(state.identity?.key===state.settings.dashboard.personalTechnician?state.identity.label:'');
  replaceChildren(priorityContent,personalName?el('p',{class:'home-identity',text:'Votre identité · '+personalName}):el('p',{class:'help',text:state.settings.dashboard.personalTechnician?'Votre identité est enregistrée. Ajoutez des tickets à votre file ou chargez un relevé avec vos affectations.':'Choisissez votre identité ou ajoutez des tickets à votre file pour construire vos priorités.'}),
   items.length?el('div',{class:'home-ticket-list'},...items):empty('Votre file est libre','Ajoutez des tickets depuis la liste, épinglez un dossier ou planifiez un rappel.'),
   toolbar(button('Précédent',safe(async()=>{page--;await loadDay();}),{disabled:page===0}),el('span',{class:'help',text:day.total?`${page*day.pageSize+1}–${Math.min(day.total,(page+1)*day.pageSize)} sur ${formatNumber(day.total)}`:'0 ticket'}),button('Suivant',safe(async()=>{page++;await loadDay();}),{disabled:(page+1)*day.pageSize>=day.total}),button('Ouvrir les tickets',()=>navigate('records'),{icon:'ticket'})));

 }
 async function loadAlerts(){alerts=await call('alerts.get');if(disposed)return;paintAlerts();paintCounters();}
 function paintAlerts(){
  const active=alerts.incidents.filter(entry=>entry.active),closed=alerts.incidents.length-active.length;
  replaceChildren(alertContent,active.length?el('div',{class:'home-signal-list'},...active.slice(0,8).map(entry=>el('article',{class:'home-signal'},el('div',{},badge(entry.state,'warning'),el('h3',{text:entry.name}),el('p',{text:entry.detail}),el('small',{text:entry.sourceLabel})),button('Examiner',()=>inspectAlert(entry),{icon:'arrow'})))):empty('Aucune alerte active','Évaluez le relevé ou définissez les seuils de surveillance utiles à votre équipe.'),
   ...(alerts.health||[]).slice(0,3).map(text=>el('p',{class:'notice warning',text})),
   active.length>8||closed?button(`Toutes les alertes (${formatNumber(alerts.incidents.length)})`,()=>allAlerts()):null);
 }
 function inspectAlert(entry){
  const modal=popup(entry.name,el('div',{class:'stack'},toolbar(badge(entry.state,entry.active?'warning':''),badge(entry.sourceLabel)),el('p',{text:entry.detail}),
   el('p',{text:`Valeur : ${entry.displayValue} · ${formatNumber(entry.occurrences)} observation(s)`}),...(entry.history||[]).map(line=>el('p',{class:'help',text:`${formatDate(line.at)} · ${line.action}${line.reason?' · '+line.reason:''}`}))),[
   {label:'Voir les tickets',action:safe(async close=>{const result=await call('alerts.rows',{id:entry.id});close();await navigate('records',{indices:result.rows,revision:result.revision});})},
   {label:'Prendre en compte',action:safe(async close=>{await call('alerts.acknowledge',{id:entry.id});close();await loadAlerts();})},
   {label:'Reporter',action:()=>{const minutes=input(60,{type:'number',min:5,max:10080});popup('Reporter l’alerte',field('Durée (minutes)',minutes),[{label:'Reporter',primary:true,action:safe(async close=>{if(!minutes.reportValidity())return;await call('alerts.snooze',{id:entry.id,minutes:Number(minutes.value)});close();modal.close();await loadAlerts();})}]);}},
   {label:'Clore',action:()=>{const reason=input('',{multiline:true});popup('Clore avec un motif',field('Motif',reason),[{label:'Clore',primary:true,action:safe(async close=>{if(!reason.value.trim())throw new Error('Le motif est obligatoire.');await call('alerts.resolve',{id:entry.id,reason:reason.value});close();modal.close();await loadAlerts();})}]);}}]);
 }
 function allAlerts(){if(!alerts)return;popup('Alertes de la session',el('div',{class:'home-alert-register'},...alerts.incidents.map(entry=>button(`${entry.name} · ${entry.state} · ${entry.displayValue}`,()=>inspectAlert(entry)))));}
 function editRules(){
  if(!alerts)return;const list=el('div');const panel=popup('Règles de surveillance',el('div',{},el('p',{class:'help',text:'Ces règles évaluent des mesures du relevé. Une donnée absente ne devient jamais un zéro.'}),toolbar(button('Créer une règle',()=>edit(null),{primary:true,icon:'plus'})),list));
  panel.element.dataset.workspacePanel='alert-rules';
  const paint=()=>replaceChildren(list,...alerts.rules.map(rule=>el('article',{class:'home-rule'},el('div',{},el('strong',{text:rule.name}),el('p',{class:'help',text:`${rule.metricKey} · ${rule.evaluationMode} · ${rule.operator} ${rule.threshold}`})),toolbar(badge(rule.enabled?'Active':'Désactivée'),button('Modifier',()=>edit(rule)),button('Retirer',safe(async()=>{if(!await confirm('Retirer la règle ?',rule.name,'Retirer'))return;await call('alerts.rules',{rules:alerts.rules.filter(value=>value.id!==rule.id)});await loadAlerts();paint();}),{danger:true})))));
  function edit(rule){
   const initial=rule||{id:crypto.randomUUID().replaceAll('-',''),metricKey:'overdue',operator:'gt',evaluationMode:'value',threshold:0,minimumSample:1,requireComplete:true,enabled:true};
   const editor=form([{key:'name',label:'Nom de la règle',required:true},{key:'metricKey',label:'Mesure',required:true,help:'Identifiant d’un indicateur ou d’une mesure de requête du pilotage.'},
    {key:'operator',label:'Comparaison',type:'select',options:[{value:'gt',label:'Supérieur à'},{value:'gte',label:'Supérieur ou égal à'},{value:'lt',label:'Inférieur à'},{value:'lte',label:'Inférieur ou égal à'}]},
    {key:'evaluationMode',label:'Évaluation',type:'select',options:[{value:'value',label:'Valeur actuelle'},{value:'delta',label:'Variation absolue'},{value:'percent',label:'Variation en pourcentage'}]},
    {key:'threshold',label:'Seuil',type:'number',min:-1e12,max:1e12,step:'any'},{key:'minimumSample',label:'Échantillon minimal',type:'number',min:0,max:1000000},
    {key:'requireComplete',label:'Exiger un relevé complet',type:'checkbox'},{key:'enabled',label:'Règle active',type:'checkbox'}],initial);
   popup(rule?'Modifier la règle':'Créer une règle',editor.element,[{label:'Enregistrer',primary:true,action:safe(async close=>{const next={...initial,...editor.read()};await call('alerts.rules',{rules:rule?alerts.rules.map(value=>value.id===rule.id?next:value):[...alerts.rules,next]});close();await loadAlerts();paint();})}]);
  }
  paint();
 }
 function editTemplates(){
  if(!day)return;const choices=select([{value:'',label:'Nouveau modèle'},...Object.keys(day.templates).map(value=>({value,label:value}))]);
  const name=input('',{maxLength:100}),lines=input('',{multiline:true,maxLength:20000,rows:10});
  choices.addEventListener('change',()=>{name.value=choices.value;lines.value=(day.templates[choices.value]||[]).join('\n');});
  popup('Mes modèles de listes',el('div',{},field('Modèle enregistré',choices),field('Nom du modèle',name),field('Une action par ligne · 40 maximum',lines)),[
   {label:'Supprimer le modèle',action:safe(async close=>{if(!choices.value||!await confirm('Supprimer le modèle ?',choices.value,'Supprimer'))return;await call('workplace.template.remove',{name:choices.value});close();await loadDay();})},
   {label:'Enregistrer',primary:true,action:safe(async close=>{await call('workplace.template.save',{name:name.value,items:lines.value.split(/\r?\n/).filter(Boolean)});close();await loadDay();notify('Modèle enregistré.');})}]);
 }
 function editReminders(){
  if(!day)return;const enabled=el('input',{type:'checkbox',checked:day.desktopReminders});
  popup('Rappels personnels',el('div',{},el('label',{class:'work-check'},enabled,el('span',{text:'Afficher une notification Windows pour mes rappels'})),
   el('p',{class:'help',text:'Les notifications indiquent uniquement le nombre de rappels. Le verrouillage de session et les heures silencieuses différeront leur affichage.'})),[
   {label:'Vérifier maintenant',action:safe(async()=>{const result=await call('workplace.reminders.check');notify(`${formatNumber(result.count)} rappel(s) à traiter${result.deferred?' · notification différée':''}.`);})},
   {label:'Enregistrer',primary:true,action:safe(async close=>{await call('workplace.preferences.save',{desktopReminders:enabled.checked});day.desktopReminders=enabled.checked;close();notify('Préférence enregistrée.');})}]);
 }
 function showBriefing(){if(!day)return;let offset=0;const lines=day.briefing.lines||[],body=el('div');const paint=()=>replaceChildren(body,el('p',{text:day.briefing.summary}),...lines.slice(offset,offset+50).map(line=>el('p',{},el('strong',{text:`${line.ticketId||''} · ${line.kind}`}),el('span',{text:' '+line.message}))),toolbar(button('Précédent',()=>{offset=Math.max(0,offset-50);paint();},{disabled:offset===0}),el('span',{text:`${lines.length?offset+1:0}–${Math.min(lines.length,offset+50)} sur ${formatNumber(lines.length)}`}),button('Suivant',()=>{offset+=50;paint();},{disabled:offset+50>=lines.length})));paint();popup('Briefing de session',body);}
 scope.addEventListener('change',safe(async()=>{page=0;await saveView();await loadDay();}));search.addEventListener('keydown',event=>{if(event.key==='Enter')safe(async()=>{page=0;await saveView();await loadDay();})();});
 priorityContent.append(el('p',{class:'loading',text:'Chargement de vos priorités…'}));alertContent.append(el('p',{class:'loading',text:'Lecture des alertes…'}));paintCounters();
 paintScope();
 tourContent.append(el('p',{class:'loading',text:'Lecture de l’agenda…'}));
 // The shell and its navigation are ready while optional cards are still loading.
 async function loadCards(){
  const load=async(task,target)=>{try{await task;}catch(error){if(!disposed&&!signal?.aborted&&error.name!=='AbortError')showError(target,error);}};
  await Promise.all([load(loadCache(),priorityContent),load(loadDay(true),priorityContent),load(loadAlerts(),alertContent),
   load(loadOverview(),priorityContent),load(loadCalendar(),tourContent),
   load(call('dashboard.business.bootstrap').then(value=>{if(!disposed&&!signal?.aborted){bootstrap=value;paintScope();}}),priorityContent)]);
  if(disposed||signal?.aborted)return;
  const panel=ctx.params?.panel||ctx.params?.section;if(panel==='alert-rules')editRules();else if(panel==='templates')editTemplates();else if(panel==='reminders'||panel==='preferences')editReminders();else if(panel==='briefing')showBriefing();else if(['priorities','alerts','tours'].includes(panel))anchor(panel);
 }
 void loadCards().catch(error=>{if(!disposed&&!signal?.aborted)notify(error.message,'danger');});
 return ()=>{disposed=true;window.removeEventListener('pilot:identity-display',paintIdentity);for(const modal of [...popups])modal.close();popups.clear();};
}
