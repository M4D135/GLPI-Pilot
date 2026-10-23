import {el,button,card,toolbar,formatNumber,dialog,select,input,field} from './ui.js';

export const localFileTestHelp='CSV / TSV ou Excel XLSX / XLSM. CSV : première ligne en en-têtes. Excel : choisissez la feuille du tableau et, si nécessaire, le numéro de sa ligne d’en-têtes. Seules les valeurs enregistrées sont lues ; aucune macro, formule ou liaison externe n’est exécutée. Les feuilles masquées et le format .xls ne sont pas importés.';

export function localFileTestSummary(session) {
  const rows=formatNumber(session.rows||0),columns=formatNumber(session.columns||0);
  const total=Number.isFinite(session.testSourceRows)?formatNumber(session.testSourceRows):null;
  const missing=Number(session.testMissingFormulaValues)||0;
  const receipt=session.testFileReadComplete?'Fichier entièrement lu':missing>0?`${formatNumber(missing)} valeurs calculées indisponibles${total&&Number(session.testSourceRows)>Number(session.rows)?` · ${rows} / ${total} lignes chargées`:''}`:`Lecture limitée${total?` · ${rows} / ${total} lignes chargées`:''}`;
  return `${rows} lignes · ${columns} colonnes · ${receipt}`;
}

export async function importLocalTestFile(ctx) {
  let session=await ctx.api.call('records.test.import');
  if(session?.selectionRequired)session=await chooseTestSheet(ctx,session);
  if(session?.cancelled)return;
  ctx.state.session=session;
  ctx.notify(`Fichier de test ouvert : ${session.testFileName||'fichier local'}.`);
  await ctx.navigate('records');
}

function chooseTestSheet(ctx,selection) {
  return new Promise(resolve=>{
    const sheet=select(selection.sheets,''),row=input('',{type:'number',min:1,max:1048576,step:1,placeholder:'Première ligne non vide'});
    const content=el('div',{},el('p',{text:selection.fileName}),
      field('Feuille contenant les incidents',sheet,'Choisissez la liste source, avec une ligne par incident.'),
      field('Numéro de la ligne d’en-têtes',row,'Laissez vide pour utiliser la première ligne non vide. Par exemple, saisissez 4 si les colonnes ID, Titre et Statut se trouvent à la ligne 4.'),
      el('p',{class:'help',text:'Les autres feuilles du classeur ne seront pas importées.'}));
    let imported=false;
    const modal=dialog('Choisir le tableau Excel',content,[{label:'Annuler',action:close=>close()},
      {label:'Importer en mode test',primary:true,action:async close=>{
        if(!row.reportValidity())return;
        const result=await ctx.api.call('records.test.import',{token:selection.token,sheetName:sheet.value,headerRow:row.value===''?0:Number(row.value)});
        imported=true;close(result);
      }}]);
    modal.element.addEventListener('pilot:closed',event=>{
      if(imported){resolve(event.detail);return;}
      // The server owns the selected path. Closing the chooser discards its token,
      // and never changes the current observation or its restoration point.
      ctx.api.call('records.test.import',{token:selection.token,cancelled:true}).catch(()=>{});
      resolve({cancelled:true});
    },{once:true});
  });
}

export function localFileTestCard(ctx) {
  const session=ctx.state.session;
  return card('Explorer le fichier de test',
    el('p',{class:'local-test-file-name',text:session.testFileName||'Fichier local'}),
    el('p',{class:'help',text:localFileTestSummary(session)}),
    el('p',{class:'help',text:'Les calculs portent sur ce fichier local. Ils ne confirment ni la recherche ni le périmètre GLPI.'}),
    el('p',{class:'help',text:'La collecte, les actions sur les tickets, le partage et l’archivage sont suspendus. Les personnalisations de dashboards et de requêtes restent enregistrées dans vos réglages.'}),
    toolbar(button('Relevé',()=>ctx.navigate('records'),{icon:'table'}),
      button('Dashboard',()=>ctx.navigate('dashboard'),{primary:true,icon:'chart'}),
      button('Correspondances',()=>ctx.navigate('mapping')),
      button('Sites',()=>ctx.navigate('terrain-reference'),{icon:'map'}),
      button('Importer un fichier de test',()=>importLocalTestFile(ctx),{icon:'folder'})),
    el('details',{class:'local-test-help'},el('summary',{text:'Formats et règles de lecture'}),el('p',{class:'help',text:localFileTestHelp})));
}
