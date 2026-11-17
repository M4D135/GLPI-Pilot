'use strict';
// Synthetic business replies only; all shell, map, list and reader code is production code.
module.exports = async function install(page) {
 await page.evaluate(async()=>{
  const root=document.querySelector('#root');root.id='app';root.removeAttribute('style');
  const {api}=await window.importProduction('/web/js/bridge.js');
  const listeners=new Map();window.journeyCalls=[];window.fixtureUnexpected=[];
  window.emitJourney=(event,data)=>{for(const callback of listeners.get(event)||[])callback(data);};
  api.available=true;api.on=(event,callback)=>{if(!listeners.has(event))listeners.set(event,new Set());listeners.get(event).add(callback);return()=>listeners.get(event)?.delete(callback);};
  const headers=['ID','Titre','Statut','Attribué à - Technicien','Lieu','Description','Priorité','Ouverture'];
  const records=Array.from({length:154},(_,index)=>{
   const placeKey=index<45?'city:nord':index<70?'site:annexe':index<120?'city:sud':index<150?'site:val':'unknown';
   const place=placeKey==='city:nord'?'Nordville':placeKey==='site:annexe'?'Annexe du Parc':placeKey==='city:sud'?'Rive-Sud':placeKey==='site:val'?'Val-des-Rives':'Lieu non reconnu';
   const topics=['Accès au dossier partagé','Écran de la salle de réunion','Installation du poste de travail','Impression des documents','Connexion au réseau'];
   const descriptions=['L’accès au dossier de l’équipe est indisponible depuis ce matin. Vérifier les droits du groupe et la connexion du poste.',
    'Le second écran n’est plus détecté après la sortie de veille. Le câble a été vérifié ; prévoir un adaptateur de remplacement.',
    'Préparer le poste du nouvel arrivant, installer les outils autorisés et vérifier les périphériques avec le demandeur.',
    'Les documents restent dans la file d’impression. Le redémarrage de l’imprimante n’a pas résolu le problème.',
    'La connexion au réseau est intermittente dans le bureau. Contrôler le port et tester avec un autre câble.'];
   return{index,key:'demo-'+index,placeKey,cells:[String(2401+index),topics[index%5],index%4===0?'En attente':'En cours',index%2?'Sam Martin':'Alex Bernard',place,descriptions[index%5],index%7===0?'Haute':'Normale','2026-11-16 09:30:00']};
  });
  const session={loaded:true,revision:77,rows:records.length,columns:headers.length,complete:true,receiptComplete:true,demo:true,testMode:true,title:'Parcours de démonstration',testFileName:'Jeu de démonstration · aucun ticket professionnel',status:'Prêt',sourceKind:'simulation'};
  const settings={theme:'dark',interfaceZoomPercent:100,interface:{motion:'reduced'},dashboard:{personalTechnician:'',columnMappings:{description:'Description'}},automation:{},history:{},cities:{cities:[],sites:[]}};
  const bootstrap={version:'4.0.0',session,settings,capabilities:[],deployment:{offer:{available:false}}};
  const places=[{key:'city:nord',label:'Nordville',latitude:43.50,longitude:5.12,precision:'Commune · approximative'},
   {key:'site:annexe',label:'Annexe du Parc',latitude:43.53,longitude:5.22,precision:'Site'},
   {key:'city:sud',label:'Rive-Sud',latitude:43.34,longitude:5.06,precision:'Commune · approximative'},
   {key:'site:val',label:'Val-des-Rives',latitude:43.43,longitude:5.36,precision:'Site'},
   {key:'unknown',label:'Lieu non reconnu',latitude:null,longitude:null,precision:'À vérifier'}];
  const cityGroups=[{key:'city:nord',label:'Nordville',placeKeys:['city:nord','site:annexe']},{key:'city:sud',label:'Rive-Sud',placeKeys:['city:sud']},{key:'city:val',label:'Val-des-Rives',placeKeys:['site:val']}];
  const views=new Map();let viewNumber=0;
  const query=args=>{
   let rows=records.filter(row=>!Array.isArray(args.indices)||args.indices.includes(row.index));
   if(args.selectedRows?.length)rows=rows.filter(row=>args.selectedRows.includes(row.index));
   const fold=value=>String(value).toLocaleLowerCase('fr');
   if(args.search)rows=rows.filter(row=>fold(row.cells.join(' ')).includes(fold(args.search)));
   for(const filter of args.transform?.filters||[])rows=rows.filter(row=>fold(row.cells[filter.column]||'').includes(fold(filter.value||'')));
   for(const sort of [...(args.transform?.sorts||[])].reverse())rows.sort((a,b)=>(sort.type==='number'?Number(a.cells[sort.column])-Number(b.cells[sort.column]):a.cells[sort.column].localeCompare(b.cells[sort.column]))*(sort.descending?-1:1));
   return rows;
  };
  api.call=async(op,args={})=>{
   journeyCalls.push({op,args:structuredClone(args)});
   if(op==='app.bootstrap')return bootstrap;
   if(['app.ready','host.editor','automation.tick','workplace.reminders.check','operations.cancel','calendar.reminders','workplace.ticket.position'].includes(op))return{};
   if(op==='app.appearance'){Object.assign(settings,args);if(args.preferences)settings.interface=args.preferences;emitJourney('settings.changed',{settings});return{};}
   if(op==='cache.state')return{available:false};
   if(op==='workplace.overview')return{hasSource:true,revision:77,metrics:[],priority:[]};
   if(op==='workplace.day.get')return{rows:[],total:0,search:'',scope:0,templates:{}};
   if(op==='calendar.get')return{occurrences:[],resources:[],teams:[],sites:[],cities:[],issues:[],sync:{},summary:{interventions:0,linkedTickets:0,personHours:0},sourceRevision:77,timeZoneId:'Europe/Paris'};
   if(op==='calendar.summary')return{summary:{interventions:0},upcoming:[],issues:[]};
   if(op==='alerts.get')return{incidents:[],health:[],rules:[]};
   if(op==='dashboard.business.bootstrap')return{focus:[],groups:[],hasSource:true};
   if(op==='records.list'){
    const rows=query(args),offset=args.offset||0,limit=args.limit||100;
    return{rows:rows.slice(offset,offset+limit),headers,total:rows.length,sourceTotal:records.length,revision:77,offset,limit,focusPosition:args.locateRow!=null?rows.findIndex(row=>row.index===args.locateRow):null,
      columns:headers.map((_,sourceIndex)=>({sourceIndex,displayIndex:sourceIndex,visible:true,width:160})),session};
   }
   if(op==='records.schema')return{available:false};
   if(op==='records.cell'){const r=records.find(row=>row.index===args.row);return{header:headers[args.column],raw:r?.cells[args.column]||'',value:r?.cells[args.column]||'',text:r?.cells[args.column]||'',interpretations:[]};}
   if(op==='workplace.scope')return{technicians:[],profile:{},personalTechnician:''};
   if(op==='workplace.get')return{checklistTemplates:{},tabs:[]};
   if(op==='workplace.ticket.open'){
    const index=args.rowIndex??Number((args.ticketKey||'demo-0').replace('demo-','')),row=records[index];
    return{personal:{key:row.key,id:row.cells[0],title:row.cells[1],notes:'',checklist:[],readingPosition:{}},title:row.cells[1],canRead:false,readReason:'Données de démonstration : aucune lecture de votre portail GLPI.',loaded:false,warnings:[]};
   }
   if(op==='maps.workspace.get'){
    const rows=query(args.ticketSearch||{}),token='demo-map-'+(++viewNumber);views.set(token,rows);
    const locations=places.map(place=>({key:place.key,place:{...place,id:place.key},count:rows.filter(row=>row.placeKey===place.key).length}));
    return{token,revision:77,sourceDate:'2026-11-17T08:30:00+01:00',grain:'tickets',activeAvailable:true,sourceUnknownStatus:0,selectionLimit:2000,
     totals:{tickets:rows.length,placed:rows.filter(row=>row.placeKey!=='unknown').length,approximate:rows.filter(row=>row.placeKey.startsWith('city:')).length,withoutCoordinates:rows.filter(row=>row.placeKey==='unknown').length,review:rows.filter(row=>row.placeKey==='unknown').length},
     choices:{teams:[],groups:[],technicians:[],typeAvailable:true,technicianAvailable:true},filters:{focusKeys:[],technicianKeys:[],mine:false,type:'all',statusCodes:[1,2,3,4],visitDate:'2026-11-17'},locations,cityGroups,
     routes:[{id:'tickets',title:'Lieux fictifs · tickets',ordered:false,places:locations.filter(location=>location.count>0).map(location=>({...location.place,count:location.count,detail:location.count+' tickets',weight:location.count}))}]};
   }
   if(op==='maps.workspace.search'){
    const rows=views.get(args.token);if(!rows)throw Error('Invalid fixture token');
    const selected=new Set((args.placeKeys||[]).flatMap(key=>cityGroups.find(city=>city.key===key)?.placeKeys||[key]));
    const focused=rows.find(row=>row.index===args.rowIndex);
    const indices=rows.filter(row=>!selected.size||selected.has(row.placeKey)).map(row=>row.index);
    return{revision:77,total:indices.length,indices,baseIndices:rows.map(row=>row.index),places:(args.placeKeys||[]).map(key=>({key,label:cityGroups.find(city=>city.key===key)?.label||places.find(place=>place.key===key)?.label})),focusedRow:focused?.index,focusedPlaceKey:focused?.placeKey};
   }
   if(op==='maps.tiles')return{enabled:false};
   fixtureUnexpected.push(op);throw Error('Unexpected journey fixture operation '+op);
  };
  await window.importProduction('/web/js/app.js');
 });
};
