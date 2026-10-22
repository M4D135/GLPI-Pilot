import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption, details,
  number, formatNumber, formatDate, rowDialog, pager, getRevision, svg, error } from './analytics-ui.js';
import { confirm } from '../ui.js';

const sections = { movements: 'Mouvements observés', transfers: 'Flux d’affectation', calendar: 'Calendrier observé', cohorts: 'Cohortes J+1 / J+7 / J+30',
  durations: 'Durées conditionnelles par statut', intervals: 'Intervalles de statut', variations: 'Variations expliquées', anomalies: 'Variations inhabituelles', scenarios: 'Scénarios du stock' };
const columns = {
  movements: [['from', 'Du', 'date'], ['to', 'Au', 'date'], ['appeared', 'Apparus'], ['absent', 'Absents'], ['balance', 'Solde'], ['resolved', 'Résolutions observées'], ['reopened', 'Réouvertures'], ['changed', 'Modifiés'], ['comparable', 'Comparable', 'bool'], ['note', 'Méthode / rupture', 'text']],
  calendar: [['date', 'Date', 'date'], ['observations', 'Collectes'], ['changesObserved', 'Changements constatés'], ['gap', 'Collecte absente', 'bool'], ['note', 'Couverture', 'text']],
  transfers: [['dimension', 'Dimension', 'text'], ['before', 'Avant', 'text'], ['after', 'Après', 'text'], ['count', 'Tickets distincts']],
  cohorts: [['date', 'Première observation', 'date'], ['initial', 'Effectif initial'], ['horizonDays', 'J +'], ['active', 'Actifs'], ['resolvedOrClosed', 'Résolus / clos'], ['absent', 'Absents'], ['unknown', 'Inconnus'], ['observation', 'Observation cible', 'date'], ['note', 'Interprétation', 'text']],
  durations: [['ticket', 'Ticket', 'text'], ['status', 'Statut', 'text'], ['minimumHours', 'Borne basse (h)'], ['maximumHours', 'Borne haute (h)'], ['intervals', 'Intervalles'], ['assumption', 'Hypothèse nécessaire', 'text']],
  intervals: [['ticket', 'Ticket', 'text'], ['before', 'Statut avant', 'text'], ['after', 'Statut après', 'text'], ['from', 'Du', 'date'], ['to', 'Au', 'date'], ['maximumHours', 'Borne maximale (h)'], ['note', 'Incertitude, borne minimale 0 h', 'text']],
  variations: [['label', 'Indicateur', 'text'], ['before', 'Avant'], ['after', 'Après'], ['enteredContributors', 'Entrées'], ['leftContributors', 'Sorties'], ['changedContributors', 'Changements internes'], ['explainedDelta', 'Écart expliqué'], ['note', 'Décomposition', 'text']],
  anomalies: [['at', 'Observation', 'date'], ['metric', 'Indicateur', 'text'], ['value', 'Valeur'], ['referenceMedian', 'Médiane de référence'], ['robustDeviation', 'Écart robuste'], ['unusual', 'Signal', 'bool'], ['note', 'Référence', 'text']],
  scenarios: [['horizonDays', 'Horizon (jours)'], ['central', 'Scénario central'], ['lower', 'Borne basse'], ['upper', 'Borne haute'], ['sample', 'Échantillon'], ['note', 'Hypothèses', 'text']]
};

