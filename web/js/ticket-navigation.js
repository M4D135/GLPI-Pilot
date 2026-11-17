// Browser side buttons and keyboard shortcuts use exactly the same guarded reader action.
export function bindTicketNavigation({root,signal,enabled,move,onError=()=>{}}){
 const controller=new AbortController(),options={capture:true,signal:controller.signal};
 const usable=()=>enabled()&&root.isConnected&&!document.querySelector('dialog[open]')&&!document.activeElement?.closest('input,textarea,select,[contenteditable="true"]');
 const direction=event=>event.key==='BrowserBack'||event.altKey&&!event.ctrlKey&&!event.metaKey&&event.key==='ArrowLeft'?-1:event.key==='BrowserForward'||event.altKey&&!event.ctrlKey&&!event.metaKey&&event.key==='ArrowRight'?1:0;
 const invoke=delta=>Promise.resolve().then(()=>move(delta)).catch(onError);
 const mouse=event=>{if(!usable()||![3,4].includes(event.button))return;event.preventDefault();event.stopImmediatePropagation();if(event.type==='mouseup')void invoke(event.button===3?-1:1);};
 for(const type of ['mousedown','mouseup','auxclick'])window.addEventListener(type,mouse,options);
 window.addEventListener('keydown',event=>{const delta=direction(event);if(!delta||!usable())return;event.preventDefault();event.stopImmediatePropagation();if(!event.repeat)void invoke(delta);},options);
 const dispose=()=>controller.abort();signal?.addEventListener('abort',dispose,{once:true});
 return()=>{signal?.removeEventListener('abort',dispose);dispose();};
}
