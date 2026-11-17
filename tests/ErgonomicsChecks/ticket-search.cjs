'use strict';
module.exports = async ({page,reset,ok,capture}) => {
 await page.setViewportSize({width:1440,height:1000});await reset();
 await page.evaluate(async()=>{
  const {renderTicketsWorkspace}=await window.importProduction('/web/js/pages/tickets-workspace.js');
  window.searchCalls=[];window.searchNavigations=[];window.searchErrors=[];
  window.searchState={session:{loaded:true,revision:7,rows:150},settings:{dashboard:{},interface:{}},capabilities:[]};
  const source=Array.from({length:150},(_,index)=>({index,key:`ticket-${index}`,cells:[String(index+1),`Réseau ${index+1}`,'Nouveau']}));
  const headers=['ID','Titre','Statut'];
  const api={on:()=>()=>{},call:async(op,args={})=>{
   searchCalls.push({op,args:structuredClone(args)});
   if(op==='records.list'){
    let rows=args.indices?source.filter(r=>args.indices.includes(r.index)):source;
    if(args.search)rows=rows.filter(r=>r.cells.join(' ').includes(args.search));
    return{rows:rows.slice(args.offset||0,(args.offset||0)+(args.limit||100)),headers,total:rows.length,sourceTotal:150,revision:7,offset:args.offset||0,limit:args.limit||100,columns:headers.map((_,sourceIndex)=>({sourceIndex,displayIndex:sourceIndex,visible:true})),session:searchState.session};
   }
   if(op==='records.schema')return{available:false};
   if(op==='workplace.scope')return{technicians:[],profile:{},personalTechnician:''};
   if(op==='workplace.get')return{checklistTemplates:{}};
   if(op==='workplace.ticket.open')return{personal:{key:`ticket-${args.rowIndex}`,id:String(args.rowIndex+1),title:'Fiche synthétique',notes:'',checklist:[],readingPosition:{}},canRead:false,readReason:'Fixture locale',loaded:false,warnings:[]};
   if(op==='workplace.ticket.position')return{};
   throw Error('Unexpected synthetic operation '+op);
  }};
  window.searchCtx={root:document.querySelector('#root'),state:searchState,params:{},api,signal:controller.signal,navigate:(id,args={})=>searchNavigations.push({id,args}),notify:(text)=>searchErrors.push(text)};
  window.searchCleanup=await renderTicketsWorkspace(searchCtx);
 });
 await page.getByLabel('Recherche du relevé',{exact:true}).fill('Réseau');
 await page.getByRole('button',{name:'Outils du tableau',exact:true}).click();
 await page.getByRole('button',{name:'Vue des colonnes',exact:true}).click();
 await page.getByLabel('Filtre de colonne Titre',{exact:true}).fill('Réseau');
 await page.getByLabel('Filtrer Statut',{exact:true}).fill('Nouveau');
 await page.getByRole('button',{name:'Carte',exact:true}).click();
 await page.waitForFunction(()=>searchNavigations.length>0);
 ok(await page.evaluate(()=>{const p=searchNavigations.at(-1).args;return p.ticketSearch.search==='Réseau'&&p.ticketSearch.revision===7&&!p.ticketSearch.indices&&!p.ticketSearch.selectedRows;}),'Map receives the complete search, not the first 100 displayed rows');
 ok(await page.evaluate(()=>searchNavigations.at(-1).args.ticketSearch.transform.filters.length===2),'Rapid edits in separate filter fields both reach the next tool');
 await page.getByLabel('Sélectionner la ligne 3',{exact:true}).check();
 await page.getByRole('button',{name:'Matériel',exact:true}).click();
 ok(await page.evaluate(()=>{const p=searchNavigations.at(-1).args;return p.tab==='preparation'&&p.ticketSearch.selectedRows.length===1&&p.ticketSearch.selectedRows[0]===2;}),'Material uses the explicit ticket selection within the original search');
 await page.locator('[data-focus-key="open-2"]').click();
 await page.locator('.tickets-detail-pane .work-ticket-heading h2').waitFor();
 ok(!await page.locator('.tickets-list-pane').isVisible()&&await page.locator('.tickets-detail-pane').isVisible(),'Opening a ticket hides the search at desktop width');
 ok(await page.locator('.tickets-detail-pane').evaluate(n=>n.clientWidth>document.querySelector('.tickets-workspace').clientWidth*.95),'Ticket occupies the full available workspace');
 await capture('search-full-ticket');
 await page.getByRole('button',{name:'Retour à la liste',exact:true}).click();
 ok(await page.getByLabel('Recherche du relevé',{exact:true}).inputValue()==='Réseau'&&await page.getByLabel('Sélectionner la ligne 3',{exact:true}).isChecked(),'Returning from a ticket preserves the query and selection');
 await page.locator('[data-focus-key="open-99"]').click();
 await page.locator('.tickets-detail-pane .work-ticket-heading h2').waitFor();
 await page.getByRole('button',{name:'Ticket suivant',exact:true}).click();
 await page.waitForFunction(()=>document.querySelector('.work-ticket-heading h2')?.textContent.startsWith('#101'));
 ok(await page.evaluate(()=>searchCalls.some(c=>c.op==='records.list'&&c.args.offset===100&&c.args.search==='Réseau')),'Next ticket crosses pagination while retaining the committed query');
 await page.getByRole('button',{name:'Retour à la liste',exact:true}).click();
 await page.getByLabel('Recherche du relevé',{exact:true}).fill('Réseau 150');
 await page.waitForFunction(()=>document.querySelectorAll('.tickets-record-grid tbody tr').length===1);
 await page.getByRole('button',{name:'Planning',exact:true}).click();
 ok(await page.evaluate(()=>!searchNavigations.at(-1).args.ticketSearch.selectedRows),'A changed query removes selections that belonged to the prior query');
 await page.evaluate(async()=>{await searchCleanup();searchCtx.root.replaceChildren();const {publishTicketSearch}=await window.importProduction('/web/js/ticket-search-context.js');publishTicketSearch(searchState,{search:'Réseau 2'},11);const {renderTicketsWorkspace}=await window.importProduction('/web/js/pages/tickets-workspace.js');searchCleanup=await renderTicketsWorkspace({...searchCtx,params:{returnToSearch:true}});});
 ok(await page.getByLabel('Recherche du relevé',{exact:true}).inputValue()==='Réseau 2','A new shared search replaces an older saved list query on return');
 await page.evaluate(async()=>{searchState.session.revision=8;const {searchForTools}=await window.importProduction('/web/js/ticket-search-context.js');window.staleSearch=searchForTools(searchState);});
 ok(await page.evaluate(()=>staleSearch===undefined),'Source revision changes invalidate shared row coordinates');
 await page.evaluate(()=>searchCleanup());
 await reset();
 await page.evaluate(async()=>{
  const {renderInterventionsWorkspace}=await window.importProduction('/web/js/pages/interventions-workspace.js');
  window.calendarCalls=[];
  const state={session:{loaded:true,revision:8},settings:{dashboard:{},interface:{}}};
  const {publishTicketSearch}=await window.importProduction('/web/js/ticket-search-context.js');publishTicketSearch(state,{search:'Réseau'},120,[],['Statut contient Nouveau']);
  const api={on:()=>()=>{},call:async(op,args)=>{
   calendarCalls.push({op,args});
   if(op==='calendar.get')return{occurrences:[],resources:[{key:'alex',label:'Alex',active:true},{key:'sam',label:'Sam',active:true}],teams:[],sites:[],cities:[],issues:[],sync:{},summary:{interventions:0,linkedTickets:0,personHours:0},sourceRevision:8,timeZoneId:'Europe/Paris'};
   if(op==='calendar.reminders')return{};
   if(op==='records.list')return{revision:8,total:17,rows:[]};
   throw Error('Unexpected calendar fixture operation '+op);
  }};
  window.calendarCtx={root:document.querySelector('#root'),state,params:{tab:'planning',date:'2026-11-16'},api,signal:controller.signal,navigate:()=>{},notify:()=>{}};window.calendarCleanup=await renderInterventionsWorkspace(calendarCtx);
 });
 ok(await page.locator('.calendar-mini-day').count()===42,'Mini-calendar includes a complete six-week date grid');
 ok(await page.evaluate(()=>calendarCalls.find(c=>c.op==='calendar.get').args.ticketSearch.search==='Réseau'),'Calendar receives the shared search filter');
 await page.getByRole('button',{name:'Mois suivant',exact:true}).click();
 await page.waitForFunction(()=>calendarCalls.some(c=>c.op==='calendar.get'&&c.args.from.startsWith('2026-12')));
 ok((await page.locator('.calendar-mini-heading strong').innerText()).toLocaleLowerCase('fr')==='décembre 2026','Mini-calendar month navigation changes the actual calendar period');
 await page.getByLabel('Sam',{exact:true}).uncheck();
 await page.waitForFunction(()=>calendarCalls.at(-1)?.args?.technicians?.length===1);
 ok(await page.evaluate(()=>calendarCalls.at(-1).args.technicians[0]==='alex'),'Agenda checkboxes filter the real calendar request');
 await page.getByLabel('Limiter aux tickets de la recherche',{exact:true}).uncheck();
 await page.waitForFunction(()=>!calendarCalls.at(-1)?.args?.ticketSearch);
 ok(await page.evaluate(()=>!calendarCalls.at(-1).args.ticketSearch),'Calendar can show all appointments without discarding the ticket search');
 await capture('search-calendar');
 for(const width of [1093,390]){
  await page.setViewportSize({width,height:800});
  ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Shared planning remains within ${width}px viewport`);
 }
 await page.evaluate(async()=>{await calendarCleanup();calendarCtx.root.replaceChildren();const {renderInterventionsWorkspace}=await window.importProduction('/web/js/pages/interventions-workspace.js');calendarCleanup=await renderInterventionsWorkspace({...calendarCtx,params:{tab:'planning',focusKeys:['team:new-scope']}});});
 ok(await page.evaluate(()=>{const q=calendarCalls.findLast(c=>c.op==='calendar.get').args.ticketSearch;return q.focusKeys[0]==='team:new-scope'&&q.search==='';}),'An explicit team deep link replaces the previous shared search');
 await page.evaluate(()=>calendarCleanup());
};