export async function renderHistory(ctx) {
  const cleanup = heading(ctx, 'Historique des relevés', 'Retrouvez les observations telles qu’elles ont été collectées. Une disparition d’un relevé ne prouve ni une résolution ni une clôture.');
  const catalogue = el('div'); const comparison = el('div'); const analysis = el('div'); const ticket = el('div');
  const pages = new Map([['catalogue', catalogue], ['compare', comparison], ['analysis', analysis], ['ticket', ticket]]);
  const switchTab = name => { for (const [key, node] of pages) node.hidden = key !== name; };
  const nav = toolbar(...[['catalogue', 'Relevés conservés'], ['compare', 'Comparer'], ['analysis', 'Analyses historiques'], ['ticket', 'Historique d’un ticket']].map(([name, label]) => button(label, () => switchTab(name))),
    command(ctx, 'Effectifs des sites', () => ctx.navigate('populations')));
  ctx.root.append(nav, catalogue, comparison, analysis, ticket); switchTab(ctx.params?.ticketId ? 'ticket' : 'catalogue');
  let catalogueData = null; const known = new Map(); let selectedSource = ctx.params?.sourceKey ?? ''; let currentComparison = null; let report = null;
  const source = select([{ value: '', label: 'Toutes les sources' }]);
  source.addEventListener('change', () => { selectedSource = source.value; loadCatalogue(0).catch(reason => error(ctx, reason)); });
  const before = select([]); const after = select([]); const analysisSource = select([]); const ticketSource = select([]);
  const catalogueBody = el('div'); const changeBody = el('div'); const analysisBody = el('div'); const ticketBody = el('div');
  const search = input('', { placeholder: 'Ticket, champ, valeur ou évolution' }); const kind = select([{ value: '', label: 'Toutes les évolutions' }]);
  const compareSummary = el('div');
  catalogue.append(toolbar(field('Source / contexte', source), command(ctx, 'Actualiser', () => loadCatalogue(0)),
    command(ctx, 'Archiver le relevé actuel', async () => { const receipt = await call(ctx, 'history.archive', { revision: getRevision(ctx) }); ctx.notify(`Relevé archivé : ${formatDate(receipt.capturedAt)}`); await loadCatalogue(0); }, { disabled: !ctx.state.session?.loaded || ctx.state.session?.historical || ctx.state.session?.demo })), catalogueBody);
  comparison.append(toolbar(field('Avant', before), field('Après', after), command(ctx, 'Comparer', async () => {
    currentComparison = await call(ctx, 'history.compare', { beforeId: before.value, afterId: after.value });
    compareSummary.replaceChildren(badge(currentComparison.compatible ? 'Relevés comparables' : 'Comparaison métier indisponible', currentComparison.compatible ? 'success' : 'warning'),
      el('p', { text: currentComparison.summary }), caption(`${currentComparison.resolutionsObserved} résolutions observées · ${currentComparison.reopeningsObserved} réouvertures · ${currentComparison.reassignmentsObserved} réaffectations`),
      ...currentComparison.warnings.map(w => caption(w, 'warning')), caption(`Colonnes ajoutées : ${currentComparison.addedColumns.join(', ') || 'aucune'} · retirées : ${currentComparison.removedColumns.join(', ') || 'aucune'}`),
      ...currentComparison.truncated ? [caption('La liste des changements atteint sa limite ; le détail affiché est tronqué.', 'warning')] : []);
    await loadChanges(0); switchTab('compare');
  }, { primary: true }), command(ctx, 'Vérifier les absents dans GLPI', async () => {
    if (!currentComparison) throw new Error('Comparez deux relevés avant de préparer cette vérification.');
    const refs = await call(ctx, 'history.absent.references', { comparisonId: currentComparison.comparisonId, revision: getRevision(ctx) });
    if (!refs.references.length) throw new Error('Cette comparaison ne contient aucun absent vérifiable.');
    await call(ctx, 'workplace.enrichment.prepare', refs); ctx.navigate('enrichment');
  })), compareSummary, toolbar(search, kind, command(ctx, 'Filtrer les différences', () => loadChanges(0))), changeBody);

  const count = input(30, { type: 'number', min: 2, max: 120 }); const cadence = input(24, { type: 'number', min: 1, max: 720 });
  const section = select(Object.entries(sections).map(([value, label]) => ({ value, label })), 'movements'); const analysisSummary = el('div'); const progress = caption('');
  const unsubscribe = ctx.api.on('analytics.progress', data => { if (data.operation === 'history') progress.textContent = `Chargement ${data.done} / ${data.total} archives…`; });
  let analysisAbort = null;
  analysis.append(toolbar(field('Dernier relevé de la sélection', analysisSource), field('Nombre de relevés', count), field('Cadence attendue (h)', cadence),
    command(ctx, 'Analyser', async () => {
      analysisAbort?.abort(); analysisAbort = new AbortController(); progress.textContent = 'Vérification et lecture des archives…';
      report = await ctx.api.call('history.analyze', { snapshotId: analysisSource.value, count: Number(count.value), cadenceHours: Number(cadence.value) }, { signal: AbortSignal.any([ctx.signal, analysisAbort.signal]) });
      progress.textContent = 'Analyse terminée.';
      analysisSummary.replaceChildren(el('strong', { text: `${report.snapshotCount} relevés · ${report.comparablePairs} intervalles comparables · ${report.brokenPairs} ruptures` }),
        caption(`Couverture temporelle : ${number(report.coveragePercent, '%')} · ${formatDate(report.firstObservation)} → ${formatDate(report.lastObservation)} · âge du dernier relevé : ${number(report.freshnessHours, 'h')}`),
        ...report.warnings.map(w => caption(w, 'warning'))); await loadAnalysis(0); switchTab('analysis');
    }, { primary: true }), button('Annuler l’analyse', () => { analysisAbort?.abort(); progress.textContent = 'Annulation demandée.'; })),
    progress, analysisSummary, field('Analyse', section), analysisBody);
  section.addEventListener('change', () => loadAnalysis(0).catch(reason => error(ctx, reason)));
  const ticketId = input(ctx.params?.ticketId ?? '', { placeholder: 'Identifiant numérique du ticket' });
  ticket.append(toolbar(field('Archive du contexte recherché', ticketSource), field('Identifiant', ticketId), command(ctx, 'Rechercher les observations', async () => {
    const selection = known.get(ticketSource.value); const sourceKey = selection?.sourceKey || ctx.params?.sourceKey || selectedSource || catalogueData.currentSourceKey;
    if (!sourceKey) throw new Error('Choisissez une archive du contexte GLPI recherché.');
    const rows = await call(ctx, 'history.ticket', { sourceKey, ticketId: ticketId.value });
    ticketBody.replaceChildren(caption(`${rows.length} observations indexées. Les archives restent la source de vérité ; les index manquants ou corrompus sont reconstruits.`),
      table(rows, [{ key: 'capturedAt', label: 'Observation', render: r => formatDate(r.capturedAt) }, { key: 'ticketId', label: 'Ticket' }, { key: 'status', label: 'Statut constaté' }, { key: 'assignee', label: 'Affectation constatée' },
        { key: 'comparable', label: 'Comparable', render: r => r.comparable ? 'Oui' : 'Non' }, { key: 'open', label: 'Archive', render: r => command(ctx, 'Ouvrir', () => openArchive(r.snapshotId)) }], { maxRows: 500 }));
  }, { primary: true })), ticketBody);

  async function openArchive(id, rowIndex = null) {
    const response = await call(ctx, 'history.open', { id }); ctx.navigate(rowIndex == null ? 'records' : 'tickets', { rowIndex, revision: response.revision });
  }
  function fillArchiveChoices(control, preferred) {
    const current = control.value || preferred;
    control.replaceChildren(...[...known.values()].sort((a, b) => Date.parse(b.capturedAt) - Date.parse(a.capturedAt)).map(m => el('option', { value: m.id, text: `${formatDate(m.capturedAt)} · ${m.rows} lignes · ${m.sourceLabel}` })));
    if (known.has(current)) control.value = current;
  }
  async function loadCatalogue(offset) {
    catalogueData = await call(ctx, 'history.list', { sourceKey: selectedSource, offset, limit: 100 });
    source.replaceChildren(el('option', { value: '', text: 'Toutes les sources' }), ...catalogueData.sources.map(s => el('option', { value: s.key, text: s.label }))); source.value = selectedSource;
    for (const metadata of catalogueData.rows) known.set(metadata.id, metadata);
    fillArchiveChoices(before, catalogueData.rows[1]?.id); fillArchiveChoices(after, catalogueData.rows[0]?.id); fillArchiveChoices(analysisSource, catalogueData.rows[0]?.id);
    fillArchiveChoices(ticketSource, catalogueData.rows[0]?.id);
    catalogueBody.replaceChildren(table(catalogueData.rows, [
      { key: 'capturedAt', label: 'Collecte', render: r => formatDate(r.capturedAt) }, { key: 'sourceLabel', label: 'Source' }, { key: 'rows', label: 'Lignes' }, { key: 'state', label: 'Comparabilité' }, { key: 'method', label: 'Méthode' },
      { key: 'protected', label: 'Rétention', render: r => command(ctx, r.protected ? 'Protégée · libérer' : 'Conserver', async () => { await call(ctx, 'history.protect', { id: r.id, protect: !r.protected }); await loadCatalogue(offset); }) },
      { key: 'actions', label: 'Actions', render: r => toolbar(command(ctx, 'Ouvrir', () => openArchive(r.id)), command(ctx, 'Exporter', () => {
        const format = select([{ value: 'xlsx', label: 'Excel' }, { value: 'csv', label: 'CSV' }], 'xlsx');
        dialog('Exporter cette archive', field('Format', format), [{ label: 'Exporter', primary: true, action: async close => { const output = await call(ctx, 'history.export', { id: r.id, format: format.value }); if (!output.cancelled) { close(); ctx.notify(`Archive exportée : ${output.path}`); } } }]);
      }), command(ctx, 'Utiliser avant', () => { before.value = r.id; switchTab('compare'); }), command(ctx, 'Utiliser après', () => { after.value = r.id; switchTab('compare'); })) }
    ], { key: 'id', maxRows: 100 }), pager(ctx, catalogueData.total, catalogueData.offset, 100, loadCatalogue),
      caption(`Rétention configurée : ${catalogueData.retention.retentionDays} jours, ${catalogueData.retention.maximumSnapshots} relevés. Les archives protégées sont conservées.`));
  }
  async function loadChanges(offset) {
    if (!currentComparison) { changeBody.replaceChildren(empty('Choisissez deux relevés puis lancez la comparaison')); return; }
    const result = await call(ctx, 'history.changes', { comparisonId: currentComparison.comparisonId, kind: kind.value, search: search.value, offset, limit: 100 });
    const selectedKind = kind.value; kind.replaceChildren(el('option', { value: '', text: 'Toutes les évolutions' }), ...result.kinds.map(value => el('option', { value, text: value }))); kind.value = selectedKind;
    changeBody.replaceChildren(table(result.rows, [{ key: 'kind', label: 'Évolution' }, { key: 'ticketId', label: 'Ticket' }, { key: 'field', label: 'Champ' }, { key: 'before', label: 'Avant' }, { key: 'after', label: 'Après' },
      { key: 'open', label: 'Observation', render: change => toolbar(change.previousRow == null ? null : command(ctx, 'Avant', () => openArchive(currentComparison.before.id, change.previousRow)),
        change.currentRow == null ? null : command(ctx, 'Après', () => openArchive(currentComparison.after.id, change.currentRow))) }], { maxRows: 100 }),
      pager(ctx, result.total, result.offset, 100, loadChanges));
  }
  async function loadAnalysis(offset) {
    if (!report) { analysisBody.replaceChildren(empty('Choisissez une période puis lancez l’analyse')); return; }
    const kind = section.value; const result = await call(ctx, 'history.analysis.page', { reportId: report.reportId, section: kind, offset, limit: 100 });
    const defs = columns[kind].map(([key, label, type]) => ({ key, label, render: row => type === 'text' ? row[key] : type === 'date' ? formatDate(row[key]) : type === 'bool' ? row[key] ? 'Oui' : 'Non' : number(row[key]) }));
    if (kind === 'transfers') defs.push({ key: 'details', label: 'Contributeurs', render: row => command(ctx, 'Tickets présents dans le relevé courant', () => rowDialog(ctx, 'history.analysis.rows',
      { reportId: report.reportId, revision: getRevision(ctx), transferIndex: row.index }, `${row.before} → ${row.after} · présents dans le relevé courant`)) });
    const visual = kind === 'calendar' ? activityCalendar(result.rows) : kind === 'transfers' ? transferDiagram(ctx, result.rows, row => rowDialog(ctx, 'history.analysis.rows',
      { reportId: report.reportId, revision: getRevision(ctx), transferIndex: row.index }, 'Contributeurs observés présents dans le relevé courant')) : kind === 'scenarios' ? scenarioChart(result.rows) : null,
      grid = table(result.rows, defs, { maxRows: 100 });
    analysisBody.replaceChildren(...[visual, grid, pager(ctx, result.total, result.offset, 100, loadAnalysis)].filter(Boolean));
  }
  await loadCatalogue(0);
  return () => { analysisAbort?.abort(); unsubscribe(); cleanup(); };
}

