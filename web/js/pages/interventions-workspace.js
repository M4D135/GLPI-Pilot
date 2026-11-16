import {el} from '../ui.js';
import {renderInterventionsWorkspace as renderPlanning} from './interventions-planning.js';
import {renderFieldPreparation} from './field-preparation.js';
import {currentScope} from '../work-context.js';
import {ticketToolNavigation,searchForTools,publishTicketSearch} from '../ticket-search-context.js';

export const pages=[{id:'interventions',title:'Interventions',group:'Interventions',icon:'calendar',render:renderInterventionsWorkspace}];
export async function renderInterventionsWorkspace(ctx){
 const params={...ctx.params,toolsShell:true};
 if(!params.ticketSearch&&ctx.state.session?.loaded){
  const queryKeys=['focusKeys','technicianKeys','mine','indices','search','transform','basketOnly'],explicit=queryKeys.some(key=>Object.hasOwn(params,key))||Array.isArray(params.rows);
  let search=explicit?null:searchForTools(ctx.state);
  if(!search){
   const query={revision:params.revision??ctx.state.session.revision,...currentScope(ctx.state)};
   for(const key of queryKeys)if(Object.hasOwn(params,key))query[key]=structuredClone(params[key]);
   if(Array.isArray(params.rows))query.indices=[...params.rows];
   if(Array.isArray(query.indices)){if(!Object.hasOwn(params,'focusKeys'))query.focusKeys=[];if(!Object.hasOwn(params,'technicianKeys'))query.technicianKeys=[];}
   const result=await ctx.api.call('records.list',{...query,limit:1},{signal:ctx.signal});
   publishTicketSearch(ctx.state,query,result.total);search=searchForTools(ctx.state);
  }
  params.ticketSearch=search;
 }
 const active=params.tab||'planning',navigation=ticketToolNavigation(ctx,active),host=el('div',{class:'ticket-tool-screen'});
 ctx.root.append(navigation,host);
 const local={...ctx,root:host,params,navigate:(id,args={})=>ctx.navigate(id,args)};
 try{
  const cleanup=await(active==='preparation'?renderFieldPreparation(local):renderPlanning(local));
  const dispose=async()=>{await cleanup?.();navigation.remove();host.remove();};
  if(cleanup)Object.assign(dispose,cleanup);
  dispose.refresh=async()=>{
   if(params.ticketSearch&&params.ticketSearch.revision!==ctx.state.session?.revision)
    return ctx.navigate('interventions',{tab:host.querySelector('.interventions-workspace')?.dataset.tab||active,mapSearchText:host.querySelector('[aria-label="Rechercher dans les tickets du périmètre"]')?.value||''});
   await cleanup?.refresh?.();
  };
  return dispose;
 }catch(error){navigation.remove();host.remove();throw error;}
}
