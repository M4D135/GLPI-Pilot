import {el,replaceChildren,button,toolbar,field,select,confirm,formatNumber} from '../ui.js';
import {readWorkspaceView,saveWorkspaceView} from '../work-context.js';

export function createFieldFilters({call,run,state,params,onTour,onSelectAll,onChange}){
 const remembered=readWorkspaceView(state,'field-preparation',{});
 const initial={...remembered,...(params?.fieldTourId?{tourId:params.fieldTourId}:{})};
 const controls={siteId:select([{value:'',label:'Tous les sites'}]),technicianKey:select([{value:'',label:'Tous les techniciens'}]),teamKey:select([{value:'',label:'Toutes les équipes'}]),tourId:select([{value:'',label:'Toutes les tournées'}]),
  readiness:select([{value:'',label:'Tous les états'},{value:'unspecified',label:'À renseigner'},{value:'to-prepare',label:'À préparer'},{value:'partial',label:'Préparation partielle'},{value:'ready',label:'Prêt'},{value:'none',label:'Aucun matériel nécessaire'},{value:'done',label:'Effectué'},{value:'cancelled',label:'Annulé'}],initial.readiness||'')};
 const archive=el('input',{type:'checkbox'});let initialized=false,loadGeneration=0;
 const summary=el('summary',{text:'Filtres supplémentaires'});
 const tourReport=button('Fiches de la tournée entière',run(()=>{if(!controls.tourId.value)throw new Error('Choisissez une tournée dans le filtre.');return onTour(controls.tourId.value);}),{icon:'route',disabled:true,attributes:{'data-field-action':'tour-report'}});
 const selectAll=button('Sélectionner tous les résultats',run(onSelectAll),{icon:'check',attributes:{'data-field-action':'select-all'}});
 const element=el('details',{class:'field-advanced-filters reading-options','aria-label':'Filtres de préparation'},summary,
  el('div',{class:'reading-options-body'},toolbar(field('Site',controls.siteId),field('Technicien',controls.technicianKey),field('Équipe',controls.teamKey),field('Tournée',controls.tourId),field('État du matériel',controls.readiness)),
   el('label',{class:'field-check'},archive,el('span',{text:'Autoriser les archives non vérifiées, exclues de la liste de départ'}))));
 function describe(){
  const active=Object.values(controls).filter(node=>node.value);
  summary.textContent=active.length?'Filtres ('+active.length+') · '+active.map(node=>node.selectedOptions[0]?.textContent||node.value).join(' · '):'Filtres supplémentaires';
  if(archive.checked)summary.textContent+=' · archives autorisées';
  tourReport.hidden=!controls.tourId.value;tourReport.disabled=!controls.tourId.value;
 }
 async function load(){
  const generation=++loadGeneration;const options=await call('field.options');if(generation!==loadGeneration)return;
  for(const [key,source,label] of [['siteId','sites','Tous les sites'],['technicianKey','technicians','Tous les techniciens'],['teamKey','teams','Toutes les équipes'],['tourId','tours','Toutes les tournées']]){
   const value=initialized?controls[key].value:initial[key]||'';
   const values=options[source]||[];
   replaceChildren(controls[key],el('option',{value:'',text:label}),...values.map(item=>el('option',{value:item.id,text:item.label,selected:item.id===value})),
    value&&!values.some(item=>item.id===value)?el('option',{value,text:'Sélection absente : '+value,selected:true}):null);
  }
  initialized=true;describe();
 }
 function read(){const result=Object.fromEntries(Object.entries(controls).map(([key,node])=>[key,node.value]));saveWorkspaceView(state,'field-preparation',result);return result;}
 for(const node of Object.values(controls))node.addEventListener('change',()=>{describe();if(onChange)void run(onChange)();});
 archive.addEventListener('change',describe);
 function reset(){for(const node of Object.values(controls))node.value='';archive.checked=false;read();describe();}
 function readiness(value){controls.readiness.value=value;read();describe();}
 describe();
 return{element,load,read,reset,readiness,selectAll,tourReport,archive:()=>archive.checked};
}

export function attachFieldKitPicker({root,api,signal,token,plan,markDirty,renderLines,onError,readOnly}){
 if(readOnly)return;
 const body=el('div',{class:'field-kit-list'}),section=el('details',{class:'field-new-reference'},el('summary',{text:'Reprendre un kit de matériel déjà utilisé'}),
  el('p',{class:'field-help',text:'Suggestions locales issues des préparations récentes. Rien n’est ajouté sans votre confirmation ; les quantités préparées repartent à zéro.'}),body);
 let loaded=false;
 section.addEventListener('toggle',async()=>{
  if(!section.open||loaded)return;
  try{
   const response=await api.call('field.kits',{token},{signal});if(signal?.aborted)return;loaded=true;
   replaceChildren(body,...response.suggestions.map(kit=>el('section',{class:'field-material-line'},el('h3',{text:kit.label}),el('p',{class:'field-help',text:kit.reason}),
    el('ul',{},...kit.lines.map(line=>el('li',{text:`${line.label} : ${formatNumber(line.quantity,{maximumFractionDigits:3})} ${line.unit==='metre'?'m':line.unit==='lot'?'lot(s)':'pièce(s)'}`}))),
    button('Ajouter ce kit à la saisie',async()=>{
     if(plan.lines.length+kit.lines.length>100){onError(new Error('La préparation dépasserait 100 lignes.'));return;}
     if(!await confirm('Ajouter ce matériel ?',kit.reason+' Les lignes existantes sont conservées ; vérifiez les éventuels doublons.','Ajouter à la saisie'))return;
     if(signal?.aborted)return;
     plan.lines.push(...kit.lines.map(line=>({...structuredClone(line),id:crypto.randomUUID().replaceAll('-',''),preparedQuantity:0,preparedBy:''})));
     plan.noMaterialsRequired=false;markDirty();renderLines();
    },{icon:'plus'}))));
   if(!response.suggestions.length)body.append(el('p',{class:'field-help',text:'Aucun kit réutilisable pour le moment. Les préparations avec des références actives alimenteront ces suggestions.'}));
  }catch(error){if(error.name!=='AbortError')onError(error);}
 });
 root.append(section);
}
