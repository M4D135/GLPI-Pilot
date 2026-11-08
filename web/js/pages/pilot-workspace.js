import {button,toolbar} from '../ui.js';
import {pages as dashboardPages,renderPilotWorkspace as renderDashboards} from './pilot-dashboards.js';
import {renderFieldPreparation} from './field-preparation.js';
import {retainFieldAction} from './field-navigation.js';

export const pages=dashboardPages.map(page=>page.id==='pilot'?{...page,render:renderPilotWorkspace}:page);
export async function renderPilotWorkspace(ctx){
 if(ctx.params?.fieldPreparation!==true){
  const cleanup=await renderDashboards(ctx);
  const action=button('Matériel prévu',()=>ctx.navigate('pilot',{...ctx.params,fieldPreparation:true}),
   {icon:'layers',attributes:{'data-field-nav':'pilot'}});
  return retainFieldAction(ctx,cleanup,action,()=>ctx.root.querySelector('.workspace-commandbar .workspace-menu-content')||ctx.root.querySelector('.workspace-commandbar .toolbar'));
 }
 const switcher=toolbar(
  button('Tableaux de pilotage',()=>ctx.navigate('pilot',{...ctx.params,fieldPreparation:false}),{icon:'chart'}),
  button('Matériel prévu',null,{icon:'layers',attributes:{'aria-pressed':'true','data-field-nav':'pilot'}}));
 switcher.classList.add('field-workspace-switch');ctx.root.append(switcher);
 try{
  const cleanup=await renderFieldPreparation(ctx);
  const dispose=async()=>{if(typeof cleanup==='function')await cleanup();switcher.remove();};
  if(cleanup)Object.assign(dispose,cleanup);return dispose;
 }catch(error){switcher.remove();throw error;}
}
