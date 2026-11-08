import {el,replaceChildren,button,toolbar,empty,confirm,formatDate} from '../ui.js';
import {attachFieldKitPicker} from './field-preparation-tools.js';

const units={piece:'pièce(s)',metre:'mètre(s)',lot:'lot(s)'};
const labels={unspecified:'Matériel non renseigné',none:'Aucun matériel nécessaire','to-prepare':'À préparer',partial:'Préparation partielle',ready:'Prête',done:'Effectuée',cancelled:'Annulée',invalid:'Quantités à vérifier'};
const quantity=value=>new Intl.NumberFormat('fr-FR',{maximumFractionDigits:3}).format(value);
const field=(label,node,help='')=>el('label',{class:'field'},el('span',{class:'field-label',text:label}),node,help?el('small',{class:'field-help',text:help}):null);
const normalize=value=>String(value||'').normalize('NFD').replace(/\p{Diacritic}/gu,'').toLocaleLowerCase('fr');
const id=()=>crypto.randomUUID().replaceAll('-','');
const pick=(values,current='')=>el('select',{},...values.map(([value,label])=>el('option',{value,text:label,selected:value===current})));

export function materialReadiness(plan){
 if(plan.state==='done'||plan.state==='cancelled')return plan.state;
 if(plan.noMaterialsRequired)return 'none';
 if(!plan.lines.length)return 'unspecified';
 if(plan.lines.some(line=>!Number.isFinite(line.quantity)||!Number.isFinite(line.preparedQuantity)||line.quantity<=0||line.preparedQuantity<0||line.preparedQuantity>line.quantity))return 'invalid';
 if(plan.lines.every(line=>line.preparedQuantity===line.quantity))return 'ready';
 return plan.lines.some(line=>line.preparedQuantity>0)?'partial':'to-prepare';
}

