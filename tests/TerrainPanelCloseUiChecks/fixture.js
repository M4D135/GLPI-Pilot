import {renderTicketMap} from '/js/pages/terrain-map.js';

// Real terrain-map, terrain-common, review, dialogs, CSS and Leaflet modules.
// Only the read-only API responses are fictitious and deliberately pausable.
const root=document.querySelector('#app');root.className='field-ui-test-root';root.removeAttribute('role');
const controller=new AbortController();let cleanup=null,releaseRead=null,rejectRead=null;
const fixture=window.terrainCloseFixture={holdRead:false,reads:0,mapReads:0,closed:0,notices:[],
 releaseRead(){releaseRead?.();},failRead(){rejectRead?.(new Error('Fictitious review read failure'));},
 async dispose(){controller.abort();await cleanup?.();}};
const filters={focusKeys:[],technicianKeys:[],mine:false,type:'all',statusCodes:[1,2,3,4],visitDate:'2026-10-27'};
const api={available:true,on(){return()=>{};},async call(operation){
 if(operation==='maps.tiles')return{enabled:false};
 controller.signal.throwIfAborted();
 if(operation==='maps.workspace.get'){
  fixture.mapReads++;
  return{token:'fictitious-map-token',revision:1,grain:'tickets',filters:structuredClone(filters),sourceDate:'2026-10-27T08:00:00Z',activeAvailable:true,
   totals:{tickets:0,placed:0,approximate:0,withoutCoordinates:0,review:0},locations:[],routes:[],visits:[],
   choices:{teams:[],groups:[],technicians:[],typeAvailable:true,technicianAvailable:true,personalKey:'',personalName:''}};
 }
 if(operation==='maps.workspace.rows')return{items:[],total:0,offset:0,limit:40};
 if(operation==='terrain.review.list'){
  fixture.reads++;
  if(fixture.holdRead){root.dataset.reviewWaiting='true';await new Promise((resolve,reject)=>{releaseRead=resolve;rejectRead=reject;});}
  controller.signal.throwIfAborted();
  return{revision:1,items:[],total:0,offset:0,limit:50,summary:{matchedCount:0,siteOnlyCount:0,unmatchedCount:0,ambiguousCount:0,titleUnavailableCount:0,manualCount:0,message:'Fictitious local review'}};
 }
 throw new Error('Unexpected fixture operation: '+operation);
}};
const state={session:{loaded:true,revision:1,source:'fictitious-local-source'},settings:{interface:{},dashboard:{personalTechnician:''}}};
cleanup=await renderTicketMap({root,api,state,params:{},signal:controller.signal,
 notify:(...args)=>fixture.notices.push(args),navigate:()=>{throw new Error('Unexpected navigation outside this test');}});
root.dataset.fixtureReady='true';
