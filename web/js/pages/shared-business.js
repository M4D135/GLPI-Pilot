import {el,button,toolbar,badge,empty,input,table,dialog,formatDate,formatNumber,showError} from '../ui.js';

export const sharedBusinessKinds={
 'team-configuration':'Sites, règles et tableaux de bord','team-tour':'Tournée commune',
 'team-technician':'Capacités et compétences','team-dispatch':'Répartition commune',
 'team-dataset':'Relevé commun','team-location':'Localisation corrigée','team-order':'Ordre de tournée'
};
const fieldLabels={cities:'Communes et sites',sites:'Sites',name:'Nom',address:'Adresse',latitude:'Latitude',longitude:'Longitude',postalCode:'Code postal',cityId:'Commune',terms:'Termes de détection',dashboard:'Tableaux de bord',queries:'Requêtes',rules:'Règles',thresholds:'Seuils',weather:'Météo',tickets:'Tickets',ticketIds:'Tickets',technicians:'Techniciens',stops:'Étapes',date:'Date',start:'Début',end:'Fin',durationMinutes:'Durée (min)',travelMinutes:'Trajet (min)',capacity:'Capacité',capacityDays:'Capacités par date',skills:'Compétences',availability:'Disponibilités',exceptions:'Exceptions',assignments:'Affectations',notes:'Notes',rows:'Lignes',columns:'Colonnes',observedAt:'Observation',sha256:'Empreinte du relevé',state:'État',label:'Libellé',kind:'Type',priority:'Priorité',description:'Description',title:'Titre'};
const readablePath=path=>path.split('/').filter(Boolean).map((part,index)=>part==='cities'?(index===0?'Référentiel':'Communes'):/^\d+$/.test(part)?`Élément ${Number(part)+1}`:fieldLabels[part]||part).join(' › ')||'Document';
const textValue=value=>value===undefined?'Champ absent':value===null?'Valeur nulle':value===true?'Oui':value===false?'Non':value===''?'Texte vide':typeof value==='string'?value:JSON.stringify(value);

function comparisonFields(current,incoming){
 const limit=5000,maximumDepth=18;
 const flatten=value=>{const fields=new Map();let visited=0,complete=true;const visit=(next,path,depth)=>{if(++visited>limit){complete=false;return;}if(next&&typeof next==='object'&&Object.keys(next).length){if(depth>=maximumDepth){complete=false;fields.set(path,{value:JSON.stringify(next).slice(0,2000),truncated:true});return;}for(const [key,item]of Object.entries(next)){if(visited>=limit){complete=false;break;}visit(item,`${path}/${key}`,depth+1);}return;}const raw=textValue(next);fields.set(path,{value:raw.slice(0,2000),truncated:raw.length>2000});};visit(value,'',0);return{fields,complete};};
 const left=flatten(current),right=flatten(incoming),rows=[];
 for(const path of new Set([...left.fields.keys(),...right.fields.keys()])){const a=left.fields.get(path),b=right.fields.get(path);if(a?.value!==b?.value||a?.truncated||b?.truncated)rows.push({path,current:a?.value??(left.complete?'Champ absent':'Non parcouru dans cet aperçu'),incoming:b?.value??(right.complete?'Champ absent':'Non parcouru dans cet aperçu'),truncated:!!a?.truncated||!!b?.truncated});}
 return{rows,truncated:!left.complete||!right.complete||rows.some(row=>row.truncated)};
}

