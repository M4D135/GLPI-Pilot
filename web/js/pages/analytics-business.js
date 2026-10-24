import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption,
  checkbox, textArea, clone, uuid, formatNumber } from './analytics-ui.js';
import { renderDashboard } from './analytics-dashboard.js';

const definitions = [
  ['weather', 'pilot-weather', 'Météo', 'Stock, catégories et équipes : volumes, météo et actions du service.', 'sun'],
  ['pilotage', 'pilot-summary', 'Pilotage', 'Chiffres clés des incidents et demandes, statuts et affectations.', 'chart'],
  ['groups', 'pilot-groups', 'Groupes', 'Stock actif de chaque groupe et répartition entre les quatre statuts.', 'users'],
  ['categories', 'pilot-categories', 'Catégories', 'Catégories, sous-catégories et statuts du relevé.', 'layers'],
  ['oldest', 'pilot-oldest', 'Plus anciens', 'Les cinq plus anciens par groupe et les dix du périmètre.', 'history'],
  ['followup', 'pilot-followup', 'Suivi agents', 'Tickets rédigés et attribués, par personne ou équipe.', 'users'],
  ['participants', 'pilot-participants', 'Agents actifs', 'Suivis, tâches, intersection et union des participations.', 'chart'],
  ['sites', 'pilot-sites', 'Sites', 'Stock des sites, identifiants et préparation des interventions.', 'map']
].map(([key, route, title, description, icon]) => ({ key, route, title, description, icon }));
// Keep each view's choices for this UI session, independently in normal and
// simulation workspaces. Nothing is written to the user's saved parameters.
const focusSessions = new WeakMap();
export const pages = [...definitions.map(view => ({ id: view.route, title: view.title, group: 'Pilotage GLPI', icon: view.icon,
  render: ctx => renderBusiness(ctx, view) })), { id: 'pilot-settings', title: 'Paramètres du pilotage', group: 'Pilotage GLPI', icon: 'settings', render: renderBusinessSettings }];

async function renderBusiness(ctx, view) {
  const cleanup = heading(ctx, view.title, view.description);
  ctx.root.dataset.businessView = view.key;
  const bootstrap = await call(ctx, 'dashboard.business.bootstrap'); if (ctx.signal.aborted) return cleanup;
  if (!bootstrap.hasSource) {
    ctx.root.append(card('Vos rubriques sont prêtes', caption('Chargez une liste d’incidents pour calculer les volumes, ou explorez ces vues avec la démonstration.'),
      toolbar(command(ctx, 'Charger la démonstration', async () => { await call(ctx, 'app.demo'); await ctx.navigate(view.route); }, { primary: true, icon: 'spark' }),
        button('Importer un fichier ou collecter', () => ctx.navigate('collect'), { icon: 'download' }), button('Régler le pilotage', () => ctx.navigate('pilot-settings'), { icon: 'settings' }))));
    if (view.key === 'weather') {
      const board = bootstrap.emptyWeather;
      for (const section of board.sections.filter(section => !section.hidden)) {
        const widgets = board.widgets.filter(widget => !widget.hidden && widget.section === section.key);
        ctx.root.append(el('section', { class: 'business-empty-section' }, el('h2', { text: section.title }), caption(section.description || ''),
          el('div', { class: 'business-empty-cards' }, ...widgets.map(widget => el('article', { class: 'business-empty-card' }, el('h3', { text: widget.title || widget.sourceKey }),
            widget.kind === 'note' ? caption(widget.note) : el('div', {}, el('strong', { class: 'business-empty-number', text: '—' }), caption('En attente du relevé')))))));
      }
    } else ctx.root.append(empty(view.title, view.description));
    ctx.root.dataset.ready = 'true'; return cleanup;
  }
  let session = focusSessions.get(ctx.state);
  if (!session) { session = new Map(); focusSessions.set(ctx.state, session); }
  const sessionKey = `${ctx.state.session?.testMode || ctx.state.session?.simulation ? 'simulation' : 'normal'}:${view.key}`;
  const explicit = ctx.params?.focusKeys !== undefined ? ctx.params.focusKeys
    : ctx.params?.focusKey !== undefined ? (ctx.params.focusKey ? [ctx.params.focusKey] : []) : session.get(sessionKey) ?? [];
  const requested = Array.isArray(explicit) ? [...new Set(explicit)] : [];
  const available = new Set([...bootstrap.focus.map(item => item.key), ...bootstrap.groups.map(label => `group:${label}`)]);
  const focusKeys = requested.filter(key => available.has(key));
  const removed = requested.length - focusKeys.length;
  const business = { ...view, groups: bootstrap.groups, focus: bootstrap.focus, rules: bootstrap.business.weatherRules,
    customLayout: bootstrap.business.boardOverrides?.some(board => board.id === `business-${view.key}`) === true,
    usingWeatherDefaults: bootstrap.usingWeatherDefaults === true,
    focusWarning: removed ? `${formatNumber(removed)} sélection(s) ne sont plus disponibles dans ce relevé. Le filtre a été actualisé ; vérifiez les équipes et groupes retenus.` : '',
    focusEnabled: ['oldest', 'followup', 'participants', 'sites', 'groups'].includes(view.key) };
  const api = { ...ctx.api, call: (operation, args = {}, options) => ctx.api.call(operation,
    operation.startsWith('dashboard.') ? { ...args, businessView: view.key, focusKeys } : args, options) };
  const result = await renderDashboard({ ...ctx, params: { ...ctx.params, focusKeys }, api, business });
  if (!ctx.signal.aborted) session.set(sessionKey, [...focusKeys]);
  return result;
}

