import { navigationVisible } from '../navigation.js';
import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption, details, clone, uuid,
  number, formatNumber, formatDate, rowDialog, pager, checkbox, textArea, getRevision, error } from './analytics-ui.js';
import { confirm } from '../ui.js';

const aggregateNames = { count: 'Compter les lignes', percent: 'Pourcentage de la population de départ', distinct: 'Valeurs distinctes', sum: 'Somme', average: 'Moyenne', min: 'Minimum', max: 'Maximum', median: 'Médiane', percentile: 'Percentile' };
const operatorNames = { contains: 'Contient', 'not-contains': 'Ne contient pas', equals: 'Est égal à', 'not-equals': 'Est différent de', 'starts-with': 'Commence par', 'ends-with': 'Se termine par', empty: 'Est vide', 'not-empty': 'Est renseigné',
  'number-eq': 'Nombre égal à', 'number-gt': 'Nombre supérieur à', 'number-gte': 'Nombre supérieur ou égal à', 'number-lt': 'Nombre inférieur à', 'number-lte': 'Nombre inférieur ou égal à', in: 'Appartient à la liste', 'not-in': 'N’appartient pas à la liste',
  'number-equals': 'Nombre égal à', 'number-not-equals': 'Nombre différent de', 'number-greater': 'Nombre supérieur à', 'number-greater-or-equal': 'Nombre supérieur ou égal à', 'number-less': 'Nombre inférieur à', 'number-less-or-equal': 'Nombre inférieur ou égal à',
  'number-between': 'Nombre entre', 'number-outside': 'Nombre hors intervalle', 'date-before': 'Date avant', 'date-after': 'Date après', 'date-between': 'Date entre', 'date-outside': 'Date hors intervalle', 'last-days': 'Dans les derniers jours', 'next-days': 'Dans les prochains jours', 'age-at-least': 'Âge d’au moins (jours)' };
const bucketNames = { none: 'Valeur exacte', day: 'Jour', week: 'Semaine', month: 'Mois', quarter: 'Trimestre', year: 'Année' };
const functionNames = { add: 'Addition', subtract: 'Soustraction', multiply: 'Multiplication', divide: 'Division', 'days-between': 'Jours entre deux dates', 'age-days': 'Âge en jours', length: 'Longueur du texte', lower: 'Texte en minuscules', concat: 'Concaténation', coalesce: 'Première valeur renseignée' };
const pick = (values, names) => values.map(value => ({ value, label: names[value] ?? value }));
function freshQuery() { return { id: uuid(), name: 'Nouvelle requête', description: '', category: 'Personnalisées', baseProbe: 'total', match: 'all', aggregate: 'count', aggregateField: '', groupBy: '', groupBySecondary: '', groupByTertiary: '',
  groupDateBucket: 'none', secondaryDateBucket: 'none', tertiaryDateBucket: 'none', percentile: 90, favorite: false, conditionExpression: '', parameters: [], computedFields: [], conditions: [] }; }

