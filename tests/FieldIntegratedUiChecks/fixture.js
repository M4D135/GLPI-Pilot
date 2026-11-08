import {api} from '/js/bridge.js';
import {renderFieldPreparation} from '/js/pages/field-preparation.js';

// Only the surrounding shell is minimal. Every API request crosses the production
// WebBridge and executes PilotApplication against the actual temporary SQLite DB.
const root=document.querySelector('#app');root.className='field-ui-test-root';root.removeAttribute('role');
const bootstrap=await api.call('app.bootstrap');
const state={settings:bootstrap.settings,session:bootstrap.session,identity:{label:'Agent fictif de qualification'}};
const controller=new AbortController();
const notices=[];
window.fieldIntegration={notices};
await renderFieldPreparation({root,api,state,params:{},signal:controller.signal,
 notify:(...args)=>notices.push(args),navigate:()=>{throw new Error('Unexpected navigation outside the integrated field fixture.');}});