export async function renderBusinessSettings(ctx) {
  const cleanup = heading(ctx, 'Paramètres du pilotage', 'Seuils, correspondances d’équipes, catégories, notes et référentiel de sites.');
  ctx.root.classList.add('business-settings'); ctx.root.dataset.businessView = 'settings';
  const model = await call(ctx, 'dashboard.business.settings.get'); if (ctx.signal.aborted) return cleanup;
  const business = clone(model.business), sites = clone(model.sites);
  const notes = textArea(business.notes || '', 5); notes.maxLength = 4000;
  const freshness = input(business.freshnessWarningHours ?? '', { type: 'number', min: 1, max: 8760, step: 1 });
  const intervention = input(business.siteInterventionThreshold ?? 4, { type: 'number', min: 1, max: 1000000, step: 1 });
  const ruleHost = el('div', { class: 'business-rules-host' }), siteHost = el('div', { class: 'business-sites-host' });
  const status = el('p', { class: 'business-settings-status', role: 'status', 'aria-live': 'polite' });
  let dirty = false;
  function changed() { dirty = true; status.textContent = 'Modifications à enregistrer'; }
  for (const control of [notes, freshness, intervention]) control.addEventListener('input', changed);
  const save = command(ctx, 'Enregistrer les paramètres', async () => {
    business.notes = notes.value; business.freshnessWarningHours = freshness.value.trim() === '' ? null : Number(freshness.value);
    business.siteInterventionThreshold = Number(intervention.value);
    await call(ctx, 'dashboard.business.settings.save', { business, sites, baseFingerprint: model.fingerprint, revision: model.revision });
    dirty = false; ctx.notify('Paramètres du pilotage enregistrés.'); await ctx.navigate('pilot-settings');
  }, { primary: true, icon: 'check' });
  const fillThresholds = command(ctx, 'Remplir les seuils vides', () => {
    let count = 0, skipped = 0;
    for (const rule of business.weatherRules) {
      if (rule.orangeAbove != null && rule.redAbove != null) continue;
      const preset = (model.weatherThresholdPresets || []).find(preset => preset.kind === rule.kind && preset.sourceKey === rule.sourceKey);
      if (!preset) { skipped++; continue; }
      const orange = rule.orangeAbove ?? preset.orangeAbove, red = rule.redAbove ?? preset.redAbove;
      // A partial custom pair may be incompatible with a starter threshold.
      // Keep it unchanged instead of silently overwriting a value already set.
      if (orange > red) { skipped++; continue; }
      if (rule.orangeAbove == null) { rule.orangeAbove = orange; count++; }
      if (rule.redAbove == null) { rule.redAbove = red; count++; }
    }
    if (count) { changed(); drawRules(); }
    status.textContent = `${formatNumber(count)} seuil(s) complété(s)${count ? ' · modifications à enregistrer' : ''}.`
      + (skipped ? ` ${formatNumber(skipped)} règle(s) à compléter manuellement : indicateur sans repère ou seuil existant incompatible.` : ' Les valeurs déjà saisies sont conservées.');
  }, { icon: 'spark' });
  const profileImport = command(ctx, 'Reprendre les paramètres d’un classeur Excel', async () => {
    const preview = await call(ctx, 'dashboard.business.profile.preview'); if (preview.cancelled) return;
    const profile = preview.profile;
    const warnings = [...new Set([...(profile.warnings || []), ...(preview.warnings || [])])];
    dialog('Vérifier les paramètres du classeur', el('div', {}, el('h3', { text: preview.fileName }),
      caption(`${formatNumber(profile.weatherRuleCount)} règles météo · ${formatNumber(profile.visibleWeatherRuleCount)} cartes visibles · ${formatNumber(profile.siteCount)} sites · ${formatNumber(profile.aliasCount)} alias.`),
      caption(`${formatNumber(preview.sitesAdded)} site(s) à ajouter · ${formatNumber(preview.sitesUpdated)} à mettre à jour. Les correspondances, seuils et notes proviennent des valeurs enregistrées du classeur.`),
      caption(preview.weatherReplaced ? 'Les règles météo seront reprises et la présentation de cette rubrique reconstruite. Les autres tableaux sont conservés. Les macros et formules ne sont pas exécutées.'
        : 'Les sites détectés seront repris. Les règles météo actuelles sont conservées. Les macros et formules ne sont pas exécutées.'),
      dirty ? caption('Les modifications non enregistrées de cette page seront remplacées par les paramètres appliqués.', 'warning') : null,
      profile.notes ? el('div', { class: 'business-import-notes' }, el('h4', { text: 'Analyse et actions' }), caption(profile.notes)) : null,
      ...warnings.map(warning => caption(warning, 'warning'))),
    [{ label: 'Annuler', action: close => close() }, { label: 'Appliquer les paramètres', primary: true, disabled: !profile.available,
      action: async close => { await call(ctx, 'dashboard.business.profile.apply', { token: preview.token }); close(); await ctx.navigate('pilot-settings'); } }]);
  }, { icon: 'download' });
  ctx.root.append(toolbar(save, button('Voir la météo', () => ctx.navigate('pilot-weather'), { icon: 'chart' }), profileImport), status,
    card('Règles de lecture', caption('Un seuil orange ou rouge n’est franchi qu’au-delà de la valeur saisie. Laisser les seuils vides affiche le volume avec « Seuils à définir ».'),
      caption('Repères de départ à adapter : stock global 100 / 200 ; incidents et demandes 50 / 100 ; catégories, équipes et autres compteurs de stock 10 / 20 (orange / rouge). Ce ne sont pas des objectifs de capacité ou de SLA.'),
      caption('Les équipes suivent l’ordre ci-dessous : chaque ticket est rattaché à la première équipe correspondante, même si cette équipe est masquée. « Météo » désactive seulement la couleur ; « Afficher » masque la carte.'),
      !model.configured ? caption('Les règles proposées reprennent une disposition existante ou les groupes et catégories du relevé. Enregistrer les rend explicites pour les prochains relevés.') : null),
    card('Actualité du service', el('div', { class: 'analytics-form-grid' }, field('Rappel de fraîcheur · heures', freshness, 'Vide : aucun rappel. Ce délai ne recolore pas la météo.'),
      field('Intervention à planifier à partir de', intervention, 'Nombre de tickets actifs d’un site connu. Les sites inconnus sont à vérifier dès le premier ticket.')), field('Analyse et actions en cours', notes)),
    card('Météo · seuils et correspondances', toolbar(command(ctx, 'Ajouter une règle', () => {
      if (business.weatherRules.length >= 48) throw new Error('Quarante-huit règles météo au maximum.');
      business.weatherRules.push({ id: uuid(), label: 'Nouvelle règle', kind: 'metric', sourceKey: 'active', values: [], orangeAbove: null, redAbove: null, visible: true, weatherEnabled: true, includeDescendants: false, note: '' }); changed(); drawRules();
    }, { icon: 'plus' }), fillThresholds, caption(`${business.weatherRules.length} règles · valeurs multiples séparées par une ligne ou un point-virgule`)), ruleHost),
    card('Sites · noms et alias', toolbar(command(ctx, 'Ajouter un site', () => {
      if (sites.length >= 2000) throw new Error('Deux mille sites au maximum.');
      sites.push({ id: uuid(), name: 'Nouveau site', cityId: '', aliases: [], address: '', latitude: null, longitude: null, openings: [], closedDates: [], contact: '', equipment: '' }); changed(); drawSites();
    }, { icon: 'plus' }), button('Référentiel géographique complet', () => ctx.navigate('terrain-reference'), { icon: 'map' })),
      caption('Un site peut être reconnu sans commune. Les alias sont recherchés dans les champs configurés du relevé ; les cas ambigus restent à vérifier.'), siteHost));

  const split = value => value.split(/[;\r\n]+/u).map(item => item.trim()).filter(Boolean);
  const numberValue = control => control.value.trim() === '' ? null : Number(control.value);
  function bind(control, event, update) { control.addEventListener(event, () => { update(); changed(); }); return control; }
  function drawRules() {
    ruleHost.replaceChildren();
    const orderNote = el('p', { class: 'help', text: 'Les boutons ↑ et ↓ règlent la priorité. Les correspondances d’une même règle sont reliées par OU.' });
    const rows = business.weatherRules.map((rule, index) => {
      const label = bind(input(rule.label, { maxLength: 180, 'aria-label': `Libellé de la règle ${index + 1}` }), 'input', () => rule.label = label.value);
      const kind = select([{ value: 'metric', label: 'Indicateur' }, { value: 'type', label: 'Type' }, { value: 'category', label: 'Catégorie' }, { value: 'team', label: 'Équipe' }], rule.kind);
      kind.setAttribute('aria-label', `Périmètre de ${rule.label}`);
      kind.addEventListener('change', () => { rule.kind = kind.value; if (rule.kind !== 'metric') rule.sourceKey = 'active'; changed(); drawRules(); });
      const values = textArea((rule.values || []).join('\n'), 3); values.maxLength = 32032; values.setAttribute('aria-label', `Correspondances de ${rule.label}`);
      bind(values, 'input', () => rule.values = split(values.value));
      const sourceOptions = [{ value: 'active', label: 'Tickets actifs' }, { value: 'active-incidents', label: 'Incidents actifs' }, { value: 'active-requests', label: 'Demandes actives' },
        { value: 'pending', label: 'En attente' }, { value: 'without-group', label: 'Sans groupe' }, { value: 'without-technician', label: 'Sans technicien' },
        { value: 'active-multiple-groups', label: 'Plusieurs groupes' }, { value: 'active-no-author', label: 'Sans rédacteur visible' }, { value: 'high', label: 'Priorités hautes' }, { value: 'old', label: 'Anciens' }, { value: 'stale', label: 'Sans modification récente' }];
      if (!sourceOptions.some(option => option.value === rule.sourceKey)) sourceOptions.push({ value: rule.sourceKey, label: rule.sourceKey });
      const source = bind(select(sourceOptions, rule.sourceKey), 'change', () => rule.sourceKey = source.value); source.setAttribute('aria-label', `Indicateur de ${rule.label}`);
      const orange = bind(input(rule.orangeAbove ?? '', { type: 'number', min: 0, step: 'any', 'aria-label': `Seuil orange de ${rule.label}` }), 'input', () => rule.orangeAbove = numberValue(orange));
      const red = bind(input(rule.redAbove ?? '', { type: 'number', min: 0, step: 'any', 'aria-label': `Seuil rouge de ${rule.label}` }), 'input', () => rule.redAbove = numberValue(red));
      const visible = checkbox('Afficher', rule.visible !== false); bind(visible.control, 'change', () => rule.visible = visible.control.checked);
      const weather = checkbox('Météo', rule.weatherEnabled !== false); bind(weather.control, 'change', () => rule.weatherEnabled = weather.control.checked);
      const descendants = checkbox('Inclure les sous-catégories', rule.includeDescendants === true); bind(descendants.control, 'change', () => rule.includeDescendants = descendants.control.checked);
      const note = textArea(rule.note || '', 2); note.maxLength = 4000; note.setAttribute('aria-label', `Note de ${rule.label}`); bind(note, 'input', () => rule.note = note.value);
      const choices = rule.kind === 'team' ? model.groups : rule.kind === 'category' ? model.categories : rule.kind === 'type' ? ['Incident', 'Demande'] : [];
      const observed = select([{ value: '', label: 'Ajouter une valeur du relevé…' }, ...choices.map(value => ({ value, label: value }))]); observed.setAttribute('aria-label', `Valeur observée pour ${rule.label}`);
      observed.addEventListener('change', () => { if (!observed.value) return; rule.values = [...new Set([...(rule.values || []), observed.value])]; values.value = rule.values.join('\n'); observed.value = ''; changed(); });
      const move = delta => { const next = index + delta; if (next < 0 || next >= business.weatherRules.length) return; business.weatherRules.splice(index, 1); business.weatherRules.splice(next, 0, rule); changed(); drawRules(); };
      return el('article', { class: 'business-rule-row', 'data-rule-id': rule.id },
        el('div', { class: 'business-rule-order' }, badge(String(index + 1)), button('↑', () => move(-1), { disabled: index === 0, title: `Monter ${rule.label}` }),
          button('↓', () => move(1), { disabled: index === business.weatherRules.length - 1, title: `Descendre ${rule.label}` })),
        el('div', { class: 'business-rule-identity' }, field('Libellé', label), field('Rubrique', kind), toolbar(visible.element, weather.element)),
        el('div', { class: 'business-rule-scope' }, rule.kind === 'metric' ? field('Indicateur', source) : el('div', {}, field('Correspondances', values), observed, rule.kind === 'category' ? descendants.element : null),
          field('Note de la carte', note)),
        el('div', { class: 'business-rule-thresholds' }, field('Orange au-delà de', orange), field('Rouge au-delà de', red),
          button('Retirer', () => { business.weatherRules.splice(index, 1); changed(); drawRules(); }, { danger: true, title: `Retirer ${rule.label}` })));
    });
    ruleHost.append(orderNote, ...rows);
  }
  function drawSites() {
    siteHost.replaceChildren();
    if (!sites.length) { siteHost.append(empty('Aucun site configuré', 'Ajoutez vos sites ou reprenez le paramétrage de votre classeur Excel.')); return; }
    siteHost.append(table(sites, [
      { key: 'name', label: 'Site', render: site => { const control = input(site.name, { maxLength: 200, 'aria-label': 'Nom du site' }); return bind(control, 'input', () => site.name = control.value); } },
      { key: 'aliases', label: 'Alias · une variante par ligne', render: site => { const control = textArea((site.aliases || []).join('\n'), 3); control.setAttribute('aria-label', `Alias de ${site.name}`); return bind(control, 'input', () => site.aliases = split(control.value)); } },
      { key: 'cityId', label: 'Commune facultative', render: site => { const control = select([{ value: '', label: 'Sans commune renseignée' }, ...model.cities.map(city => ({ value: city.id, label: city.name }))], site.cityId || ''); control.setAttribute('aria-label', `Commune de ${site.name}`); return bind(control, 'change', () => site.cityId = control.value); } },
      { key: 'remove', label: 'Action', render: site => button('Retirer', () => { sites.splice(sites.indexOf(site), 1); changed(); drawSites(); }, { danger: true, title: `Retirer ${site.name}` }) }
    ], { maxRows: 2000 }));
  }
  drawRules(); drawSites(); ctx.root.dataset.ready = 'true';
  return cleanup;
}
