import { el, button, card, toolbar, empty, badge, input, select, field, table, form, formatNumber, formatDate } from '../ui.js';
import { page, pager } from './terrain-common.js';
import { mountMap } from './maps.js';
import { navigationVisible } from '../navigation.js';

const statusChoices = [
  { key: '1', label: 'Nouveau' }, { key: '2', label: 'En cours · attribué' },
  { key: '3', label: 'En cours · planifié' }, { key: '4', label: 'En attente' }
];

export async function renderTicketMap(ctx) {
  const p = page(ctx, 'Terrain', 'Explorez les lieux des tickets, leurs affectations et les visites prévues. Préparez les interventions depuis cette carte.');
  p.root.classList.add('terrain-map-page');
  const simulationText = p.root.querySelector('[data-simulation] p');
  if (simulationText) simulationText.textContent = 'Tickets et tournées restent dans la simulation. Les rues OpenStreetMap utilisent Internet ; un fond local reste disponible hors connexion.';
  const loaded = ctx.state.session?.loaded === true;
  const initial = ctx.params || {};
  let model, map = null, filters = {
    focusKeys: Array.isArray(initial.focusKeys) ? [...initial.focusKeys] : initial.focusKey ? [initial.focusKey] : [],
    technicianKeys: Array.isArray(initial.technicianKeys) ? [...initial.technicianKeys] : initial.technicianKey ? [initial.technicianKey] : [],
    mine: initial.mine === true, type: initial.type || 'all', statusCodes: Array.isArray(initial.statusCodes) ? [...initial.statusCodes] : [1, 2, 3, 4],
    ...(initial.visitDate ? { visitDate: initial.visitDate } : {})
  };
  let selected = new Set(), placeKey = '', offset = 0, generation = 0;
  let activePanel = null;
  function panelButton(label, name, options = {}) {
    const control = button(label, p.act(() => openPanel(name)), options);
    control.dataset.openPanel = name; return control;
  }
  const actions = toolbar(
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
  actions.classList.add('maps-workspace-actions');
  const controls = el('div', { class: 'maps-scope-toolbar toolbar' });
  const scopeSummary = el('p', { class: 'maps-scope-summary', role: 'status', 'aria-live': 'polite' });
  const totals = el('div', { class: 'maps-workload-totals' });
  const warnings = el('div', { class: 'maps-workload-warnings' });
  const mapHost = el('div', { class: 'maps-inline-host' });
  const rowsHost = el('div', { class: 'maps-ticket-rows' });
  const locationTitle = el('h2', { text: 'Tickets du périmètre' });
  const search = input('', { type: 'search', placeholder: 'Titre ou identifiant', maxLength: 300, 'aria-label': 'Rechercher dans les tickets du périmètre' });
  const selectionSummary = el('span', { class: 'maps-selection-count', role: 'status', 'aria-live': 'polite' });
  const prepare = button('Préparer une tournée', () => prepareTour(), { icon: 'route', primary: true, disabled: true });
  const allPlaces = button('Tous les lieux', p.act(async () => { placeKey = ''; offset = 0; paintLocation(); await loadRows(); }), { icon: 'map' });
  const selectLocation = button('Sélectionner les tickets de ce lieu', p.act(() => changeSelection({ placeKeys: [placeKey] })), { disabled: true });
  const listPanel = card('',
    el('div', { class: 'maps-ticket-heading' }, locationTitle, allPlaces),
    toolbar(search, button('Rechercher', p.act(async () => { offset = 0; await loadRows(); }), { icon: 'search' }), selectLocation),
    toolbar(selectionSummary, prepare,
      button('Sélectionner tout le périmètre', p.act(() => changeSelection({ all: true }))),
      button('Vider la sélection', p.act(() => changeSelection({ clear: true })))),
    el('p', { class: 'help', text: 'Sélectionnez des tickets sur plusieurs pages ou un lieu entier. Une tournée accepte jusqu’à 2 000 tickets ; aucune sélection n’est tronquée.' }), rowsHost);
  listPanel.classList.add('maps-ticket-panel');
  p.body.append(actions, controls, scopeSummary, totals, warnings, mapHost, listPanel);
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); void p.run(async () => { offset = 0; await loadRows(); }); } });

  function tokenArgs(extra = {}) { return { token: model.token, ...extra }; }
  function unit(count = model.totals.tickets) { return model.grain === 'tickets' ? count === 1 ? 'ticket' : 'tickets' : count === 1 ? 'ligne' : 'lignes'; }
  function paintSelection() {
    selectionSummary.textContent = `${formatNumber(selected.size)} ${unit(selected.size)} sélectionné${selected.size === 1 ? '' : 's'}`;
    prepare.disabled = selected.size === 0;
  }
  function paintLocation() {
    const location = model.locations.find(item => item.key === placeKey);
    locationTitle.textContent = location ? `${location.place.label} · ${formatNumber(location.count)} ${unit(location.count)}` : 'Tickets du périmètre';
    allPlaces.disabled = !placeKey; selectLocation.disabled = !placeKey;
  }
  async function changeSelection(extra) {
    try {
      const result = await p.call('maps.workspace.select', tokenArgs(extra));
      selected = new Set(result.rows); paintSelection(); await loadRows();
    } catch (error) {
      // A rejected oversized selection must also restore the checkbox that
      // initiated it; the server keeps the preceding selection unchanged.
      try { if (!ctx.signal.aborted) await loadRows(); } catch { /* Keep the original failure. */ }
      throw error;
    }
  }
  async function choosePlace(place, visit) {
    if (model.locations.some(location => location.key === place.id)) { placeKey = place.id; offset = 0; paintLocation(); await loadRows(); }
    else if (visit) p.message(`Visite PRÉVUE le ${visit.date} · ${visit.technician}. Aucun ticket de ce lieu dans le périmètre affiché.`);
  }
  async function loadRows() {
    const sequence = ++generation;
    const data = await p.call('maps.workspace.rows', tokenArgs({ placeKey, search: search.value, offset, limit: 40 }));
    if (ctx.signal.aborted || sequence !== generation) return;
    rowsHost.replaceChildren(table(data.items, [
      { key: 'selected', label: 'Choix', width: 58, render: row => {
        const control = input('', { type: 'checkbox', checked: row.selected, 'aria-label': `Sélectionner le ticket ${row.ticket.id || row.ticket.rowIndex + 1}` });
        control.addEventListener('change', () => { void p.run(() => changeSelection({ rows: [row.ticket.rowIndex], selected: control.checked })); }); return control;
      } },
      { key: 'id', label: 'ID', width: 100, render: row => row.ticket.id || 'Non renseigné' },
      { key: 'title', label: 'Ticket', width: 360, render: row => el('div', { class: 'maps-ticket-title' },
        el('strong', { text: row.ticket.title || 'Titre non renseigné' }),
        el('small', { text: `${row.ticket.type || 'Type non renseigné'} · ${row.ticket.status || 'Statut non reconnu'}` })) },
      { key: 'place', label: 'Localisation', width: 240, render: row => el('div', { class: 'maps-ticket-location' },
        el('span', { text: row.ticket.placeLabel }), el('small', { text: row.ticket.locationKind === 'city' ? 'Commune · approximatif'
          : row.ticket.locationKind === 'site' ? row.ticket.located ? 'Site' : 'Site · coordonnées à compléter' : 'À vérifier' }),
        row.ticket.located ? button('Localiser', () => map?.selectPlace(row.ticket.placeKey), { icon: 'map' }) : null) },
      { key: 'assigned', label: 'Affectation observée', width: 230, render: row => row.ticket.assignmentKnown
        ? row.ticket.technicians.length ? row.ticket.technicians.map(person => person.name).join(' · ') : 'Sans technicien attribué'
        : 'Colonne non disponible' },
      { key: 'actions', label: 'Fiche', width: 110, render: row => button('Ouvrir', () => ctx.navigate('tickets', { rowIndex: row.ticket.rowIndex, revision: model.revision }), { icon: 'ticket' }) }
    ], { key: row => row.ticket.rowIndex, maxRows: 40, emptyText: 'Aucun ticket ne correspond à ce lieu ou à cette recherche.' }),
    pager(data, next => p.run(async () => { offset = next; await loadRows(); })));
    rowsHost.dataset.total = data.total; rowsHost.dataset.offset = data.offset;
  }
  function multiChoice(title, choices, current, apply, { allowEmpty = true } = {}) {
    const draft = new Set(current), maximum = 64;
    const errors = el('div', { role: 'alert' });
    const query = input('', { type: 'search', placeholder: 'Rechercher…', maxLength: 200 });
    const counter = el('p', { class: 'maps-picker-count', role: 'status', 'aria-live': 'polite' });
    const fold = value => value.normalize('NFD').replace(/[\u0300-\u036f]/gu, '').toLocaleLowerCase('fr');
    const rows = choices.map(choice => {
      const control = input('', { type: 'checkbox', checked: draft.has(choice.key) });
      const label = el('label', { class: 'maps-picker-option' }, control, el('span', { text: choice.label }));
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
    const teams = [...model.choices.teams.map(team => ({ key: team.key, label: `Équipe · ${team.label}`, search: team.groups.join(' ') })),
      ...model.choices.groups.map(group => ({ key: `group:${group}`, label: `Groupe · ${group}` }))];
    const technicians = [...model.choices.technicians.map(person => ({ key: person.key, label: person.name })), { key: '@unassigned', label: 'Sans technicien attribué' }];
    const type = select([{ value: 'all', label: 'Tous les types' }, { value: 'incident', label: 'Incidents' }, { value: 'request', label: 'Demandes' }, { value: 'unknown', label: 'Type non reconnu' }], filters.type);
    type.setAttribute('aria-label', 'Type de ticket'); type.disabled = !model.choices.typeAvailable;
    type.addEventListener('change', () => { filters.type = type.value; void p.run(() => load()); });
    const date = input(filters.visitDate, { type: 'date', min: '2000-01-01', max: '2100-12-31' });
    date.setAttribute('aria-label', 'Jour des visites prévues');
    date.addEventListener('change', () => { if (date.value) { filters.visitDate = date.value; void p.run(() => load()); } });
    controls.replaceChildren(
      button('Tous les techniciens', p.act(async () => { filters.mine = false; filters.technicianKeys = []; await load(); }), { primary: !filters.mine && !filters.technicianKeys.length, icon: 'users' }),
      button('Mes tickets', p.act(async () => { if (!model.choices.personalKey) { await ctx.navigate('technicians'); return; } filters.mine = true; filters.technicianKeys = []; await load(); }),
        { primary: filters.mine, icon: 'ticket', title: model.choices.personalName ? `Tickets attribués à ${model.choices.personalName}` : 'Choisir d’abord mon identité dans les techniciens' }),
      button(`Équipes et groupes${filters.focusKeys.length ? ` (${filters.focusKeys.length})` : ''}`, () => multiChoice('Équipes et groupes', teams, filters.focusKeys, values => { filters.focusKeys = values; }), { icon: 'layers' }),
      button(`Techniciens${filters.technicianKeys.length ? ` (${filters.technicianKeys.length})` : ''}`, () => multiChoice('Techniciens attribués', technicians, filters.technicianKeys,
        values => { filters.technicianKeys = values; filters.mine = false; }), { icon: 'users', disabled: !model.choices.technicianAvailable }),
      type, button(`Statuts actifs (${filters.statusCodes.length}/4)`, () => multiChoice('Statuts actifs', statusChoices, filters.statusCodes.map(String),
        values => { filters.statusCodes = values.map(Number); }, { allowEmpty: false })),
      el('label', { class: 'maps-visit-date' }, el('span', { text: 'Visites prévues' }), date),
      button('Actualiser', p.act(() => load()), { icon: 'refresh' }));
    const groupLabels = teams.filter(choice => filters.focusKeys.includes(choice.key)).map(choice => choice.label);
    const personLabels = filters.mine ? [model.choices.personalName] : technicians.filter(choice => filters.technicianKeys.includes(choice.key)).map(choice => choice.label);
    scopeSummary.textContent = [groupLabels.length ? groupLabels.join(' · ') : 'Toutes les équipes et tous les groupes',
      personLabels.length ? personLabels.join(' · ') : 'Tous les techniciens', `${formatNumber(model.totals.tickets)} ${unit()} · relevé du ${formatDate(model.sourceDate)}`].join(' — ');
  }
  function paintTotals() {
    const facts = [
      ['Stock du périmètre', model.totals.tickets, unit(), 'ticket'], ['Sur la carte', model.totals.placed, `${unit(model.totals.placed)} avec coordonnées`, 'map'],
      ['Localisation approximative', model.totals.approximate, `${unit(model.totals.approximate)} · repère de commune`, 'map'],
      ['Sans coordonnées', model.totals.withoutCoordinates, `${unit(model.totals.withoutCoordinates)} conservé${model.totals.withoutCoordinates === 1 ? '' : 's'} dans la liste`, 'search']
    ];
    totals.replaceChildren(...facts.map(([label, value, detail, symbol]) => el('div', { class: `maps-workload-total tone-${symbol}` },
      el('span', { text: label }), el('strong', { text: formatNumber(value) }), el('small', { text: detail }))));
    warnings.replaceChildren();
    if (!model.activeAvailable) warnings.append(el('p', { class: 'notice warning', text: 'Le stock actif ne peut pas être déterminé. Vérifiez la colonne Statut et ses correspondances.' }));
    if (model.sourceUnknownStatus) warnings.append(el('p', { class: 'help', text: `${formatNumber(model.sourceUnknownStatus)} lignes au statut non reconnu dans le relevé complet sont exclues du stock actif. Les tickets résolus et clos sont également exclus.` }));
    if (model.totals.review) warnings.append(el('p', { class: 'help', text: `${formatNumber(model.totals.review)} ${unit(model.totals.review)} avec une localisation à vérifier. La liste conserve toutes les lignes de ce périmètre.` }));
    if (model.visitsWarning) warnings.append(el('p', { class: 'notice warning', text: model.visitsWarning }));
  }
  async function load() {
    const previousSelection = selected.size;
    const next = await p.call('maps.workspace.get', filters);
    if (ctx.signal.aborted) return;
    map?.dispose(); mapHost.replaceChildren(); generation++;
    model = next; filters = { ...model.filters }; selected = new Set(); placeKey = ''; offset = 0;
    paintControls(); paintTotals(); paintSelection(); paintLocation();
    map = mountMap(ctx, model, mapHost, { onSelect: choosePlace });
    await loadRows();
    if (previousSelection) p.message('Périmètre actualisé. La sélection de la carte précédente a été remise à zéro.');
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
      ], { key: 'key', maxRows: 2000, emptyText: 'Aucun site dans le référentiel. Ajoutez les lieux utilisés dans les titres de vos tickets.' })),
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
    modal.element.addEventListener('click', event => {
      if (event.target.closest('.modal-heading>.button')?.closest('dialog') !== modal.element) return;
      event.preventDefault(); event.stopImmediatePropagation(); void p.run(close, panelErrors);
    }, true);
    modal.element.addEventListener('cancel', event => { event.preventDefault(); event.stopImmediatePropagation(); void p.run(close, panelErrors); }, true);
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
  }
  p.on('session.changed', () => p.message('Le relevé a changé. Actualisez la carte pour reprendre ses filtres et ses sélections sur les données courantes.'));
  if (loaded) await load();
  else {
    controls.hidden = true; totals.hidden = true; listPanel.hidden = true; scopeSummary.hidden = true;
    p.body.insertBefore(empty('Votre terrain est prêt', 'Chargez un relevé pour répartir les tickets. Le référentiel et les tournées restent accessibles ici.'), mapHost);
    actions.prepend(button('Importer des tickets', () => ctx.navigate('collect'), { primary: true, icon: 'download' }));
    const reference = await p.call('maps.preview', { kind: 'sites' }); if (!ctx.signal.aborted) map = mountMap(ctx, reference, mapHost);
  }
  if (ctx.params?.panel) await openPanel(ctx.params.panel, ctx.params);
  p.root.dataset.ready = 'true';
  return () => { generation++; activePanel?.forceClose(); map?.dispose(); p.cleanup(); };
}
