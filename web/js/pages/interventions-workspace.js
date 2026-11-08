import {button,toolbar} from '../ui.js';
import {renderInterventionsWorkspace as renderPlanning} from './interventions-planning.js';
import {renderFieldPreparation} from './field-preparation.js';
import {retainFieldAction} from './field-navigation.js';

export const pages=[{id:'interventions',title:'Interventions',group:'Interventions',icon:'calendar',render:renderInterventionsWorkspace}];
export async function renderInterventionsWorkspace(ctx){
 if(ctx.params?.tab!=='preparation'){
  const cleanup=await renderPlanning(ctx);
  const action=button('Matériel',()=>ctx.navigate('interventions',{...ctx.params,tab:'preparation'}),
   {icon:'layers',title:'Préparer le matériel des tickets, interventions et tournées',attributes:{'data-field-nav':'interventions'}});
  return retainFieldAction(ctx,cleanup,action,()=>ctx.root.querySelector('.calendar-commandbar > .toolbar'));
 }
 const switcher=toolbar(
  button('Planning, cartes et tournées',()=>ctx.navigate('interventions',{...ctx.params,tab:'planning'}),{icon:'calendar'}),
  button('Préparer le matériel',null,{icon:'layers',attributes:{'aria-pressed':'true','data-field-nav':'interventions'}}));
 switcher.classList.add('field-workspace-switch');ctx.root.append(switcher);
 try{
  const cleanup=await renderFieldPreparation(ctx);
  const dispose=async()=>{if(typeof cleanup==='function')await cleanup();switcher.remove();};
  if(cleanup)Object.assign(dispose,cleanup);return dispose;
 }catch(error){switcher.remove();throw error;}
}
