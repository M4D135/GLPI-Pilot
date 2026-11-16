import {el,button,formatNumber} from './ui.js';
import {workspaceContextKey} from './work-context.js';

// Ephemeral and revision-bound: source rows and ticket selections never enter localStorage.
const contexts=new WeakMap();
const identity=state=>`${workspaceContextKey(state)}:${state.session?.revision}`;
export function ticketSearchContext(state){const value=contexts.get(state);return value?.identity===identity(state)?structuredClone(value):null;}
export function publishTicketSearch(state,args,total,selectedRows=[],labels=[]){
 const query={revision:state.session.revision,search:args.search||'',transform:structuredClone(args.transform||{filters:[],sorts:[]}),
  focusKeys:[...(args.focusKeys||[])],technicianKeys:[...(args.technicianKeys||[])],mine:!!args.mine,basketOnly:!!args.basketOnly};
 if(Array.isArray(args.indices))query.indices=[...args.indices];
 const value={identity:identity(state),query,total,selectedRows:[...selectedRows],labels:[...labels]};
 contexts.set(state,value);return structuredClone(value);
}
export function searchForTools(state){const context=ticketSearchContext(state);if(!context)return undefined;return{...context.query,...(context.selectedRows.length?{selectedRows:context.selectedRows}:{})};}
export function searchToolParams(state,params={}){const ticketSearch=searchForTools(state);return{...(ticketSearch?{ticketSearch}:{}),...params};}
export function ticketToolNavigation(ctx,active='search'){
 const host=el('div',{class:'ticket-tools-context'});
 const paint=()=>{
  const context=ticketSearchContext(ctx.state),count=context?.selectedRows.length||context?.total||0;
  const summary=context?`${context.selectedRows.length?'Sélection':'Recherche'} · ${formatNumber(count)} ticket${count>1?'s':''}`:ctx.state.session?.loaded?'Tous les tickets du relevé':'Aucun relevé chargé';
  const tabs=[['search','Rechercher','search',()=>ctx.navigate('records',{returnToSearch:true})],['map','Carte','map',()=>ctx.navigate('interventions',searchToolParams(ctx.state,{tab:'map',...(active==='search'?{ticketMap:ctx.state.session?.loaded===true}:{})}))],['planning','Planning','calendar',()=>ctx.navigate('interventions',searchToolParams(ctx.state,{tab:'planning'}))],['tours','Tournées','route',()=>ctx.navigate('interventions',searchToolParams(ctx.state,{tab:'tours'}))],['preparation','Matériel','layers',()=>ctx.navigate('interventions',searchToolParams(ctx.state,{tab:'preparation'}))]];
  host.replaceChildren(el('nav',{class:'ticket-tools-nav','aria-label':'Outils de la recherche'},...tabs.map(([id,label,icon,action])=>button(label,async()=>{await ctx.beforeTicketToolNavigate?.();if(!ctx.signal?.aborted)return action();},{icon,attributes:{class:`button ticket-tool ${active===id?'active':''}`,'aria-current':active===id?'page':undefined,dataset:{...(id==='planning'||id==='map'||id==='tours'?{calendarTab:id}:{})}}}))),
   el('div',{class:'ticket-context-summary'},el('strong',{text:summary}),context?.query.search?el('span',{class:'ticket-context-chip',text:`« ${context.query.search} »`}):null,...(context?.labels||[]).map(text=>el('span',{class:'ticket-context-chip',text})),
    active!=='search'?button('Modifier la recherche',()=>ctx.navigate('records',{returnToSearch:true}),{icon:'search'}):null));
 };
 paint();host.refresh=paint;return host;
}
