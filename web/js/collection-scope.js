import { el, button, toolbar, field, input, select, formatNumber, formatDate } from './ui.js';
import { teamScopeCatalog } from './team-scope.js';

const clone = value => structuredClone(value);
const scopeKey = value => JSON.stringify(value);
const emptyScope = () => ({ enabled: true, dateRole: 'created', fromDate: '', toDate: '', ticketIds: [], text: '', similarSubject: '', fields: [] });
const normalizeScope = value => ({ ...emptyScope(), ...clone(value || {}), fields: clone(value?.fields || []), ticketIds: [...(value?.ticketIds || [])] });
const sourceKey = data => data?.catalog?.sourceFingerprint || data?.inventory?.sourceFingerprint || '';

// A completed attempt and a complete local copy are different facts. Only the
// confirmed-complete count supplied by the engine may enter this percentage.
export function collectionCoverage(value) {
  if (!value || !Number.isSafeInteger(value.selected) || !Number.isSafeInteger(value.complete)
    || value.selected < 0 || value.complete < 0 || value.complete > value.selected) return null;
  return { ...value, toRead: value.selected - value.complete,
    percent: value.selected ? Math.round(value.complete * 1000 / value.selected) / 10 : null };
}

export function createCollectionScope({ preview: requestPreview, search: requestValues, onChange = () => {}, onError = () => {}, onDateSettings }) {
  let data = null, draft = emptyScope(), initialized = false, dirty = false, locked = false, held = false, disposed = false;
  let preview = null, previewRequest = null, revision = 0, liveCoverage = null, liveCoverageVerifiedAt = null, fieldRows = [], modeConfirmed = false;
  let preflightKey = '', livePreflight = null;
  const heading = el('h2', { text: 'Périmètre de collecte' });
  const source = el('p', { class: 'collection-scope-source' });
  const sourceNote = el('p', { class: 'collection-scope-note' });
  const unavailable = el('p', { class: 'notice warning collection-scope-unavailable', hidden: true });
  const frozen = el('p', { class: 'collection-scope-frozen', hidden: true });
  const previewStatus = el('p', { class: 'collection-scope-status', role: 'status', 'aria-live': 'polite' });
  const controls = el('fieldset', { class: 'collection-scope-controls' }, el('legend', { class: 'sr-only', text: 'Filtres du périmètre de collecte' }));
  const mainFields = el('div', { class: 'collection-scope-target' });
  const dates = el('div', { class: 'collection-scope-dates' });
  const dateRole = select([], ''), from = input('', { type: 'date' }), to = input('', { type: 'date' });
  const dateNotice = el('p', { class: 'help collection-scope-date-notice' });
  dates.append(field('Date utilisée', dateRole), field('Historique à partir du', from, 'Début inclus. Vide : toutes les dates connues.'), field('Jusqu’au', to, 'Fin incluse. Vide : aucune limite de fin.'));
  const ids = input('', { placeholder: '12345, 12346…', maxLength: 24000, 'aria-label': 'Numéros de tickets' });
  const text = input('', { type: 'search', maxLength: 2000, placeholder: 'Mots à chercher dans le titre' });
  const similar = input('', { type: 'search', maxLength: 2000, placeholder: 'Ex. connexion imprimante réseau' });
  const textFields = el('div', { class: 'collection-fields' },
    field('Numéros de tickets', ids, 'Plusieurs numéros séparés par une virgule, un espace ou un point-virgule.'),
    field('Sujet contenant', text),
    field('Sujet proche', similar, 'Mots communs du titre, sans distinction d’accents ou de casse. Ce filtre ne compare pas le sens des phrases.'));
  const filters = el('div', { class: 'collection-scope-filters' });
  const fieldChoice = select([], ''); fieldChoice.setAttribute('aria-label', 'Colonne à filtrer');
  const addFilter = action('Ajouter un filtre', async () => {
    const column = fieldChoice.value;
    if (!editable() || !column || fieldRows.includes(column)) return;
    fieldRows.push(column); invalidate(); drawFilters();
  }, { icon: 'plus' });
  const teamsHost = el('div', { class: 'collection-scope-teams' });
  const fieldsNotice = el('p', { class: 'help collection-scope-fields-notice' });
  const globalCheck = input('', { type: 'checkbox', checked: false });
  const advanced = el('details', { class: 'collection-scope-advanced' }, el('summary', { text: 'Collecte sans filtre du relevé' }),
    field('Parcourir toute la liste GLPI accessible', globalCheck),
    el('p', { class: 'help', text: 'Ce choix peut lire un très grand nombre de tickets. Le volume et le taux ne seront connus qu’après la découverte de la liste GLPI.' }));
  const previewButton = action('Prévisualiser le périmètre', async () => {
    if (!editable() || previewRequest || !canPreview()) return;
    if (!validate()) return;
    const request = { revision, key: scopeKey(draft), source: sourceKey(data) }; previewRequest = request;
    previewStatus.textContent = 'Calcul du périmètre et des contenus déjà disponibles…'; paint();
    try {
      const result = await requestPreview(draft.enabled ? clone(draft) : { ...emptyScope(), enabled: false });
      if (disposed || previewRequest !== request || request.revision !== revision || request.key !== scopeKey(draft)
        || request.source !== sourceKey(data)) return;
      if (draft.enabled && sourceKey(result) && sourceKey(result) !== request.source) {
        data = { ...data, ...result, coverage: null }; preview = null; dirty = true; revision++; draw();
        previewStatus.textContent = 'Le relevé a changé. Vérifier les filtres puis prévisualiser à nouveau le périmètre.';
        return;
      }
      preview = result;
      // The server normalizes only after validating the exact requested scope.
      if (result.scope) { draft = normalizeScope(result.scope); fieldRows = draft.fields.map(filter => filter.column); }
      dirty = false;
      draw();
      previewStatus.textContent = result.preflight?.state === 'blocked' ? 'Les dates du relevé demandent un contrôle. Le lancement vérifiera l’index GLPI avant toute lecture de fiche.' : draft.enabled
        ? result.coverage?.selected === 0 ? 'Aucun ticket ne correspond à ces filtres.'
          : result.coverage?.toRead === 0 ? 'Tous les contenus connus sont complets. « Mettre à jour » vérifiera les versions dans GLPI et relira les fiches qui le nécessitent.'
            : 'Périmètre prêt. La collecte démarre uniquement avec le bouton de lancement.'
        : 'Collecte de toute la liste GLPI préparée. Son volume reste inconnu avant la découverte.';
    } catch (error) {
      if (!disposed && previewRequest === request && request.revision === revision) {
        preview = null; previewStatus.textContent = 'Le périmètre n’a pas pu être confirmé. Aucun ticket n’a été collecté.'; onError(error);
      }
    } finally {
      if (previewRequest === request) previewRequest = null;
      if (!disposed) { paint(); onChange(); }
    }
  }, { icon: 'search' });
  const reset = action('Effacer les filtres', async () => {
    if (!editable()) return;
    draft = emptyScope(); modeConfirmed = false; chooseDateRole(); fieldRows = []; invalidate(); draw();
  });
  const coverageHost = el('div', { class: 'collection-scope-coverage', hidden: true });
  const warnings = el('ul', { class: 'collection-scope-warnings', hidden: true });
  const preflightHost = el('section', { class: 'collection-preflight', 'aria-label': 'Vérification des dates avant collecte', hidden: true });
  const dateSettingsButton = onDateSettings ? button('Régler les dates GLPI', onDateSettings, { icon: 'settings' }) : null;
  mainFields.append(dates, dateNotice, textFields,
    el('div', { class: 'collection-scope-filter-heading' }, el('h3', { text: 'Groupes, utilisateurs et autres critères' }),
      el('p', { class: 'help', text: 'Les filtres se cumulent. Dans un même filtre, une seule des valeurs choisies suffit.' })),
    filters, toolbar(fieldChoice, addFilter), fieldsNotice, teamsHost);
  controls.append(mainFields, advanced, toolbar(previewButton, reset));
  const element = el('section', { class: 'card collection-scope', 'aria-label': 'Périmètre de collecte' },
    heading, source, sourceNote, unavailable, frozen, controls, previewStatus, preflightHost, coverageHost, warnings);
  dateRole.addEventListener('change', () => change('dateRole', dateRole.value));
  from.addEventListener('input', () => change('fromDate', from.value));
  to.addEventListener('input', () => change('toDate', to.value));
  text.addEventListener('input', () => change('text', text.value));
  similar.addEventListener('input', () => change('similarSubject', similar.value));
  ids.addEventListener('input', () => change('ticketIds', [...new Set(ids.value.split(/[\s,;]+/).filter(Boolean))]));
  globalCheck.addEventListener('change', () => {
    if (!editable()) return;
    modeConfirmed = globalCheck.checked; draft.enabled = !globalCheck.checked; invalidate(); paint();
  });
  function editable() { return !disposed && !locked; }
  function canPreview() { return !!data && (draft.enabled ? data.available !== false && !!data.catalog : modeConfirmed); }
  function ready() {
    const coverage = collectionCoverage(preview?.coverage);
    return !!preview?.previewToken && !dirty && !previewRequest && !locked
      && (!draft.enabled ? modeConfirmed : !!coverage && coverage.selected > 0);
  }
  function action(label, handler, options = {}) {
    const node = button(label, null, options);
    node.addEventListener('click', async () => {
      if (node.disabled || disposed) return;
      node.disabled = true; node.setAttribute('aria-busy', 'true');
      try { await handler(); } catch (error) { if (!disposed) onError(error); }
      finally { node.removeAttribute('aria-busy'); if (!disposed) { node.disabled = false; paint(); } }
    }); return node;
  }
  function change(key, value) { if (!editable()) return; draft[key] = value; invalidate(); }
  function invalidate() {
    revision++; dirty = true; preview = null; liveCoverage = null; livePreflight = null;
    previewStatus.textContent = 'Filtres modifiés. Prévisualiser le périmètre avant de lancer la collecte.';
    drawSource(); drawCoverage(); paint(); onChange();
  }
  function chooseDateRole() {
    const roles = data?.catalog?.dateRoles?.filter(role => role.available) || [];
    if (!roles.some(role => role.role === draft.dateRole) && !draft.fromDate && !draft.toDate) draft.dateRole = roles[0]?.role || 'created';
  }
  function validate() {
    if (draft.enabled && draft.fromDate && draft.toDate && draft.fromDate > draft.toDate) {
      previewStatus.textContent = 'La date de fin doit être égale ou postérieure au début de l’historique.'; return false;
    }
    if (draft.enabled && draft.ticketIds.some(id => !/^\d+$/.test(String(id)))) {
      previewStatus.textContent = 'Les numéros de tickets doivent contenir uniquement des chiffres.'; return false;
    }
    return true;
  }
  function setValues(column, values) {
    draft.fields = draft.fields.filter(filter => filter.column !== column);
    if (values.length) draft.fields.push({ column, values: [...new Set(values)] });
    invalidate();
  }
  function selected(column) { return draft.fields.find(filter => filter.column === column)?.values || []; }
  function drawFilters() {
    const columns = data?.catalog?.fields || [];
    filters.replaceChildren(...fieldRows.map(column => {
      const descriptor = columns.find(item => item.column === column);
      const title = descriptor?.label || column;
      const query = input('', { type: 'search', maxLength: 2000, placeholder: `Rechercher dans ${title}`, 'aria-label': `Rechercher des valeurs : ${title}` });
      const list = el('div', { class: 'collection-scope-values' }), chips = el('div', { class: 'collection-scope-chips' });
      const notice = el('p', { class: 'help' });
      let options = descriptor?.values || [], queryRevision = 0, searching = false, truncated = !!descriptor?.truncated;
      const paintOptions = () => {
        const needle = query.value.trim().toLocaleLowerCase('fr');
        const visible = options.filter(option => String(option.value).toLocaleLowerCase('fr').includes(needle));
        list.replaceChildren(...visible.map(option => {
          const check = input('', { type: 'checkbox', checked: selected(column).includes(option.value), 'aria-label': `${title} : ${option.value}` });
          check.addEventListener('change', () => {
            if (!editable()) return;
            setValues(column, check.checked ? [...selected(column), option.value] : selected(column).filter(value => value !== option.value)); paintChips();
          });
          return el('label', { class: 'collection-scope-option' }, check, el('span', { text: option.value }), el('small', { text: formatNumber(option.count) }));
        }));
        notice.textContent = !descriptor ? 'Cette colonne n’est plus disponible. Retirer le filtre ou charger le relevé d’origine.'
          : !visible.length ? 'Aucune valeur affichée. Rechercher dans le relevé pour trouver une autre valeur.'
            : truncated ? 'Premières valeurs affichées. La recherche parcourt les valeurs du relevé complet.' : `${formatNumber(visible.length)} valeur(s) affichée(s).`;
      };
      const paintChips = () => chips.replaceChildren(...selected(column).map(value => action(value, async () => {
        if (!editable()) return; setValues(column, selected(column).filter(item => item !== value)); paintChips(); paintOptions();
      }, { icon: 'close', attributes: { 'aria-label': `Retirer ${value} du filtre ${title}` } })));
      query.addEventListener('input', () => { queryRevision++; paintOptions(); });
      const search = action('Rechercher les valeurs', async () => {
        if (!editable() || searching || !requestValues) return;
        searching = true; const stamp = ++queryRevision, source = sourceKey(data); notice.textContent = 'Recherche dans les valeurs du relevé…';
        try {
          const result = await requestValues({ column, search: query.value, limit: 200 });
          if (disposed || stamp !== queryRevision || !fieldRows.includes(column)) return;
          if (sourceKey(result) !== source) throw new Error('Le relevé a changé. Actualiser le périmètre avant de choisir des valeurs.');
          const current = result.catalog?.fields?.find(item => item.column === column);
          options = current?.values || []; truncated = !!current?.truncated; paintOptions();
        } finally { searching = false; search.disabled = locked; }
      }, { icon: 'search', disabled: !descriptor });
      const remove = action('Retirer le filtre', async () => {
        if (!editable()) return; fieldRows = fieldRows.filter(value => value !== column); setValues(column, []); drawFilters();
      }, { icon: 'close' });
      paintOptions(); paintChips();
      return el('section', { class: 'collection-scope-filter', 'aria-label': `Filtre ${title}`, dataset: { scopeColumn: column } },
        el('div', { class: 'collection-scope-filter-title' }, el('h4', { text: title }), remove),
        chips, toolbar(query, search), list, notice);
    }));
    const available = columns.filter(column => !fieldRows.includes(column.column));
    fieldChoice.replaceChildren(...available.map(column => el('option', { value: column.column, text: column.label || column.column })));
    fieldChoice.value = available[0]?.column || '';
    fieldChoice.disabled = !available.length;
    addFilter.disabled = locked || !available.length || fieldRows.length >= 12;
  }
  function drawTeams() {
    const group = data?.catalog?.fields?.find(column => column.role === 'group');
    const teams = group ? teamScopeCatalog(data?.teams || [], []) : [];
    if (!teams.length) { teamsHost.replaceChildren(); return; }
    const query = input('', { type: 'search', placeholder: 'Rechercher une équipe', 'aria-label': 'Rechercher une équipe', maxLength: 200 });
    const list = el('div', { class: 'collection-scope-team-list' });
    const paintTeams = () => {
      const needle = query.value.trim().toLocaleLowerCase('fr');
      list.replaceChildren(...teams.filter(team => `${team.label} ${team.search}`.toLocaleLowerCase('fr').includes(needle)).map(team => action(team.label, async () => {
        if (!editable()) return;
        if (!fieldRows.includes(group.column)) fieldRows.push(group.column);
        setValues(group.column, [...selected(group.column), ...team.groups]); drawFilters();
      }, { icon: 'users', title: team.detail || team.groups.join(' · ') })));
    };
    query.addEventListener('input', paintTeams); paintTeams();
    teamsHost.replaceChildren(el('details', {}, el('summary', { text: 'Ajouter les groupes d’une équipe configurée' }),
      el('p', { class: 'help', text: 'Choisir une équipe ajoute ses groupes au filtre « Groupe attribué ». Les groupes choisis se cumulent.' }), query, list));
  }
  function displayedInventory() { return preview?.inventory || (locked ? data?.inventory : null); }
  function currentPreflight() { return livePreflight || preview?.preflight || (locked || !dirty ? data?.preflight : null); }
  function drawPreflight() {
    const value = currentPreflight(), key = JSON.stringify(value || null);
    if (key === preflightKey) return;
    preflightKey = key; preflightHost.replaceChildren(); preflightHost.hidden = !value;
    if (!value) return;
    const state = ['ready', 'warning', 'blocked', 'unknown'].includes(value.state) ? value.state : 'unknown';
    preflightHost.dataset.state = state;
    const heading = state === 'blocked' ? 'Dates à corriger avant de collecter' : state === 'warning' ? 'Certaines dates restent à vérifier'
      : state === 'ready' ? 'Dates du relevé interprétables' : 'Dates à contrôler au démarrage';
    preflightHost.append(el('h3', { text: heading }));
    if (['checked', 'reliableDates', 'unreliableDates'].every(key => Number.isSafeInteger(value[key]) && value[key] >= 0)
      && value.reliableDates + value.unreliableDates === value.checked)
      preflightHost.append(el('p', { class: 'collection-preflight-counts', text: `${formatNumber(value.checked)} dates de modification examinées · ${formatNumber(value.reliableDates)} interprétables · ${formatNumber(value.unreliableDates)} non fiables` }));
    const messages = Array.isArray(value.messages) ? value.messages.filter(message => typeof message === 'string') : [];
    if (messages.length) preflightHost.append(el('ul', {}, ...messages.map(message => el('li', { text: message }))));
    preflightHost.append(el('p', { class: 'help', text: state === 'blocked'
      ? 'Le dernier contrôle ne permettait pas de lire les fiches avec une date fiable. Une reprise vérifiera de nouveau l’index GLPI avant toute lecture de fiche. Les contenus déjà enregistrés sont conservés.'
      : state === 'warning' ? 'Les fiches dont la date reste ambiguë peuvent conserver une version non confirmée. Le moteur vérifie de nouveau les dates avant la lecture.'
        : state === 'ready' ? 'Ce contrôle porte sur les dates du relevé. La collecte doit encore confirmer les contenus et leur version dans GLPI.'
          : 'Le contrôle sera effectué sur l’index GLPI avant la lecture des fiches. Ce statut ne confirme pas encore la qualité des dates.' }));
    if (typeof value.sourceTimeZone === 'string' || typeof value.sourceDateOrder === 'string') {
      const orders = { dmy: 'jour / mois / année', mdy: 'mois / jour / année', ymd: 'année / mois / jour' };
      preflightHost.append(el('p', { class: 'help', text: `Fuseau des dates GLPI : ${value.sourceTimeZone || 'non renseigné'} · Format : ${orders[value.sourceDateOrder] || 'détection prudente'}. Une date avec un décalage explicite peut être interprétable sans fuseau configuré.` }));
    }
    if (dateSettingsButton) preflightHost.append(dateSettingsButton);
  }
  function drawSource() {
    const catalog = data?.catalog, inventory = data?.inventory;
    const shown = displayedInventory();
    const count = shown?.count ?? catalog?.inventoryCount;
    const observedAt = shown?.observedAt || catalog?.observedAt;
    const url = shown?.sourceUrl || catalog?.sourceUrl || inventory?.sourceUrl;
    let installation = data?.installation || '';
    try { if (!installation && url) installation = new URL(url).origin; } catch { /* The engine validates the source; do not invent a label for an absent URL. */ }
    source.textContent = !draft.enabled ? `Toute la liste GLPI accessible${installation ? ` · ${installation}` : ''}`
      : catalog || shown ? `${formatNumber(count)} tickets dans le relevé${observedAt ? ` · relevé du ${formatDate(observedAt)}` : ' · date du relevé non disponible'}${installation ? ` · ${installation}` : ''}`
        : locked ? 'Le périmètre est conservé avec le passage en cours.' : !initialized ? 'Chargement du périmètre…' : 'Aucun relevé de tickets disponible pour préparer la collecte.';
    const complete = shown?.complete ?? catalog?.inventoryComplete;
    sourceNote.textContent = !draft.enabled ? 'Le relevé chargé et ses filtres ne limitent pas cette collecte.'
      : catalog || shown ? `${complete === false ? 'Relevé partiel : le taux porte seulement sur les tickets connus. ' : complete !== true ? 'Complétude du relevé non confirmée. ' : ''}Les filtres utilisent ce relevé. Les changements GLPI plus récents nécessitent son actualisation.` : '';
  }
  function draw() {
    const catalog = data?.catalog;
    drawSource();
    unavailable.hidden = data?.available !== false;
    unavailable.textContent = data?.message || 'Charger une première liste de tickets depuis la source GLPI pour utiliser ces filtres.';
    const availableDates = catalog?.dateRoles?.filter(role => role.available) || [];
    const missingDateRole = !!(draft.fromDate || draft.toDate) && !availableDates.some(role => role.role === draft.dateRole);
    dateRole.replaceChildren(...(missingDateRole ? [el('option', { value: draft.dateRole, text: 'Date choisie indisponible', disabled: true })] : []),
      ...availableDates.map(role => el('option', { value: role.role, text: role.label })));
    dateRole.value = draft.dateRole; from.value = draft.fromDate; to.value = draft.toDate;
    dateRole.disabled = from.disabled = to.disabled = !availableDates.length;
    dateNotice.textContent = missingDateRole ? 'La date choisie n’est plus disponible. Choisir une date reconnue ou effacer les bornes avant de prévisualiser.'
      : !availableDates.length ? 'Aucune colonne de date reconnue dans ce relevé. Le filtre d’historique est indisponible.'
      : 'Les dates absentes ou illisibles sont exclues quand une borne d’historique est appliquée.';
    ids.value = draft.ticketIds.join(', '); text.value = draft.text; similar.value = draft.similarSubject;
    // Both title filters need a resolved title column. An unavailable role must
    // never be silently broadened to another column or the ticket contents.
    const hasTitle = catalog?.fields?.some(column => column.role === 'title') || catalog?.titleAvailable === true;
    text.disabled = similar.disabled = !hasTitle;
    text.placeholder = hasTitle ? 'Mots à chercher dans le titre' : 'Colonne de titre indisponible';
    similar.placeholder = hasTitle ? 'Ex. connexion imprimante réseau' : 'Colonne de titre indisponible';
    fieldsNotice.textContent = catalog?.fields?.length ? 'Seules les colonnes présentes dans le relevé sont proposées.' : 'Aucune colonne utilisable pour les filtres de groupe ou d’utilisateur.';
    globalCheck.checked = !draft.enabled; advanced.open = !draft.enabled;
    drawFilters(); drawTeams(); drawCoverage(); paint();
  }
  function drawCoverage() {
    drawPreflight();
    const value = liveCoverage || preview?.coverage || (!dirty && initialized ? data?.coverage : null);
    const coverage = collectionCoverage(value);
    const applicable = draft.enabled && (locked || !dirty);
    coverageHost.hidden = !applicable || !coverage;
    warnings.hidden = !preview?.warnings?.length;
    warnings.replaceChildren(...(preview?.warnings || []).map(message => el('li', { text: message })));
    if (!applicable || !coverage) { coverageHost.replaceChildren(); return; }
    const meter = el('progress', { max: Math.max(1, coverage.selected), value: coverage.complete, 'aria-label': 'Contenus complets dans le périmètre' });
    const metric = (label, count, kind = '') => el('div', { class: `collection-scope-metric ${kind}` }, el('strong', { text: formatNumber(count ?? 0) }), el('span', { text: label }));
    const inventory = displayedInventory() || data?.inventory;
    const verifiedAt = liveCoverage ? liveCoverageVerifiedAt : inventory?.verifiedAt;
    coverageHost.replaceChildren(el('div', { class: 'collection-scope-coverage-heading' }, el('h3', { text: 'Couverture du périmètre' }),
      el('strong', { text: coverage.percent == null ? 'Aucun ticket' : `${coverage.percent.toLocaleString('fr-FR')} %` })),
      meter, el('p', { class: 'collection-scope-coverage-detail', text: `${formatNumber(coverage.complete)} / ${formatNumber(coverage.selected)} contenus complets pour la version connue.` }),
      el('div', { class: 'collection-scope-metrics' }, metric('Dans le périmètre', coverage.selected), metric('Complets disponibles', coverage.complete, 'complete'),
        metric('À lire ou vérifier', coverage.toRead), metric('Sans contenu', coverage.missing), metric('Modifiés', coverage.changed), metric('Partiels', coverage.partial), metric('Version à confirmer', coverage.unconfirmed)),
      el('p', { class: 'help collection-scope-verification', text: verifiedAt
        ? `Dernière vérification des versions dans GLPI : ${formatDate(verifiedAt)}.`
        : 'Comparaison aux versions du relevé. La date d’une vérification GLPI de ce périmètre n’est pas disponible.' }),
      el('p', { class: 'help', text: 'Les contenus complets dont la version est confirmée sont réutilisés. Les erreurs du passage restent détaillées dans le journal.' }));
  }
  function paint() {
    controls.disabled = locked || !initialized;
    mainFields.hidden = !draft.enabled || !initialized;
    advanced.hidden = !initialized;
    unavailable.hidden = data?.available !== false || locked && held && !!data?.inventory;
    previewButton.disabled = locked || !!previewRequest || !canPreview();
    reset.disabled = locked || !initialized;
    addFilter.disabled = locked || !fieldChoice.value || fieldRows.length >= 12;
    frozen.hidden = !locked;
    frozen.textContent = held ? 'Périmètre conservé pour la reprise. Préparer un autre périmètre pour démarrer une collecte différente.' : 'Le périmètre reste fixe pendant ce passage.';
    if (dateSettingsButton) dateSettingsButton.disabled = locked && !held;
    element.dataset.scopeReady = String(ready());
  }
  draw();
  return {
    element,
    setModel(next, { preserveSaved = false } = {}) {
      if (disposed) return;
      const changedSource = data && sourceKey(data) !== sourceKey(next); data = next;
      if (!initialized) {
        draft = normalizeScope(preserveSaved ? next.state?.scope || next.scope : next.scope || next.state?.scope);
        if (!preserveSaved && draft.enabled === false) { draft.enabled = true; dirty = true; }
        initialized = true; chooseDateRole(); fieldRows = draft.fields.map(filter => filter.column);
        if (!previewStatus.textContent) previewStatus.textContent = 'Prévisualiser pour afficher le nombre de tickets et les contenus déjà disponibles.';
        if (!preserveSaved && next.catalog?.sourceFingerprint && next.inventory?.sourceFingerprint
          && next.catalog.sourceFingerprint !== next.inventory.sourceFingerprint) dirty = true;
      } else if (changedSource) { revision++; preview = null; dirty = true; liveCoverage = null; livePreflight = null;
        previewStatus.textContent = 'Le relevé a changé. Prévisualiser à nouveau le périmètre.'; }
      if (!dirty && next.coverage && preview && scopeKey(draft) === scopeKey(normalizeScope(next.scope || next.state?.scope))) {
        preview = { ...preview, coverage: next.coverage, inventory: next.inventory || preview.inventory };
        if (next.coverage.selected > 0 && next.coverage.toRead === 0) previewStatus.textContent = 'Tous les contenus connus sont complets. « Mettre à jour » vérifiera les versions dans GLPI et relira les fiches qui le nécessitent.';
      }
      draw(); onChange();
    },
    setState(state) {
      if (disposed) return;
      locked = !!state.locked; held = !!state.held;
      liveCoverage = state.coverage || null; liveCoverageVerifiedAt = state.coverageVerifiedAt || null;
      livePreflight = state.preflight || null;
      drawSource(); drawCoverage(); paint();
    },
    beginDraft() { if (disposed) return; locked = false; held = false; draft.enabled = true; modeConfirmed = false; invalidate(); draw(); },
    restoreSaved() {
      if (disposed) return;
      revision++; preview = null; dirty = false; modeConfirmed = false;
      draft = normalizeScope(data?.state?.scope || data?.scope); fieldRows = draft.fields.map(filter => filter.column);
      previewStatus.textContent = ''; draw(); onChange();
    },
    invalidatePreview() { if (disposed) return; invalidate(); previewStatus.textContent = 'Prévisualiser à nouveau ce périmètre avant de relancer la collecte.'; },
    ready,
    args() { return ready() ? { scope: clone(draft), previewToken: preview.previewToken, confirmedGlobal: !draft.enabled && modeConfirmed } : null; },
    preflightState() { return currentPreflight()?.state || 'unknown'; },
    dispose() { disposed = true; revision++; previewRequest = null; }
  };
}
