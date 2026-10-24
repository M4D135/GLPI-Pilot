import { el, button, card, toolbar, empty, badge, input, select, field, table, form, confirm, formatNumber } from '../ui.js';
import { page, pager, lookup, referenceField, highlighted, details, facts, labels, listText, readList, numberOrNull, guid } from './terrain-common.js';
import { openMap } from './maps.js';

const sourceNames = { manual: 'Correction locale', location: 'Champ GLPI', building: 'Bâtiment dans le titre', site: 'Site dans le titre', title: 'Ville dans le titre', combined: 'Titre complet' };
const dayNames = ['Dimanche', 'Lundi', 'Mardi', 'Mercredi', 'Jeudi', 'Vendredi', 'Samedi'];

export async function renderReference(ctx) {
  const p = page(ctx, 'Référentiel géographique', 'Villes, sites, bâtiments et règles de localisation. Préparez vos modifications, puis vérifiez leur effet sur le relevé complet.');
  let state = await p.call('terrain.reference.state'), kind = !state.cities && state.sites ? 'site' : 'city', offset = 0;
  if (['site', 'city', 'building'].includes(ctx.params?.referenceKind)) kind = ctx.params.referenceKind;
  const summary = el('div'), listing = el('div'), configuration = el('div'), warningArea = el('div');
  const search = input('', { placeholder: 'Nom, alias ou rattachement', maxLength: 120 });
  const type = select(Object.entries(labels).map(([value, label]) => ({ value, label })), kind);
  type.addEventListener('change', p.act(async () => { kind = type.value; offset = 0; await loadList(); }));
  p.body.append(summary, toolbar(
    button('Actualiser', p.act(reload)),
    button('Simuler et enregistrer le référentiel', p.act(preview), { primary: true }),
    button('Importer un CSV…', p.act(importCsv)),
    button('Carte des sites', p.act(() => openMap(ctx, { ...draft(), kind: 'sites', referenceDraft: true })), { icon: 'map' }),
    button('Exporter le CSV du brouillon…', p.act(async () => { const result = await p.call('terrain.reference.export', draft()); if (!result.cancelled) p.message(`Référentiel enregistré : ${result.path}`); })),
    button('Abandonner le brouillon', p.act(async () => { if (await confirm('Recharger le référentiel', 'Les modifications du brouillon seront abandonnées. Le référentiel enregistré sera rechargé.', 'Recharger')) { state = await p.call('terrain.reference.reset', draft()); await reload(); } }))
  ), warningArea,
  card('Références', toolbar(field('Type', type), field('Rechercher', search), button('Rechercher', p.act(async () => { offset = 0; await loadList(); })),
    button('Ajouter une référence', p.act(() => edit(kind, null)), { primary: true })), listing),
  configuration, simulation(p, () => state, draft, async result => { state = result; await reload(); }));

  function draft() { return { draftVersion: state.draftVersion, revision: state.revision }; }
  function paintState() {
    summary.replaceChildren(el('div', { class: 'terrain-statbar' },
      badge(`${formatNumber(state.cities)} villes`), badge(`${formatNumber(state.sites)} sites`), badge(`${formatNumber(state.buildings)} bâtiments`),
      state.sitesWithoutCity ? badge(`${formatNumber(state.sitesWithoutCity)} sites · commune non renseignée`) : '',
      badge(state.dirty ? 'Brouillon modifié' : 'Référentiel enregistré', state.dirty ? 'warn' : 'ok')));
    warningArea.replaceChildren();
    if (state.conflicted) warningArea.append(el('p', { class: 'terrain-warning', text: 'Le référentiel a été modifié ailleurs depuis l’ouverture du brouillon. Rechargez-le avant de reprendre les modifications.' }));
    if (state.historical) warningArea.append(el('p', { class: 'terrain-warning', text: 'Une archive est ouverte. Fermez-la avant d’appliquer le référentiel de travail.' }));
    if (state.warnings.length) warningArea.append(details(`Contrôles du dictionnaire · ${state.warnings.length}`, el('ul', {}, ...state.warnings.map(value => el('li', { text: value })))));
  }
  async function reload() { state = await p.call('terrain.reference.state'); paintState(); paintConfiguration(); await loadList(); }
  async function loadList() {
    const data = await p.call('terrain.reference.list', { kind, search: search.value, offset, limit: 40 });
    state.draftVersion = data.draftVersion; state.revision = data.revision;
    listing.replaceChildren(table(data.items, [
      { key: 'name', label: labels[kind] }, { key: 'parent', label: kind === 'city' ? 'Hiérarchie' : 'Rattachement' },
      { key: 'aliases', label: kind === 'site' ? 'Termes de détection' : 'Alias', render: row => kind === 'site' ? [row.name, row.aliases].filter(Boolean).join(' ; ') : row.aliases }, { key: 'detail', label: kind === 'city' ? 'Code postal' : kind === 'site' ? 'Adresse' : 'Étage / zone' },
      { key: 'rules', label: kind === 'site' ? 'Plages d’ouverture' : 'Règles', render: row => kind === 'building' ? '—' : String(row.rules) },
      { key: 'actions', label: 'Actions', render: row => toolbar(button('Modifier', p.act(async () => { const result = await p.call('terrain.reference.detail', { kind, id: row.id }); state.draftVersion = result.draftVersion; await edit(kind, result.entry); })),
        ...(kind === 'site' || kind === 'city' ? [button('Voir la carte', p.act(() => openMap(ctx, { ...draft(), kind, id: row.id, referenceDraft: true })))] : []),
        button('Retirer', p.act(async () => { if (await confirm(`Retirer ${row.name}`, 'La suppression sera ajoutée au brouillon. Les rattachements existants doivent d’abord être déplacés ou retirés.', 'Retirer')) { state = await p.call('terrain.reference.delete', { ...draft(), kind, id: row.id }); await reload(); } }), { danger: true })) }
    ], { key: 'id', maxRows: 40, emptyText: 'Aucune référence. Ajoutez une ville ou importez votre référentiel CSV.' }), pager(data, next => p.run(async () => { offset = next; await loadList(); })));
  }
  function paintConfiguration() {
    const config = state.configuration, priorities = [...config.sourcePriority];
    const values = form([
      { key: 'enabled', label: 'Activer la localisation locale', type: 'checkbox' },
      { key: 'titleColumn', label: 'Colonne du titre', help: 'Nom exact ; vide pour la reconnaissance habituelle des colonnes.' },
      { key: 'locationColumn', label: 'Colonne GLPI de localisation', help: 'Nom exact utilisé par la source « Champ GLPI ».' }
    ], config);
    const order = el('div'); const source = select(Object.entries(sourceNames).map(([value, label]) => ({ value, label })), 'manual');
    const paint = () => {
      order.replaceChildren(el('ol', { class: 'terrain-priority' }, ...priorities.map((key, index) => el('li', {},
        el('span', { text: sourceNames[key] }), toolbar(
          button('Monter', () => { [priorities[index - 1], priorities[index]] = [priorities[index], priorities[index - 1]]; paint(); }, { disabled: index === 0 }),
          button('Descendre', () => { [priorities[index + 1], priorities[index]] = [priorities[index], priorities[index + 1]]; paint(); }, { disabled: index === priorities.length - 1 }),
          button('Retirer', () => { priorities.splice(index, 1); paint(); }, { disabled: priorities.length === 1 })
        )))));
    }; paint();
    configuration.replaceChildren(card('Sources et priorité de détection',
      el('p', { text: 'La première source reconnue ou ambiguë arrête la recherche. Une source vide ou sans correspondance laisse la suivante décider.' }),
      values.element, order, toolbar(field('Ajouter une source', source), button('Ajouter', () => { if (!priorities.includes(source.value)) { priorities.push(source.value); paint(); } })),
      button('Mettre ces réglages dans le brouillon', p.act(async () => { state = await p.call('terrain.reference.configure', { ...draft(), ...values.read(), sourcePriority: priorities }); await reload(); p.message('Priorités ajoutées au brouillon. Simulez leur effet avant l’enregistrement.'); }))));
  }
  async function edit(entryKind, entry) {
    const value = entry || { id: '', name: '', aliases: [], rules: [], openings: [], closedDates: [] };
    const errors = el('div', { role: 'alert' }); let parent;
    const fields = [{ key: 'name', label: 'Nom', required: true }, { key: 'aliases', label: 'Alias et variantes', type: 'textarea', help: entryKind === 'site' ? 'Le nom du site est reconnu automatiquement. Ajoutez ici ses autres termes de détection : une variante par ligne ou séparée par un point-virgule ; 12 au maximum.' : 'Une variante par ligne ou séparée par un point-virgule ; 12 au maximum.' }];
    if (entryKind === 'city') fields.push(
      { key: 'postalCode', label: 'Code postal' }, { key: 'region', label: 'Région' }, { key: 'department', label: 'Département' }, { key: 'sector', label: 'Secteur' },
      { key: 'territoryPath', label: 'Hiérarchie libre', help: 'Jusqu’à 12 niveaux séparés par /.' }, { key: 'excludedTerms', label: 'Termes exclus', type: 'textarea', help: 'Un terme par ligne ou séparé par un point-virgule.' });
    if (entryKind === 'site') fields.push({ key: 'address', label: 'Adresse' }, { key: 'contact', label: 'Contact sur place' }, { key: 'equipment', label: 'Matériel habituel' });
    if (entryKind !== 'building') fields.push({ key: 'latitude', label: 'Latitude', help: 'Facultative, à renseigner avec la longitude.' }, { key: 'longitude', label: 'Longitude' });
    else fields.push({ key: 'floor', label: 'Étage / zone' });
    const editor = form(fields, { ...value, aliases: listText(value.aliases), excludedTerms: listText(value.excludedTerms), latitude: value.latitude ?? '', longitude: value.longitude ?? '' });
    const body = el('div', { class: 'terrain-editor' }, errors);
    if (entryKind !== 'city') {
      parent = referenceField(p, entryKind === 'site' ? 'Commune de rattachement · facultative' : 'Site de rattachement', entryKind === 'site' ? 'city' : 'site', entryKind === 'site' ? value.cityId : value.siteId, '',
        entryKind === 'site' ? { empty: 'Commune non renseignée', help: 'Le site et ses alias fonctionnent sans commune. Vous pourrez le rattacher plus tard ; aucune position géographique ne sera inventée.' } : {});
      body.append(parent.element);
    }
    body.append(editor.element);
    const rules = structuredClone(value.rules || []), openings = structuredClone(value.openings || []);
    if (entryKind === 'city') body.append(ruleEditor(p, rules));
    let closed;
    if (entryKind === 'site') {
      body.append(openingEditor(p, openings));
      closed = form([{ key: 'dates', label: 'Fermetures exceptionnelles', type: 'textarea', help: 'Une date AAAA-MM-JJ par ligne, jusqu’à 366 dates. Sans plage récurrente, les horaires d’ouverture restent non déclarés.' }], { dates: (value.closedDates || []).join('\n') }); body.append(closed.element);
    }
    p.dialog(`${entry ? 'Modifier' : 'Ajouter'} · ${labels[entryKind]}`, body, [
      { label: 'Annuler', action: close => close() },
      { label: 'Ajouter au brouillon', primary: true, action: close => p.run(async () => {
        const read = editor.read(); const data = { ...value, ...read, aliases: readList(read.aliases) };
        if (entryKind !== 'building') { data.latitude = numberOrNull(read.latitude); data.longitude = numberOrNull(read.longitude); }
        if (entryKind === 'city') { data.excludedTerms = readList(read.excludedTerms); data.rules = rules; }
        if (entryKind === 'site') { data.cityId = parent.read(); data.openings = openings; data.closedDates = readList(closed.read().dates); }
        if (entryKind === 'building') data.siteId = parent.read();
        state = await p.call('terrain.reference.upsert', { ...draft(), kind: entryKind, entry: data }); close(); await reload();
      }, errors) }
    ]);
  }
  async function preview() {
    const data = await p.call('terrain.reference.preview', draft()); const content = el('div'), errors = el('div', { role: 'alert' });
    const panel = el('div', {}, el('p', { text: `${formatNumber(data.totalRows)} lignes examinées ; ${formatNumber(data.changedRows)} localisations modifiées.` }),
      data.limited ? el('p', { text: 'Les 1 000 premiers changements sont consultables. Le calcul porte sur toutes les lignes.' }) : '', errors, content);
    const paint = value => content.replaceChildren(table(value.items, [
      { key: 'rowIndex', label: 'Ligne', render: row => String(row.rowIndex + 1) }, { key: 'before', label: 'Avant' }, { key: 'after', label: 'Après' }, { key: 'explanation', label: 'Décision' }
    ], { maxRows: 50, emptyText: 'Aucun changement de localisation.' }), pager(value, next => p.run(async () => paint(await p.call('terrain.reference.previewPage', { token: data.token, offset: next, limit: 50 })), errors)));
    paint(data);
    p.dialog('Impact sur le relevé complet', panel, [
      { label: 'Conserver le brouillon', action: close => close() },
      { label: 'Enregistrer et appliquer', primary: true, action: close => p.run(async () => { state = await p.call('terrain.reference.apply', { token: data.token, confirmed: true }); close(); await reload(); p.message('Référentiel enregistré et relevé recalculé.'); }, errors) }
    ]);
  }
  async function importCsv() {
    const data = await p.call('terrain.reference.import', draft()); if (data.cancelled) return;
    const content = el('div'), errors = el('div', { role: 'alert' });
    const panel = el('div', {}, el('p', { text: `${data.fileName} · ${data.additions} ajouts, ${data.modifications} modifications, ${data.identical} références identiques. Les lignes absentes du CSV sont conservées. La colonne Alias remplace les variantes des références concernées. Une colonne VilleId absente préserve le rattachement existant ; vide, elle le retire.` }), errors, content);
    const paint = value => content.replaceChildren(table(value.items, [{ key: 'kind', label: 'Décision' }, { key: 'type', label: 'Type' }, { key: 'name', label: 'Nom' }, { key: 'detail', label: 'Changement' }], { maxRows: 50 }),
      pager(value, next => p.run(async () => paint(await p.call('terrain.reference.importPage', { token: data.token, offset: next, limit: 50 })), errors)));
    paint(data);
    p.dialog('Comparatif d’import CSV', panel, [
      { label: 'Annuler', action: close => close() },
      { label: 'Fusionner dans le brouillon', primary: true, action: close => p.run(async () => { state = await p.call('terrain.reference.importApply', { token: data.token, confirmed: true }); if (!state.cities && state.sites) { kind = 'site'; type.value = kind; offset = 0; } close(); await reload(); p.message('Import fusionné dans le brouillon. Simulez maintenant le référentiel avant de l’enregistrer.'); }, errors) }
    ]);
  }
  search.addEventListener('keydown', event => { if (event.key === 'Enter') p.run(async () => { offset = 0; await loadList(); }); });
  p.on('session.changed', () => p.message('Le relevé a changé. Actualisez cet écran avant une nouvelle simulation.'));
  await reload();
  if (ctx.params?.referenceId) {
    const result = await p.call('terrain.reference.detail', { kind, id: ctx.params.referenceId }); state.draftVersion = result.draftVersion; await edit(kind, result.entry);
  }
  return () => p.cleanup();
}

