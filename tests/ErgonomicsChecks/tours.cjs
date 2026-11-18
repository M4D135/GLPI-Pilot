'use strict';
module.exports = async function tourChecks({ page, reset, ok, capture }) {
  await page.setViewportSize({width:1280,height:800}); await reset();
  await page.evaluate(async () => {
    const {renderTours}=await window.importProduction('/web/js/pages/terrain-tours.js');
    window.tourCalls=[]; window.tourNavigation=[];
    window.tourSearch={revision:7,search:'imprimante',transform:{filters:[{column:4,operator:'contains',value:'Marseille'}],sorts:[{column:0,descending:true}]},focusKeys:['team:terrain'],technicianKeys:['agent-fixture'],mine:false,basketOnly:false,indices:[12,41,42,58],selectedRows:[41,42]};
    window.tourParams={openDraft:true,ticketSearch:tourSearch};
    window.tourState={loaded:true,revision:7,draftVersion:3,selected:2,dirty:true,draft:{id:'tour-fixture',name:'Tournée fictive',date:'2026-11-17',state:'draft',stateLabel:'Brouillon',tickets:2,stops:2,startTime:'08:00',endTime:'17:00',timeZoneId:'Europe/Paris',returnMinutes:0,notes:'',technicianKey:'',stateHistory:[],sourceExtractedAt:'2026-11-17T08:00:00Z',start:{label:'Départ fictif',latitude:null,longitude:null},end:{label:'Retour fictif',latitude:null,longitude:null}}};
    window.tourCleanup=await renderTours({root:document.querySelector('#root'),signal:controller.signal,state:{session:{testMode:true}},params:tourParams,navigate:async(page,args)=>tourNavigation.push({page,args}),notify:()=>{},api:{on:()=>()=>{},call:async(op,args={})=>{
      tourCalls.push({op,args:structuredClone(args)});
      if(op==='terrain.tours.state')return structuredClone(tourState);
      if(op==='terrain.tours.stops')return{offset:0,limit:40,total:2,items:[1,2].map(number=>({number,place:'Site fictif '+number,technician:'Technicien fictif',stop:{id:'stop-'+number,locked:number===2,ticketIds:[String(40+number)],date:'2026-11-17'},visit:{start:'2026-11-17T09:00:00',finish:'2026-11-17T09:30:00',warning:number===2?'Horaire à vérifier':''}}))};
      if(op==='terrain.tours.calculate')return{offset:0,limit:50,total:0,items:[],technicians:[],warnings:[],warningCount:0,totals:{interventions:60,travel:0,waiting:0,unavailable:0},canExportCalendar:true};
      if(op==='terrain.tours.save'){tourState.dirty=false;return structuredClone(tourState);}
      if(op==='terrain.tours.edit'){Object.assign(tourState.draft,args.fields);tourState.dirty=true;tourState.draftVersion++;return structuredClone(tourState);}
      if(op==='terrain.tours.createFromSearch'){tourState.draft={...tourState.draft,id:'tour-from-search',name:args.name,date:args.date,technicianKey:args.technician};tourState.dirty=true;tourState.draftVersion++;return structuredClone(tourState);}
      throw new Error('Unexpected tour fixture operation '+op);
    }}});
  });
  ok(await page.locator('.tour-stop-card').count()===2,'Tours show numbered step cards rather than a wide action table');
  ok(await page.getByLabel('Monter l’étape 2',{exact:true}).isDisabled() && await page.getByLabel('Descendre l’étape 2',{exact:true}).isDisabled(),'A locked visit retains its disabled movement controls');
  ok(await page.getByText('Horaire à vérifier',{exact:true}).isVisible(),'The compact tour keeps an important visit warning visible');
  await page.locator('[data-tour-action=materials]').click();
  await page.getByRole('button',{name:'Annuler',exact:true}).click();
  ok(await page.evaluate(()=>tourNavigation.length===0&&!tourCalls.some(x=>x.op==='terrain.tours.save')),'Cancelling the save-before-material confirmation neither saves nor navigates');
  await page.locator('[data-tour-action=materials]').click();
  await page.getByRole('button',{name:'Enregistrer et continuer',exact:true}).click();
  await page.waitForFunction(()=>tourNavigation.length===1);
  ok(await page.evaluate(()=>tourCalls.filter(x=>x.op==='terrain.tours.save').length===1&&tourNavigation[0].args.fieldTourId==='tour-fixture'&&tourNavigation[0].args.fieldReturnTour===true),'Material navigation saves only after confirmation and preserves the tour and return context');
  await page.locator('[data-tour-action=reports]').click();
  await page.waitForFunction(()=>tourNavigation.length===2);
  ok(await page.evaluate(()=>tourCalls.filter(x=>x.op==='terrain.tours.save').length===1&&tourNavigation[1].args.fieldShowReport===true),'A clean saved tour opens its sheets without another save or a GLPI write');
  for(const [width,height] of [[1280,800],[1093,614],[390,800]]){
    await page.setViewportSize({width,height});
    ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`The tour remains bounded at ${width}×${height}`);
  }
  await capture('tour-steps');
  await page.setViewportSize({width:1280,height:800});
  ok(await page.getByRole('button',{name:'Créer une tournée depuis la recherche',exact:true}).count()===1
    && await page.getByRole('button',{name:'Créer une tournée depuis la carte',exact:true}).count()===1,
    'Tour creation presents distinct search and map actions');
  await page.locator('[data-tour-action=from-map]').click();
  ok(await page.evaluate(()=>tourNavigation.length===3&&tourNavigation[2].page==='interventions'&&tourNavigation[2].args.tab==='map'
    &&tourNavigation[2].args.ticketMap===true&&JSON.stringify(tourNavigation[2].args.ticketSearch)===JSON.stringify(tourSearch)
    &&!tourCalls.some(x=>x.op==='terrain.tours.createFromSearch')),
    'The map action opens the existing map workflow with the unchanged search and explicit row selection');
  await page.locator('[data-tour-action=from-search]').click();
  const create=page.getByRole('dialog',{name:'Créer une tournée depuis la recherche',exact:true});
  ok(await create.isVisible()&&(await create.innerText()).includes('2 tickets sélectionnés')
    &&await page.evaluate(()=>tourNavigation.length===3),
    'The search action opens a local tour form for the selected tickets without navigating to the map');
  await page.keyboard.press('Escape');
  ok(await create.count()===0&&await page.evaluate(()=>!tourCalls.some(x=>x.op==='terrain.tours.createFromSearch')&&tourState.selected===2
    &&document.activeElement?.dataset.tourAction==='from-search'),
    'Escape cancels search creation without changing the draft or basket and restores focus to its trigger');
  await page.locator('summary').filter({hasText:'Nom, horaires, départ, retour et consignes'}).click();
  await page.getByLabel('Nom',{exact:true}).fill('Modification de la tournée existante');
  await page.locator('[data-tour-action=from-search]').click();
  await create.getByLabel('Nom de la tournée',{exact:true}).fill('Tournée issue de la recherche');
  await create.getByLabel('Premier jour',{exact:true}).fill('2026-11-19');
  await create.getByRole('button',{name:'Créer le brouillon',exact:true}).click();
  const switchDraft=page.getByRole('dialog',{name:'Changer de tournée',exact:true});
  await switchDraft.waitFor({state:'visible'}); await page.keyboard.press('Escape');
  ok(await create.isVisible()&&await page.getByLabel('Nom',{exact:true}).inputValue()==='Modification de la tournée existante'
    &&await page.evaluate(()=>!tourCalls.some(x=>x.op==='terrain.tours.createFromSearch')&&tourNavigation.length===3),
    'Escape cancels only the nested draft confirmation and keeps the unsaved fields and creation form without any mutation');
  await create.getByRole('button',{name:'Créer le brouillon',exact:true}).click();
  await switchDraft.getByRole('button',{name:'Abandonner le brouillon',exact:true}).click();
  await page.waitForFunction(()=>tourCalls.some(x=>x.op==='terrain.tours.createFromSearch')&&!document.querySelector('.terrain-page[aria-busy=true]'));
  ok(await page.evaluate(()=>{const calls=tourCalls.filter(x=>x.op==='terrain.tours.createFromSearch'),args=calls[0]?.args;
    return calls.length===1&&args.revision===7&&JSON.stringify(args.ticketSearch)===JSON.stringify(tourSearch)&&args.name==='Tournée issue de la recherche'
      &&args.date==='2026-11-19'&&args.technician===''&&args.discardDraft===true&&tourState.selected===2
      &&!tourCalls.some(x=>['terrain.tours.select','terrain.tours.create'].includes(x.op));}),
    'Confirmed search creation passes the full revision-bound query and selection once without replacing the unrelated basket');
  ok(await page.getByRole('heading',{name:'Tournée issue de la recherche',exact:true}).count()===1
    &&await page.locator('.tour-stop-card').count()===2&&await create.count()===0&&await page.evaluate(()=>tourNavigation.length===3),
    'The resulting draft opens directly in the tour planning editor with no map navigation');
  await page.evaluate(()=>{delete tourSearch.selectedRows;});
  await page.locator('[data-tour-action=from-search]').click();
  ok((await create.innerText()).includes('tous les résultats de la recherche, y compris ceux des autres pages'),
    'A search without checked rows explicitly uses all results across pagination');
  await create.getByRole('button',{name:'Annuler',exact:true}).click();
  await page.evaluate(()=>{tourParams.rows=[42];tourParams.revision=7;});
  await page.locator('[data-tour-action=from-map]').click();
  ok(await page.evaluate(()=>{const query=tourNavigation.at(-1).args.ticketSearch;return JSON.stringify(query.selectedRows)==='[42]'
    &&query.revision===7&&JSON.stringify(query.indices)===JSON.stringify(tourSearch.indices)&&!Object.hasOwn(tourSearch,'selectedRows');}),
    'An explicit ticket from its detail page remains the sole selection on the map without changing the shared search');
  await page.locator('[data-tour-action=from-search]').click();
  await create.getByRole('button',{name:'Créer le brouillon',exact:true}).click();
  await switchDraft.getByRole('button',{name:'Abandonner le brouillon',exact:true}).click();
  await page.waitForFunction(()=>tourCalls.filter(x=>x.op==='terrain.tours.createFromSearch').length===2&&!document.querySelector('.terrain-page[aria-busy=true]'));
  ok(await page.evaluate(()=>{const query=tourCalls.filter(x=>x.op==='terrain.tours.createFromSearch').at(-1).args.ticketSearch;
    return JSON.stringify(query.selectedRows)==='[42]'&&query.revision===7&&query.search===tourSearch.search
      &&JSON.stringify(query.transform)===JSON.stringify(tourSearch.transform)&&JSON.stringify(query.indices)===JSON.stringify(tourSearch.indices)
      &&!Object.hasOwn(tourSearch,'selectedRows')&&tourNavigation.length===4&&tourState.selected===2;}),
    'Add-to-tour from a ticket creates only that explicit row while retaining the search constraints and unrelated basket');
  await page.evaluate(()=>tourCleanup());
};
