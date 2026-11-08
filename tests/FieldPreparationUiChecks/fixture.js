// Browser contract tests only. Names, sites and tickets are fictitious.
// Actual persistence and C# handlers are tested separately by the bridge suite.
import {renderFieldPreparation} from '/js/pages/field-preparation.js';
import {renderInterventionsWorkspace,pages as interventionPages} from '/js/pages/interventions-workspace.js';
import {renderPilotWorkspace,pages as pilotPages} from '/js/pages/pilot-workspace.js';
import {safeFieldReport,printFieldReport,clearFieldPrint} from '/js/pages/field-preparation-report.js';
import {materialReadiness} from '/js/pages/field-preparation-editor.js';

const clone=value=>structuredClone(value);
const catalogue={schema:1,categories:[{id:'cables',name:'Câbles et connectique',archived:false},{id:'computers',name:'Ordinateurs',archived:false}],references:[
 {id:'cable',categoryId:'cables',code:'C-01',label:'Câble de test',unit:'metre',note:'Compatibilité à vérifier',archived:false},
 {id:'computer',categoryId:'computers',code:'P-01',label:'Poste de test',unit:'piece',note:'Matériel fictif',archived:false}]};
const basePlan={schema:1,id:'11111111111111111111111111111111',owner:{kind:'ticket',key:'ticket-42',occurrence:''},title:'Intervention de test : poste et câble',siteId:'test-site',siteName:'Site de démonstration',address:'Adresse fictive',contact:'Contact fictif',plannedStart:null,plannedEnd:null,state:'planned',participantKeys:['test-agent'],tourIds:[],noMaterialsRequired:false,lines:[],preparationNote:''};
const documentFor=plan=>({schema:'glpi-pilot.field-preparation',format:1,plan:clone(plan),tickets:[{key:plan.owner.key,id:'42',title:'Ticket fictif',description:'Description disponible du test'}],participantLabels:['Agent de test'],observedAtUtc:'2026-10-27T06:00:00Z',sourceStamp:'aabbcc'});
const stored=new Map([[basePlan.id,{document:documentFor(basePlan),revision:1}]]),drafts=new Map();
const calls=[],notices=[];let catalogRevision=0,tokenNumber=0,cleanup=null,controller=null;
window.test={calls,notices,failSave:false,failCatalogue:false,lastSaved:null,lastCatalogue:null,lastReport:null,importsReady:interventionPages.some(p=>p.id==='interventions')&&pilotPages.some(p=>p.id==='pilot'),safeFieldReport,printFieldReport,clearFieldPrint,materialReadiness};
const api={available:true,call:async(operation,args={},options={})=>{
 options.signal?.throwIfAborted();calls.push({operation,args:clone(args)});
 if(operation==='field.options')return{sites:[],technicians:[],teams:[],tours:[]};
 if(operation==='field.kits')return{suggestions:[]};
 if(operation==='field.get'){
  if(window.test.failNextList){window.test.failNextList=false;throw new Error('Liste de test indisponible.');}
  if(window.test.holdNextList){window.test.holdNextList=false;await new Promise(resolve=>{window.test.releaseList=resolve;document.body.dataset.testListHeld="true";});}
  const values=[...stored].filter(([,value])=>args.scope!=='mine'||value.document.plan.participantKeys.includes('test-agent'));
  const plans=values.map(([,value])=>value.document.plan),allLines=plans.flatMap(plan=>plan.lines);
  return{revision:7,total:values.length,rows:values.map(([id,value])=>({id,revision:value.revision,pending:false,conflicts:0,title:value.document.plan.title,owner:value.document.plan.owner,siteName:value.document.plan.siteName,participantKeys:value.document.plan.participantKeys,participantLabels:['Agent de test'],tourIds:[],plannedStart:value.document.plan.plannedStart,observedAtUtc:'2026-10-27T06:00:00Z',readiness:materialReadiness(value.document.plan),lines:value.document.plan.lines.length})),
   summary:{planCount:plans.length,unspecifiedCount:plans.filter(p=>materialReadiness(p)==='unspecified').length,noMaterialsCount:plans.filter(p=>p.noMaterialsRequired).length,partlyPreparedCount:plans.filter(p=>materialReadiness(p)==='partial').length,readyCount:plans.filter(p=>materialReadiness(p)==='ready').length,warnings:[],materials:allLines.map(line=>({...line,remainingQuantity:line.quantity-line.preparedQuantity}))},sync:{enabled:false,label:'Préparations enregistrées sur ce poste',pending:0,conflicts:0},coverage:'Contexte enregistré de données fictives. Aucun solde de stock.'};
 }
 if(operation==='field.catalogue.get')return{catalogue:clone(catalogue),revision:catalogRevision,pending:false,conflicts:0,readOnly:false};
 if(operation==='field.catalogue.save'){
  if(window.test.failCatalogue)throw new Error('Le catalogue a changé. Votre saisie reste ouverte.');
  if(args.expectedRevision!==catalogRevision)throw new Error('Révision de catalogue périmée');
  catalogue.categories=clone(args.catalogue.categories);catalogue.references=clone(args.catalogue.references);catalogRevision++;
  window.test.lastCatalogue=clone(args);return{catalogue:clone(catalogue),revision:catalogRevision};
 }
 if(operation==='field.open'){
  const existing=args.id&&stored.get(args.id),plan=existing?clone(existing.document.plan):{...clone(basePlan),id:args.ticketKey?'22222222222222222222222222222222':'33333333333333333333333333333333',owner:args.ticketKey?{kind:'ticket',key:args.ticketKey,occurrence:''}:{kind:'intervention',key:args.occurrenceKey,occurrence:''},title:args.ticketKey?'Ticket choisi':'Visite choisie'};
  const token='draft-'+(++tokenNumber);drafts.set(token,{plan:clone(plan),revision:existing?.revision||0});
  return{token,document:documentFor(plan),expectedRevision:existing?.revision||0,sourceRevision:7,catalogue:clone(catalogue),catalogueRevision:catalogRevision,readOnly:false,conflicts:0,snapshotOnly:!!args.id,message:args.id?'Contexte enregistré, sans nouvelle lecture de GLPI.':'Aucune modification du ticket GLPI ni du stock.'};
 }
 if(operation==='field.save'){
  if(window.test.failSave)throw new Error('La préparation a changé. Votre saisie est conservée.');
  const draft=drafts.get(args.token);if(!draft)throw new Error('Brouillon absent');
  stored.set(args.plan.id,{document:documentFor(args.plan),revision:draft.revision+1});drafts.delete(args.token);window.test.lastSaved=clone(args);
  return{saved:true,id:args.plan.id,revision:draft.revision+1,pending:true};
 }
 if(operation==='field.close'){drafts.delete(args.token);return{closed:true};}
 if(operation==='calendar.tickets')return{revision:7,total:1,rows:[{key:'stable-ticket-42',id:'42',title:'Ticket fictif avec clé stable',location:'Site de démonstration',rowIndex:99}]};
 if(operation==='calendar.get')return{revision:7,occurrences:[{key:'stable-series:ordinal-5',title:'Visite fictive répétée',kind:'intervention',startLocal:'2026-11-02T09:00',location:'Site de démonstration'}]};
 if(operation==='field.report'){
  window.test.lastReport=clone(args);return{html:'<!doctype html><html><body><h1>Liste de départ</h1><p>Fiches sélectionnées : '+args.selections.map(s=>s.id).join(', ')+'</p><section class="sheet"><h2>Intervention de test</h2><p>&lt;script&gt;texte uniquement&lt;/script&gt;</p><table><tr><th>Matériel</th><td>Câble de test</td></tr></table></section></body></html>'};
 }
 if(operation==='field.export')return{cancelled:true};
 if(operation==='field.versions')return{items:[],total:0,message:'Les deux versions sont conservées.'};
 throw new Error('Unexpected fixture operation: '+operation);
}};
const root=document.querySelector('#app');root.className='field-ui-test-root';root.removeAttribute('role');
document.documentElement.dataset.theme=new URL(location.href).searchParams.get('theme')||'dark';
const params={tab:'preparation',fieldPreparation:true};
const state={identity:{label:'Agent de test'},session:{loaded:true,revision:7},settings:{dashboard:{personalTechnician:'test-agent'}}};
async function mount(mode='direct'){
 if(cleanup)await cleanup();controller?.abort();controller=new AbortController();root.replaceChildren();
 const ctx={root,api,state,signal:controller.signal,params,notify:(...args)=>notices.push(args),navigate:async(page,args)=>{Object.assign(params,args);await mount(page==='pilot'?'pilot':'interventions');}};
 cleanup=await(mode==='pilot'?renderPilotWorkspace:mode==='interventions'?renderInterventionsWorkspace:renderFieldPreparation)(ctx);
 window.test.ready=true;
}
window.test.mount=mount;
window.test.abort=()=>controller.abort();
window.test.longLabels=()=>{const value=stored.get(basePlan.id);value.document.plan.title='Titre extrêmement long avec accents et références '.repeat(12);value.document.plan.siteName='Un site au nom particulièrement long '.repeat(10);};
await mount();