function activityCalendar(days) {
  const maximum = Math.max(1, ...days.filter(d => !d.gap).map(d => d.changesObserved));
  return el('div', {}, caption('Couleur : changements constatés à la collecte. Les jours sans collecte restent inconnus, sans interpolation.'),
    el('div', { class: 'analytics-activity-calendar' }, ...days.map(day => el('div', { class: `analytics-activity-day${day.gap ? ' gap' : ''}`, style: { '--intensity': day.gap ? 0 : Math.max(.08, day.changesObserved / maximum) },
      title: `${formatDate(day.date)} · ${day.note}` }, el('strong', { text: new Date(day.date).toLocaleDateString('fr-FR', { day: 'numeric', month: 'short' }) }),
      el('span', { text: day.gap ? 'Inconnu' : `${day.changesObserved} changements` }), el('small', { text: `${day.observations} collecte(s)` })))));
}
function transferDiagram(ctx, rows, open) {
  const selected = rows.slice(0, 12); if (!selected.length) return empty('Aucun transfert comparable observé');
  const max = Math.max(1, ...selected.map(r => r.count)); const drawing = svg('svg', { viewBox: `0 0 900 ${selected.length * 48 + 20}`, role: 'img', 'aria-label': 'Flux observés entre affectations complètes, sans décomposer artificiellement les cellules multitechniciens' });
  selected.forEach((flow, i) => {
    const y = 28 + i * 48; const group = svg('g', { tabindex: 0, role: 'button', 'aria-label': `${flow.before} vers ${flow.after}, ${flow.count} tickets` },
      svg('text', { x: 0, y, fill: 'currentColor', 'font-size': 12 }, flow.before.slice(0, 28)),
      svg('path', { d: `M210 ${y - 4} C340 ${y - 18} 550 ${y + 10} 675 ${y - 4}`, fill: 'none', stroke: 'var(--accent, #066ddb)', 'stroke-width': 2 + 14 * flow.count / max, opacity: .65 }),
      svg('text', { x: 430, y: y - 13, fill: 'currentColor', 'font-size': 12 }, String(flow.count)),
      svg('text', { x: 690, y, fill: 'currentColor', 'font-size': 12 }, flow.after.slice(0, 28)), svg('title', {}, `${flow.dimension} · ${flow.before} → ${flow.after}`));
    const activate = () => open(flow).catch(reason => error(ctx, reason)); group.addEventListener('click', activate); group.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(); } }); drawing.append(group);
  });
  return el('div', { class: 'analytics-transfer-diagram' }, caption(`Premiers ${selected.length} flux de la page ; le tableau expose tous les résultats paginés.`), drawing);
}
function scenarioChart(rows) {
  const valid = rows.filter(r => r.central != null && r.lower != null && r.upper != null);
  if (!valid.length) return empty('Pas de scénario calculable', rows[0]?.note ?? 'Au moins huit observations comparables continues couvrant trois jours sont nécessaires.');
  const max = Math.max(1, ...valid.map(r => r.upper)); const drawing = svg('svg', { viewBox: '0 0 640 260', role: 'img', 'aria-label': 'Scénarios du stock, points centraux et bandes prédictives conditionnelles' });
  const y = value => 210 - 180 * value / max;
  valid.forEach((row, index) => { const x = 100 + index * 210; drawing.append(svg('line', { x1: x, x2: x, y1: y(row.lower), y2: y(row.upper), stroke: 'var(--accent, #066ddb)', 'stroke-width': 18, opacity: .3 }),
    svg('circle', { cx: x, cy: y(row.central), r: 5, fill: 'var(--accent, #066ddb)' }, svg('title', {}, `J+${row.horizonDays} : ${number(row.central)} [${number(row.lower)} ; ${number(row.upper)}]`)),
    svg('text', { x, y: 240, 'text-anchor': 'middle', fill: 'currentColor' }, `J+${row.horizonDays}`)); });
  return el('div', {}, drawing, caption('Scénarios conditionnels si la tendance linéaire continue. Les bandes décrivent le modèle, pas une certitude sur le stock futur.'));
}