function ruleEditor(p, rules) {
  const list = el('div');
  const root = card('Règles contextuelles', el('p', { text: 'Les règles combinent un terme, un contexte, une position et éventuellement un format de titre. Les expressions régulières sont limitées à 500 caractères et 30 ms par évaluation.' }), list,
    button('Ajouter une règle', () => edit(null), { disabled: rules.length >= 16 }));
  function paint() {
    list.replaceChildren(table(rules, [{ key: 'term', label: 'Terme' }, { key: 'position', label: 'Position' }, { key: 'priority', label: 'Priorité' },
      { key: 'context', label: 'Contexte requis', render: row => listText(row.requiredTerms) },
      { key: 'actions', label: '', render: row => toolbar(button('Modifier', () => edit(row)), button('Retirer', () => { rules.splice(rules.indexOf(row), 1); paint(); }, { danger: true })) }
    ], { maxRows: 16, emptyText: 'Les noms et alias simples restent utilisables sans règle contextuelle.' }));
  }
  function edit(rule) {
    if (!rule && rules.length >= 16) { p.message('Une ville accepte au maximum 16 règles contextuelles.'); return; }
    const value = rule || { id: guid(), term: '', requiredTerms: [], excludedTerms: [], contextColumn: '', priority: 0, position: 'any', tokenPosition: 0, titlePattern: '' };
    const editor = form([
      { key: 'term', label: 'Terme reconnu', required: true }, { key: 'requiredTerms', label: 'Contexte requis', type: 'textarea', help: 'Termes séparés par ; ou par une nouvelle ligne.' },
      { key: 'excludedTerms', label: 'Contexte exclu', type: 'textarea' }, { key: 'contextColumn', label: 'Colonne du contexte', help: 'Vide : contexte pris dans le titre.' },
      { key: 'priority', label: 'Priorité', type: 'number', min: 0, max: 100 },
      { key: 'position', label: 'Position du terme', type: 'select', options: [{ value: 'any', label: 'N’importe où' }, { value: 'start', label: 'Début du titre' }, { value: 'end', label: 'Fin du titre' }, { value: 'token', label: 'Position de mot' }] },
      { key: 'tokenPosition', label: 'Position de mot', type: 'number', min: 0, max: 10000, help: 'Premier mot = 1 ; utilisé seulement pour « Position de mot ».' },
      { key: 'titlePattern', label: 'Format de titre facultatif', help: 'Expression régulière ; laissez vide si aucune contrainte de format n’est nécessaire.' }
    ], { ...value, requiredTerms: listText(value.requiredTerms), excludedTerms: listText(value.excludedTerms) });
    p.dialog('Règle de reconnaissance', editor.element, [{ label: 'Annuler', action: close => close() }, { label: 'Garder cette règle', primary: true, action: close => {
      const data = editor.read(); const next = { ...value, ...data, priority: Number(data.priority), tokenPosition: Number(data.tokenPosition), requiredTerms: readList(data.requiredTerms), excludedTerms: readList(data.excludedTerms) };
      if (rule) rules.splice(rules.indexOf(rule), 1, next); else rules.push(next); close(); paint();
    } }]);
  }
  paint(); return root;
}

