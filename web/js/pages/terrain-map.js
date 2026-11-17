import { el, button, card, toolbar, empty, badge, input, select, field, table, form, formatNumber, formatDate, showError } from '../ui.js';
import { page, pager } from './terrain-common.js';
import { mountMap } from './maps.js';
import { navigationVisible } from '../navigation.js';
import { teamScopeCatalog, teamScopeSelection } from '../team-scope.js';
import {workspaceContextKey,readWorkspaceView,saveWorkspaceView,currentScope,rememberScope} from '../work-context.js';
import { createReadyAction } from '../terrain-activity.js';
import { publishMapTicketSearch } from '../ticket-search-context.js';

const selections=new WeakMap();

const statusChoices = [
  { key: '1', label: 'Nouveau' }, { key: '2', label: 'En cours · attribué' },
  { key: '3', label: 'En cours · planifié' }, { key: '4', label: 'En attente' }
];

export async function renderTicketMap(ctx) {
  const p = page(ctx, 'Carte des tickets', 'Sélectionner les lieux, puis ouvrir les tickets filtrés.');
  p.root.classList.add('terrain-map-page');
  const simulationText = p.root.querySelector('[data-simulation] p');
  if (simulationText) simulationText.textContent = 'Tickets et tournées restent dans la simulation. Les rues OpenStreetMap utilisent Internet ; un fond local reste disponible hors connexion.';
  const loaded = ctx.state.session?.loaded === true;
  const previous=readWorkspaceView(ctx.state,'map'),shared=currentScope(ctx.state);
  const linkedSearch=ctx.params?.ticketSearch;
  const initial = {focusKeys:shared.focusKeys,technicianKeys:shared.technicianKeys,mine:!!ctx.state.settings?.dashboard?.personalTechnician,...previous.filters,...ctx.params,...(linkedSearch?{focusKeys:[],technicianKeys:[],mine:false,type:'all',statusCodes:[1,2,3,4]}:{})};
  let model, map = null, filters = {
    focusKeys: Array.isArray(initial.focusKeys) ? [...initial.focusKeys] : initial.focusKey ? [initial.focusKey] : [],
    technicianKeys: Array.isArray(initial.technicianKeys) ? [...initial.technicianKeys] : initial.technicianKey ? [initial.technicianKey] : [],
    mine: initial.mine === true, type: initial.type || 'all', statusCodes: Array.isArray(initial.statusCodes) ? [...initial.statusCodes] : [1, 2, 3, 4],
    ...(initial.visitDate ? { visitDate: initial.visitDate } : {})
  };
  let selectionMemory=selections.get(ctx.state);if(!selectionMemory){selectionMemory=new Map();selections.set(ctx.state,selectionMemory);}
  const selectionKey=workspaceContextKey(ctx.state);
  const remembered=selectionMemory.get(selectionKey);
  let selected = new Set(remembered?.revision===ctx.state.session.revision?remembered.rows:[]), placeKey = ctx.params?.placeKey || ctx.params?.mapPlaceKey || previous.placeKey || '', offset = previous.offset || 0, generation = 0, viewport=previous.viewport;
  let chosenPlaces = new Set(ctx.params?.mapPlaceKeys || (remembered?.revision===ctx.state.session.revision?remembered.places||[]:[]));
  if(ctx.params?.placeKey||ctx.params?.mapPlaceKey)chosenPlaces.add(ctx.params.placeKey||ctx.params.mapPlaceKey);
  let activePanel = null;
  function panelButton(label, name, options = {}) {
    const control = button(label, p.act(() => openPanel(name)), options);
    control.dataset.openPanel = name; return control;
  }
  const management = toolbar(
    panelButton('Explorer les sites', 'sites', { icon: 'map' }),
    panelButton('Tournées prévues', 'tours', { icon: 'calendar' }),
    panelButton('Continuer le planning', 'planning', { icon: 'route' }),
    panelButton('Corriger une localisation', 'review', { icon: 'search', disabled: !loaded }),
    panelButton('Gérer le référentiel', 'reference', { icon: 'settings' }),
    el('details', { class: 'maps-terrain-tools' }, el('summary', { text: 'Options du terrain' }),
      el('div', { class: 'maps-terrain-tools-content' },
        navigationVisible('populations', ctx.state.settings?.interface) ? panelButton('Effectifs des sites · facultatif', 'populations') : null,
        panelButton('Panier et ordre des communes', 'projection'),
        panelButton('Services de route et de météo', 'services'))));
  const actions=el('details',{class:'maps-workspace-actions'},el('summary',{text:'Gérer les lieux et les tournées'}),management);
  const controls = el('div', { class: 'maps-scope-toolbar toolbar',hidden:!!linkedSearch });
  const scopeSummary = el('p', { class: 'maps-scope-summary', role: 'status', 'aria-live': 'polite' });
  const totals = el('div', { class: 'maps-workload-totals' });
  const warnings = el('div', { class: 'maps-workload-warnings' });
  const mapHost = el('div', { class: 'maps-inline-host' });
  const locationTitle = el('h2', { text: 'Choisir les lieux' });
  const cityChoices=el('div',{class:'maps-city-choices',role:'group','aria-label':'Villes'});
  const locationChips=el('div',{class:'maps-selection-chips'});
  const selectionSummary = el('span', { class: 'maps-selection-count', role: 'status', 'aria-live': 'polite' });
  const viewTickets=button('Voir les tickets',p.act(openSearch),{icon:'search',primary:true});
  const allPlaces=button('Effacer les lieux',()=>{chosenPlaces.clear();placeKey='';paintLocation();},{icon:'close'});
  const prepare=button('Préparer une tournée',p.act(async()=>{
    const scope=await p.call('maps.workspace.search',tokenArgs({placeKeys:[...chosenPlaces]}));
    if(scope.total>(model.selectionLimit||2000))throw new Error('La recherche conserve tous les tickets. Pour une tournée, réduisez la sélection à '+formatNumber(model.selectionLimit||2000)+' tickets au maximum.');
    await p.call('maps.workspace.select',tokenArgs({clear:true}));
    const result=await p.call('maps.workspace.select',tokenArgs(chosenPlaces.size?{placeKeys:[...expandedPlaces()]}:{all:true}));
    selected=new Set(result.rows);prepareTour();
  }),{icon:'route'});
  const locationPicker=el('div',{class:'maps-location-options'});
  const placeSearch=input('',{type:'search',placeholder:'Rechercher un lieu…','aria-label':'Rechercher un lieu',maxLength:200});
  const picker=el('details',{class:'maps-location-picker'},el('summary',{text:'Choisir plusieurs lieux'}),placeSearch,locationPicker);
  placeSearch.addEventListener('input',paintPicker);
  const listPanel=card('',el('div',{class:'maps-ticket-heading'},locationTitle,selectionSummary),
    cityChoices,locationChips,toolbar(viewTickets,allPlaces,prepare),picker);
  listPanel.classList.add('maps-selection-panel');
  p.body.append(controls, scopeSummary, totals, listPanel, mapHost, warnings, actions);

  async function openSearch(){
    const result=await p.call('maps.workspace.search',tokenArgs({placeKeys:[...chosenPlaces]}));
    if(ctx.signal.aborted)return;
    publishMapTicketSearch(ctx.state,result,linkedSearch||{},ctx.params?.mapFocusRow);
    await ctx.navigate('records',{returnToSearch:true,revision:result.revision});
  }
  function paintPicker(){
    if(!model)return;
    const fold=value=>String(value).normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLocaleLowerCase('fr');
    const term=fold(placeSearch.value);
    locationPicker.replaceChildren(...model.locations.filter(location=>fold(location.place.label).includes(term)).map(location=>{
      const inherited=!chosenPlaces.has(location.key)&&expandedPlaces().has(location.key);
      const check=input('',{type:'checkbox',checked:chosenPlaces.has(location.key)||inherited,disabled:inherited,'aria-label':`Inclure ${location.place.label}`,title:inherited?'Inclus par la ville sélectionnée':''});
      check.addEventListener('change',()=>{check.checked?chosenPlaces.add(location.key):chosenPlaces.delete(location.key);paintLocation();});
      return el('label',{class:'maps-location-choice'},check,el('span',{text:location.place.label}),badge(formatNumber(location.count)));
    }));
  }

  function tokenArgs(extra = {}) { return { token: model.token, ...extra }; }
  function unit(count = model.totals.tickets) { return model.grain === 'tickets' ? count === 1 ? 'ticket' : 'tickets' : count === 1 ? 'ligne' : 'lignes'; }
  function expandedPlaces(){
    const keys=new Set();
    for(const key of chosenPlaces){const group=model.cityGroups?.find(city=>city.key===key);for(const placeKey of group?.placeKeys||[key])keys.add(placeKey);}
    return keys;
  }
  function locationChoices(){return[...(model.cityGroups||[]),...model.locations.filter(location=>!model.cityGroups?.some(city=>city.key===location.key)).map(location=>({...location,label:location.place.label}))];}
  function togglePlace(key){chosenPlaces.has(key)?chosenPlaces.delete(key):chosenPlaces.add(key);placeKey=key;paintLocation();}
  function paintSelection() { paintLocation(); }
  function paintLocation() {
    if(!model)return;
    const locations=locationChoices().filter(item=>chosenPlaces.has(item.key)),expanded=expandedPlaces();
    const count=locations.length?model.locations.filter(item=>expanded.has(item.key)).reduce((sum,item)=>sum+item.count,0):model.totals.tickets;
    selectionSummary.textContent=`${formatNumber(count)} ${unit(count)} · ${locations.length?`${locations.length} lieu${locations.length>1?'x':''}`:'tout le périmètre'}`;
    locationTitle.textContent=locations.length?'Sélection géographique':'Tous les lieux du périmètre';
    viewTickets.querySelector('span').textContent=`Voir les ${formatNumber(count)} ${unit(count)}`;
    viewTickets.disabled=count===0;allPlaces.disabled=locations.length===0;prepare.disabled=count===0;
    locationChips.replaceChildren(...locations.filter(location=>!model.cityGroups?.some(city=>city.key===location.key)).map(location=>button(location.label,()=>{chosenPlaces.delete(location.key);paintLocation();},{icon:'close',title:`Retirer ${location.label}`,attributes:{class:'button maps-location-chip'}})));
    cityChoices.replaceChildren(...(model.cityGroups||[]).map(city=>button(city.label,()=>togglePlace(city.key),{attributes:{'aria-label':city.label,'aria-pressed':String(chosenPlaces.has(city.key)),class:'button maps-city-choice'}})));
    map?.setSelection?.([...expanded]);paintPicker();
    listPanel.dataset.total=String(count);
  }
  async function choosePlace(place, visit) {
    if(model.locations.some(location=>location.key===place.id)){
      togglePlace(place.id);
    }else if(visit)p.message(`Visite PRÉVUE le ${visit.date} · ${visit.technician}. Aucun ticket de ce lieu dans le périmètre affiché.`);
  }
  function multiChoice(title, choices, current, apply, { allowEmpty = true } = {}) {
    const draft = new Set(current), maximum = 64;
    const errors = el('div', { role: 'alert' });
    const query = input('', { type: 'search', placeholder: 'Rechercher…', maxLength: 200 });
    const counter = el('p', { class: 'maps-picker-count', role: 'status', 'aria-live': 'polite' });
    const fold = value => value.normalize('NFD').replace(/[\u0300-\u036f]/gu, '').toLocaleLowerCase('fr');
    const rows = choices.map(choice => {
      const control = input(choice.key, { type: 'checkbox', checked: draft.has(choice.key), 'aria-label': choice.label, 'aria-description': choice.detail });
      const label = el('label', { class: 'maps-picker-option' }, control, el('div', {}, el('div', { text: choice.label }),
        choice.detail ? el('small', { class: 'help', text: choice.detail }) : null));
      control.addEventListener('change', () => { if (control.checked) draft.add(choice.key); else draft.delete(choice.key); update(); });
      return { control, label, key: choice.key, searchable: fold(`${choice.label} ${choice.search || ''}`) };
    });
    const options = el('div', { class: 'maps-picker-options' }, ...rows.map(row => row.label));
    const popup = p.dialog(title, el('div', { class: 'maps-picker' },
      errors,
      el('p', { class: 'help', text: 'Les choix d’une même liste se cumulent. Chaque ligne du relevé est comptée une seule fois.' }),
      choices.length > 8 ? field('Recherche', query) : null,
      toolbar(button(allowEmpty ? 'Toutes' : 'Tous les statuts actifs', () => { draft.clear(); if (!allowEmpty) choices.forEach(choice => draft.add(choice.key)); update(); }), counter), options),
    [{ label: 'Annuler', action: close => close() }, { label: 'Appliquer', primary: true, action: close => p.run(async () => {
      if (!allowEmpty && !draft.size) throw new Error('Choisissez au moins un statut actif.');
      const before = { ...filters }; apply([...draft]);
      try { await load(); close(); } catch (error) { filters = before; throw error; }
    }, errors) }]);
    popup.element.classList.add('maps-filter-dialog');
    function update() {
      for (const row of rows) { row.control.checked = draft.has(row.key); row.control.disabled = draft.size >= maximum && !row.control.checked; row.label.hidden = !row.searchable.includes(fold(query.value)); }
      counter.textContent = draft.size ? `${formatNumber(draft.size)} choix retenus` : allowEmpty ? 'Tous les choix' : 'Aucun statut sélectionné';
    }
    query.addEventListener('input', update); update(); if (choices.length > 8) query.focus();
  }
  function paintControls() {
    const teams = teamScopeCatalog(model.choices.teams, model.choices.groups);
    const teamSelection = teamScopeSelection(teams, filters.focusKeys);
    const technicians = [...model.choices.technicians.map(person => ({ key: person.key, label: person.name })), { key: '@unassigned', label: 'Sans technicien attribué' }];
    const type = select([{ value: 'all', label: 'Tous les types' }, { value: 'incident', label: 'Incidents' }, { value: 'request', label: 'Demandes' }, { value: 'unknown', label: 'Type non reconnu' }], filters.type);
    type.setAttribute('aria-label', 'Type de ticket'); type.disabled = !model.choices.typeAvailable;
    type.addEventListener('change', () => { filters.type = type.value; void p.run(() => load()); });
    const date = input(filters.visitDate, { type: 'date', min: '2000-01-01', max: '2100-12-31' });
    date.setAttribute('aria-label', 'Jour des visites prévues');
    date.addEventListener('change', () => { if (date.value) { filters.visitDate = date.value; void p.run(() => load()); } });
    controls.replaceChildren(
      button('Tous les techniciens', p.act(async () => { filters.mine = false; filters.technicianKeys = []; await load(); }), { primary: !filters.mine && !filters.technicianKeys.length, icon: 'users' }),
      button('Tickets attribués', p.act(async () => { if (!model.choices.personalKey) { if(ctx.chooseIdentity)await ctx.chooseIdentity({params:{mine:true,technicianKeys:[]}});else await ctx.navigate('technicians'); return; } filters.mine = true; filters.technicianKeys = []; await load(); }),
        { primary: filters.mine, icon: 'ticket', title: model.choices.personalName ? `Tickets attribués à ${model.choices.personalName}` : 'Choisir l’identité pour afficher mes tickets' }),
      button(`Équipes${teamSelection.keys.length ? ` (${teamSelection.keys.length})` : ''}`, () => multiChoice('Équipes', teams, teamSelection.keys, values => { filters.focusKeys = values; }), { icon: 'layers' }),
      button(`Techniciens${filters.technicianKeys.length ? ` (${filters.technicianKeys.length})` : ''}`, () => multiChoice('Techniciens attribués', technicians, filters.technicianKeys,
        values => { filters.technicianKeys = values; filters.mine = false; }), { icon: 'users', disabled: !model.choices.technicianAvailable }),
      type, button(`Statuts actifs (${filters.statusCodes.length}/4)`, () => multiChoice('Statuts actifs', statusChoices, filters.statusCodes.map(String),
        values => { filters.statusCodes = values.map(Number); }, { allowEmpty: false })),
      el('label', { class: 'maps-visit-date' }, el('span', { text: 'Visites prévues' }), date),
      button('Actualiser', p.act(() => load()), { icon: 'refresh' }));
    const groupLabels = teamSelection.options.map(choice => choice.label);
    const personLabels = filters.mine ? [model.choices.personalName] : technicians.filter(choice => filters.technicianKeys.includes(choice.key)).map(choice => choice.label);
    scopeSummary.textContent = linkedSearch?`Recherche de tickets · ${formatNumber(model.totals.tickets)} ${unit()} · relevé du ${formatDate(model.sourceDate)}`:[groupLabels.length ? groupLabels.join(' · ') : 'Toutes les équipes',
      personLabels.length ? personLabels.join(' · ') : 'Tous les techniciens', `${formatNumber(model.totals.tickets)} ${unit()} · relevé du ${formatDate(model.sourceDate)}`].join(' — ');
  }
  function paintTotals() {
    const facts = [
      ['Stock du périmètre', model.totals.tickets, unit(), 'ticket'], ['Sur la carte', model.totals.placed, `${unit(model.totals.placed)} avec coordonnées`, 'map'],
      ['Localisation approximative', model.totals.approximate, `${unit(model.totals.approximate)} · commune connue, site à préciser`, 'map'],
      ['Sans coordonnées', model.totals.withoutCoordinates, `${unit(model.totals.withoutCoordinates)} conservé${model.totals.withoutCoordinates === 1 ? '' : 's'} dans la recherche`, 'search']
    ];
    totals.replaceChildren(...facts.map(([label, value, detail, symbol]) => el('div', { class: `maps-workload-total tone-${symbol}` },
      el('span', { text: label }), el('strong', { text: formatNumber(value) }), el('small', { text: detail }))));
    warnings.replaceChildren();
    if (!linkedSearch && !model.activeAvailable) warnings.append(el('p', { class: 'notice warning', text: 'Le stock actif ne peut pas être déterminé. Vérifiez la colonne Statut et ses correspondances.' }));
    if (!linkedSearch && model.sourceUnknownStatus) warnings.append(el('p', { class: 'help', text: `${formatNumber(model.sourceUnknownStatus)} lignes au statut non reconnu dans le relevé complet sont exclues du stock actif. Les tickets résolus et clos sont également exclus.` }));
    if (model.totals.review) warnings.append(el('p', { class: 'help', text: `${formatNumber(model.totals.review)} ${unit(model.totals.review)} avec une localisation à vérifier. La recherche conserve tous ces tickets, même sans coordonnées.` }));
    if (model.visitsWarning) warnings.append(el('p', { class: 'notice warning', text: model.visitsWarning }));
  }
  async function load({ correctedRevision = null } = {}) {
    if(model&&linkedSearch&&correctedRevision===null&&linkedSearch.revision!==ctx.state.session.revision){
      await ctx.refresh();return;
    }
    const before=model,oldFilters=before?JSON.stringify(before.filters):JSON.stringify(previous.filters),oldRows=[...selected];
    viewport=map?.getView()||viewport;
    const search=linkedSearch&&correctedRevision!==null?{...linkedSearch,revision:correctedRevision}:linkedSearch;
    const next = await p.call('maps.workspace.get', {...filters,...(search?{ticketSearch:search}:{})});
    if (ctx.signal.aborted) return;
    if(linkedSearch&&correctedRevision===next.revision)linkedSearch.revision=correctedRevision;
    map?.dispose(); mapHost.replaceChildren(); generation++;
    model = next; filters = { ...model.filters }; selected = new Set();
    if(placeKey&&!model.locations.some(location=>location.key===placeKey)){placeKey='';offset=0;}
    if(before&&before.revision!==model.revision&&correctedRevision===null)chosenPlaces.clear();
    chosenPlaces=new Set([...chosenPlaces].filter(key=>locationChoices().some(location=>location.key===key)));
    if(correctedRevision===null&&oldFilters!==JSON.stringify(model.filters))offset=0;
    if(oldRows.length&&(before?.revision===model.revision||remembered?.revision===model.revision||correctedRevision===model.revision)&&oldFilters===JSON.stringify(model.filters)){
      const kept=await p.call('maps.workspace.select',{token:model.token,rows:oldRows,selected:true});selected=new Set(kept.rows);
    }
    paintControls(); paintTotals(); paintSelection(); paintLocation();
    map = mountMap(ctx, model, mapHost, { onSelect: choosePlace,view:viewport });
    paintLocation();
    if(!linkedSearch)rememberScope(ctx.state,filters);
    if (oldRows.length&&!selected.size) p.message('Le périmètre a changé. Sélectionnez les tickets à planifier dans cette nouvelle vue.');
    p.root.dataset.ready = 'true';
  }
  function prepareTour() {
    const errors = el('div', { role: 'alert' });
    const editor = form([{ key: 'name', label: 'Nom de la tournée', required: true, maxLength: 120 }, { key: 'date', label: 'Date des visites', type: 'date', required: true }],
      { name: 'Tournée depuis la carte', date: filters.visitDate });
    const technician = select([{ value: '', label: 'À attribuer' }, ...model.choices.technicians.map(person => ({ value: person.key, label: person.name }))], model.choices.personalKey || '');
    const popup = p.dialog('Préparer une tournée', el('div', { class: 'maps-tour-form' },
      errors,
      el('p', { text: `${formatNumber(selected.size)} ${unit(selected.size)} sélectionné${selected.size === 1 ? '' : 's'}. Vérifiez les étapes avant de créer le brouillon.` }), editor.element, field('Technicien prévu', technician)),
    [{ label: 'Annuler', action: close => close() }, { label: 'Voir l’aperçu', primary: true, action: close => p.run(async () => {
      const values = editor.read();
      const preview = await p.call('maps.workspace.tour.preview', tokenArgs({ ...values, technicianKey: technician.value }));
      close(); reviewTour(preview);
    }, errors) }]);
    popup.element.classList.add('maps-tour-dialog');
  }
  function reviewTour(preview) {
    const replace = input('', { type: 'checkbox' });
    const errors = el('div', { role: 'alert' });
    const popup = p.dialog('Vérifier le brouillon de tournée', el('div', { class: 'maps-tour-preview' },
      errors,
      toolbar(badge(preview.date), badge(preview.technician), badge(`${formatNumber(preview.tickets)} ticket${preview.tickets === 1 ? '' : 's'}`)),
      el('h3', { text: preview.name }),
      preview.unlocated ? el('p', { class: 'notice warning', text: `${formatNumber(preview.unlocated)} tickets resteront dans la tournée sans étape localisée. Corrigez leur lieu avant de finaliser le planning.` }) : null,
      table(preview.stops, [{ key: 'place', label: 'Étape' }, { key: 'count', label: 'Tickets' }, { key: 'date', label: 'Date prévue' },
        { key: 'point', label: 'Adresse / position', render: stop => stop.point?.label || 'À compléter dans le référentiel' }], { maxRows: 2000 }),
      el('p', { class: 'help', text: 'Le brouillon s’ouvre dans le planning pour régler les horaires et l’ordre, puis l’enregistrer. Aucune affectation n’est modifiée dans GLPI.' }),
      preview.dirtyDraft ? el('label', { class: 'maps-replace-draft' }, replace, el('span', { text: `Remplacer le brouillon non enregistré « ${preview.currentDraftName} »` })) : null),
    [{ label: 'Retour à la carte', action: close => close() }, { label: 'Créer le brouillon', primary: true, action: close => p.run(async () => {
      if (preview.dirtyDraft && !replace.checked) throw new Error('Cochez le remplacement du brouillon existant ou revenez à la carte pour le conserver.');
      await p.call('maps.workspace.tour.apply', tokenArgs({ previewToken: preview.token, discardDraft: replace.checked }));
      close(); await openPanel('planning', { openDraft: true });
    }, errors) }]);
    popup.element.classList.add('maps-tour-dialog');
  }
  async function siteOverview() {
    if (!loaded) return openPanel('reference', { referenceKind: 'site' });
    if (!filters.includeEmptySites) { filters.includeEmptySites = true; await load(); }
    const sites = model.locations.filter(location => location.kind === 'site');
    const unassignedSite = model.locations.filter(location => location.kind !== 'site').reduce((count, location) => count + location.count, 0);
    const errors = el('div', { role: 'alert' }), feedback = el('p', { role: 'status', 'aria-live': 'polite', class: 'help' });
    const popup = p.dialog('Sites du périmètre', el('div', { class: 'maps-site-overview' },
      errors, feedback,
      el('p', { class: 'help', text: `Les sites sans ticket sont conservés. À partir de ${model.siteInterventionThreshold} tickets actifs, une intervention est à planifier. Les chiffres suivent les filtres de la carte.` }),
      unassignedSite ? el('p', { class: 'notice warning', text: `${formatNumber(unassignedSite)} ${unit(unassignedSite)} sans site précis. Les lieux à vérifier et les repères de commune restent consultables sur la carte.` }) : null,
      table(sites, [{ key: 'site', label: 'Site', render: location => location.place.label }, { key: 'count', label: 'Stock actif' },
        { key: 'advice', label: 'Préconisation', render: location => location.count === 0 ? 'Aucun ticket' : location.count >= model.siteInterventionThreshold ? 'Intervention à planifier' : 'À suivre' },
        { key: 'position', label: 'Position', render: location => Number.isFinite(location.place.latitude) ? 'Coordonnées du site' : 'À compléter' },
        { key: 'actions', label: '', render: location => toolbar(
          button('Voir sur la carte', () => { popup.close(); map?.selectPlace(location.key); }),
          button('Copier les IDs', () => p.run(async () => { const result = await p.call('maps.workspace.copyIds', tokenArgs({ placeKey: location.key })); feedback.textContent = `${result.copied} identifiant${result.copied === 1 ? '' : 's'} copié${result.copied === 1 ? '' : 's'}${result.missing ? ` · ${result.missing} lignes sans ID` : ''}.`; }, errors), { disabled: location.count === 0 }),
          button('Modifier le site', p.act(async () => { popup.close(); await openPanel('reference', { referenceKind: 'site', referenceId: location.key.slice(5) }); }))) }
      ], { key: 'key', maxRows: 2000, emptyText: 'Aucun site dans le référentiel. Ajouter les lieux utilisés dans les titres des tickets.' })),
      [{ label: 'Gérer le référentiel', action: async close => { close(); await openPanel('reference', { referenceKind: 'site' }); } }]);
    popup.element.classList.add('maps-site-dialog');
    popup.element.dataset.workspacePanel = 'sites';
  }
  async function openPanel(name, params = {}) {
    if (name === 'populations' && !navigationVisible('populations', ctx.state.settings?.interface)) {
      p.message('Activez « Effectifs des sites » dans Réglages → Fonctions facultatives pour ouvrir cet outil.'); return;
    }
    if (name === 'sites') return siteOverview();
    const panels = {
      reference: { title: 'Référentiel des lieux', module: './terrain-reference.js', render: 'renderReference' },
      review: { title: 'Vérifier une localisation', module: './terrain-review.js', render: 'renderReview' },
      populations: { title: 'Effectifs des sites · facultatif', module: './analytics-history.js', render: 'renderPopulations' },
      tours: { title: 'Tournées prévues et calendrier', module: './terrain-tours.js', render: 'renderTours', tab: 'saved' },
      planning: { title: 'Préparer les interventions', module: './terrain-tours.js', render: 'renderTours', tab: 'planning' },
      projection: { title: 'Composer le panier et ordonner les communes', module: './terrain-tours.js', render: 'renderTours', tab: 'basket' },
      services: { title: 'Services de route et de météo', module: './terrain-tours.js', render: 'renderTours', tab: 'services' }
    };
    const definition = panels[name]; if (!definition) return;
    if (activePanel) await activePanel.close();
    const previousDialogs = new Set(document.querySelectorAll('dialog'));
    const controller = new AbortController(), host = el('div', { class: 'terrain-function-panel', dataset: { terrainPanel: name } });
    const panelErrors = el('div', { class: 'terrain-panel-errors', role: 'alert' });
    const modal = p.dialog(definition.title, el('div', {}, panelErrors, host)); modal.element.classList.add('terrain-workspace-panel'); modal.element.dataset.workspacePanel = name;
    let rendererCleanup = null, closing = false, finished = false;
    const close = async () => {
      if (closing || finished) return;
      closing = true;
      try {
        if (rendererCleanup?.beforeClose) await rendererCleanup.beforeClose();
        modal.close();
        if (!ctx.signal.aborted && loaded) await load();
      }
      catch (error) { closing = false; throw error; }
    };
    const cleanup = () => {
      if (finished) return; finished = true; controller.abort(); rendererCleanup?.();
      for (const nested of document.querySelectorAll('dialog')) if (nested !== modal.element && !previousDialogs.has(nested)) nested.dispatchEvent(new Event('cancel', { cancelable: true }));
      if (activePanel?.element === modal.element) activePanel = null;
    };
    activePanel = { element: modal.element, close, forceClose: () => modal.close() };
    modal.element.addEventListener('pilot:closed', cleanup, { once: true });
    const deferredClose = createReadyAction(() => p.runAfterCurrent(close, panelErrors));
    const requestClose = event => {
      event.preventDefault(); event.stopImmediatePropagation();
      // Do not drop this click through p.run's ordinary re-entry protection:
      // the visible dialog may still be importing its module or loading data.
      void deferredClose.request().catch(error => {
        if (!ctx.signal.aborted) showError(panelErrors, error);
      });
    };
    modal.element.addEventListener('click', event => {
      if (event.target.closest('.modal-heading>.button')?.closest('dialog') !== modal.element) return;
      requestClose(event);
    }, true);
    modal.element.addEventListener('cancel', requestClose, true);
    const navigate = async (route, next = {}) => {
      const target = route === 'terrain-map' ? next.panel : { 'terrain-reference': 'reference', 'terrain-review': 'review', 'pilot-sites': 'sites', populations: 'populations', tours: next.tourId || next.openDraft ? 'planning' : 'tours' }[route];
      await close();
      if (target) await openPanel(target, next); else await ctx.navigate(route, next);
    };
    try {
      const module = await import(definition.module);
      if (controller.signal.aborted) return;
      rendererCleanup = await module[definition.render]({ ...ctx, root: host, signal: controller.signal, navigate,
        params: { ...params, terrainPanel: definition.tab || name } });
      if (controller.signal.aborted) rendererCleanup?.();
      host.querySelectorAll(':scope > .analytics-heading, :scope > .terrain-page > .terrain-heading, :scope > .terrain-page > [data-simulation]').forEach(node => { node.hidden = true; });
      host.dataset.ready = 'true';
    } catch (error) { modal.close(); throw error; }
    finally { deferredClose.settled(); }
  }
  p.on('session.changed', () => p.message('Le relevé a changé. Actualisez la carte pour reprendre ses filtres et ses sélections sur les données courantes.'));
  async function refreshReferenceMap() {
    const reference = await p.call('maps.preview', { kind: 'sites' });
    if (ctx.signal.aborted) return;
    viewport = map?.getView() || viewport; map?.dispose(); mapHost.replaceChildren();
    map = mountMap(ctx, reference, mapHost, { view: viewport });
  }
  if (loaded) await load();
  else {
    controls.hidden = true; totals.hidden = true; listPanel.hidden = true; scopeSummary.hidden = true;
    p.body.insertBefore(empty('Aucun relevé chargé', 'Chargez un relevé pour répartir les tickets. Le référentiel et les tournées restent accessibles ici.'), mapHost);
    actions.prepend(button('Importer des tickets', () => ctx.navigate('collect'), { primary: true, icon: 'download' }));
    await refreshReferenceMap();
  }
  if (ctx.params?.panel) await openPanel(ctx.params.panel, ctx.params);
  p.root.dataset.ready = 'true';
  const cleanup = () => {
    saveWorkspaceView(ctx.state,'map',{filters,placeKey,offset,viewport:map?.getView()||viewport});
    selectionMemory.set(selectionKey,{revision:model?.revision,rows:[...selected],places:[...chosenPlaces]});
    generation++; activePanel?.forceClose(); map?.dispose(); p.cleanup();
  };
  cleanup.refresh = p.act(() => loaded ? load() : refreshReferenceMap());
  return cleanup;
}