export function renderMaterialEditor(ctx){
 const {root,model,state,api,signal,markDirty,onSaved,onCancel,onError}=ctx;
 const plan=structuredClone(model.document.plan);let catalogue=structuredClone(model.catalogue);
 const form=el('form',{class:'field-material-editor'}),lines=el('div',{class:'field-line-list'}),status=el('span',{class:'badge'});
 const openLineDetails=new Set();let saving=false;
 const none=el('input',{type:'checkbox',checked:plan.noMaterialsRequired,disabled:model.readOnly,'data-field-none':'true'});
 const note=el('textarea',{value:plan.preparationNote||'',rows:3,maxLength:4000,disabled:model.readOnly,'aria-label':'Consignes de préparation'});
 const search=el('input',{type:'search',maxLength:300,placeholder:'Nom ou référence…','aria-label':'Chercher du matériel'});
 const category=pick([['','Toutes les catégories']]);
 const reference=pick([['','Choisir une référence']]);reference.setAttribute('aria-label','Référence à ajouter');
 const count=el('input',{type:'number',value:'1',min:'1',max:'10000',step:'1','aria-label':'Quantité à ajouter'});
 const compatibility=el('p',{class:'field-help'}),catalogueStatus=el('p',{class:'field-help',role:'status'});
 const act=fn=>async()=>{try{await fn();}catch(error){if(error.name!=='AbortError')onError(error);}};
 const call=(operation,args)=>api.call(operation,args,{signal});
 const alive=()=>!signal?.aborted&&root.isConnected;
 function paintStatus(){const value=materialReadiness(plan);status.textContent='État de la saisie : '+labels[value];status.className=`badge ${value==='ready'||value==='none'?'success':value==='invalid'?'warning':''}`;}
 function touch(){markDirty();paintStatus();}
 function paintCatalogue(wanted=''){
  const previous=category.value;
  replaceChildren(category,el('option',{value:'',text:'Toutes les catégories'}),...catalogue.categories.filter(value=>!value.archived).map(value=>el('option',{value:value.id,text:value.name,selected:value.id===previous})));
  const allowed=new Set(catalogue.categories.filter(value=>!value.archived).map(value=>value.id));
  const term=normalize(search.value),choice=wanted||reference.value;
  replaceChildren(reference,el('option',{value:'',text:'Choisir une référence'}),...catalogue.references.filter(value=>!value.archived&&allowed.has(value.categoryId)&&(!category.value||value.categoryId===category.value)&&normalize(value.label+' '+value.code).includes(term))
   .map(value=>el('option',{value:value.id,text:[value.label,value.code,units[value.unit]].filter(Boolean).join(' · '),selected:value.id===choice})));
  if(!reference.value&&term&&reference.options.length===2)reference.value=reference.options[1].value;
  updateReference();
 }
 function updateReference(){const selected=catalogue.references.find(value=>value.id===reference.value);compatibility.textContent=selected?.note||'Une référence générique ne garantit pas la compatibilité avec un appareil.';count.step=selected?.unit==='metre'?'0.001':'1';count.min=count.step;}
 function renderLines(){
  lines.replaceChildren();none.checked=plan.noMaterialsRequired;
  if(!plan.lines.length)lines.append(empty(plan.noMaterialsRequired?'Aucun matériel nécessaire':'Matériel non renseigné',plan.noMaterialsRequired?'Ce choix explicite sera conservé avec la fiche.':'Ajoutez des références ou indiquez explicitement qu’aucun matériel n’est nécessaire.'));
  for(const line of plan.lines){
   const planned=el('input',{type:'number',value:String(line.quantity),min:line.unit==='metre'?'0.001':'1',max:'10000',step:line.unit==='metre'?'0.001':'1',required:true,disabled:model.readOnly,'aria-label':'Quantité prévue : '+line.label});
   const prepared=el('input',{type:'number',value:String(line.preparedQuantity),min:'0',max:String(line.quantity),step:line.unit==='metre'?'0.001':'1',required:true,disabled:model.readOnly,'aria-label':'Quantité préparée : '+line.label});
   const author=el('input',{type:'text',value:line.preparedBy||'',maxLength:200,disabled:model.readOnly,'aria-label':'Préparé par : '+line.label});
   const memo=el('textarea',{value:line.note||'',rows:2,maxLength:2000,disabled:model.readOnly,'aria-label':'Précisions : '+line.label});
   const remaining=el('span',{class:'field-remaining'});
   const update=()=>{remaining.textContent=Number.isFinite(line.quantity)&&Number.isFinite(line.preparedQuantity)&&line.quantity>=line.preparedQuantity?`Restant : ${quantity(line.quantity-line.preparedQuantity)} ${units[line.unit]}`:'Quantités à vérifier';};
   planned.addEventListener('input',()=>{line.quantity=planned.value===''?NaN:Number(planned.value);prepared.max=String(line.quantity);update();touch();});
   prepared.addEventListener('input',()=>{line.preparedQuantity=prepared.value===''?NaN:Number(prepared.value);update();touch();});
   author.addEventListener('input',()=>{line.preparedBy=author.value;touch();});memo.addEventListener('input',()=>{line.note=memo.value;touch();});update();
   const heading=el('header',{},el('h3',{text:line.label}),el('p',{class:'field-help',text:[line.code,line.category,units[line.unit]].filter(Boolean).join(' · ')}));
   const remove=model.readOnly?null:button('Retirer cette ligne',()=>{plan.lines=plan.lines.filter(value=>value.id!==line.id);touch();renderLines();},{icon:'close',attributes:{'aria-label':'Retirer '+line.label}});
   const ready=model.readOnly?null:button('Ligne préparée',()=>{
    if(!planned.reportValidity())return;
    line.preparedQuantity=line.quantity;prepared.value=String(line.quantity);
    if(!line.preparedBy){line.preparedBy=state.identity?.label||'';author.value=line.preparedBy;}
    update();touch();renderLines();
   },{icon:'check',disabled:line.preparedQuantity===line.quantity,attributes:{'data-field-ready':line.id}});
   const refreshReady=()=>{if(ready)ready.disabled=Number.isFinite(line.quantity)&&line.preparedQuantity===line.quantity;};
   planned.addEventListener('input',refreshReady);prepared.addEventListener('input',refreshReady);
   const details=el('details',{class:'field-line-details',open:openLineDetails.has(line.id)},
    el('summary',{text:line.note?'Précisions et compatibilité · renseignées':'Précisions et préparateur'}),
    field('Préparé par',author),field('Précisions et compatibilité',memo),remove);
   details.addEventListener('toggle',()=>{if(details.open)openLineDetails.add(line.id);else openLineDetails.delete(line.id);});
   lines.append(el('section',{class:'field-material-line field-material-line-compact','data-field-line':line.id},
    el('div',{class:'field-line-main'},heading,field('Prévu',planned),field('Préparé',prepared),remaining,ready),details));
  }
  paintStatus();
 }
 async function addReference(){
  if(model.readOnly)return;
  const value=catalogue.references.find(item=>item.id===reference.value),group=value&&catalogue.categories.find(item=>item.id===value.categoryId);
  if(!value||value.archived||!group||group.archived)throw new Error('Choisissez une référence active dans le catalogue.');
  if(!count.reportValidity())return;
  if(plan.lines.length>=100)throw new Error('Une préparation accepte au maximum 100 lignes.');
  plan.lines.push({id:id(),referenceId:value.id,code:value.code,label:value.label,category:group.name,unit:value.unit,quantity:Number(count.value),preparedQuantity:0,preparedBy:'',note:value.note||''});
  plan.noMaterialsRequired=false;touch();renderLines();search.focus({preventScroll:true});search.select();
 }
 none.addEventListener('change',act(async()=>{
  if(none.checked&&plan.lines.length&&!await confirm('Indiquer aucun matériel nécessaire ?','Les lignes actuellement saisies seront retirées. La préparation ne sera modifiée qu’après enregistrement.','Retirer les lignes')){none.checked=false;return;}
  plan.noMaterialsRequired=none.checked;if(none.checked)plan.lines=[];touch();renderLines();
 }));
 note.addEventListener('input',()=>{plan.preparationNote=note.value;touch();});
 search.addEventListener('input',()=>paintCatalogue());category.addEventListener('change',()=>paintCatalogue());reference.addEventListener('change',updateReference);
 const newLabel=el('input',{type:'text',maxLength:180,'aria-label':'Nom de la nouvelle référence'});
 const newCode=el('input',{type:'text',maxLength:100,'aria-label':'Code de la nouvelle référence'});
 const newCategory=pick(catalogue.categories.filter(value=>!value.archived).map(value=>[value.id,value.name]));newCategory.setAttribute('aria-label','Catégorie de la nouvelle référence');
 const newCategoryName=el('input',{type:'text',maxLength:120,placeholder:'Facultatif','aria-label':'Nouvelle catégorie facultative'});
 const newUnit=pick(Object.entries(units),'piece'),newNote=el('textarea',{rows:2,maxLength:2000});
 async function createReference(){
  if(!newLabel.value.trim())throw new Error('Donnez un nom à la nouvelle référence.');
  // Use a fresh revision so an addition cannot replace unrelated catalogue edits.
  const current=await call('field.catalogue.get',{});if(!alive())return;
  if(current.readOnly)throw new Error('Le catalogue possède des versions en conflit. Examinez-les avant d’ajouter une référence.');
  const next=structuredClone(current.catalogue);let categoryId=newCategory.value;
  if(newCategoryName.value.trim()){categoryId='custom-'+id();next.categories.push({id:categoryId,name:newCategoryName.value.trim(),archived:false});}
  if(!next.categories.some(value=>value.id===categoryId&&!value.archived))throw new Error('Choisissez une catégorie active ou créez-en une.');
  const referenceId='custom-'+id();next.references.push({id:referenceId,categoryId,code:newCode.value.trim(),label:newLabel.value.trim(),unit:newUnit.value,note:newNote.value,archived:false});
  const saved=await call('field.catalogue.save',{catalogue:next,expectedRevision:current.revision});if(!alive())return;
  catalogue=saved.catalogue;search.value='';category.value='';paintCatalogue(referenceId);
  replaceChildren(newCategory,...catalogue.categories.filter(value=>!value.archived).map(value=>el('option',{value:value.id,text:value.name,selected:value.id===categoryId})));
  newLabel.value='';newCode.value='';newCategoryName.value='';newNote.value='';
  catalogueStatus.textContent='Référence créée et sélectionnée. Ajoutez-la maintenant à la préparation ; les quantités ne sont pas encore enregistrées.';
 }
 const add=el('section',{class:'field-add-material'},el('h3',{text:'Ajouter du matériel'}),
  toolbar(field('Rechercher',search),field('Catégorie',category)),
  toolbar(field('Référence',reference),field('Quantité',count),button('Ajouter à la préparation',act(addReference),{icon:'plus',attributes:{'data-field-action':'add-line'}})),compatibility,
  el('details',{class:'field-new-reference'},el('summary',{text:'Créer une référence ou une catégorie sans quitter la saisie'}),
   toolbar(field('Nom de la référence',newLabel),field('Code / modèle',newCode),field('Unité',newUnit)),
   toolbar(field('Catégorie existante',newCategory),field('Ou nouvelle catégorie',newCategoryName)),field('Précisions de compatibilité',newNote),
   button('Créer et sélectionner dans le catalogue',act(createReference),{icon:'plus'})),catalogueStatus);
 const validatePlan=()=>{
  for(const control of [...lines.querySelectorAll('input,textarea'),note]){
   if(!control.checkValidity()){const detail=control.closest('details');if(detail)detail.open=true;control.reportValidity();return false;}
  }
  return true;
 };
 const save=async()=>{
  if(model.readOnly||saving||!validatePlan())return;
  saving=true;form.setAttribute('aria-busy','true');
  const controls=[...form.querySelectorAll('input,select,textarea,button')].map(node=>[node,node.disabled]);
  for(const [node] of controls)node.disabled=true;
  try{await call('field.save',{token:model.token,plan:structuredClone(plan)});if(alive())await onSaved();}
  finally{saving=false;if(alive()){form.removeAttribute('aria-busy');for(const [node,disabled] of controls)node.disabled=disabled;}}
 };
 form.addEventListener('submit',event=>event.preventDefault());
 replaceChildren(form,el('h3',{text:plan.title}),el('p',{class:'field-help',text:[plan.siteName,plan.plannedStart?formatDate(plan.plannedStart):'Sans créneau enregistré',(model.document.participantLabels||[]).join(' · ')].filter(Boolean).join(' · ')}),
  el('div',{class:`notice ${model.snapshotOnly?'warning':'info'}`,text:model.message}),
  model.conflicts?el('div',{class:'notice warning',text:'Cette préparation possède des versions en conflit et reste en lecture seule.'}):null,
  status,el('label',{class:'field-check'},none,el('span',{text:'Aucun matériel nécessaire'})),lines,
  model.readOnly?null:add,field('Consignes de préparation',note),
  toolbar(model.readOnly?null:button('Tout marquer préparé',act(async()=>{
    if(!validatePlan()||!plan.lines.length)return;
    if(!await confirm('Confirmer la préparation complète ?',`Les ${plan.lines.length} lignes seront marquées préparées à leur quantité prévue. Cette saisie restera à enregistrer.`,'Marquer les lignes préparées'))return;
    if(!alive())return;
    for(const line of plan.lines){line.preparedQuantity=line.quantity;if(!line.preparedBy)line.preparedBy=state.identity?.label||'';}touch();renderLines();
   }),{icon:'check'}),
   button('Fermer',onCancel),model.readOnly?null:button('Enregistrer la préparation',act(save),{primary:true,icon:'check',attributes:{'data-field-action':'save-plan'}})));
 form.lastElementChild.classList.add('field-editor-actions');
 for(const control of [search,reference,count])control.addEventListener('keydown',event=>{
  if(event.key!=='Enter'||event.isComposing)return;event.preventDefault();
  if(reference.value)void act(addReference)();else reference.focus({preventScroll:true});
 });
 root.append(form);paintCatalogue();renderLines();
 attachFieldKitPicker({root:form,api,signal,token:model.token,plan,markDirty:touch,renderLines,onError,readOnly:model.readOnly});
 form.append(form.querySelector('.field-editor-actions'));
}
