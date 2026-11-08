import { el, button, toolbar, input, select, field, dialog } from '../ui.js';
import { weatherGroupCatalog, selectedGroupValues, MAX_TEAM_GROUPS } from '../weather-groups.js';

const help = text => el('p', { class: 'help', text });
const details = (title, ...children) => el('details', { class: 'widget-filter-advanced' }, el('summary', { text: title }), ...children);
const fold = value => String(value).normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');
export const isGroupFilter = (kind, source) => kind === 'matrix-row' && source === 'group-status'
  || kind === 'distribution' && ['group', 'active-group'].includes(source);
const initialValues = value => value.values?.length ? [...value.values] : value.value ? [value.value] : [];

// Only the editor draft changes. C# still validates and applies the original
// filter contract, including exclusions, OR within a condition and AND across it.
export function widgetFilterEditor(ctx, catalog, initial = [], { weatherTeam = false } = {}) {
  const root = el('div', { class: 'widget-filter-editor' });
  const list = el('div', { class: 'widget-filter-list' });
  const rows = [], popups = new Set(), lifetime = new AbortController();
  let disposed = false, groupsRequest = null;
  function dispose() {
    if (disposed) return;
    disposed = true; lifetime.abort();
    for (const popup of [...popups]) popup.close();
    popups.clear(); ctx.signal?.removeEventListener('abort', dispose);
  }
  ctx.signal?.addEventListener('abort', dispose, { once: true });
  if (ctx.signal?.aborted) dispose();
  async function groups() {
    if (!groupsRequest) groupsRequest = ctx.api.call('dashboard.business.bootstrap', {}, { signal: lifetime.signal })
      .then(data => { if (!Array.isArray(data.groups)) throw new Error('La liste des groupes est indisponible.'); return data.groups; })
      .catch(error => { groupsRequest = null; throw error; });
    return groupsRequest;
  }
  async function chooseGroups(item) {
    // Fetch only when explicitly requested; opening/saving a card does not
    // launch a GLPI collection or overwrite the active source.
    const observed = await groups();
    if (disposed || !rows.includes(item)) return;
    const entries = weatherGroupCatalog(observed, [{ kind: 'team', values: item.values }]);
    const aliases = new Map(entries.flatMap(entry => entry.aliases.map(key => [key, entry.key])));
    const chosen = new Set(item.values.map(value => aliases.get(`group:${value}`)).filter(Boolean));
    const query = input('', { type: 'search', placeholder: 'Rechercher un groupe…', 'aria-label': 'Rechercher un groupe', maxLength: 200 });
    const count = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
    const errors = el('p', { class: 'notice warning', role: 'alert', hidden: true });
    const options = el('div', { class: 'widget-group-options', 'aria-label': 'Groupes disponibles' });
    const choices = entries.map(entry => {
      const control = input('', { type: 'checkbox', 'aria-label': entry.value });
      const label = el('label', { class: 'widget-group-option' }, control,
        el('span', {}, el('strong', { text: entry.value }),
          !entry.observed ? el('small', { class: 'help', text: 'Absent du relevé actuel ; sélection conservée' }) : null));
      control.addEventListener('change', () => { control.checked ? chosen.add(entry.key) : chosen.delete(entry.key); update(); });
      options.append(label); return { entry, control, label, search: fold(entry.value) };
    });
    const empty = help('Aucun groupe ne correspond à cette recherche.'); options.append(empty);
    function update() {
      let shown = 0;
      for (const choice of choices) {
        choice.label.hidden = !choice.search.includes(fold(query.value));
        choice.control.checked = chosen.has(choice.entry.key);
        choice.control.disabled = chosen.size >= MAX_TEAM_GROUPS && !choice.control.checked;
        if (!choice.label.hidden) shown++;
      }
      empty.hidden = shown > 0;
      count.textContent = `${chosen.size} groupe(s) sélectionné(s) / ${MAX_TEAM_GROUPS} · ${shown} résultat(s)`;
    }
    const selectShown = button('Tout cocher dans les résultats', () => {
      const additions = choices.filter(choice => !choice.label.hidden && !chosen.has(choice.entry.key));
      if (chosen.size + additions.length > MAX_TEAM_GROUPS) {
        errors.textContent = `Au maximum ${MAX_TEAM_GROUPS} groupes par condition. Affinez la recherche : aucune sélection partielle n’a été appliquée.`;
        errors.hidden = false; return;
      }
      additions.forEach(choice => chosen.add(choice.entry.key)); errors.hidden = true; update();
    });
    const clearShown = button('Décocher les résultats', () => {
      choices.filter(choice => !choice.label.hidden).forEach(choice => chosen.delete(choice.entry.key)); errors.hidden = true; update();
    });
    const popup = dialog(item.exclude.value === 'exclude' ? 'Choisir les groupes à exclure' : 'Choisir les groupes à inclure',
      el('div', { class: 'widget-group-picker' },
        help('Cochez les groupes, sans recopier leur nom. Les espaces et accents restent inchangés. Annuler conserve la sélection précédente.'),
        field('Recherche', query), toolbar(selectShown, clearShown), count, errors, options),
      [{ label: 'Annuler', action: close => close() }, { label: 'Appliquer la sélection', primary: true, action: close => {
        if (disposed || !rows.includes(item)) { close(); return; }
        item.values = selectedGroupValues(entries, chosen); item.syncValues(); item.paint(); close();
      } }]);
    popup.element.classList.add('widget-group-dialog'); popups.add(popup);
    popup.element.addEventListener('pilot:closed', () => popups.delete(popup), { once: true });
    query.addEventListener('input', update); update(); query.focus();
  }
  function add(value = {}) {
    if (rows.length >= 12) throw new Error('Douze conditions au maximum par périmètre.');
    const kind = select([{ value: 'distribution', label: 'Catégorie / répartition' }, { value: 'matrix-row', label: 'Groupe / ligne de tableau' },
      { value: 'metric', label: 'Population' }, { value: 'date', label: 'Période datée' }], value.kind ?? 'distribution');
    const source = select([], value.sourceKey ?? '');
    const exclude = select([{ value: 'include', label: 'Inclure : garder ces tickets' }, { value: 'exclude', label: 'Exclure : écarter ces tickets' }], value.exclude ? 'exclude' : 'include');
    const first = input(value.value ?? '', { type: 'date' }), last = input(value.value2 ?? '', { type: 'date' });
    const dates = el('div', { class: 'widget-filter-fields' }, field('Début inclus', first), field('Fin incluse', last));
    const alternatives = el('textarea', { class: 'analytics-textarea', rows: 3, 'aria-label': 'Valeurs de la condition' });
    const descendants = input('', { type: 'checkbox', checked: value.includeDescendants ?? false });
    const descendantField = el('label', { class: 'widget-filter-check' }, descendants, el('span', { text: 'Inclure les sous-catégories' }));
    const body = el('div', { class: 'widget-filter-values' }), title = el('strong');
    const advanced = details('Source et type avancés', el('div', { class: 'widget-filter-fields' }, field('Type de filtre', kind), field('Source des valeurs', source)));
    const manual = details('Saisie manuelle avancée', field('Valeurs exactes · une par ligne', alternatives));
    const chips = el('div', { class: 'widget-group-chips' });
    const row = el('section', { class: 'widget-filter-condition' });
    const item = { values: initialValues(value), exclude, row, kind, source, syncValues: () => { alternatives.value = item.values.join('\n'); } };
    const categoryBranch = () => kind.value === 'distribution' && ['category', 'active-category'].includes(source.value);
    const choose = button('Choisir les groupes', () => chooseGroups(item), { icon: 'users' });
    const summary = help('');
    item.paint = () => {
      const group = isGroupFilter(kind.value, source.value), negative = exclude.value === 'exclude';
      const multiple = ['distribution', 'matrix-row'].includes(kind.value);
      row.dataset.filterKind = group ? 'groups' : kind.value; row.dataset.filterMode = exclude.value;
      title.textContent = group ? negative ? 'Groupes exclus de cette carte' : 'Groupes inclus dans cette carte' : negative ? 'Condition d’exclusion' : 'Condition d’inclusion';
      summary.textContent = group ? negative
        ? 'Un ticket appartenant à l’un de ces groupes est écarté, même s’il appartient aussi à un groupe inclus.'
        : 'Un ticket appartenant à au moins un des groupes cochés peut être compté. Les autres filtres de la carte et du périmètre restent appliqués.'
        : 'Cette condition se combine avec les autres filtres de la carte et du périmètre.';
      choose.hidden = chips.hidden = !group;
      dates.hidden = kind.value !== 'date'; manual.hidden = !multiple;
      manual.open = multiple && !group; advanced.open = !group;
      descendantField.hidden = !categoryBranch();
      chips.replaceChildren(...item.values.map((name, index) => button(`${name} ×`, () => {
        item.values.splice(index, 1); item.syncValues(); item.paint();
      }, { title: `Retirer ${name} de cette condition`, attributes: { 'aria-label': `Retirer le groupe ${name}` } })));
      if (!item.values.length) chips.append(help('Aucun groupe sélectionné. Choisissez des groupes ou retirez cette condition.'));
    };
    function refreshSources() {
      const previous = source.value || value.sourceKey;
      const available = kind.value === 'metric' ? catalog.metrics : kind.value === 'date' ? catalog.dates
        : kind.value === 'matrix-row' ? catalog.matrices : catalog.distributions;
      source.replaceChildren(...(available || []).map(entry => el('option', { value: entry.key, text: `${entry.label}${entry.available === false ? ' · indisponible' : ''}` })));
      if (previous && ![...source.options].some(option => option.value === previous)) source.append(el('option', { value: previous, text: `${previous} · indisponible` }));
      if (previous) source.value = previous;
    }
    kind.addEventListener('change', () => { source.replaceChildren(); value = {}; refreshSources(); item.paint(); });
    source.addEventListener('change', () => item.paint()); exclude.addEventListener('change', () => item.paint());
    alternatives.addEventListener('input', () => {
      item.values = [...new Set(alternatives.value.split(/\r?\n/).map(text => text.trim()).filter(Boolean))];
      // Keep the advanced input open and focused while typing.
      const open = manual.open; item.paint(); manual.open = open;
    });
    item.read = () => {
      const multiple = ['distribution', 'matrix-row'].includes(kind.value);
      if (multiple && item.values.length > 32) throw new Error('Une condition accepte au maximum 32 valeurs.');
      if (isGroupFilter(kind.value, source.value) && !item.values.length) throw new Error('Choisissez au moins un groupe ou retirez la condition vide.');
      return { kind: kind.value, sourceKey: source.value,
        value: kind.value === 'date' ? first.value : kind.value === 'metric' ? '' : item.values.length === 1 ? item.values[0] : '',
        values: multiple && item.values.length > 1 ? [...item.values] : [], value2: kind.value === 'date' ? last.value : '',
        exclude: exclude.value === 'exclude', includeDescendants: categoryBranch() && descendants.checked };
    };
    row.append(el('div', { class: 'widget-filter-heading' }, title,
      button('Retirer la condition', () => { row.remove(); rows.splice(rows.indexOf(item), 1); updateCapacity(); })),
      field('Action sur les tickets', exclude), summary, body, advanced);
    body.append(choose, chips, manual, dates, descendantField);
    rows.push(item); list.append(row); item.syncValues(); refreshSources(); item.paint(); updateCapacity();
  }
  function updateCapacity() { addGroups.disabled = addOther.disabled = rows.length >= 12; }
  const addGroups = button('Ajouter un filtre de groupes', () => add({ kind: 'matrix-row', sourceKey: 'group-status' }), { icon: 'users' });
  const addOther = button('Ajouter une autre condition', () => add({}), { icon: 'plus' });
  root.append(help('Choisissez les groupes à inclure ou à exclure. Les groupes d’une même condition se cumulent (OU) ; les conditions se combinent (ET).'));
  if (weatherTeam && initial.some(filter => filter.exclude && isGroupFilter(filter.kind, filter.sourceKey)))
    root.append(el('p', { class: 'notice info', text: 'Priorité des équipes : cette carte météo contient des exclusions pour ne pas recompter les tickets des équipes précédentes. Elles restent conservées. L’ordre général se règle dans « Seuils et équipes ».' }));
  root.append(list, toolbar(addGroups, addOther));
  for (const value of initial) add(value);
  return { element: root, read: () => rows.map(item => item.read()), dispose };
}
