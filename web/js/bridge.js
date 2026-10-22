const pending=new Map(),listeners=new Map();let sequence=0;
const host=globalThis.chrome?.webview;
function receive(event){const message=event.data;if(!message||typeof message!=='object')return;if(message.type==='event'){for(const handler of listeners.get(message.name)||[])try{handler(message.data);}catch(error){window.dispatchEvent(new CustomEvent('pilot:error',{detail:error}));}return;}
 const request=pending.get(message.id);if(!request)return;pending.delete(message.id);request.cleanup();if(message.ok)request.resolve(message.result);else{const error=new Error(message.error?.message||'L’opération a échoué.');error.code=message.error?.code;if(error.code==='CANCELLED')error.name='AbortError';request.reject(error);}}
host?.addEventListener('message',receive);
export const api={
 available:!!host,
 call(op,args={},options={}){if(!host)return Promise.reject(new Error('Ouvrez cette interface depuis GLPI-Pilot.exe pour accéder au moteur C#.'));if(options.signal?.aborted)return Promise.reject(new DOMException('Opération annulée.','AbortError'));
  return new Promise((resolve,reject)=>{const id=`p${Date.now().toString(36)}_${++sequence}`;const abort=()=>{if(!pending.has(id))return;pending.delete(id);host.postMessage({type:'cancel',id});cleanup();reject(new DOMException('Opération annulée.','AbortError'));};const cleanup=()=>options.signal?.removeEventListener('abort',abort);pending.set(id,{resolve,reject,cleanup,abort,op});options.signal?.addEventListener('abort',abort,{once:true});try{host.postMessage({id,op,args});}catch(error){pending.delete(id);cleanup();reject(error);}});
 },
 on(name,handler){if(!listeners.has(name))listeners.set(name,new Set());listeners.get(name).add(handler);return()=>listeners.get(name)?.delete(handler);},
 cancelAll(){for(const request of [...pending.values()])if(!['app.bootstrap','app.ready','host.editor'].includes(request.op))request.abort();},
 get pending(){return pending.size;}
};
window.addEventListener('pagehide',()=>api.cancelAll());