function openingEditor(p, openings) {
  const list = el('div');
  const root = card('Ouvertures récurrentes du site', list, button('Ajouter une plage', () => edit(null)));
  function paint() { list.replaceChildren(table(openings, [
    { key: 'day', label: 'Jour', render: row => dayNames[row.day] }, { key: 'from', label: 'Début' }, { key: 'until', label: 'Fin' },
    { key: 'actions', label: '', render: row => toolbar(button('Modifier', () => edit(row)), button('Retirer', () => { openings.splice(openings.indexOf(row), 1); paint(); })) }
  ], { maxRows: 28, emptyText: 'Horaires non déclarés.' })); }
  function edit(opening) {
    if (!opening && openings.length >= 28) { p.message('Un site accepte au maximum 28 plages d’ouverture.'); return; }
    const editor = form([{ key: 'day', label: 'Jour', type: 'select', options: dayNames.map((label, value) => ({ value: String(value), label })) },
      { key: 'from', label: 'Ouverture', type: 'time', required: true }, { key: 'until', label: 'Fermeture', type: 'time', required: true }], opening || { day: '1', from: '08:00', until: '12:00' });
    p.dialog('Plage d’ouverture', editor.element, [{ label: 'Annuler', action: close => close() }, { label: 'Garder cette plage', primary: true, action: close => {
      const value = editor.read(); const next = { day: Number(value.day), from: value.from, until: value.until };
      if (opening) openings.splice(openings.indexOf(opening), 1, next); else openings.push(next); close(); paint();
    } }]);
  }
  paint(); return root;
}

