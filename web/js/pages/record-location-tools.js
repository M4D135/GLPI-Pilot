import {dialog,showError,el} from '../ui.js';
import {editLocationCorrection} from './terrain-correction.js';

// Same guarded correction editor as Terrain, hosted by the unique search list.
export function createRecordLocationTools(ctx){
 const owned=new Set(),errors=el('div',{class:'record-correction-errors',role:'alert'});ctx.root.append(errors);let running=false;
 const p={ctx,errors,call:(op,args)=>ctx.api.call(op,args,{signal:ctx.signal}),
  message:text=>ctx.notify(text,'info'),
  async run(action,target=errors){if(running||ctx.signal.aborted)return;running=true;target.replaceChildren();try{return await action();}catch(error){if(error.name!=='AbortError')showError(target,error);}finally{running=false;}},
  dialog(title,body,actions){const popup=dialog(title,body,actions);owned.add(popup);popup.element.addEventListener('pilot:closed',()=>owned.delete(popup),{once:true});return popup;}};
 const dispose=()=>{for(const popup of owned)popup.close();owned.clear();errors.remove();};ctx.signal.addEventListener('abort',dispose,{once:true});
 return{open:options=>editLocationCorrection(p,options),dispose:()=>{ctx.signal.removeEventListener('abort',dispose);dispose();}};
}