export async function renderQueries(ctx) {
  const cleanup = heading(ctx, 'Atelier de requêtes', 'Construisez une requête typée sur les colonnes observées. Prévisualisez, expliquez les décisions puis enregistrez explicitement.');
  const library = await call(ctx, 'queries.list');
  let schema = { fields: [], total: 0, revision: getRevision(ctx) };
  if (ctx.state.session?.loaded) schema = await call(ctx, 'queries.fields');
  if (ctx.signal.aborted) return cleanup;
  let dirty = false; let query = null; let preview = null; let generation = 0;
  const search = input('', { placeholder: 'Nom, description ou catégorie' }); const favorite = checkbox('Favoris');
  const category = select([{ value: '', label: 'Toutes les catégories' }, ...[...new Set(library.queries.map(q => q.category))].sort().map(value => ({ value, label: value }))]);
  const side = el('aside', { class: 'analytics-query-library' }); const editor = el('section', { class: 'analytics-query-editor' });
  const results = el('section', { class: 'analytics-query-results' }); const libraryList = el('div');
  const switchTo = async next => {
    if (dirty && !await confirm('Brouillon modifié', 'Remplacer le brouillon non enregistré par la requête choisie ?', 'Remplacer')) return;
    query = next; dirty = false; preview = null; generation++; renderEditor();
  };
  const listLibrary = () => {
    const term = search.value.toLocaleLowerCase();
    const rows = library.queries.filter(q => (!category.value || q.category === category.value) && (!favorite.control.checked || q.favorite) && (!term || `${q.name} ${q.description} ${q.category}`.toLocaleLowerCase().includes(term)));
    libraryList.replaceChildren(...rows.map(q => command(ctx, `${q.favorite ? '★ ' : ''}${q.name}`, async () => switchTo(await call(ctx, 'queries.get', { id: q.id })), { title: `${q.category} · ${q.description}` })));
    if (!rows.length) libraryList.append(empty('Aucune requête ne correspond'));
  };
  search.addEventListener('input', listLibrary); category.addEventListener('change', listLibrary); favorite.control.addEventListener('change', listLibrary);
  side.append(field('Rechercher', search), field('Catégorie', category), favorite.element, caption(`${library.queries.length} / ${library.maximum} requêtes`), libraryList);
  ctx.root.append(toolbar(command(ctx, '+ Requête', () => switchTo(freshQuery()), { primary: true }),
    command(ctx, 'Packs métier', () => packsDialog()), command(ctx, 'Importer un pack', async () => previewPack(await call(ctx, 'queries.import.preview'))),
    command(ctx, 'Exporter la bibliothèque', async () => { const r = await call(ctx, 'queries.export'); if (!r.cancelled) ctx.notify(`Pack enregistré : ${r.path}`); }),
    navigationVisible('assistant', ctx.state.settings?.interface) ? command(ctx, 'Assistant de langage', () => ctx.navigate('assistant', { operation: 'query' })) : null),
    el('div', { class: 'analytics-query-workspace' }, side, editor), results);
  listLibrary();
  if (ctx.params?.assistantDraft) {
    const draft = await call(ctx, 'assistant.draft', { draftId: ctx.params.assistantDraft, revision: schema.revision });
    query = draft.query; dirty = true; editor.before(caption(`Proposition de l’assistant · brouillon non enregistré. ${draft.explanation}`, 'warning'));
  } else query = ctx.params?.queryId ? await call(ctx, 'queries.get', { id: ctx.params.queryId }) : library.queries.length ? await call(ctx, 'queries.get', { id: library.queries[0].id }) : freshQuery();
  renderEditor();

  function fieldSelect(value = '', allowEmpty = true) {
    const values = [...schema.fields, ...(query?.computedFields ?? []).map(f => ({ key: `calc:${f.key}`, label: `${f.label} · calculé` }))];
    if (value && !values.some(f => f.key === value)) values.push({ key: value, label: `${value} · champ conservé` });
    return select([...(allowEmpty ? [{ value: '', label: 'Aucun' }] : []), ...values.map(f => ({ value: f.key, label: f.label }))], value);
  }
  function renderEditor() {
    const draft = clone(query); const status = badge(dirty ? 'Brouillon non enregistré' : 'Définition enregistrée');
    const name = input(draft.name, { maxLength: 120 }); const description = textArea(draft.description); const categoryText = input(draft.category, { maxLength: 80 }); const star = checkbox('Favori', draft.favorite);
    const base = select(library.bases.map(b => ({ value: b.key, label: b.label })), draft.baseProbe); const aggregate = select(pick(library.aggregates, aggregateNames), draft.aggregate); const aggregateField = fieldSelect(draft.aggregateField);
    const percentile = input(draft.percentile, { type: 'number', min: 0, max: 100, step: .1 });
    const group1 = fieldSelect(draft.groupBy); const group2 = fieldSelect(draft.groupBySecondary); const group3 = fieldSelect(draft.groupByTertiary);
    const period1 = select(pick(library.dateBuckets, bucketNames), draft.groupDateBucket); const period2 = select(pick(library.dateBuckets, bucketNames), draft.secondaryDateBucket); const period3 = select(pick(library.dateBuckets, bucketNames), draft.tertiaryDateBucket);
    const match = select([{ value: 'all', label: 'Toutes les conditions (ET)' }, { value: 'any', label: 'Au moins une condition (OU)' }], draft.match);
    const expression = input(draft.conditionExpression, { placeholder: '(1 ET 2) OU NON 3' });
    const conditions = conditionEditor(draft.conditions); const parameters = parameterEditor(draft.parameters); const computed = computedEditor(draft.computedFields, () => read());
    const overrideHost = el('div', { class: 'analytics-form-grid' }); let overrides = new Map();
    const read = () => ({ ...draft, name: name.value, description: description.value, category: categoryText.value, favorite: star.control.checked, baseProbe: base.value,
      aggregate: aggregate.value, aggregateField: aggregateField.value, percentile: Number(percentile.value), groupBy: group1.value, groupBySecondary: group2.value, groupByTertiary: group3.value,
      groupDateBucket: period1.value, secondaryDateBucket: period2.value, tertiaryDateBucket: period3.value, match: match.value, conditionExpression: expression.value,
      conditions: conditions.read(), parameters: parameters.read(), computedFields: computed.read() });
    const change = () => { dirty = true; generation++; preview = null; status.textContent = 'Brouillon modifié · prévisualisation à relancer'; results.replaceChildren(); };
    const form = el('div', { class: 'analytics-query-form' },
      el('div', { class: 'analytics-form-grid' }, field('Nom', name), field('Catégorie', categoryText), field('Population de départ', base), field('Agrégat', aggregate), field('Champ de l’agrégat', aggregateField), field('Percentile', percentile)),
      field('Description', description), star.element, el('h3', { text: 'Conditions' }), field('Combinaison par défaut', match), conditions.element,
      field('Expression booléenne facultative', expression, 'Références aux conditions numérotées, AND/OR/NOT ou ET/OU/NON, et parenthèses. La syntaxe est validée en C#.'),
      el('h3', { text: 'Regroupements et sous-totaux' }), el('div', { class: 'analytics-form-grid' }, field('Premier niveau', group1), field('Période du premier niveau', period1), field('Deuxième niveau', group2), field('Période du deuxième niveau', period2), field('Troisième niveau', group3), field('Période du troisième niveau', period3)),
      caption('Chaque sous-total est recalculé sur ses lignes uniques. Les moyennes ne sont pas des moyennes de sous-groupes et les dénominateurs restent affichés.'),
      details('Paramètres nommés', parameters.element), details('Champs calculés autorisés', computed.element),
      details('Valeurs de paramètres pour cet aperçu', command(ctx, 'Préparer les valeurs', () => {
        overrides = new Map(); overrideHost.replaceChildren();
        for (const parameter of parameters.read()) { const control = input(parameter.defaultValue, { type: parameter.type === 'date' ? 'date' : 'text' }); overrides.set(parameter.key, control); overrideHost.append(field(parameter.label || parameter.key, control)); }
      }), overrideHost));
    form.addEventListener('input', change); form.addEventListener('change', change);
    const previewButton = command(ctx, 'Prévisualiser', async () => {
      const definition = read(); const captured = generation; const values = Object.fromEntries([...overrides].map(([key, control]) => [key, control.value]));
      const response = await call(ctx, 'queries.preview', { revision: schema.revision, query: definition, parameters: values });
      if (captured !== generation) { results.replaceChildren(caption('Le brouillon a changé pendant le calcul. Relancez la prévisualisation.', 'warning')); return; }
      preview = response; renderResult(response);
    }, { primary: true, disabled: !ctx.state.session?.loaded });
    editor.replaceChildren(toolbar(status, previewButton,
      command(ctx, 'Enregistrer', async () => { const definition = read(); await call(ctx, 'queries.save', { query: definition }); dirty = false; ctx.navigate('queries', { queryId: definition.id }); }),
      command(ctx, 'Dupliquer le brouillon', () => { query = { ...read(), id: uuid(), name: `${name.value} · copie`.slice(0, 120) }; dirty = true; generation++; renderEditor(); }),
      command(ctx, 'Supprimer', async () => {
        if (!library.queries.some(q => q.id === draft.id)) { query = freshQuery(); dirty = false; renderEditor(); return; }
        if (!await confirm('Supprimer la requête', 'Les widgets associés conserveront une source indisponible. Supprimer cette définition ?', 'Supprimer')) return;
        await call(ctx, 'queries.delete', { id: draft.id }); dirty = false; ctx.refresh();
      }, { danger: true })), form);
    if (!ctx.state.session?.loaded) editor.prepend(caption('Chargez un relevé pour prévisualiser. Les définitions peuvent être préparées et enregistrées sans données.', 'warning'));
    if (schema.total > schema.fields.length) editor.append(field('Chercher une colonne au-delà des 512 premières', input('', { placeholder: 'Recherche exacte ou partielle', onChange: async event => {
      try { const found = await call(ctx, 'queries.fields', { search: event.target.value }); schema.fields = [...new Map([...schema.fields, ...found.fields].map(f => [f.key, f])).values()]; query = read(); renderEditor(); }
      catch (reason) { error(ctx, reason); }
    } })));
  }

  function conditionEditor(initial) {
    const list = el('div'); const rows = [];
    const add = condition => {
      if (rows.length >= 20) throw new Error('Vingt conditions au maximum.');
      const column = fieldSelect(condition.field ?? 'role:title', false); const operator = select(pick(library.operators, operatorNames), condition.operator ?? 'contains');
      const value = input(condition.value ?? ''); const value2 = input(condition.value2 ?? '');
      const source = select([{ value: 'literal', label: 'Constante' }, { value: 'field', label: 'Autre champ (clé)' }, { value: 'parameter', label: 'Paramètre (nom)' }], condition.valueSource ?? 'literal');
      const source2 = select([{ value: 'literal', label: 'Constante' }, { value: 'field', label: 'Autre champ (clé)' }, { value: 'parameter', label: 'Paramètre (nom)' }], condition.value2Source ?? 'literal');
      const label = el('strong'); const line = el('div', { class: 'analytics-condition' });
      const item = { read: () => ({ field: column.value, operator: operator.value, value: value.value, value2: value2.value, valueSource: source.value, value2Source: source2.value }), label };
      line.append(label, el('div', { class: 'analytics-form-grid' }, field('Champ', column), field('Opérateur', operator), field('Source de la valeur', source), field('Valeur', value), field('Source de la borne haute', source2), field('Borne haute', value2)),
        button('Retirer la condition', () => { line.remove(); rows.splice(rows.indexOf(item), 1); renumber(); list.dispatchEvent(new Event('change', { bubbles: true })); }));
      rows.push(item); list.append(line); renumber();
    };
    const renumber = () => rows.forEach((r, i) => r.label.textContent = `Condition ${i + 1}`);
    const element = el('div', {}, list, command(ctx, '+ Condition', () => { add({}); element.dispatchEvent(new Event('change', { bubbles: true })); }));
    initial.forEach(add); return { element, read: () => rows.map(r => r.read()) };
  }
  function parameterEditor(initial) {
    const list = el('div'); const rows = [];
    const add = value => {
      if (rows.length >= 12) throw new Error('Douze paramètres au maximum.');
      const key = input(value.key ?? `parametre${rows.length + 1}`); const label = input(value.label ?? 'Paramètre');
      const type = select([{ value: 'text', label: 'Texte' }, { value: 'number', label: 'Nombre' }, { value: 'date', label: 'Date' }], value.type ?? 'text'); const fallback = input(value.defaultValue ?? '');
      const line = el('div', { class: 'analytics-form-grid' }); const row = { read: () => ({ key: key.value, label: label.value, type: type.value, defaultValue: fallback.value }) };
      line.append(field('Nom technique', key), field('Libellé', label), field('Type', type), field('Valeur par défaut', fallback), button('Retirer', () => { line.remove(); rows.splice(rows.indexOf(row), 1); list.dispatchEvent(new Event('change', { bubbles: true })); })); rows.push(row); list.append(line);
    };
    const element = el('div', {}, list, command(ctx, '+ Paramètre', () => { add({}); element.dispatchEvent(new Event('change', { bubbles: true })); })); initial.forEach(add);
    return { element, read: () => rows.map(r => r.read()) };
  }
  function computedEditor(initial, readDraft) {
    const list = el('div'); const rows = [];
    const add = value => {
      if (rows.length >= 12) throw new Error('Douze champs calculés au maximum.');
      const key = input(value.key ?? `calcul${rows.length + 1}`); const label = input(value.label ?? 'Champ calculé'); const fn = select(pick(library.functions, functionNames), value.function ?? 'age-days');
      const first = fieldSelect(value.field ?? 'role:created', false); const second = fieldSelect(value.otherField ?? ''); const constant = input(value.constant ?? ''); const unit = input(value.unit ?? '');
      const line = el('div', { class: 'analytics-computed' }); const row = { read: () => ({ key: key.value, label: label.value, function: fn.value, field: first.value, otherField: second.value, constant: constant.value, unit: unit.value }) };
      line.append(el('div', { class: 'analytics-form-grid' }, field('Nom technique', key), field('Libellé', label), field('Fonction', fn), field('Premier champ', first), field('Second champ', second), field('Constante (si pas de second champ)', constant), field('Unité', unit)),
        button('Retirer', () => { line.remove(); rows.splice(rows.indexOf(row), 1); list.dispatchEvent(new Event('change', { bubbles: true })); })); rows.push(row); list.append(line);
    };
    const element = el('div', {}, list, command(ctx, '+ Champ calculé', () => { add({}); element.dispatchEvent(new Event('change', { bubbles: true })); }),
      caption('Pour utiliser un champ ajouté dans une autre liste, appliquez les champs au brouillon. Les cycles et dépendances de plus de huit niveaux sont refusés.'),
      command(ctx, 'Appliquer les champs au brouillon', () => { query = { ...readDraft(), computedFields: rows.map(r => r.read()) }; dirty = true; generation++; renderEditor(); })); initial.forEach(add);
    return { element, read: () => rows.map(r => r.read()) };
  }

  function renderResult(result) {
    const args = { runId: result.runId, revision: result.revision };
    const header = card('Prévisualisation calculée', toolbar(badge(result.available ? 'Disponible' : 'Indisponible', result.available ? 'success' : 'warning'),
      el('strong', { text: number(result.metric.value, result.metric.unit) })), caption(result.message), caption(result.metric.explanation),
      caption(`${formatNumber(result.rows)} lignes retenues · ${formatNumber(result.unknownRows)} décisions inconnues · relevé ${formatDate(result.extractedAt)} · calcul ${formatDate(result.calculatedAt)}`));
    results.replaceChildren(header);
    if (!result.available) return;
    const rowNumber = input(1, { type: 'number', min: 1, max: ctx.state.session.rows });
    header.append(toolbar(command(ctx, 'Lignes retenues', () => rowDialog(ctx, 'queries.rows', args, 'Lignes retenues par la requête', args)),
      command(ctx, 'Exporter les résultats', () => {
        const format = select([{ value: 'xlsx', label: 'Excel' }, { value: 'csv', label: 'CSV' }]); const hierarchy = checkbox('Exporter les sous-totaux plutôt que les lignes source');
        dialog('Exporter cette prévisualisation', el('div', {}, field('Format', format), hierarchy.element,
          caption('L’export porte sur toutes les lignes du résultat, au-delà de la page affichée.')), [{ label: 'Exporter', primary: true, action: async close => {
            const exported = await call(ctx, 'queries.results.export', { ...args, format: format.value, hierarchy: hierarchy.control.checked });
            if (!exported.cancelled) { close(); ctx.notify(`${exported.rows} lignes exportées : ${exported.path}`); }
          } }]);
      }),
      command(ctx, 'Ajouter un widget', async () => {
        if (dirty || !library.queries.some(q => q.id === result.definition.id)) throw new Error('Enregistrez d’abord la définition vérifiée avant de l’utiliser dans un dashboard.');
        const personal = checkbox('Disposition personnelle'); const grouped = checkbox('Widget de répartition', !!result.definition.groupBy);
        dialog('Ajouter la requête au dashboard actif', el('div', {}, personal.element, grouped.element), [{ label: 'Ajouter', primary: true, action: async close => {
          const added = await call(ctx, 'queries.widget.add', { queryId: result.definition.id, personal: personal.control.checked, grouped: grouped.control.checked }); close(); ctx.notify('Widget ajouté à la disposition sélectionnée.');
          if (!personal.control.checked) ctx.navigate('dashboard', { boardId: added.boardId });
        } }]);
      }), field('Expliquer la ligne', rowNumber), command(ctx, 'Expliquer cette décision', async () => {
        const explanation = await call(ctx, 'queries.explain', { ...args, rowIndex: Number(rowNumber.value) - 1 });
        dialog('Explication ligne par ligne', el('div', {}, badge(explanation.decision), caption(explanation.logic), caption(explanation.message), table(explanation.conditions,
          [{ key: 'number', label: 'N°' }, { key: 'field', label: 'Champ' }, { key: 'comparison', label: 'Comparaison' }, { key: 'decision', label: 'Décision' }, { key: 'explanation', label: 'Explication' }])));
      })));
    if (!result.groups) return;
    const hierarchy = checkbox('Afficher les sous-totaux hiérarchiques', true); const groupHost = el('div'); results.append(card('Regroupements', hierarchy.element, groupHost));
    const load = async offset => {
      const page = await call(ctx, 'queries.results', { ...args, offset, hierarchy: hierarchy.control.checked, limit: 100 });
      if (preview?.runId !== result.runId) return;
      groupHost.replaceChildren(table(page.rows, [
        { key: 'label', label: 'Groupe', render: row => el('span', { style: { paddingInlineStart: `${Math.max(0, row.path.length - 1) * 20}px` }, text: row.path.join(' › ') || row.label }) },
        { key: 'count', label: 'Lignes uniques' }, { key: 'value', label: 'Valeur', render: row => number(row.value, row.unit) }, { key: 'sample', label: 'Échantillon' }, { key: 'denominator', label: 'Dénominateur' },
        { key: 'details', label: 'Détails', render: row => toolbar(command(ctx, 'Lignes', () => rowDialog(ctx, 'queries.rows', { ...args, hierarchy: hierarchy.control.checked, groupIndex: row.index }, row.label, args)),
          command(ctx, 'Contributeurs mesurables', () => rowDialog(ctx, 'queries.rows', { ...args, hierarchy: hierarchy.control.checked, groupIndex: row.index, contributors: true }, row.label + ' · contributeurs mesurables', args))) }
      ], { maxRows: 100 }), pager(ctx, page.total, page.offset, 100, load));
    };
    hierarchy.control.addEventListener('change', () => load(0).catch(reason => error(ctx, reason))); load(0).catch(reason => error(ctx, reason));
  }
  function packsDialog() {
    dialog('Packs métier disponibles', table(library.packs, [{ key: 'name', label: 'Métier' }, { key: 'count', label: 'Requêtes' }, { key: 'description', label: 'Description' },
      { key: 'open', label: 'Action', render: pack => command(ctx, 'Prévisualiser', async () => previewPack(await call(ctx, 'queries.import.preview', { builtIn: pack.name }))) }]));
  }
  function previewPack(preview) {
    if (preview.cancelled) return;
    dialog('Vérifier les ajouts du pack', el('div', {}, el('h3', { text: preview.name }), caption(preview.description),
      caption(`${preview.added} ajouts · ${preview.identical} identiques ignorées · ${preview.conflicts} conflits renommés et dotés d’un nouvel identifiant. Aucune définition existante n’est écrasée.`),
      el('ul', {}, ...preview.names.map(name => el('li', { text: name })))), [{ label: 'Ajouter ces définitions', primary: true, action: async close => {
        await call(ctx, 'queries.import.commit', { previewId: preview.previewId }); close(); ctx.refresh();
      } }]);
  }
  return cleanup;
}
