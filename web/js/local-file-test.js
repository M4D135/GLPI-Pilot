import {el,button,card,toolbar,formatNumber,dialog,select,input,field} from './ui.js';

export const localFileTestHelp='CSV / TSV ou Excel XLSX / XLSM. CSV : première ligne en en-têtes. Excel : choisissez la feuille du tableau et, si nécessaire, le numéro de sa ligne d’en-têtes. Seules les valeurs enregistrées sont lues ; aucune macro, formule ou liaison externe n’est exécutée. Les feuilles masquées et le format .xls ne sont pas importés.';

export function localFileTestSummary(session) {
  const rows=formatNumber(session.rows||0),columns=formatNumber(session.columns||0);
  if(session.demo)return `${rows} lignes fictives · ${columns} colonnes · Simulation complète`;
  const total=Number.isFinite(session.testSourceRows)?formatNumber(session.testSourceRows):null;
  const missing=Number(session.testMissingFormulaValues)||0;
  const receipt=session.testFileReadComplete?'Fichier entièrement lu':missing>0?`${formatNumber(missing)} valeurs calculées indisponibles${total&&Number(session.testSourceRows)>Number(session.rows)?` · ${rows} / ${total} lignes chargées`:''}`:`Lecture limitée${total?` · ${rows} / ${total} lignes chargées`:''}`;
  return `${rows} lignes · ${columns} colonnes · ${receipt}`;
}

export async function importLocalTestFile(ctx) {
  let session=await ctx.api.call('records.test.import',{prepareOnly:true});
  if(session?.selectionRequired)session=await chooseTestSheet(ctx,session);
  else if(session?.readyToImport){
    const token=session.token;
    try{session=await ctx.api.call('records.test.import',{token});}
    catch(error){await ctx.api.call('records.test.import',{token,cancelled:true}).catch(()=>{});throw error;}
  }
  if(session?.cancelled)return;
  ctx.state.session=session;
  ctx.notify(`Simulation ouverte : ${session.testFileName||'fichier local'}.`);
  await ctx.navigate('pilot-weather');
}

function chooseTestSheet(ctx,selection) {
  return new Promise(resolve=>{
    const suggestedSheet=selection.suggestedSheet||'',suggestedRow=Number(selection.suggestedHeaderRow)||'';
    const sheet=select(selection.sheets,suggestedSheet),row=input(suggestedRow,{type:'number',min:1,max:1048576,step:1,placeholder:'Première ligne non vide'});
    const profile=selection.workbookProfile?.available?selection.workbookProfile:null;
    const useProfile=el('input',{type:'checkbox',checked:!!profile});
    sheet.addEventListener('change',()=>{row.value=sheet.value===suggestedSheet?String(suggestedRow):'';});
    const content=el('div',{},el('p',{text:selection.fileName}),
      field('Feuille contenant les incidents',sheet,'Choisissez la liste source, avec une ligne par incident.'),
      field('Numéro de la ligne d’en-têtes',row,'Laissez vide pour utiliser la première ligne non vide. Par exemple, saisissez 4 si les colonnes ID, Titre et Statut se trouvent à la ligne 4.'),
      el('p',{class:'help',text:'Seule cette feuille fournit les lignes d’incidents.'}));
    if(profile)content.append(card('Paramétrage du pilotage détecté',
      el('p',{text:`Feuille ${profile.parameterSheet||'Paramétrage'} · ${formatNumber(profile.weatherRuleCount)} règles météo, dont ${formatNumber(profile.visibleWeatherRuleCount)} visibles · ${formatNumber(profile.siteCount)} sites · ${formatNumber(profile.aliasCount)} alias.`}),
      profile.freshnessWarningHours?el('p',{class:'help',text:`Rappel de fraîcheur après ${formatNumber(profile.freshnessWarningHours)} heures.`}):null,
      el('label',{class:'library-check'},useProfile,el('span',{text:'Reprendre la météo, les équipes et les sites du classeur dans la simulation'})),
      el('p',{class:'help',text:'Ces réglages seront appliqués à la simulation. Votre configuration habituelle sera retrouvée à sa fermeture.'}),
      ...(Array.isArray(profile.notes)?profile.notes:profile.notes?[profile.notes]:[]).map(text=>el('p',{class:'help',text})),
      ...(profile.warnings||[]).map(text=>el('p',{class:'notice warning',text}))));
    for(const warning of selection.warnings||[])content.append(el('p',{class:'notice warning',text:warning}));
    let imported=false;
    const modal=dialog('Choisir le tableau Excel',content,[{label:'Annuler',action:close=>close()},
      {label:'Ouvrir la simulation',primary:true,action:async close=>{
        if(!row.reportValidity())return;
        const result=await ctx.api.call('records.test.import',{token:selection.token,sheetName:sheet.value,headerRow:row.value===''?0:Number(row.value),useWorkbookProfile:!!profile&&useProfile.checked});
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
  return card('Votre espace de simulation',
    el('p',{class:'local-test-file-name',text:session.testFileName||(session.demo?'Données fictives':'Fichier local')}),
    el('p',{class:'help',text:localFileTestSummary(session)}),
    el('p',{class:'help',text:session.demo?'Les calculs portent sur le jeu fictif de démonstration. Vos essais peuvent faire évoluer ses tickets et ses indicateurs.':'Les calculs portent sur ce fichier local. Ils ne confirment ni la recherche ni le périmètre GLPI.'}),
    el('p',{class:'help',text:'Explorez toutes les rubriques, ouvrez les fiches, préparez vos tournées et essayez les actions, les collectes et le partage. Ces essais utilisent les données de la simulation et restent séparés de votre espace habituel.'}),
    toolbar(button('Pilotage',()=>ctx.navigate('pilot'),{primary:true,icon:'chart'}),
      button('Tickets',()=>ctx.navigate('records'),{icon:'ticket'}),
      button('Terrain',()=>ctx.navigate('terrain-map'),{icon:'map'}),
      button('Données',()=>ctx.navigate('data'),{icon:'folder'}),
      button('Charger un autre fichier',()=>importLocalTestFile(ctx),{icon:'folder'})),
    el('details',{class:'local-test-help'},el('summary',{text:'Formats et règles de lecture'}),el('p',{class:'help',text:localFileTestHelp})));
}