function simulation(p, state, draft, updated) {
  const title = form([{ key: 'title', label: 'Titre à tester', type: 'textarea', required: true }], { title: '' });
  const contexts = [], contextRows = el('div'), result = el('div');
  const root = card('Tester une reconnaissance', title.element, contextRows, toolbar(
    button('Ajouter une colonne de contexte', () => {
      if (contexts.length >= 32) return;
      const name = input('', { placeholder: 'Nom exact de colonne', maxLength: 120 }), value = input('', { placeholder: 'Valeur', maxLength: 2000 });
      const item = { name, value }; contexts.push(item);
      const row = toolbar(field('Colonne', name), field('Valeur', value), button('Retirer', () => { contexts.splice(contexts.indexOf(item), 1); row.remove(); })); contextRows.append(row);
    }),
    button('Tester le brouillon', p.act(async () => {
      const context = {};
      for (const entry of contexts) { if (!entry.name.value) throw new Error('Nommez chaque colonne de contexte.'); if (Object.hasOwn(context, entry.name.value)) throw new Error('Deux colonnes de contexte portent le même nom.'); context[entry.name.value] = entry.value.value; }
      const data = await p.call('terrain.reference.simulate', { ...draft(), title: title.read().title, context });
      result.replaceChildren(highlighted(data.title, data.highlights), facts([['Ville', data.match.cityName], ['Site', data.match.siteName], ['Bâtiment', data.match.buildingName], ['Décision', data.match.explanation]]),
        table(data.match.evidence, [{ key: 'cityName', label: 'Lieu', render: row => row.siteName ? `${row.siteName} · ${row.cityName || 'commune non renseignée'}` : row.cityName }, { key: 'term', label: 'Terme' }, { key: 'decision', label: 'Décision' }, { key: 'explanation', label: 'Justification' }], { maxRows: 100 }));
    }), { primary: true })
  ), result);
  return root;
}