export async function renderPopulations(ctx) {
  const cleanup = heading(ctx, 'Effectifs et postes par site', 'Inventaire local daté pour les taux de tickets pour 100 unités. Les dénominateurs absents, périmés ou ambigus restent inconnus.');
  const host = el('div'); let current = null;
  ctx.root.append(toolbar(command(ctx, '+ Période d’effectif', () => edit(null), { primary: true }), command(ctx, 'Actualiser', () => load(0)),
    command(ctx, 'Importer un référentiel', async () => {
      const preview = await call(ctx, 'populations.import.preview'); if (preview.cancelled) return;
      dialog('Vérifier l’inventaire importé', el('div', {}, caption(`${preview.added} nouvelles périodes · ${preview.replaced} remplacements de même site/unité/date · ${preview.identical} identiques ignorées.`),
        caption(`${preview.previewRows} périodes dans le fichier ; les 200 premières sont visibles ci-dessous.`), table(preview.rows, populationColumns(), { maxRows: 200 })),
        [{ label: 'Enregistrer cet inventaire', primary: true, action: async close => { await call(ctx, 'populations.import.commit', { previewId: preview.previewId }); close(); await load(0); } }]);
    }), command(ctx, 'Exporter le référentiel', async () => { const r = await call(ctx, 'populations.export'); if (!r.cancelled) ctx.notify(`Inventaire exporté : ${r.path}`); })), host);
  async function load(offset) {
    current = await call(ctx, 'populations.list', { offset, limit: 100 });
    host.replaceChildren(caption(current.note), current.sourceKey ? badge('Contexte du relevé actif') : caption('Aucun contexte confirmé : chargez un relevé vérifié pour associer des effectifs.', 'warning'),
      table(current.rows, [...populationColumns(), { key: 'edit', label: 'Actions', render: entry => toolbar(command(ctx, 'Modifier', () => edit(entry)),
        command(ctx, 'Retirer', async () => {
          if (!await confirm('Retirer cette période', `Retirer l’effectif ${entry.count} ${entry.unitLabel} de ${entry.siteLabel}, du ${entry.fromDate} au ${entry.untilDate} ?`, 'Retirer')) return;
          await call(ctx, 'populations.delete', { entry }); await load(offset);
        }, { danger: true })) }], { maxRows: 100 }), pager(ctx, current.total, current.offset, 100, load));
  }
  function edit(previous) {
    if (!current?.sourceKey) throw new Error('Chargez un relevé au contexte confirmé avant de définir des effectifs.');
    if (!current.sites.length) throw new Error('Créez d’abord les sites dans le référentiel géographique.');
    const site = select(current.sites.map(s => ({ value: s.id, label: s.name })), previous?.siteId ?? current.sites[0].id);
    const unit = select([{ value: 'people', label: 'Personnes' }, { value: 'workstations', label: 'Postes' }], previous?.unit ?? 'people');
    const count = input(previous?.count ?? '', { type: 'number', min: 1, max: 1000000000, step: 1 }); const today = new Date().toISOString().slice(0, 10);
    const from = input(previous?.fromDate ?? today, { type: 'date' }); const until = input(previous?.untilDate ?? today, { type: 'date' }); const reference = input(previous?.reference ?? '', { maxLength: 1000 });
    dialog(previous ? 'Modifier la période d’effectif' : 'Ajouter un effectif daté', el('div', { class: 'analytics-form-grid' }, field('Site', site), field('Unité', unit), field('Effectif entier positif', count),
      field('Valide à partir du', from), field('Valide jusqu’au inclus', until), field('Référence de l’inventaire', reference)), [{ label: 'Enregistrer', primary: true, action: async close => {
        const selected = current.sites.find(s => s.id === site.value); const entry = { sourceKey: current.sourceKey, siteId: site.value, siteLabel: selected.name, unit: unit.value,
          count: Number(count.value), fromDate: from.value, untilDate: until.value, reference: reference.value };
        await call(ctx, 'populations.upsert', { revision: current.revision, entry, previous }); close(); await load(0);
      } }]);
  }
  await load(0); return cleanup;
}
function populationColumns() { return [{ key: 'siteLabel', label: 'Site' }, { key: 'unitLabel', label: 'Unité', render: e => e.unit === 'people' ? 'Personnes' : 'Postes' },
  { key: 'count', label: 'Effectif' }, { key: 'fromDate', label: 'Du' }, { key: 'untilDate', label: 'Au inclus' }, { key: 'reference', label: 'Référence' }]; }