export async function reviewSharedBusinessConflict(ctx,item,onChanged=async()=>{}){
 const {api,signal,notify}=ctx,call=(operation,args={})=>api.call(operation,args,{signal});
 const review=await call('sharing.business.conflict',{id:item.id,conflictId:item.conflictId});
 if(signal?.aborted)return;
 const comparison=comparisonFields(review.current?.data,review.incoming?.data),fields=el('div'),pager=el('div',{class:'library-pagination'}),counter=el('p',{class:'help',role:'status'});
 const search=input('',{type:'search',placeholder:'Filtrer un champ ou une valeur…','aria-label':'Filtrer les données communes'});let offset=0;
 const metadata=el('div',{class:'library-conflict-pair'},...[[review.current,'Version actuelle'],[review.incoming,'Proposition reçue']].map(([version,label])=>el('section',{},el('h3',{text:label}),toolbar(badge(version?.deleted?'Retirée':'Conservée',version?.deleted?'warning':''),el('span',{class:'help',text:`${version?.author||'Auteur non renseigné'} · ${formatDate(version?.updatedAt)}`})))));
 function paint(){const query=search.value.toLocaleLowerCase('fr'),rows=comparison.rows.filter(row=>`${row.path} ${row.current} ${row.incoming}`.toLocaleLowerCase('fr').includes(query));
  counter.textContent=`${formatNumber(rows.length)} différence(s)${comparison.truncated?' dans l’aperçu disponible':''}`;
  fields.replaceChildren(rows.length?table(rows.slice(offset,offset+40),[
   {label:'Information',render:row=>el('div',{},el('strong',{text:readablePath(row.path)}),el('code',{class:'library-field-path',text:row.path||'/'}) )},
   ...[['current','Version actuelle'],['incoming','Proposition reçue']].map(([key,label])=>({label,render:row=>el('div',{},el('pre',{class:'library-detail-text',text:row[key]}),row.truncated?el('span',{class:'help',text:'Valeur complète dans l’export des versions.'}):null)}))
  ]):empty(query?'Aucune différence correspondante':'Données identiques dans cet aperçu','Les auteurs, dates et éventuels retraits restent indiqués au-dessus.'));
  pager.replaceChildren(button('Champs précédents',()=>{offset=Math.max(0,offset-40);paint();},{disabled:offset===0}),el('span',{text:`${rows.length?offset+1:0}–${Math.min(rows.length,offset+40)} sur ${formatNumber(rows.length)}`}),button('Champs suivants',()=>{offset+=40;paint();},{disabled:offset+40>=rows.length}));
 }
 search.addEventListener('input',()=>{offset=0;paint();});paint();
 const body=el('div',{class:'library-detail library-business-review'},el('p',{text:review.title||sharedBusinessKinds[review.kind]||'Données communes'}),metadata,
  review.kind==='team-dataset'?el('p',{class:'notice info',text:'L’écran compare le résumé de chaque relevé : dimensions, observation et empreinte. L’export conserve les deux relevés complets pour les examiner.'}):null,
  comparison.truncated?el('p',{class:'notice warning',text:'Cette comparaison est limitée en nombre de champs ou en longueur. Exportez les deux versions complètes pour examiner les valeurs absentes de l’aperçu.'}):null,
  el('p',{class:'help',text:'Votre choix porte sur la version entière. Il sera transmis aux autres postes à la prochaine synchronisation.'}),search,counter,fields,pager);
 const apply=choice=>async close=>{const result=await call('sharing.business.resolve',{id:review.id,conflictId:review.conflictId,expectedRevision:review.expectedRevision,choice});close();notify(result.message||'Version retenue.');await onChanged();};
 const modal=dialog('Comparer les données communes',body,[
  {label:'Conserver la version actuelle',action:apply('current')},
  {label:'Appliquer la proposition reçue',primary:true,danger:review.incoming?.deleted===true,action:apply('incoming')},
  {label:'Exporter les versions complètes',action:async()=>{const result=await call('sharing.business.export',{id:review.id,conflictId:review.conflictId,expectedRevision:review.expectedRevision});if(!result.cancelled)notify('Les deux versions complètes ont été exportées.');}}
 ]);
 const close=()=>modal.close();signal?.addEventListener('abort',close,{once:true});modal.element.addEventListener('pilot:closed',()=>signal?.removeEventListener('abort',close),{once:true});
}

export async function renderSharedBusinessStatus(ctx,host=ctx.root){
 const {api,signal}=ctx;let disposed=false,generation=0;
 const section=el('section',{class:'card library-business-status','aria-label':'Données communes entre postes'});host.replaceChildren(section);
 async function refresh(){const version=++generation;try{const status=await api.call('sharing.business.status',{}, {signal});if(disposed||signal?.aborted||version!==generation)return;
   section.replaceChildren(el('div',{class:'library-business-heading'},el('div',{},el('h3',{text:'Données communes'}),el('p',{class:'help',text:status.enabled?`${formatNumber(status.documents)} document(s) commun(s) conservé(s) · sites, règles, relevés, tournées et organisation de l’équipe.`:'Le partage est facultatif. Les sites, règles, relevés, tournées et paramètres d’organisation sont disponibles sur ce poste.'})),button('Actualiser le partage',refresh,{icon:'refresh'})),
    ...(status.warnings||[]).map(text=>el('p',{class:'notice warning',text})),
    ...(status.conflicts||[]).map(item=>el('article',{class:'library-business-conflict'},el('div',{},badge(item.label||sharedBusinessKinds[item.kind]||'Données communes','warning'),el('strong',{text:item.title||'Version commune'}),el('span',{class:'help',text:`Proposition reçue le ${formatDate(item.recordedAt)}`})),button('Comparer et choisir',()=>reviewSharedBusinessConflict(ctx,item,refresh),{icon:'layers'}))),
    ...(status.moreConflicts?[el('p',{class:'help',text:'D’autres propositions restent à examiner. La liste sera actualisée après vos décisions.'})]:[]),
    ...(!status.conflicts?.length?[el('p',{class:'help',text:'Aucune proposition concurrente à examiner.'})]:[]));
  }catch(error){if(!disposed&&!signal?.aborted)showError(section,error);}
 }
 const stop=api.on('library.changed',()=>{if(!disposed)void refresh();});const cleanup=()=>{disposed=true;generation++;stop();signal?.removeEventListener('abort',cleanup);};signal?.addEventListener('abort',cleanup,{once:true});
 await refresh();cleanup.refresh=refresh;return cleanup;
}
