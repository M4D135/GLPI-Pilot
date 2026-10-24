import { el, button, card, toolbar, empty, badge, input, select, field, table, form, confirm, formatNumber, formatDate } from '../ui.js';
import { page, pager, lookup, referenceField, details, facts, listText, readList, numberOrNull, minuteText, timeText, dayText, stateLabels, text } from './terrain-common.js';
import { publishSharedOperation } from './shared-operations.js';
import { openGoogleMaps, openGoogleMapsSettings } from './google-maps.js';

export async function renderTours(ctx) {
  const p = page(ctx, 'Tournées et interventions', 'Préparez un panier, ordonnez les étapes et vérifiez les horaires, les compétences et les capacités déclarées avant le départ.');
  let state = await p.call('terrain.tours.state'), tab = ctx.params?.tourId ? 'planning' : 'basket';
  let headerForm = null, headerInitial = '', pointForms = null, headerTechnician = null;
  let projectionOffset = 0, basketOffset = 0, stopsOffset = 0, scheduleOffset = 0, manualOrder = false;
  let selectedCity = '', visibleBasket = [];
  const tabs = el('div', { class: 'terrain-tabs', role: 'tablist', 'aria-label': 'Espaces de tournée' });
  const summary = el('div'), content = el('div'), registry = el('div'), projection = el('div'), basket = el('div'), editor = el('div'), schedule = el('div'), provider = el('div');
  const sections = { basket: el('div'), planning: el('div'), saved: el('div'), services: el('div') };
  const search = input('', { placeholder: 'Titre ou identifiant', maxLength: 120 }), allStatuses = input('', { type: 'checkbox' });
  const selectedOnly = input('', { type: 'checkbox' });
  const scope = referenceField(p, 'Technicien', 'technician', '', '', { saved: true, empty: 'Toute l’équipe', onChange: () => { manualOrder = false; } });
  const order = select([{ value: 'saved', label: 'Ordre mémorisé' }, { value: 'alphabetical', label: 'Alphabétique' }, { value: 'priority', label: 'Priorités actives' }, { value: 'due', label: 'Échéances actives' }, { value: 'proximity', label: 'Proximité à vol d’oiseau' }], 'saved');
  const firstCity = referenceField(p, 'Première ville pour la proximité', 'city', '', '', { saved: true });
  const newTour = form([{ key: 'name', label: 'Nom de la tournée', required: true }, { key: 'date', label: 'Premier jour', type: 'date', required: true }], { name: 'Nouvelle tournée', date: new Date().toLocaleDateString('en-CA') });
  p.body.append(summary, tabs, content);

  function args(extra = {}) { return { revision: state.revision, draftVersion: state.draftVersion, ...extra }; }
  function filterArgs() { return { revision: state.revision, technician: scope.read(), allStatuses: allStatuses.checked }; }
  function paintSummary() {
    summary.replaceChildren(el('div', { class: 'terrain-statbar' }, badge(`${state.selected} tickets dans le panier`),
      state.draft ? badge(`${state.draft.name} · ${stateLabels[state.draft.state]}`, state.draft.state === 'ready' ? 'ok' : '') : badge('Aucune tournée ouverte'),
      state.dirty ? badge('Brouillon à enregistrer', 'warn') : ''));
  }
  function paintTabs() {
    tabs.replaceChildren(...[['basket', 'Panier et villes'], ['planning', 'Planning daté'], ['saved', 'Tournées et calendrier'], ['services', 'Routes et météo']].map(([id, label]) =>
      button(label, p.act(async () => { if (headerChanged()) await applyHeader(); tab = id; paintTabs(); await loadTab(); }), { primary: tab === id })));
    content.replaceChildren(sections[tab]);
  }
  async function refreshState() { state = await p.call('terrain.tours.state'); paintSummary(); }
  async function loadTab() {
    if (tab === 'basket') { if (state.loaded) { await loadProjection(); await loadBasket(); } }
    else if (tab === 'planning') await paintEditor();
    else if (tab === 'saved') await loadRegistry();
    else await loadProviders();
  }
  async function refreshAll() { await refreshState(); paintTabs(); await loadTab(); }
  async function abandonForSwitch() {
    if (!state.dirty && !headerChanged()) return true;
    return confirm('Changer de tournée', 'Le brouillon de la tournée en cours n’est pas enregistré. Voulez-vous abandonner ses modifications pour ouvrir une autre tournée ?', 'Abandonner le brouillon');
  }
  async function openTour(id) {
    if (!await abandonForSwitch()) return;
    state = await p.call('terrain.tours.open', args({ id, discardDraft: true })); headerForm = null; pointForms = null; tab = 'planning'; stopsOffset = 0; scheduleOffset = 0;
    paintSummary(); paintTabs(); await paintEditor();
  }
  if (state.loaded) {
    sections.basket.append(card('Périmètre du relevé', toolbar(scope.element, field('Inclure tous les statuts', allStatuses),
      button('Appliquer ce périmètre', p.act(async () => { manualOrder = false; projectionOffset = 0; basketOffset = 0; selectedCity = ''; await loadProjection(); await loadBasket(); })))),
    card('Regroupement par ville', toolbar(field('Ordonner', order), button('Calculer cet ordre', p.act(async () => { manualOrder = false; await loadProjection(); }))), firstCity.element, projection,
      toolbar(button('Mémoriser cet ordre', p.act(async () => { const result = await p.call('terrain.tours.projectionSave', { revision: state.revision }); p.message(`Ordre conservé pour ${result.cities} villes et ce périmètre.`); })),
        button('Exporter les villes et tickets…', p.act(async () => { const result = await p.call('terrain.tours.projectionExport', { revision: state.revision }); if (!result.cancelled) p.message(`Regroupement enregistré : ${result.path}`); })),
        button('Google Maps · ordre des communes', p.act(() => openGoogleMaps(ctx, { kind: 'projection', revision: state.revision }))))),
    card('Panier de tickets', toolbar(field('Rechercher', search), field('Afficher uniquement le panier', selectedOnly),
      button('Filtrer', p.act(async () => { basketOffset = 0; await loadBasket(); })), button('Toutes les villes', p.act(async () => { selectedCity = ''; basketOffset = 0; await loadBasket(); }))),
      toolbar(button('Ajouter la page au panier', p.act(async () => { const result = await p.call('terrain.tours.select', { revision: state.revision, rows: visibleBasket.map(row => row.row), selected: true }); state.selected = result.selected; paintSummary(); await loadBasket(); })),
        button('Vider le panier', p.act(async () => { await p.call('terrain.tours.select', { revision: state.revision, clear: true }); await refreshState(); await loadBasket(); }))), basket,
      newTour.element, button('Créer une tournée avec ce panier', p.act(async () => {
        if (!await abandonForSwitch()) return;
        state = await p.call('terrain.tours.create', { ...filterArgs(), ...newTour.read(), discardDraft: true }); headerForm = null; pointForms = null; tab = 'planning'; stopsOffset = 0; scheduleOffset = 0;
        paintSummary(); paintTabs(); await paintEditor();
      }), { primary: true })));
  } else sections.basket.append(empty('Chargez un relevé pour préparer un panier', 'Vous pouvez consulter vos tournées enregistrées dans l’onglet calendrier.'));
  sections.planning.append(editor, schedule);
  sections.saved.append(registry); sections.services.append(provider);

  async function loadProjection() {
    const data = await p.call('terrain.tours.projection', { ...filterArgs(), order: manualOrder ? 'current' : order.value, firstCity: firstCity.read(), offset: projectionOffset, limit: 30 });
    projection.replaceChildren(el('p', { text: `${data.scopeLabel} · ${data.statusLabel} · ${data.orderLabel}. ${data.tickets} tickets, ${data.unlocated} sans commune repérée, ${data.ambiguous} ambiguïtés.` }),
      el('p', { class: 'terrain-muted', text: data.message }),
      data.straightLineDistanceKm != null ? el('p', { text: `${formatNumber(data.straightLineDistanceKm, { maximumFractionDigits: 1 })} km à vol d’oiseau. Cet ordre reste indicatif ; il ne tient pas lieu d’itinéraire routier.` }) : '',
      table(data.items, [{ key: 'number', label: '#' }, { key: 'cityName', label: 'Ville' }, { key: 'count', label: 'Tickets' }, { key: 'activeCount', label: 'Actifs', render: row => text(row.activeCount) },
        { key: 'highPriorityCount', label: 'Prioritaires', render: row => text(row.highPriorityCount) }, { key: 'overdueCount', label: 'En retard', render: row => text(row.overdueCount) },
        { key: 'firstActiveDue', label: 'Première échéance', render: row => row.firstActiveDue ? formatDate(row.firstActiveDue) : '—' },
        { key: 'actions', label: 'Actions', render: row => toolbar(button('Tickets', p.act(async () => { selectedCity = row.cityId; basketOffset = 0; await loadBasket(); basket.scrollIntoView({ block: 'start' }); })),
          button('↑', p.act(async () => { await p.call('terrain.tours.projectionMove', { revision: state.revision, cityId: row.cityId, delta: -1 }); manualOrder = true; await loadProjection(); }), { title: 'Monter la ville', disabled: row.number === 1 }),
          button('↓', p.act(async () => { await p.call('terrain.tours.projectionMove', { revision: state.revision, cityId: row.cityId, delta: 1 }); manualOrder = true; await loadProjection(); }), { title: 'Descendre la ville', disabled: row.number === data.total })) }
      ], { key: 'cityId', maxRows: 30, emptyText: 'Aucune ville reconnue dans ce périmètre.' }), pager(data, next => p.run(async () => { projectionOffset = next; await loadProjection(); })));
  }
  async function loadBasket() {
    const data = await p.call('terrain.tours.basket', { ...filterArgs(), cityId: selectedCity, search: search.value, selectedOnly: selectedOnly.checked, offset: basketOffset, limit: 50 });
    visibleBasket = data.items; state.selected = data.selected; paintSummary();
    basket.replaceChildren(table(data.items, [
      { key: 'selected', label: 'Panier', render: row => {
        const check = input('', { type: 'checkbox', 'aria-label': `Sélectionner le ticket ${row.id || row.row + 1}` }); check.checked = row.selected;
        check.addEventListener('change', () => p.run(async () => { const result = await p.call('terrain.tours.select', { revision: state.revision, rows: [row.row], selected: check.checked }); state.selected = result.selected; paintSummary(); await loadBasket(); })); return check;
      } }, { key: 'id', label: 'ID' }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'Statut' }, { key: 'priority', label: 'Priorité' }, { key: 'city', label: 'Ville' }, { key: 'site', label: 'Site' }
    ], { key: 'row', maxRows: 50, emptyText: 'Aucun ticket correspondant à ce filtre.' }), pager(data, next => p.run(async () => { basketOffset = next; await loadBasket(); })));
  }

  function readHeader() {
    if (!headerForm || !pointForms) return null;
    const values = headerForm.read(); const points = pointForms.map(value => value.read());
    return { ...values, returnMinutes: Number(values.returnMinutes), technicianKey: headerTechnician.read(),
      start: { label: points[0].label, latitude: numberOrNull(points[0].latitude), longitude: numberOrNull(points[0].longitude) },
      end: { label: points[1].label, latitude: numberOrNull(points[1].latitude), longitude: numberOrNull(points[1].longitude) } };
  }
  function headerChanged() { if (!headerForm) return false; try { return JSON.stringify(readHeader()) !== headerInitial; } catch { return true; } }
  async function applyHeader() {
    if (!headerForm || !headerChanged()) return;
    state = await p.call('terrain.tours.edit', args({ fields: readHeader() })); headerInitial = JSON.stringify(readHeader()); paintSummary();
  }
  async function paintEditor() {
    editor.replaceChildren(); schedule.replaceChildren();
    if (!state.draft) { headerForm = null; pointForms = null; editor.append(empty('Aucune tournée ouverte', 'Créez un panier ou ouvrez une tournée enregistrée.'), button('Voir les tournées', p.act(async () => { tab = 'saved'; paintTabs(); await loadRegistry(); }))); return; }
    const tour = state.draft;
    headerForm = form([{ key: 'name', label: 'Nom', required: true }, { key: 'date', label: 'Premier jour', type: 'date', required: true },
      { key: 'startTime', label: 'Début de journée', type: 'time', required: true }, { key: 'endTime', label: 'Fin de journée', type: 'time', required: true },
      { key: 'timeZoneId', label: 'Fuseau horaire', help: 'Identifiant reconnu sur ce poste ; utilisé pour l’export ICS.' },
      { key: 'returnMinutes', label: 'Retour estimé en minutes', type: 'number', min: 0, max: 1440 }, { key: 'notes', label: 'Consignes de la tournée', type: 'textarea' }], tour);
    headerTechnician = referenceField(p, 'Technicien par défaut', 'technician', tour.technicianKey, '', { saved: true, empty: 'À attribuer' });
    const pointCards = [tour.start, tour.end].map((point, index) => {
      let values = form([{ key: 'label', label: 'Libellé' }, { key: 'latitude', label: 'Latitude' }, { key: 'longitude', label: 'Longitude' }], { ...point, latitude: point.latitude ?? '', longitude: point.longitude ?? '' });
      const slot = el('div', {}, values.element);
      const root = card(index === 0 ? 'Départ' : 'Retour', slot, button('Choisir dans le référentiel…', p.act(() => lookup(p, 'place', choice => p.run(async () => {
        const selected = await p.call('terrain.tours.point', { revision: state.revision, place: choice.value });
        values = form([{ key: 'label', label: 'Libellé' }, { key: 'latitude', label: 'Latitude' }, { key: 'longitude', label: 'Longitude' }], { ...selected, latitude: selected.latitude ?? '', longitude: selected.longitude ?? '' });
        slot.replaceChildren(values.element); pointForms[index] = values;
      }), { saved: true }))));
      return { root, values };
    }); pointForms = pointCards.map(value => value.values); headerInitial = JSON.stringify(readHeader());
    const actionBar = toolbar(
      button('Calculer le planning', p.act(async () => { await applyHeader(); await loadStops(); await loadSchedule(); }), { primary: true, disabled: !state.loaded }),
      button('Enregistrer la tournée', p.act(async () => { await applyHeader(); state = await p.call('terrain.tours.save', args()); paintSummary(); await paintEditor(); p.message('Tournée enregistrée localement.'); })),
      button('Enregistrer et partager la tournée', p.act(async () => {
        await applyHeader(); state = await p.call('terrain.tours.save', args()); paintSummary(); await paintEditor();
        await publishSharedOperation(ctx, 'workspace.publishTour', { id: state.draft.id });
      }), { icon: 'users' }),
      button('Voir le travail partagé', () => ctx.navigate('library', { view: 'team' })),
      button('Dupliquer', p.act(duplicate)), button('Changer d’état', p.act(lifecycle), { disabled: !state.loaded }),
      button('Google Maps · tournée', p.act(async () => { await applyHeader(); await openGoogleMaps(ctx, args({ kind: 'tour' })); })),
      button('Exporter le briefing HTML…', p.act(() => exportTour('html')), { disabled: !state.loaded }), button('Exporter le calendrier ICS…', p.act(() => exportTour('ics')), { disabled: !state.loaded })
    );
    editor.append(card(tour.name, facts([['État', tour.stateLabel], ['Tickets', tour.tickets], ['Étapes', tour.stops], ['Relevé de préparation', formatDate(tour.sourceExtractedAt)]]), actionBar,
      details('Nom, horaires, départ, retour et consignes', headerForm.element, headerTechnician.element, el('div', { class: 'terrain-columns' }, ...pointCards.map(value => value.root))),
      details('Historique des états', table(tour.stateHistory, [{ key: 'at', label: 'Date', render: row => formatDate(row.at) }, { key: 'from', label: 'De', render: row => stateLabels[row.from] }, { key: 'to', label: 'Vers', render: row => stateLabels[row.to] }, { key: 'reason', label: 'Motif' }], { maxRows: 100 }))));
    if (!state.loaded) { editor.append(empty('Chargez un relevé pour rapprocher cette tournée', 'Les références locales sont conservées. Le calcul et les exports nécessitent les tickets du relevé.')); return; }
    const stops = el('div'); editor.append(card('Étapes', toolbar(
      button('Actualiser les étapes depuis le relevé', p.act(rebuild)),
      button('Proposer un ordre de proximité', p.act(async () => { await applyHeader(); state = await p.call('terrain.tours.proximity', args()); paintSummary(); await paintEditor(); p.message('Ordre indicatif à vol d’oiseau recalculé ; les étapes verrouillées restent à leur place.'); })),
      button('Répartir sur plusieurs jours', p.act(allocate)), button('Comparer deux tournées', p.act(compare)),
      button('Rapprocher les tickets', p.act(reconciliation))), stops));
    editor._stops = stops;
    await loadStops(); await loadSchedule();
  }
  async function loadStops() {
    if (!state.draft || !editor._stops) return;
    const data = await p.call('terrain.tours.stops', args({ offset: stopsOffset, limit: 40 }));
    editor._stops.replaceChildren(table(data.items, [
      { key: 'number', label: '#' }, { key: 'place', label: 'Lieu' }, { key: 'date', label: 'Date', render: row => row.stop.date || state.draft.date },
      { key: 'technician', label: 'Technicien' }, { key: 'tickets', label: 'Tickets', render: row => String(row.stop.ticketIds.length) },
      { key: 'start', label: 'Début', render: row => timeText(row.visit?.start) }, { key: 'finish', label: 'Fin', render: row => timeText(row.visit?.finish) },
      { key: 'warning', label: 'Contrôles', render: row => row.visit?.warning || '—' },
      { key: 'actions', label: '', render: row => toolbar(button(row.stop.locked ? 'Verrouillée · modifier' : 'Modifier', p.act(() => editStop(row))),
        button('↑', p.act(() => moveStop(row, -1)), { title: 'Monter l’étape', disabled: row.stop.locked || row.number === 1 }),
        button('↓', p.act(() => moveStop(row, 1)), { title: 'Descendre l’étape', disabled: row.stop.locked || row.number === data.total })) }
    ], { key: 'number', maxRows: 40, emptyText: 'Aucune étape localisée. Corrigez les lieux, puis actualisez les étapes.' }), pager(data, next => p.run(async () => { stopsOffset = next; await applyHeader(); await loadStops(); })));
  }
  async function moveStop(row, delta) { await applyHeader(); state = await p.call('terrain.tours.move', args({ stopId: row.stop.id, delta })); paintSummary(); await paintEditor(); }
  async function editStop(row) {
    await applyHeader(); const stop = row.stop; const errors = el('div', { role: 'alert' });
    const technician = referenceField(p, 'Technicien de cette étape', 'technician', stop.technicianKey, row.technician, { saved: true, empty: 'Affectation par défaut de la tournée' });
    const editor = form([{ key: 'date', label: 'Date de cette étape', type: 'date', help: 'Vide : premier jour de la tournée.' },
      { key: 'durationMinutes', label: 'Intervention en minutes', type: 'number', min: 1, max: 1440 }, { key: 'travelMinutes', label: 'Trajet estimé avant cette étape', type: 'number', min: 0, max: 1440 },
      { key: 'earliest', label: 'Heure la plus tôt', type: 'time' }, { key: 'latest', label: 'Fin d’intervention au plus tard', type: 'time' },
      { key: 'locked', label: 'Verrouiller l’étape contre les réorganisations', type: 'checkbox' }, { key: 'requiredSkills', label: 'Compétences requises', type: 'textarea', help: 'Une compétence par ligne ou séparée par ;.' },
      { key: 'contact', label: 'Contacts sur place', type: 'textarea' }, { key: 'equipment', label: 'Matériel à prévoir', type: 'textarea' }, { key: 'notes', label: 'Consignes de l’étape', type: 'textarea' }
    ], { ...stop, requiredSkills: listText(stop.requiredSkills) });
    const content = el('div', { class: 'terrain-editor' }, errors, el('p', { text: `${stop.ticketIds.length} tickets : ${stop.ticketIds.join(', ')}` }), technician.element, editor.element);
    if (row.weather) content.append(details('Prévision reçue pour cette visite', facts([['Date', row.weather.day], ['Code WMO', row.weather.weatherCode], ['Minimum °C', row.weather.minimumCelsius], ['Maximum °C', row.weather.maximumCelsius], ['Pluie %', row.weather.rainProbabilityPercent], ['Fournisseur', row.weather.provider], ['Reçu le', formatDate(row.weather.retrievedAt)]])));
    p.dialog(`Étape · ${row.place}`, content, [{ label: 'Annuler', action: close => close() }, { label: 'Appliquer à la tournée', primary: true, action: close => p.run(async () => {
      const fields = editor.read(); fields.requiredSkills = readList(fields.requiredSkills); fields.technicianKey = technician.read(); fields.durationMinutes = Number(fields.durationMinutes); fields.travelMinutes = Number(fields.travelMinutes);
      state = await p.call('terrain.tours.stopEdit', args({ stopId: stop.id, fields })); close(); paintSummary(); await paintEditor();
    }, errors) }]);
  }
  async function loadSchedule() {
    if (!state.draft || !state.loaded) return;
    const data = await p.call('terrain.tours.calculate', args({ offset: scheduleOffset, limit: 50 }));
    schedule.replaceChildren(card('Planning calculé',
      el('div', { class: 'terrain-statbar' }, badge(`${data.totals.interventions} min d’intervention`), badge(`${data.totals.travel} min de trajet`), badge(`${data.totals.waiting} min d’attente`), badge(`${data.totals.unavailable} présences incompatibles`, data.totals.unavailable ? 'warn' : 'ok')),
      data.warnings.length ? details(`Points à vérifier · ${data.warningCount}`, el('ul', {}, ...data.warnings.map(value => el('li', { text: value })))) : el('p', { text: 'Aucun avertissement détecté par le calcul.' }),
      table(data.items, [{ key: 'number', label: '#' }, { key: 'place', label: 'Lieu' }, { key: 'start', label: 'Date', render: row => dayText(row.start) }, { key: 'technician', label: 'Technicien' },
        { key: 'arrival', label: 'Arrivée', render: row => timeText(row.arrival) }, { key: 'start', label: 'Début', render: row => timeText(row.start) }, { key: 'finish', label: 'Fin', render: row => timeText(row.finish) },
        { key: 'waitMinutes', label: 'Attente · min' }, { key: 'travelMinutesUsed', label: 'Trajet · min' }, { key: 'roadKm', label: 'Route · km', render: row => row.roadKm == null ? 'Non calculée' : formatNumber(row.roadKm, { maximumFractionDigits: 1 }) }, { key: 'warning', label: 'Contrôles' }
      ], { maxRows: 50 }), pager(data, next => p.run(async () => { scheduleOffset = next; await applyHeader(); await loadSchedule(); })),
      details('Charges par journée et technicien', table(data.technicians, [{ key: 'date', label: 'Jour' }, { key: 'technician', label: 'Technicien' }, { key: 'stops', label: 'Étapes' }, { key: 'tickets', label: 'Tickets' },
        { key: 'interventionMinutes', label: 'Intervention · min' }, { key: 'travelMinutes', label: 'Trajet · min' }, { key: 'waitMinutes', label: 'Attente · min' }, { key: 'finish', label: 'Fin', render: row => timeText(row.finish) }, { key: 'outsideDay', label: 'Hors journée', render: row => row.outsideDay ? 'Oui' : 'Non' }], { maxRows: 448 })),
      el('p', { class: 'terrain-muted', text: data.canExportCalendar ? 'Le rapprochement permet un export ICS. Vérifiez les avertissements avant diffusion.' : 'L’export ICS est bloqué par le rapprochement ou une présence incompatible. Corrigez les points signalés.' })));
  }
  async function rebuild() {
    await applyHeader(); const editor = form([{ key: 'removeCompleted', label: 'Retirer les tickets résolus ou clos', type: 'checkbox' }], { removeCompleted: false }); const errors = el('div', { role: 'alert' });
    p.dialog('Actualiser les étapes', el('div', {}, el('p', { text: 'Les tickets seront rapprochés du relevé actuel. Une étape verrouillée ne sera ni déplacée ni vidée ; une incompatibilité doit être résolue avant de poursuivre.' }), errors, editor.element),
      [{ label: 'Annuler', action: close => close() }, { label: 'Actualiser le brouillon', primary: true, action: close => p.run(async () => { state = await p.call('terrain.tours.rebuild', args(editor.read())); close(); paintSummary(); await paintEditor(); }, errors) }]);
  }
  async function reconciliation() {
    await applyHeader(); const body = el('div'), errors = el('div', { role: 'alert' }); const changed = input('', { type: 'checkbox' });
    const popup = p.dialog('Rapprochement des tickets', el('div', {}, field('Afficher seulement les changements', changed), errors, body));
    async function load(offset = 0) {
      const data = await p.call('terrain.tours.reconciliation', args({ offset, limit: 50, changedOnly: changed.checked }));
      body.replaceChildren(el('p', { text: `${data.missing} références sans rapprochement, ${data.changed} tickets modifiés, ${data.completed} tickets terminés.` }),
        table(data.items, [{ key: 'ticketId', label: 'ID' }, { key: 'state', label: 'État' }, { key: 'detail', label: 'Contrôle' }], { maxRows: 50 }), pager(data, next => p.run(() => load(next), errors)));
    }
    changed.addEventListener('change', () => p.run(() => load(0), errors)); await load();
  }
  async function lifecycle() {
    await applyHeader(); const errors = el('div', { role: 'alert' });
    const editor = form([{ key: 'state', label: 'Nouvel état', type: 'select', options: Object.entries(stateLabels).filter(([key]) => key !== state.draft.state).map(([value, label]) => ({ value, label })) },
      { key: 'reason', label: 'Motif', type: 'textarea', required: true }], { state: state.draft.state === 'draft' ? 'ready' : 'draft', reason: '' });
    p.dialog('État de la tournée', el('div', {}, errors, el('p', { text: 'Une tournée prête doit être rapprochée, localisée, affectée et compatible avec les horaires déclarés. Une visite future ne peut pas être déclarée effectuée. Le changement d’état est enregistré avec son motif.' }), editor.element),
      [{ label: 'Annuler', action: close => close() }, { label: 'Valider et enregistrer', primary: true, action: close => p.run(async () => { state = await p.call('terrain.tours.transition', args(editor.read())); close(); paintSummary(); await paintEditor(); }, errors) }]);
  }
  async function duplicate() {
    await applyHeader(); const editor = form([{ key: 'name', label: 'Nom de la copie', required: true }], { name: `${state.draft.name} · copie`.slice(0, 120) }); const errors = el('div', { role: 'alert' });
    p.dialog('Dupliquer la tournée', el('div', {}, errors, editor.element), [{ label: 'Annuler', action: close => close() }, { label: 'Créer la copie', primary: true, action: close => p.run(async () => {
      state = await p.call('terrain.tours.duplicate', args(editor.read())); close(); paintSummary(); await paintEditor(); p.message('Copie créée en brouillon. Enregistrez-la pour la conserver.');
    }, errors) }]);
  }
  async function exportTour(format) { await applyHeader(); const result = await p.call('terrain.tours.export', args({ format })); if (!result.cancelled) p.message(`${result.visits} visites exportées : ${result.path}`); }

  async function allocate() {
    await applyHeader(); const errors = el('div', { role: 'alert' }), selected = [], chips = el('div', { class: 'terrain-chips' }), capacity = el('div'), result = el('div');
    const settings = form([{ key: 'days', label: 'Nombre de jours à partir du premier jour', type: 'number', min: 1, max: 14 }], { days: 5 });
    if (state.draft.technicianKey) selected.push({ value: state.draft.technicianKey, label: state.draft.technicianKey });
    const paint = () => chips.replaceChildren(...selected.map(value => toolbar(badge(value.label), button('Retirer', () => { selected.splice(selected.indexOf(value), 1); paint(); })))); paint();
    const request = () => args({ technicians: selected.map(value => value.value), days: Number(settings.read().days) });
    const content = el('div', {}, errors, el('p', { text: 'La proposition respecte les compétences, absences, fenêtres de présence, capacités du matin et de l’après-midi, horaires de site et étapes verrouillées. Les capacités inconnues sont exclues. Il s’agit d’une heuristique, sans garantie d’optimum.' }),
      settings.element, chips, toolbar(button('Ajouter un technicien…', () => p.run(() => lookup(p, 'technician', value => { if (!selected.some(item => item.value === value.value) && selected.length < 32) { selected.push(value); paint(); } }, { saved: true }), errors)),
        button('Consulter les capacités', () => p.run(() => loadCapacity(0), errors)), button('Ouvrir les calendriers techniciens', () => { popup.close(); ctx.navigate('technicians'); })), capacity, result);
    const popup = p.dialog('Répartition sur plusieurs jours', content, [{ label: 'Fermer', action: close => close() }, { label: 'Calculer la proposition dans le brouillon', primary: true, action: close => p.run(async () => {
      const data = await p.call('terrain.tours.allocate', request()); state = data.state; paintSummary(); await paintEditor();
      result.replaceChildren(el('p', { text: `${data.assignedStops} étapes affectées ; ${data.unassigned.length} sans créneau compatible ; ${data.unknownCapacity} capacités journalières inconnues.` }),
        el('p', { text: data.method }), table(data.unassigned, [{ key: 'place', label: 'Étape non affectée' }, { key: 'reason', label: 'Motif' }], { maxRows: 500, emptyText: 'Toutes les étapes ont une affectation compatible.' }));
      p.message('Proposition ajoutée au brouillon. Vérifiez le planning avant l’enregistrement.');
    }, errors) }]);
    async function loadCapacity(offset) {
      const data = await p.call('terrain.tours.capacity', { ...request(), offset, limit: 40 });
      capacity.replaceChildren(el('p', { text: `${data.unknown} capacités journalières non déclarées.` }), table(data.items, [
        { key: 'technicianKey', label: 'Technicien' }, { key: 'day', label: 'Jour' }, { key: 'capacityMinutes', label: 'Journée', render: row => minuteText(row.capacityMinutes) },
        { key: 'morningCapacityMinutes', label: 'Matin', render: row => minuteText(row.morningCapacityMinutes) }, { key: 'afternoonCapacityMinutes', label: 'Après-midi', render: row => minuteText(row.afternoonCapacityMinutes) },
        { key: 'presence', label: 'Présence', render: row => row.unavailable ? 'Absent' : row.workWindows == null ? 'Horaires non déclarés' : row.workWindows.length ? row.workWindows.map(window => `${minutesClock(window.fromMinute)}–${minutesClock(window.untilMinute)}`).join(' ; ') : 'Aucune plage ce jour' },
        { key: 'skills', label: 'Compétences', render: row => listText(row.skills) }
      ], { maxRows: 40 }), pager(data, next => p.run(() => loadCapacity(next), errors)));
    }
  }
  async function compare() {
    await applyHeader(); const errors = el('div', { role: 'alert' }), choices = el('div'), result = el('div');
    p.dialog('Comparer les tournées', el('div', {}, errors, choices, result));
    async function load(offset = 0) {
      const data = await p.call('terrain.tours.list', { offset, limit: 20 });
      choices.replaceChildren(table(data.items, [{ key: 'name', label: 'Tournée' }, { key: 'date', label: 'Date' }, { key: 'tickets', label: 'Tickets' }, { key: 'action', label: '', render: row => button('Comparer', () => p.run(async () => {
        const values = await p.call('terrain.tours.compare', args({ otherId: row.id }));
        result.replaceChildren(el('p', { text: values.sameBasket ? 'Les deux scénarios portent sur le même panier.' : 'Les paniers diffèrent : leurs volumes ne mesurent pas le même travail.' }),
          table([values.current, values.other], [{ key: 'name', label: 'Scénario' }, { key: 'tickets', label: 'Tickets' }, { key: 'stops', label: 'Étapes' }, { key: 'interventionMinutes', label: 'Intervention · min' }, { key: 'travelMinutes', label: 'Trajet · min' },
            { key: 'waitingMinutes', label: 'Attente · min' }, { key: 'missedWindows', label: 'Fenêtres dépassées' }, { key: 'closedSites', label: 'Sites fermés' }, { key: 'missedDueDates', label: 'Échéances dépassées' },
            { key: 'unknownDueDates', label: 'Échéances inconnues' }, { key: 'roadKm', label: 'Route · km', render: row => row.roadKm == null ? 'Incomplète' : formatNumber(row.roadKm, { maximumFractionDigits: 1 }) }], { maxRows: 2 }));
      }, errors)) }], { maxRows: 20 }), pager(data, next => p.run(() => load(next), errors)));
    }
    await load();
  }

  const savedSearch = input('', { placeholder: 'Nom de tournée', maxLength: 120 }), savedDate = input('', { type: 'date' });
  const savedState = select([{ value: '', label: 'Tous les états' }, ...Object.entries(stateLabels).map(([value, label]) => ({ value, label }))], '');
  async function loadRegistry(offset = 0) {
    const data = await p.call('terrain.tours.list', { offset, limit: 20, search: savedSearch.value, date: savedDate.value, state: savedState.value });
    const rows = table(data.items, [{ key: 'name', label: 'Tournée' }, { key: 'date', label: 'Premier jour' }, { key: 'stateLabel', label: 'État' }, { key: 'tickets', label: 'Tickets' }, { key: 'stops', label: 'Étapes' },
      { key: 'actions', label: '', render: row => button('Ouvrir', p.act(() => openTour(row.id))) }], { key: 'id', maxRows: 20, onSelect: row => p.run(() => openTour(row.id)), emptyText: 'Aucune tournée enregistrée dans cette sélection.' });
    registry.replaceChildren(card('Tournées enregistrées', toolbar(field('Nom', savedSearch), field('Date de visite', savedDate), field('État', savedState), button('Filtrer', p.act(() => loadRegistry(0)))), rows,
      pager(data, next => p.run(() => loadRegistry(next))), state.draft ? button('Supprimer la tournée ouverte', p.act(async () => {
        if (!await confirm('Supprimer la tournée', `La tournée locale « ${state.draft.name} » sera retirée du registre.`, 'Supprimer')) return;
        state = await p.call('terrain.tours.delete', args({ confirmed: true })); headerForm = null; pointForms = null; paintSummary(); await loadRegistry();
      }), { danger: true }) : ''), calendarCard());
  }
  function calendarCard() {
    const date = input(new Date().toLocaleDateString('en-CA'), { type: 'date' }), results = el('div');
    const technician = referenceField(p, 'Technicien du calendrier', 'technician', '', '', { saved: true, empty: 'Toute l’équipe' });
    const root = card('Visites prévues pour une journée', toolbar(field('Jour', date), technician.element, button('Afficher la journée', p.act(() => load(0)))), results);
    async function load(offset) {
      const data = await p.call('terrain.tours.day', { date: date.value, technician: technician.read(), offset, limit: 30 });
      results.replaceChildren(table(data.items, [{ key: 'name', label: 'Tournée' }, { key: 'state', label: 'État', render: row => stateLabels[row.state] }, { key: 'technicianKey', label: 'Technicien' }, { key: 'startTime', label: 'Début de journée' }, { key: 'endTime', label: 'Fin de journée' },
        { key: 'tickets', label: 'Tickets', render: row => String(row.ticketIds.length) }, { key: 'action', label: '', render: row => button('Ouvrir', p.act(() => openTour(row.tourId))) }], { maxRows: 30, emptyText: 'Aucune étape prévue ce jour dans le registre local.' }), pager(data, next => p.run(() => load(next))));
    }
    return root;
  }

  async function loadProviders() {
    const settings = await p.call('terrain.services.get'); const editor = form([
      { key: 'routingEndpoint', label: 'Adresse de base du serveur routier OSRM v5', help: 'HTTP(S), sans identifiant, paramètre ni fragment. Laissez vide tant que le serveur autorisé n’est pas choisi.' },
      { key: 'routingProfile', label: 'Profil routier', help: 'Profil configuré sur le serveur, par exemple driving.' },
      { key: 'weatherEndpoint', label: 'Point d’accès météo compatible Open-Meteo', help: 'Adresse complète des prévisions quotidiennes ; renseignez votre service autorisé.' }
    ], settings);
    const consent = input('', { type: 'checkbox' }); editor.element.addEventListener('input', () => { consent.checked = false; });
    const serviceStatus = el('div', { role: 'status' });
    provider.replaceChildren(card('Google Maps', el('p', { text: 'Ouvrez les lieux et les tournées dans Google Maps sans clé. La carte intégrée est facultative et nécessite votre clé Maps Embed API. Aucune connexion n’est lancée avant votre clic.' }),
      button('Réglages Google Maps', p.act(() => openGoogleMapsSettings(ctx)))),
      card('Services facultatifs', el('p', { text: 'Le calcul routier envoie uniquement les coordonnées et le profil routier. La météo envoie les coordonnées et dates des visites. Les fournisseurs restent vides par défaut ; chaque demande est déclenchée ici.' }), editor.element,
      field('J’autorise l’envoi des coordonnées et dates aux adresses saisies pour cette demande.', consent),
      toolbar(button('Enregistrer les adresses', p.act(async () => { await p.call('terrain.services.save', { settings: editor.read() }); consent.checked = false; p.message('Adresses enregistrées. L’autorisation de transmission n’est pas conservée.'); })),
        button('Calculer les routes de la tournée', p.act(() => request('route')), { disabled: !state.draft || !state.loaded }),
        button('Charger la météo des visites', p.act(() => request('weather')), { disabled: !state.draft || !state.loaded })),
      serviceStatus, el('p', { class: 'terrain-muted', text: 'Après modification des dates, techniciens, coordonnées ou de l’ordre, les mesures périmées sont ignorées. Une prévision absente reste inconnue. Les résultats sont conservés avec la tournée lorsque vous l’enregistrez.' })),
      state.draft ? facts([['Tournée ouverte', state.draft.name], ['Segments routiers conservés', state.draft.roadSegments], ['Prévisions conservées', state.draft.weatherObservations]]) : empty('Ouvrez une tournée pour utiliser ces services'));
    async function request(kind) {
      if (!consent.checked) throw new Error('Autorisez l’envoi des coordonnées et dates avant ce calcul.');
      await applyHeader(); const values = editor.read(); const endpoint = kind === 'route' ? values.routingEndpoint : values.weatherEndpoint;
      let url; try { url = new URL(endpoint).href; } catch { throw new Error('Saisissez une adresse complète HTTP(S) pour ce fournisseur.'); }
      const result = await p.call(kind === 'route' ? 'terrain.tours.route' : 'terrain.tours.weather', args({ settings: values, consent: true, consentEndpoint: url }));
      consent.checked = false; state = result.state; headerForm = null; pointForms = null; paintSummary();
      serviceStatus.replaceChildren(el('p', { text: `${kind === 'route' ? result.segments + ' segments routiers' : result.forecasts + ' prévisions'} reçus de ${result.provider}. Vérifiez le planning puis enregistrez la tournée.` }));
    }
  }
  p.on('terrain.progress', data => p.message(data.message));
  p.on('session.changed', () => p.message('Le relevé a changé. Actualisez le planning pour reprendre le rapprochement ; le panier du précédent relevé sera vidé.'));
  p.body.prepend(toolbar(button('Actualiser le relevé et le planning', p.act(async () => {
    if (headerChanged() && !await confirm('Actualiser le planning', 'Les champs de tournée qui ne sont pas encore appliqués seront rechargés. Continuer ?', 'Actualiser')) return;
    headerForm = null; pointForms = null; await refreshAll();
  }))));
  paintSummary(); paintTabs();
  if (ctx.params?.tourId) await openTour(String(ctx.params.tourId)); else await loadTab();
  return () => p.cleanup();
}

function minutesClock(value) { return `${String(Math.floor(value / 60)).padStart(2, '0')}:${String(value % 60).padStart(2, '0')}`; }
