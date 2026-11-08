import {el,button,input,dialog,toolbar} from './ui.js';
import {teamScopeCatalog,teamScopeSelection} from './team-scope.js';

export function chooseWorkspaceTeams(ctx,bootstrap,current,onApply){
 const options=teamScopeCatalog(bootstrap.focus||[],bootstrap.groups||[]),selection=teamScopeSelection(options,current);
 const chosen=new Set(selection.keys),query=input('',{type:'search',placeholder:'Rechercher une équipe','aria-label':'Rechercher une équipe',maxLength:200}),list=el('div',{class:'maps-picker-options'});
 const paint=()=>list.replaceChildren(...options.filter(option=>(option.label+' '+(option.search||'')).toLocaleLowerCase('fr').includes(query.value.toLocaleLowerCase('fr'))).map(option=>{
  const check=input('',{type:'checkbox',checked:chosen.has(option.key),'aria-label':option.label});
  check.addEventListener('change',()=>{check.checked?chosen.add(option.key):chosen.delete(option.key);});
  return el('label',{class:'maps-picker-option'},check,el('span',{text:option.label}));
 }));
 query.addEventListener('input',paint);
 const modal=dialog('Équipes du périmètre',el('div',{},el('p',{class:'help',text:'Les équipes choisies se cumulent. Chaque ticket est compté une seule fois.'}),
  toolbar(query,button('Toutes les équipes',()=>{chosen.clear();paint();})),list),[
  {label:'Annuler',action:close=>close()},
  {label:'Appliquer',primary:true,action:async close=>{if(chosen.size>64)throw new Error('Choisissez au maximum 64 équipes.');await onApply([...chosen]);close();}}
 ]);paint();return modal;
}

export function chooseWorkspaceTechnicians(ctx,choices,current,onApply){
 const chosen=new Set(current||[]),list=el('div',{class:'maps-picker-options'}),query=input('',{type:'search',placeholder:'Rechercher un technicien','aria-label':'Rechercher un technicien',maxLength:200});
 function paint(){list.replaceChildren(...choices.filter(person=>person.name.toLocaleLowerCase('fr').includes(query.value.toLocaleLowerCase('fr'))).map(person=>{
  const check=input('',{type:'checkbox',checked:chosen.has(person.key),'aria-label':person.name});check.addEventListener('change',()=>check.checked?chosen.add(person.key):chosen.delete(person.key));
  return el('label',{class:'maps-picker-option'},check,el('span',{text:person.name}));
 }));}
 query.addEventListener('input',paint);const modal=dialog('Techniciens du périmètre',el('div',{},toolbar(query,button('Tous les techniciens',()=>{chosen.clear();paint();})),list),[
  {label:'Annuler',action:close=>close()},
  {label:'Appliquer',primary:true,action:async close=>{if(chosen.size>64)throw new Error('Choisissez au maximum 64 techniciens.');await onApply([...chosen]);close();}}
 ]);paint();return modal;
}
