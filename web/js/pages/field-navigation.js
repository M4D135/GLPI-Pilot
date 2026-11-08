// Attach the field action to existing commands, not another full-height toolbar.
// Keep the original workspace root and let its renderer retain geometry and CSS.
export function retainFieldAction(ctx,cleanup,action,target){
 const place=()=>{
  if(ctx.signal?.aborted)return;
  const host=target();if(host&&!host.contains(action))host.append(action);
 };
 const observer=new MutationObserver(place);observer.observe(ctx.root,{childList:true,subtree:true});place();
 const disconnect=()=>observer.disconnect();ctx.signal?.addEventListener('abort',disconnect,{once:true});
 const dispose=async()=>{
  // Do not detach the action until the existing dirty-editor guard accepts leaving.
  if(typeof cleanup==='function')await cleanup();
  observer.disconnect();ctx.signal?.removeEventListener('abort',disconnect);action.remove();
 };
 if(cleanup)Object.assign(dispose,cleanup);
 return dispose;
}
