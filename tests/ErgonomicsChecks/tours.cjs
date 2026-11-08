'use strict';
module.exports = async function tourChecks({ page, reset, ok, capture }) {
  await page.setViewportSize({width:1280,height:800}); await reset();
  await page.evaluate(async () => {
    const {renderTours}=await window.importProduction('/web/js/pages/terrain-tours.js');
    window.tourCalls=[]; window.tourNavigation=[];
    window.tourState={loaded:true,revision:7,draftVersion:3,selected:2,dirty:true,draft:{id:'tour-fixture',name:'Tournée fictive',date:'2026-11-17',state:'draft',stateLabel:'Brouillon',tickets:2,stops:2,startTime:'08:00',endTime:'17:00',timeZoneId:'Europe/Paris',returnMinutes:0,notes:'',technicianKey:'',stateHistory:[],sourceExtractedAt:'2026-11-17T08:00:00Z',start:{label:'Départ fictif',latitude:null,longitude:null},end:{label:'Retour fictif',latitude:null,longitude:null}}};
    window.tourCleanup=await renderTours({root:document.querySelector('#root'),signal:controller.signal,state:{session:{testMode:true}},params:{openDraft:true},navigate:async(page,args)=>tourNavigation.push({page,args}),notify:()=>{},api:{on:()=>()=>{},call:async(op,args={})=>{
      tourCalls.push({op,args:structuredClone(args)});
      if(op==='terrain.tours.state')return structuredClone(tourState);
      if(op==='terrain.tours.stops')return{offset:0,limit:40,total:2,items:[1,2].map(number=>({number,place:'Site fictif '+number,technician:'Technicien fictif',stop:{id:'stop-'+number,locked:number===2,ticketIds:[String(40+number)],date:'2026-11-17'},visit:{start:'2026-11-17T09:00:00',finish:'2026-11-17T09:30:00',warning:number===2?'Horaire à vérifier':''}}))};
      if(op==='terrain.tours.calculate')return{offset:0,limit:50,total:0,items:[],technicians:[],warnings:[],warningCount:0,totals:{interventions:60,travel:0,waiting:0,unavailable:0},canExportCalendar:true};
      if(op==='terrain.tours.save'){tourState.dirty=false;return structuredClone(tourState);}
      if(op==='terrain.tours.edit'){Object.assign(tourState.draft,args.fields);tourState.dirty=true;tourState.draftVersion++;return structuredClone(tourState);}
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
  await capture('tour-steps'); await page.evaluate(()=>tourCleanup());
};
