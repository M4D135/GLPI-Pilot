import {el,replaceChildren,button,toolbar,badge,empty} from '../ui.js';

const field=(label,node)=>el('label',{class:'field'},el('span',{class:'field-label',text:label}),node);
const normalized=value=>String(value||'').normalize('NFD').replace(/\p{Diacritic}/gu,'').toLocaleLowerCase('fr');
const id=()=> 'custom-'+crypto.randomUUID().replaceAll('-','');
const units=[['piece','Pièce'],['metre','Mètre'],['lot','Lot']];
const pick=(values,current='')=>el('select',{},...values.map(([value,label])=>el('option',{value,text:label,selected:current===value})));

export function renderCatalogueEditor(ctx){
 const {root,model,api,signal,markDirty,onSaved,onCancel,onError}=ctx;
 const catalogue=structuredClone(model.catalogue);let offset=0;
 const form=el('form',{class:'field-catalogue-editor'}),categories=el('div',{class:'field-category-list'}),references=el('div',{class:'field-catalogue-list'}),paging=el('div',{class:'field-pagination'});
 const search=el('input',{type:'search',placeholder:'Nom ou code…',maxLength:300,'aria-label':'Rechercher une référence du catalogue'});
 const categoryFilter=pick([['','Toutes les catégories']]),archived=el('input',{type:'checkbox'});
 const newCategory=el('input',{type:'text',maxLength:120,placeholder:'Nom de la catégorie','aria-label':'Nom de la nouvelle catégorie'});
 const count=el('span',{class:'badge'});
 const act=fn=>async()=>{try{await fn();}catch(error){if(error.name!=='AbortError')onError(error);}};
 const touch=()=>{markDirty();count.textContent=`${catalogue.categories.length} catégorie(s) · ${catalogue.references.length} référence(s)`;};
 const categoryOptions=()=>catalogue.categories.map(value=>[value.id,value.name+(value.archived?' (archivée)':'')]);
 function refreshFilter(){
  const previous=categoryFilter.value;replaceChildren(categoryFilter,el('option',{value:'',text:'Toutes les catégories'}),...categoryOptions().map(([value,label])=>el('option',{value,text:label,selected:value===previous})));
 }
 function renderCategories(){
  categories.replaceChildren();
  for(const category of catalogue.categories){
   const name=el('input',{type:'text',value:category.name,maxLength:120,required:true,disabled:model.readOnly,'aria-label':'Nom de catégorie : '+category.name});
   const archive=el('input',{type:'checkbox',checked:category.archived,disabled:model.readOnly,'aria-label':'Archiver la catégorie '+category.name});
   name.addEventListener('input',()=>{category.name=name.value;touch();});
   name.addEventListener('change',()=>{refreshFilter();renderReferences();});
   archive.addEventListener('change',()=>{category.archived=archive.checked;touch();refreshFilter();renderReferences();});
   categories.append(el('div',{class:'field-category-row'},field('Catégorie',name),el('label',{class:'field-check'},archive,el('span',{text:'Archivée'}))));
  }
 }
 function filtered(){const term=normalized(search.value);return catalogue.references.filter(value=>(archived.checked||!value.archived)&&(!categoryFilter.value||value.categoryId===categoryFilter.value)&&normalized(value.label+' '+value.code).includes(term));}
 function renderReferences(){
  const values=filtered();if(offset>=values.length)offset=Math.max(0,Math.floor(Math.max(0,values.length-1)/20)*20);
  references.replaceChildren();
  for(const reference of values.slice(offset,offset+20)){
   const name=el('input',{type:'text',value:reference.label,maxLength:180,required:true,disabled:model.readOnly,'aria-label':'Libellé de référence : '+reference.label});
   const code=el('input',{type:'text',value:reference.code,maxLength:100,disabled:model.readOnly,'aria-label':'Code de référence : '+reference.label});
   const category=pick(categoryOptions(),reference.categoryId),unit=pick(units,reference.unit);
   category.disabled=unit.disabled=!!model.readOnly;
   const note=el('textarea',{value:reference.note||'',rows:2,maxLength:2000,disabled:model.readOnly,'aria-label':'Compatibilité : '+reference.label});
   const archive=el('input',{type:'checkbox',checked:reference.archived,disabled:model.readOnly});
   name.addEventListener('input',()=>{reference.label=name.value;touch();});code.addEventListener('input',()=>{reference.code=code.value;touch();});
   category.addEventListener('change',()=>{reference.categoryId=category.value;touch();});unit.addEventListener('change',()=>{reference.unit=unit.value;touch();});
   note.addEventListener('input',()=>{reference.note=note.value;touch();});archive.addEventListener('change',()=>{reference.archived=archive.checked;touch();});
   references.append(el('section',{class:'field-catalogue-reference','data-field-reference':reference.id},
    toolbar(field('Libellé',name),field('Code / modèle',code)),toolbar(field('Catégorie',category),field('Unité',unit)),
    field('Précisions et compatibilité',note),el('label',{class:'field-check'},archive,el('span',{text:'Référence archivée : interdite pour les nouveaux besoins, conservée dans les anciennes fiches'}))));
  }
  if(!values.length)references.append(empty('Aucune référence correspondante','Modifiez les filtres ou ajoutez une référence.'));
  replaceChildren(paging,button('Précédent',()=>{offset=Math.max(0,offset-20);renderReferences();},{disabled:offset===0}),
   el('span',{text:`${values.length?offset+1:0}–${Math.min(offset+20,values.length)} sur ${values.length} référence(s)`}),
   button('Suivant',()=>{offset+=20;renderReferences();},{disabled:offset+20>=values.length}));
 }
 function addCategory(){
  const name=newCategory.value.trim();if(!name)throw new Error('Donnez un nom à la nouvelle catégorie.');
  if(catalogue.categories.length>=100)throw new Error('Le catalogue accepte au maximum 100 catégories.');
  catalogue.categories.push({id:id(),name,archived:false});newCategory.value='';touch();renderCategories();refreshFilter();renderReferences();
 }
 function addReference(){
  if(catalogue.references.length>=2000)throw new Error('Le catalogue accepte au maximum 2 000 références.');
  const category=catalogue.categories.find(value=>!value.archived&&(categoryFilter.value?value.id===categoryFilter.value:true))||catalogue.categories.find(value=>!value.archived);
  if(!category)throw new Error('Créez d’abord une catégorie active.');
  const reference={id:id(),categoryId:category.id,code:'',label:'Nouvelle référence',unit:'piece',note:'',archived:false};
  catalogue.references.push(reference);search.value='';categoryFilter.value='';offset=Math.floor((filtered().length-1)/20)*20;touch();renderReferences();
  references.querySelector(`[data-field-reference="${reference.id}"] input`)?.focus();
 }
 async function save(){
  if(model.readOnly||!form.reportValidity())return;
  if(catalogue.categories.some(value=>!value.name.trim())||catalogue.references.some(value=>!value.label.trim()))throw new Error('Chaque catégorie et chaque référence doit avoir un nom, y compris sur les autres pages.');
  await api.call('field.catalogue.save',{catalogue,expectedRevision:model.revision},{signal});
  if(!signal?.aborted&&root.isConnected)await onSaved();
 }
 search.addEventListener('input',()=>{offset=0;renderReferences();});categoryFilter.addEventListener('change',()=>{offset=0;renderReferences();});archived.addEventListener('change',()=>{offset=0;renderReferences();});
 form.addEventListener('submit',event=>event.preventDefault());
 replaceChildren(form,el('div',{class:'notice info',text:'Les noms, modèles, unités et catégories sont personnalisables. Archivez au lieu de supprimer : les lignes déjà préparées conservent leurs libellés, références et unités d’origine.'}),
  model.readOnly?el('div',{class:'notice warning',text:'Des versions de ce catalogue sont en conflit. La consultation reste possible, mais l’enregistrement est bloqué.'}):null,
  count,el('details',{class:'field-category-editor'},el('summary',{text:'Modifier les catégories'}),categories,
   model.readOnly?null:toolbar(field('Nouvelle catégorie',newCategory),button('Ajouter la catégorie',act(addCategory),{icon:'plus'}))),
  toolbar(field('Rechercher',search),field('Catégorie',categoryFilter),el('label',{class:'field-check'},archived,el('span',{text:'Voir les références archivées'})),
   model.readOnly?null:button('Ajouter une référence',act(addReference),{icon:'plus',attributes:{'data-field-action':'new-reference'}})),
  references,paging,toolbar(button('Fermer',onCancel),model.readOnly?null:button('Enregistrer le catalogue',act(save),{primary:true,icon:'check',attributes:{'data-field-action':'save-catalogue'}})));
 root.append(form);count.textContent=`${catalogue.categories.length} catégorie(s) · ${catalogue.references.length} référence(s)`;renderCategories();refreshFilter();renderReferences();
}
