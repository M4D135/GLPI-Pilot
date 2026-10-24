import {el,replaceChildren,button,icon,badge,toolbar,card,empty,formatNumber,formatDate,dialog,field,input,select,form,confirm,showError,isSimulation,extractionStatus} from '../ui.js';
import {importLocalTestFile} from '../local-file-test.js';

export async function renderHomeWorkspace(ctx){
 const {root,api,state,navigate,notify,signal}=ctx;
 root.classList.add('home-workspace');
 const popups=new Set();let disposed=false,day=null,alerts=null,page=0;
 const popup=(title,body,actions)=>{const opened=dialog(title,body,actions);popups.add(opened);opened.element.addEventListener('pilot:closed',()=>popups.delete(opened),{once:true});return opened;};
 const safe=handler=>async(...args)=>{if(disposed||signal?.aborted)return;try{return await handler(...args);}catch(error){if(error.name!=='AbortError')notify(error.message,'danger');}};
 const call=(op,args={})=>api.call(op,args,{signal});
 const today=new Intl.DateTimeFormat('fr-FR',{weekday:'long',day:'numeric',month:'long'}).format(new Date());
 const source=state.session,simulation=isSimulation(source),receipt=extractionStatus(source);
 const sourceText=source.loaded?`${formatNumber(source.rows)} lignes · ${source.testFileName||source.title||'Relevé courant'}`:'Aucun relevé chargé';
 const primary=state.session.loaded?button('Ouvrir le pilotage',()=>navigate('pilot'),{primary:true,icon:'chart'}):button('Charger des données',()=>navigate('data'),{primary:true,icon:'download'});
 root.append(el('section',{class:'hero home-hero'},el('div',{class:'home-hero-copy'},el('p',{class:'home-date',text:today}),
  el('h2',{text:'Votre journée, en un regard.'}),el('p',{text:'Les tickets à traiter, les signaux à suivre et vos interventions réunis au même endroit.'}),
  toolbar(primary,button('Carte des tickets',()=>navigate('terrain-map'),{icon:'map'}),button('Importer en simulation',()=>importLocalTestFile(ctx),{icon:'folder'})),
  el('div',{class:'home-source-line'},badge(simulation?'Simulation':source.historical?'Archive':source.loaded?receipt.receipt:'Prêt à démarrer',simulation?'info':source.loaded?receipt.tone:''),el('span',{text:sourceText})),
  source.loaded&&receipt.attention?el('p',{class:'notice warning home-source-attention',text:receipt.attention}):null,
  !source.loaded?button('Simulation avec données fictives',safe(async()=>{await api.call('app.demo');notify('Simulation ouverte avec des tickets fictifs.');await navigate('pilot');}),{icon:'spark'}):null),
  el('div',{class:'hero-icon home-hero-mark','aria-hidden':'true'},icon('layers'))));
 const counters=el('div',{class:'home-counters','aria-label':'Repères de la journée'});
 const priorityContent=el('div'),alertContent=el('div'),tourContent=el('div');
 const search=input('',{placeholder:'Ticket, titre, ville ou notes','aria-label':'Rechercher dans mes priorités',maxLength:300});
 const scopes=['Aujourd’hui','Mes tickets du relevé','File personnelle','Épinglés','Rappels à traiter','Reportés','Tous les conservés'];
 const scope=select(scopes.map((label,value)=>({value:String(value),label})));scope.setAttribute('aria-label','Périmètre de mes priorités');
 const priorities=card('Mes priorités',el('p',{class:'home-section-intro',text:'Une file personnelle, indépendante des listes de l’équipe.'}),
  toolbar(search,scope,button('Appliquer',safe(async()=>{page=0;await saveView();await loadDay();}),{icon:'search'})),priorityContent);
 priorities.classList.add('home-priority-panel');priorities.dataset.homeSection='priorities';
 const signals=card('À surveiller',toolbar(button('Évaluer le relevé actuel',safe(async()=>{await call('alerts.evaluate');await loadAlerts();}),{icon:'refresh',disabled:!source.loaded||(!simulation&&source.historical)}),
  button('Règles de surveillance',()=>editRules(),{icon:'settings',attributes:{dataset:{openPanel:'alert-rules'}}})),alertContent);
 signals.classList.add('home-alert-panel');signals.dataset.homeSection='alerts';
 const tours=card('Aujourd’hui sur le terrain',tourContent,button('Préparer une intervention',()=>navigate('terrain-map'),{icon:'map'}));tours.dataset.homeSection='tours';
 const tools=el('details',{class:'home-personal-tools'},el('summary',{text:'Organiser mon travail'}),toolbar(
  button('Choisir mon identité',()=>navigate('pilot',{panel:'technicians'}),{icon:'users'}),
  button('Modèles de listes',()=>editTemplates(),{icon:'layers'}),button('Rappels personnels',()=>editReminders(),{icon:'bell'}),button('Briefing de session',()=>showBriefing(),{icon:'log'})));
 root.append(counters,el('div',{class:'grid home-columns'},priorities,signals),el('div',{class:'grid home-bottom'},tours,card('Mes outils',tools)));
 function anchor(name){root.querySelector(`[data-home-section="${name}"]`)?.scrollIntoView({block:'start',behavior:'instant'});}
 function paintCounters(){
  const active=alerts?.incidents?.filter(entry=>entry.active).length;
  replaceChildren(counters,...[
   ['priorities','À traiter',day?.total,'ticket'],['alerts','Alertes actives',active,'bell'],['tours','Tournées du jour',day?.tours?.length,'route']
  ].map(([section,label,value,symbol])=>{const node=button('',()=>anchor(section),{attributes:{class:'button home-counter metric',dataset:{openPanel:section}}});replaceChildren(node,icon(symbol),el('span',{},el('strong',{class:'metric-value',text:value==null?'—':formatNumber(value)}),el('span',{class:'metric-label',text:label})));return node;}));
 }
 async function saveView(){await call('workplace.preferences.save',{daySearch:search.value,dayScope:Number(scope.value)});}
 async function loadDay(initial=false){
  const args=initial?{page:0,pageSize:8}:{page,pageSize:8,search:search.value,scope:Number(scope.value)};
  day=await call('workplace.day.get',args);if(disposed)return;
  if(initial){search.value=day.search||'';scope.value=String(day.scope||0);}
  paintDay();paintCounters();
 }
 function openTicket(row){return navigate('records',row.saved?{ticketKey:row.ticketKey}:{rowIndex:row.rowIndex,revision:day.revision});}
 function paintDay(){
  const items=day.rows.map(row=>el('article',{class:'home-ticket-row'},
   el('div',{class:'home-ticket-main'},button(`#${row.id} · ${row.title||'Sans titre'}`,()=>openTicket(row),{attributes:{class:'button home-ticket-title'}}),
    el('div',{class:'home-ticket-meta'},badge(row.status||'État non vérifié'),row.city?el('span',{text:row.city}):null,
     row.reminderDue?badge('Rappel à traiter','warning'):null,row.pinned?badge('Épinglé','info'):null,!row.inSource?badge('Absent du relevé · à vérifier','warning'):null)),
   el('div',{class:'home-ticket-side'},row.reminderAt?el('small',{text:formatDate(row.reminderAt)}):row.due?el('small',{text:'Échéance '+formatDate(row.due)}):null,
    row.queueOrder!=null?toolbar(button('↑',safe(async()=>{await call('workplace.queue.move',{ticketKey:row.ticketKey,direction:-1});await loadDay();}),{title:'Monter dans ma file'}),button('↓',safe(async()=>{await call('workplace.queue.move',{ticketKey:row.ticketKey,direction:1});await loadDay();}),{title:'Descendre dans ma file'})):null)));
  replaceChildren(priorityContent,day.personalName?el('p',{class:'home-identity',text:'Votre identité · '+day.personalName}):el('p',{class:'help',text:'Choisissez votre identité ou ajoutez des tickets à votre file pour construire vos priorités.'}),
   items.length?el('div',{class:'home-ticket-list'},...items):empty('Votre file est libre','Ajoutez des tickets depuis la liste, épinglez un dossier ou planifiez un rappel.'),
   toolbar(button('Précédent',safe(async()=>{page--;await loadDay();}),{disabled:page===0}),el('span',{class:'help',text:day.total?`${page*day.pageSize+1}–${Math.min(day.total,(page+1)*day.pageSize)} sur ${formatNumber(day.total)}`:'0 ticket'}),button('Suivant',safe(async()=>{page++;await loadDay();}),{disabled:(page+1)*day.pageSize>=day.total}),button('Ouvrir les tickets',()=>navigate('records'),{icon:'ticket'})));
  replaceChildren(tourContent,day.tours.length?el('div',{class:'home-tour-list'},...day.tours.map(tour=>button('',()=>navigate('terrain-map',{panel:'planning',tourId:tour.tourId}),{attributes:{class:'button home-tour'}})).map((node,index)=>{const tour=day.tours[index];replaceChildren(node,icon('route'),el('span',{},el('strong',{text:tour.name}),el('small',{text:[tour.startTime,tour.endTime].filter(Boolean).join(' – ')||'Horaire à préciser'})),badge(({draft:'Brouillon',ready:'Prête',done:'Terminée'})[tour.state]||tour.state));return node;})):empty('Pas de tournée aujourd’hui','La carte permet de choisir les tickets à visiter et de préparer leur ordre.'));
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
 const loaded=await Promise.allSettled([loadDay(true),loadAlerts()]);
 loaded.forEach((result,index)=>{if(result.status==='rejected'&&result.reason.name!=='AbortError')showError(index===0?priorityContent:alertContent,result.reason);});
 if(!disposed&&!signal?.aborted){const panel=ctx.params?.panel||ctx.params?.section;if(panel==='alert-rules')editRules();else if(panel==='templates')editTemplates();else if(panel==='reminders'||panel==='preferences')editReminders();else if(panel==='briefing')showBriefing();else if(['priorities','alerts','tours'].includes(panel))anchor(panel);}
 return ()=>{disposed=true;for(const modal of [...popups])modal.close();popups.clear();};
}
