import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption, details, choices, clone, uuid,
  number, formatNumber, formatDate, rowDialog, pager, filterEditor, checkbox, nullableNumber, textArea, sparkline, error } from './analytics-ui.js';
import { widgetContent } from './analytics-charts.js';
import { ensureSections, moveDashboardWidget, moveDashboardSection, bindDashboardLayout } from './analytics-layout.js';
import { renderOldestGroups } from './analytics-oldest.js';
import { participationTable, participationRosterEditor } from './analytics-participation.js';
import { sitesTable } from './analytics-sites.js';

const kindNames = { metric: 'Indicateur', distribution: 'Répartition', weather: 'Météo', timeline: 'Chronologie', attention: 'Points d’attention', quality: 'Qualité',
  note: 'Note', matrix: 'Matrice', histogram: 'Histogramme', boxplot: 'Boîtes à moustaches', scatter: 'Nuage de points', history: 'Historique', map: 'Carte locale',
  oldest: 'Plus anciens', participation: 'Participation des agents', sites: 'Stock des sites' };
const presetNames = { empty: 'Vide', overview: 'Pilotage', service: 'Météo et pilotage du service', operations: 'Opérations', personal: 'Personnel', team: 'Équipe', deadlines: 'Échéances', quality: 'Qualité', geography: 'Géographie', analysis: 'Analyse' };
const accentNames = { blue: 'Bleu', teal: 'Turquoise', green: 'Vert', orange: 'Ambre', red: 'Rouge', purple: 'Violet', slate: 'Neutre' };
const styleNames = { surface: 'Carte', outline: 'Contour', accent: 'Accent', minimal: 'Sans cadre' };
const presentationNames = { bars: 'Barres', list: 'Liste', compact: 'Compacte', donut: 'Anneau', gauge: 'Jauge', pareto: 'Pareto', columns: 'Colonnes', heatmap: 'Carte de chaleur', stacked: 'Empilage', 'weather-volume': 'Météo des volumes' };
const namedChoices = (values, names) => values.map(value => ({ value, label: names[value] || value }));

export async function renderDashboard(ctx) {
  const cleanup = heading(ctx, 'Tableaux de bord', 'Indicateurs calculés sur le relevé actif, périmètres reproductibles et observations historiques traçables.');
  let editing = ctx.params?.editing === true;
  const params = { boardId: ctx.params?.boardId, technicianKey: ctx.params?.technicianKey ?? '', crossFilters: ctx.params?.crossFilters ?? [] };
  const loading = empty('Calcul du tableau de bord…', 'Les widgets sont publiés ensemble lorsque leurs périmètres sont prêts.'); ctx.root.append(loading);
  const model = await call(ctx, 'dashboard.get', Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined)));
  if (ctx.signal.aborted) return cleanup;
  loading.remove(); ctx.analyticsRevision = model.revision;
  const layout = model.layout; let board = layout.boards.find(b => b.id === model.boardId); const personal = model.technicianKey.length > 0;
  ensureSections(board); let savedLayout = clone(layout); let saving = false;
  const runArgs = { runId: model.runId, revision: model.revision }; const selected = new Set();
  const go = extra => ctx.navigate('dashboard', { ...params, boardId: board.id, editing, ...extra });
  const persist = async (focus = null) => {
    if (saving) throw new Error('L’enregistrement précédent est encore en cours.');
    saving = true; ctx.root.dataset.ready = 'false'; ctx.root.setAttribute('aria-busy', 'true');
    const lockedUi = [boardHost, editorHost, selectionHost, controls, visibilityHost, provenanceRow, ...document.querySelectorAll('dialog')].map(node => ({ node, wasInert: node.inert }));
    for (const { node } of lockedUi) node.inert = true;
    const positions = new Map([...boardHost.querySelectorAll('[data-widget-id]')].map(node => [node.dataset.widgetId, node.getBoundingClientRect()]));
    historyController?.abort(); let committed = false;
    try {
      await call(ctx, 'dashboard.layout.save', { personal, layout }); committed = true;
      const fresh = await call(ctx, 'dashboard.get', { ...params, boardId: board.id });
      if (ctx.signal.aborted) return;
      Object.assign(model, fresh); Object.assign(layout, fresh.layout); board = layout.boards.find(item => item.id === fresh.boardId); ensureSections(board);
      Object.assign(runArgs, { runId: fresh.runId, revision: fresh.revision }); savedLayout = clone(layout); historyData = null;
      for (const id of selected) if (!board.widgets.some(widget => widget.id === id && !widget.hidden)) selected.delete(id);
      list.replaceChildren(...layout.boards.map(item => el('option', { value: item.id, text: item.name, selected: item.id === board.id })));
      undoControl.disabled = !fresh.layoutHistory.undo; redoControl.disabled = !fresh.layoutHistory.redo;
      cross.control.checked = board.crossFilterOnClick; draw(); editStatus.textContent = 'Disposition enregistrée';
      if (document.documentElement.dataset.motion !== 'reduce' && !matchMedia('(prefers-reduced-motion: reduce)').matches) for (const node of boardHost.querySelectorAll('[data-widget-id]')) {
        const before = positions.get(node.dataset.widgetId); if (!before) continue; const after = node.getBoundingClientRect();
        if (before.left !== after.left || before.top !== after.top) node.animate([{ transform: `translate(${before.left - after.left}px, ${before.top - after.top}px)` }, { transform: 'none' }], { duration: 220, easing: 'cubic-bezier(.16,1,.3,1)' });
      }
    } catch (reason) {
      if (!committed) { Object.assign(layout, clone(savedLayout)); board = layout.boards.find(item => item.id === model.boardId); draw(); }
      editStatus.textContent = committed ? 'Disposition enregistrée ; actualisez le calcul.' : 'Modification non enregistrée';
      throw reason;
    } finally {
      saving = false; ctx.root.dataset.ready = 'true'; ctx.root.removeAttribute('aria-busy');
      for (const { node, wasInert } of lockedUi) node.inert = wasInert;
    }
    if (focus) {
      const nodes = boardHost.querySelectorAll(focus.kind === 'widget' ? '[data-widget-id]' : '[data-section-key]');
      const node = [...nodes].find(item => (focus.kind === 'widget' ? item.dataset.widgetId : item.dataset.sectionKey) === focus.key);
      node?.querySelector(focus.action === 'resize' ? '[data-resize-widget]' : '[data-drag-kind]')?.focus({ preventScroll: true });
    }
    if (board.widgets.some(widget => !widget.hidden && (widget.kind === 'history' || widget.kind === 'metric' && widget.showMiniHistory))) void loadHistory();
  };
  const list = select(layout.boards.map(b => ({ value: b.id, label: b.name })), board.id);
  list.addEventListener('change', async () => { try { layout.activeBoardId = list.value; await call(ctx, 'dashboard.layout.save', { personal, layout }); go({ boardId: list.value }); } catch (reason) { error(ctx, reason); } });
  const people = select([{ value: '', label: 'Équipe entière' }, ...model.roster.map(t => ({ value: t.key, label: `${t.name} · ${formatNumber(t.count)} lignes` }))], model.technicianKey);
  people.addEventListener('change', () => go({ technicianKey: people.value, boardId: undefined, crossFilters: [] }));
  const editControl = button(editing ? 'Quitter l’édition' : 'Modifier le dashboard', () => { editing = !editing; draw(); });
  const undoControl = command(ctx, 'Annuler la modification', async () => { const result = await call(ctx, 'dashboard.layout.undo', { personal }); go({ boardId: result.boardId }); }, { disabled: !model.layoutHistory.undo });
  const redoControl = command(ctx, 'Rétablir la modification', async () => { const result = await call(ctx, 'dashboard.layout.redo', { personal }); go({ boardId: result.boardId }); }, { disabled: !model.layoutHistory.redo });
  const organization = details('Autres actions', toolbar(command(ctx, 'Actualiser', () => go({})), command(ctx, '+ Tableau de bord', () => newBoardDialog(ctx, personal, go)), undoControl, redoControl));
  const controls = toolbar(field('Disposition', list), field('Périmètre personnel', people), editControl,
    command(ctx, 'Filtres et comparaison', () => filtersDialog()), command(ctx, 'Exporter', () => exportDialog()), organization);
  controls.classList.add('analytics-primary-toolbar', 'no-print');
  const provenance = el('details', { class: 'analytics-provenance print-provenance', 'aria-label': 'Provenance du tableau de bord' });
  const provenanceRow = el('div', { class: 'analytics-provenance-row' }, provenance,
    command(ctx, 'Détails des contrôles', () => controlsDialog(), { attributes: { class: 'button analytics-controls-access no-print' } }));
  const receptionIssue = el('div', { class: 'analytics-reception-issue', role: 'status', hidden: true });
  const freshnessIssue = el('div', { class: 'analytics-reception-issue analytics-freshness-issue', role: 'status', hidden: true });
  const boardHost = el('div', { class: 'analytics-board-host' }); const editorHost = el('div', { class: 'analytics-editor-toolbar no-print' });
  const editStatus = el('span', { class: 'analytics-edit-status', role: 'status', 'aria-live': 'polite', text: '' });
  const selectionHost = el('div', { class: 'analytics-selection-toolbar no-print' }); const status = el('div', { class: 'analytics-history-status no-print', role: 'status' });
  const cross = checkbox('Cliquer une catégorie pour filtrer les widgets', board.crossFilterOnClick);
  cross.control.addEventListener('change', () => { board.crossFilterOnClick = cross.control.checked; persist().catch(reason => error(ctx, reason)); });
  const showUnavailable = checkbox('Afficher les widgets indisponibles', board.hideUnavailableWidgets === false);
  const availabilitySummary = el('span', { class: 'analytics-availability-summary', role: 'status' });
  const visibilityHost = el('div', { class: 'analytics-visibility no-print' }, showUnavailable.element, availabilitySummary);
  showUnavailable.control.addEventListener('change', () => {
    if (saving) { showUnavailable.control.checked = board.hideUnavailableWidgets === false; return; }
    board.hideUnavailableWidgets = !showUnavailable.control.checked; persist().catch(reason => error(ctx, reason));
  });
  ctx.root.append(controls, provenanceRow, receptionIssue, freshnessIssue, toolbar(cross.element,
    command(ctx, 'Voir les lignes du périmètre', () => rowDialog(ctx, 'dashboard.rows', { ...runArgs, kind: 'scope' })),
    command(ctx, 'Effacer les filtres', async () => { if (board.filters.length) { board.filters = []; await call(ctx, 'dashboard.layout.save', { personal, layout }); } go({ crossFilters: [] }); }, { disabled: model.commonFilters.length === 0 }),
    command(ctx, 'Effectifs datés', () => ctx.navigate('populations')), command(ctx, 'Assistant sur ce périmètre', () => ctx.navigate('assistant', runArgs))),
    visibilityHost, editorHost, selectionHost);
  if (model.comparison) ctx.root.append(comparisonCard(model.snapshot, model.comparison));
  ctx.root.append(status, boardHost);
  let historyController = null; let historyData = null;

  function drawOverview() {
    const snapshot = model.snapshot; const quality = snapshot.quality;
    const received = snapshot.receptionComplete === true;
    const receiptLabel = received ? 'Réception complète' : 'Réception partielle ou non confirmée';
    provenance.replaceChildren(
      el('summary', {}, el('strong', { text: `${board.name}${personal ? ` · ${model.technicianKey}` : ''}` }),
        el('span', { text: `${formatNumber(snapshot.rowCount)} / ${formatNumber(snapshot.sourceRowCount)} lignes · ${formatDate(snapshot.extractedAt)}` }),
        badge(receiptLabel, received ? 'success' : 'warning'),
        model.historical ? badge('Archive') : ctx.state.session?.demo ? badge('Démonstration', 'warning') : null,
        model.commonFilters.length ? badge(`${model.commonFilters.length} filtre(s)`) : null),
      el('strong', { text: 'GLPI Pilot · provenance de ce calcul' }),
      caption(`Relevé : ${formatDate(snapshot.extractedAt)} · calcul : ${formatDate(snapshot.calculatedAt)} · ${formatNumber(snapshot.rowCount)} / ${formatNumber(snapshot.sourceRowCount)} lignes dans ce périmètre · ${receiptLabel.toLocaleLowerCase()}`),
      caption(`${snapshot.scopeLabel} · ${snapshot.sourceUrl || 'source locale'}`),
      caption(`Total annoncé dans la source : ${snapshot.sourceExpectedTotal == null ? 'inconnu' : formatNumber(snapshot.sourceExpectedTotal)} lignes.`),
      caption(`Filtres communs : ${describeFilters(model.commonFilters) || 'aucun'}`),
      ...board.widgets.filter(widget => !widget.hidden && widget.filters?.length).map(widget => caption(`Widget ${widget.title || widget.sourceKey} : ${describeFilters(widget.filters)}`)),
      model.historical ? badge('Archive · règles et dénominateurs conservés') : null);
    const issues = [snapshot.receptionComplete === false ? 'Réception partielle ou non confirmée.' : '',
      quality?.scopeStable === false ? 'Le périmètre de la collecte doit être vérifié.' : '',
      quality?.sampleIdentityVerified === false ? 'Des identifiants de l’échantillon manquent dans le relevé.' : '',
      quality?.finalTotalVerified === false ? 'Le total GLPI a changé pendant la collecte.' : ''].filter(Boolean);
    receptionIssue.hidden = issues.length === 0;
    receptionIssue.replaceChildren(...(issues.length ? [el('strong', { text: 'Collecte à vérifier' }), el('span', { text: issues.join(' ') })] : []));
    const elapsedHours = (Date.parse(snapshot.calculatedAt) - Date.parse(snapshot.extractedAt)) / 3600000;
    const stale = !model.historical && board.freshnessWarningHours > 0 && Number.isFinite(elapsedHours) && elapsedHours > board.freshnessWarningHours;
    freshnessIssue.hidden = !stale;
    freshnessIssue.replaceChildren(...(stale ? [el('strong', { text: 'Relevé à actualiser' }),
      el('span', { text: `Ce relevé date de ${number(elapsedHours, 'h')}. Rappel configuré après ${board.freshnessWarningHours} h ; les volumes et les couleurs météo restent ceux du relevé.` })] : []));
  }
  function controlsDialog(extraWarnings = []) {
    const warnings = [...new Set([...(model.snapshot.warnings ?? []), ...extraWarnings].filter(text => typeof text === 'string' && text.trim()))];
    dialog('Détails des contrôles', el('div', { class: 'analytics-controls-detail' },
      qualitySummary(model.snapshot.quality) ?? caption('Les observations détaillées des contrôles ne sont pas disponibles pour ce relevé.'),
      caption('La réception porte sur les lignes reçues ; les contrôles d’identifiants et de valeurs portent sur l’échantillon observé.'),
      warnings.length ? el('div', { class: 'analytics-control-notes' }, el('h3', { text: 'Observations du relevé' }), ...warnings.map(text => caption(text))) : null),
    model.historical ? [] : [{ label: 'Ouvrir la page Qualité', action: close => { close(); ctx.navigate('quality'); } }]);
  }

  async function activate(widget, selection) {
    if (board.crossFilterOnClick && ['metric', 'distribution', 'map'].includes(selection.kind)) {
      const filter = selection.kind === 'metric' ? { kind: 'metric', sourceKey: selection.key, value: '', value2: '', exclude: false }
        : { kind: 'distribution', sourceKey: selection.kind === 'map' ? `geography-points-${selection.active ? 'active' : 'all'}` : selection.key, value: selection.label, value2: '', exclude: false };
      if (model.commonFilters.some(f => JSON.stringify(f) === JSON.stringify(filter))) return;
      if (model.commonFilters.length >= 12) throw new Error('Douze filtres sont déjà appliqués. Retirez une condition.');
      go({ crossFilters: [...params.crossFilters, filter] });
    } else await rowDialog(ctx, 'dashboard.rows', { ...runArgs, widgetId: widget.id, ...selection }, `${widget.settings.title || widget.settings.sourceKey} · lignes contributrices`);
  }
  async function copyIds(widget, selection) {
    const result = await call(ctx, 'dashboard.copyIds', { ...runArgs, widgetId: widget.id, ...selection });
    ctx.notify(`${formatNumber(result.count)} identifiants copiés${result.missing ? ` · ${formatNumber(result.missing)} lignes sans identifiant` : ''}.`);
  }
  async function more(widget, kind, key) {
    const content = el('div'); const modal = dialog('Toutes les valeurs', content);
    const load = async offset => {
      const data = await call(ctx, 'dashboard.data', { ...runArgs, widgetId: widget.id, kind, key, offset, limit: 100 });
      if (['oldest', 'participation', 'sites'].includes(kind)) {
        const activateDetail = selection => activate(widget, selection);
        const view = kind === 'oldest' ? renderOldestGroups(ctx, data, activateDetail)
          : kind === 'participation' ? participationTable(ctx, data.participants, widget.settings.sourceKey, activateDetail, data.maximumCount)
          : sitesTable(ctx, data.sites, activateDetail, selection => copyIds(widget, selection));
        content.replaceChildren(view, pager(ctx, data.total, data.offset, 100, load));
        return;
      }
      const columns = kind === 'distribution' ? [{ key: 'label', label: 'Catégorie' }, { key: 'count', label: 'Lignes' }, { key: 'value', label: 'Valeur', render: r => number(r.isAggregate ? r.value : r.value ?? r.count, r.unit) }, { key: 'sample', label: 'Échantillon' }]
        : kind === 'matrix' ? [{ key: 'rowLabel', label: 'Ligne' }, { key: 'columnLabel', label: 'Colonne' }, { key: 'value', label: 'Valeur' }]
        : kind === 'map' ? [{ key: 'label', label: 'Lieu' }, { key: 'latitude', label: 'Latitude' }, { key: 'longitude', label: 'Longitude' }, { key: 'count', label: 'Tickets' }, { key: 'active', label: 'Actifs' }]
        : kind === 'boxplot' ? [{ key: 'label', label: 'Groupe' }, { key: 'sample', label: 'Échantillon' }, { key: 'minimum', label: 'Minimum' }, { key: 'firstQuartile', label: 'Q1' }, { key: 'median', label: 'Médiane' }, { key: 'thirdQuartile', label: 'Q3' }, { key: 'p90', label: 'P90' }, { key: 'maximum', label: 'Maximum' }]
        : kind === 'scatter' ? [{ key: 'label', label: 'Ticket' }, { key: 'x', label: 'X' }, { key: 'y', label: 'Y' }]
        : [{ key: 'id', label: 'Ticket' }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'Statut' }, { key: 'reasons', label: 'Points d’attention' }];
      content.replaceChildren(table(data.rows, columns, { maxRows: 100, onSelect: row => {
        modal.close();
        if (kind === 'attention' || kind === 'scatter') ctx.navigate('tickets', { rowIndex: row.index ?? row.row, revision: model.revision });
        else activate(widget, { kind, key: kind === 'map' ? row.key : key, label: row.label ?? row.rowLabel, columnLabel: row.columnLabel,
          active: kind === 'map' && widget.settings.sourceKey === 'active' }).catch(reason => error(ctx, reason));
      } }), pager(ctx, data.total, data.offset, 100, load));
    };
    await load(0);
  }
  function draw() {
    editControl.textContent = editing ? 'Quitter l’édition' : 'Modifier le dashboard';
    editControl.setAttribute('aria-pressed', String(editing));
    drawOverview(); showUnavailable.control.checked = board.hideUnavailableWidgets === false;
    editorHost.replaceChildren(); selectionHost.replaceChildren(); boardHost.replaceChildren();
    editorHost.hidden = !editing; selectionHost.hidden = !editing;
    boardHost.classList.toggle('is-editing', editing); boardHost.dataset.density = board.density || 'comfortable';
    if (editing) {
      const transfer = details('Disposition', toolbar(command(ctx, 'Nom et organisation', () => boardDialog()),
        command(ctx, 'Importer une disposition', async () => {
          const preview = await call(ctx, 'dashboard.layout.import', { personal }); if (preview.cancelled) return;
          dialog('Vérifier la disposition importée', el('div', {}, caption(`${preview.layout.boards.length} tableau(x) à importer · ${preview.existingBoards} déjà présent(s) dans la disposition ${personal ? 'personnelle' : 'globale'}.`),
            caption('Ajout recommandé : vos tableaux actuels sont conservés. Les noms déjà utilisés reçoivent un suffixe « importé ». Vous pourrez annuler cet ajout.'),
            preview.canAppend ? table(preview.addedBoards, [{ key: 'originalName', label: 'Nom dans le fichier' }, { key: 'name', label: 'Nom après ajout' }, { key: 'widgetCount', label: 'Widgets' }])
              : caption(preview.appendReason, 'warning'),
            details('Remplacement complet', caption('Le remplacement retire tous les tableaux de cette disposition et charge ceux du fichier. Les tableaux personnels et globaux sont indépendants.'))),
          [{ label: 'Ajouter à mes tableaux', primary: true, disabled: !preview.canAppend, action: async close => {
            const result = await call(ctx, 'dashboard.layout.append', { personal, layout: preview.layout, baseFingerprint: preview.baseFingerprint }); close(); go({ boardId: result.boardId });
          } }, { label: 'Remplacer cette disposition', danger: true, action: async close => { await call(ctx, 'dashboard.layout.save', { personal, layout: preview.layout, baseFingerprint: preview.baseFingerprint }); close(); go({ boardId: preview.layout.activeBoardId }); } }]);
        }), command(ctx, 'Exporter la disposition', async () => { const r = await call(ctx, 'dashboard.layout.export', { personal }); if (!r.cancelled) ctx.notify(`Disposition exportée : ${r.path}`); }),
        command(ctx, 'Widgets masqués', () => hiddenDialog())));
      editorHost.append(toolbar(el('strong', { class: 'analytics-edit-label', text: 'Personnaliser' }), command(ctx, '+ Widget', () => catalogDialog()),
        command(ctx, '+ Section', () => sectionDialog()), transfer, editStatus),
        el('p', { class: 'analytics-edit-help', id: 'dashboard-edit-help', text: 'Glissez les poignées pour déplacer. Étirez le coin pour redimensionner. Au clavier : ↑ ↓ pour l’ordre, ← → pour changer de section ; les flèches du coin règlent la taille.' }));
      drawBulk();
    }
    if (board.description) boardHost.append(caption(board.description, 'analytics-board-description'));
    const groups = ensureSections(board).filter(section => editing || !section.hidden);
    const widgetById = new Map(model.widgets.map(widget => [widget.id, widget]));
    const visibleSections = new Set(groups.map(section => section.key));
    const unavailableCount = board.widgets.filter(widget => !widget.hidden && visibleSections.has(widget.section || '') && widgetById.get(widget.id)?.available === false).length;
    const hideUnavailable = !editing && board.hideUnavailableWidgets !== false;
    availabilitySummary.hidden = unavailableCount === 0;
    availabilitySummary.textContent = unavailableCount === 0 ? '' : editing
      ? `${formatNumber(unavailableCount)} widget(s) indisponible(s) visible(s) pour être réglé(s).`
      : hideUnavailable ? `${formatNumber(unavailableCount)} widget(s) sans données calculables masqué(s) automatiquement.`
        : `${formatNumber(unavailableCount)} widget(s) indisponible(s) affiché(s).`;
    let renderedSections = 0;
    for (const section of groups) {
      const sectionWidgets = board.widgets.filter(widget => !widget.hidden && (widget.section || '') === section.key);
      const widgets = sectionWidgets.filter(widget => !hideUnavailable || widgetById.get(widget.id)?.available !== false);
      if (!editing && sectionWidgets.length > 0 && widgets.length === 0) continue;
      renderedSections++;
      const expanded = !board.collapsedSections.includes(section.key);
      const gridId = `dashboard-section-${uuid()}`;
      const grid = el('div', { class: 'analytics-board', id: gridId, 'aria-label': section.title, hidden: !expanded });
      const sectionRoot = el('section', { class: `analytics-board-section accent-${section.accent || 'blue'}${section.hidden ? ' is-hidden-section' : ''}`, 'data-section-key': section.key });
      const sectionHeader = el('header', { class: 'analytics-section-heading' });
      if (editing) sectionHeader.append(button('⠿', null, { title: 'Glisser la section ou utiliser les flèches du clavier', attributes: {
        class: 'button analytics-drag-handle', draggable: true, 'data-drag-kind': 'section', 'aria-label': `Déplacer la section ${section.title}`, 'aria-describedby': 'dashboard-edit-help' } }));
      const toggle = button(section.title, async () => {
        board.collapsedSections = board.collapsedSections.filter(key => key !== section.key); if (expanded) board.collapsedSections.push(section.key);
        await persist();
      }, { attributes: { class: 'button analytics-section-toggle', 'aria-expanded': String(expanded), 'aria-controls': gridId } });
      toggle.prepend(el('span', { class: 'analytics-section-chevron', text: '›', 'aria-hidden': 'true' }));
      toggle.append(el('span', { class: 'analytics-section-count', text: String(widgets.length), 'aria-label': `${widgets.length} widgets` }));
      sectionHeader.append(toggle);
      if (section.hidden) sectionHeader.append(badge('Section masquée', 'warning'));
      if (editing) sectionHeader.append(toolbar(command(ctx, 'Régler la section', () => sectionDialog(section)),
        command(ctx, '+ Widget ici', () => catalogDialog(section.key))));
      sectionRoot.append(sectionHeader);
      if (section.description && expanded) sectionRoot.append(caption(section.description, 'analytics-section-description'));
      for (const setting of widgets) {
        const widget = widgetById.get(setting.id); if (!widget) continue;
        if (setting.startNewRow) grid.append(el('div', { class: 'analytics-new-row', 'aria-hidden': 'true' }));
        const title = widgetTitle(widget, model.catalog);
        const header = el('div', { class: 'analytics-widget-heading' });
        if (editing) header.append(button('⠿', () => moveDialog(setting), { disabled: setting.locked, title: 'Glisser ou choisir une position', attributes: {
          class: 'button analytics-drag-handle', draggable: !setting.locked, 'data-drag-kind': 'widget', 'aria-label': `Déplacer ${title}`, 'aria-describedby': 'dashboard-edit-help' } }));
        header.append(el('h2', { text: title }));
        const controls = el('div', { class: 'analytics-widget-controls no-print' });
        if (editing) {
          const check = checkbox('', selected.has(setting.id)); check.control.setAttribute('aria-label', `Sélectionner ${title}`);
          check.control.addEventListener('change', () => { if (check.control.checked) selected.add(setting.id); else selected.delete(setting.id); drawBulk(); });
          controls.append(check.element, command(ctx, 'Régler', () => widgetDialog(setting)));
        } else controls.append(command(ctx, 'Lignes', () => rowDialog(ctx, 'dashboard.rows', { ...runArgs, widgetId: setting.id, kind: 'scope' }, title)));
        header.append(controls);
        const article = el('article', { class: `analytics-widget style-${setting.style} accent-${setting.accent}${setting.locked ? ' locked' : ''}${widget.available === false ? ' is-unavailable' : ''}`,
          style: { '--span': setting.span, '--widget-height': `${setting.height}px` }, 'data-widget-id': setting.id, 'data-widget-kind': setting.kind,
          'data-available': widget.available ?? 'unknown', 'data-auto-height': setting.autoHeight }, header,
          caption(`${formatNumber(widget.rowCount)} lignes · ${setting.filters?.length ? describeFilters(setting.filters) : widget.scopeLabel}`, 'analytics-widget-scope'),
          widgetContent(ctx, widget, selection => activate(widget, selection).catch(reason => error(ctx, reason)), (kind, key) => more(widget, kind, key),
            { snapshot: model.snapshot, historical: model.historical, onControls: () => controlsDialog(widget.content?.warnings), copyIds: selection => copyIds(widget, selection) }));
        if (editing) article.append(el('div', { class: 'analytics-widget-edit-footer no-print' },
          el('span', { text: setting.locked ? 'Position verrouillée' : `${setting.span} / 12 · ${setting.autoHeight ? 'hauteur auto' : `${setting.height} px`}` }),
          setting.locked ? null : button('↘', () => widgetDialog(setting), { title: 'Étirer pour redimensionner, utiliser les flèches ou ouvrir les dimensions', attributes: {
            class: 'button analytics-resize-handle', 'data-resize-widget': setting.id, 'aria-label': `Redimensionner ${title}`, 'aria-describedby': 'dashboard-edit-help' } })));
        grid.append(article);
      }
      if (editing) grid.append(el('div', { class: `analytics-drop-zone${widgets.length ? ' has-widgets' : ''}`, 'data-drop-section': section.key },
        el('span', { text: widgets.length ? 'Déposer un widget ici' : 'Glissez un widget dans cette section' }),
        widgets.length ? null : command(ctx, 'Choisir un widget', () => catalogDialog(section.key))));
      else if (!widgets.length) grid.append(caption('Cette section ne contient pas de widget visible.', 'analytics-section-empty'));
      sectionRoot.append(grid); boardHost.append(sectionRoot);
    }
    if (!groups.length) boardHost.append(empty('Toutes les sections sont masquées', 'Passez en édition pour choisir celles à afficher.'));
    else if (!renderedSections) boardHost.append(empty('Aucun widget calculable dans ce périmètre', 'Tous les widgets sont conservés. Affichez les indisponibles ou passez en édition pour les régler.'));
    if (historyData) displayHistory(historyData);
  }
  function sectionChoices() { return ensureSections(board).map(section => ({ value: section.key, label: `${section.title}${section.hidden ? ' · masquée' : ''}` })); }
  function moveDialog(widget) {
    if (widget.locked) return;
    const section = select(sectionChoices(), widget.section); const position = select([]);
    const update = () => position.replaceChildren(el('option', { value: '', text: 'À la fin de la section' }),
      ...board.widgets.filter(item => !item.hidden && item.id !== widget.id && item.section === section.value).map(item => el('option', { value: item.id, text: `Avant ${item.title || kindNames[item.kind] + ' · ' + item.sourceKey}` })));
    section.addEventListener('change', update); update();
    dialog('Déplacer le widget', el('div', {}, field('Section de destination', section), field('Position', position)), [
      { label: 'Déplacer', primary: true, action: async close => { if (moveDashboardWidget(board, widget.id, section.value, position.value || null)) await persist({ kind: 'widget', key: widget.id, action: 'move' }); close(); } }
    ]);
  }
  function sectionDialog(original = null) {
    const section = clone(original || { key: uuid(), title: 'Nouvelle section', description: '', accent: 'blue', hidden: false });
    const title = input(section.title, { maxLength: 120 }); const description = textArea(section.description, 3); description.maxLength = 600;
    const accent = select(namedChoices(model.catalog.accents, accentNames), section.accent);
    const hidden = checkbox('Masquer cette section et ses widgets en lecture', section.hidden);
    const position = select([{ value: '__end__', label: 'À la fin' }, ...sectionChoices().filter(item => item.value !== section.key).map(item => ({ value: item.value, label: `Avant ${item.label}` }))], '__end__');
    if (original) { const index = board.sections.indexOf(original); if (board.sections[index + 1]) position.value = board.sections[index + 1].key; }
    const read = () => Object.assign(section, { title: title.value.trim(), description: description.value, accent: accent.value, hidden: hidden.control.checked });
    const actions = [{ label: 'Enregistrer la section', primary: true, action: async close => {
      read(); if (!section.title) throw new Error('Donnez un titre à la section.');
      if (!original && board.sections.length >= 60) throw new Error('Ce tableau contient déjà 60 sections.');
      if (original) {
        const index = board.sections.findIndex(item => item.key === original.key); if (index < 0) throw new Error('Cette section n’est plus disponible.');
        board.sections[index] = section;
      } else board.sections.push(section);
      if (position.value !== '__end__') moveDashboardSection(board, section.key, position.value);
      else { board.sections.splice(board.sections.indexOf(section), 1); board.sections.push(section); }
      await persist({ kind: 'section', key: section.key, action: 'move' }); close();
    } }];
    const body = el('div', {}, el('div', { class: 'analytics-form-grid' }, field('Titre de la section', title), field('Couleur de la section', accent), field('Position de la section', position)),
      field('Description de la section', description), hidden.element);
    if (original) {
      const destination = select(sectionChoices().filter(item => item.value !== section.key));
      const count = board.widgets.filter(widget => widget.section === section.key).length;
      const explanation = count ? field('Déplacer ses widgets vers', destination, `${count} widgets conservés, y compris ceux qui sont masqués.`) : caption('Cette section est vide.');
      body.append(details('Retirer cette section', explanation, command(ctx, 'Supprimer la section', async () => {
        if (board.sections.length === 1) throw new Error('Conservez au moins une section.');
        if (board.widgets.some(widget => widget.section === section.key && widget.locked)) throw new Error('Déverrouillez les widgets de cette section avant de les déplacer.');
        for (const widget of board.widgets.filter(item => item.section === section.key)) widget.section = destination.value;
        board.sections = board.sections.filter(item => item.key !== section.key); board.collapsedSections = board.collapsedSections.filter(key => key !== section.key);
        await persist(); modal.close();
      }, { danger: true })));
    }
    const modal = dialog(original ? 'Modifier la section' : 'Créer une section', body, actions);
  }
  function drawBulk() {
    selectionHost.hidden = !editing || selected.size === 0;
    const span = select([3, 4, 6, 8, 9, 12].map(v => ({ value: String(v), label: `${v} / 12 colonnes` })), '4');
    const height = input(320, { type: 'number', min: 160, max: 960, step: 20 }); const section = select([{ value: '__keep__', label: 'Conserver les sections' }, ...sectionChoices()], '__keep__');
    const style = select([{ value: '', label: 'Conserver les styles' }, ...namedChoices(model.catalog.styles, styleNames)]);
    const accent = select([{ value: '', label: 'Conserver les accents' }, ...namedChoices(model.catalog.accents, accentNames)]);
    selectionHost.replaceChildren(toolbar(caption(`${selected.size} widgets sélectionnés`), field('Largeur', span), field('Hauteur', height), field('Section', section), field('Style commun', style), field('Accent commun', accent),
      command(ctx, 'Aligner la sélection', async () => {
        const chosen = board.widgets.filter(w => selected.has(w.id) && !w.locked); let sum = 0;
        for (const widget of chosen) {
          widget.span = Number(span.value); widget.height = Number(height.value); widget.autoHeight = false;
          if (section.value !== '__keep__') widget.section = section.value;
          if (style.value) widget.style = style.value; if (accent.value) widget.accent = accent.value;
          widget.startNewRow = sum === 0 || sum + widget.span > 12; sum = widget.startNewRow ? widget.span : sum + widget.span;
        }
        await persist();
      }, { disabled: selected.size === 0 }), command(ctx, 'Masquer la sélection', async () => { for (const widget of board.widgets) if (selected.has(widget.id) && !widget.locked) widget.hidden = true; await persist(); }, { disabled: selected.size === 0 })));
  }
  function boardDialog() {
    const name = input(board.name, { maxLength: 120 }); const description = textArea(board.description || '', 3); description.maxLength = 600;
    const density = select([{ value: 'comfortable', label: 'Aérée' }, { value: 'compact', label: 'Compacte' }], board.density || 'comfortable');
    const freshness = input(board.freshnessWarningHours ?? '', { type: 'number', min: 1, max: 8760, step: 1 });
    const message = el('div', { role: 'alert' });
    dialog('Organiser ce tableau de bord', el('div', {}, field('Nom', name), field('Description du tableau', description), field('Densité du tableau', density),
      field('Rappel après un relevé ancien · heures', freshness, 'Vide : aucun rappel. Un rappel ne modifie pas les couleurs météo.'), message), [
      { label: 'Enregistrer', primary: true, action: async close => { board.name = name.value; board.description = description.value; board.density = density.value;
        board.freshnessWarningHours = freshness.value.trim() === '' ? null : Number(freshness.value); await persist(); close(); } },
      { label: 'Dupliquer', action: async close => { const copy = clone(board); copy.id = uuid(); copy.name = `${name.value} · copie`.slice(0, 120); copy.widgets.forEach(w => w.id = uuid()); layout.boards.push(copy); layout.activeBoardId = copy.id; await call(ctx, 'dashboard.layout.save', { personal, layout }); close(); go({ boardId: copy.id }); } },
      { label: 'Supprimer ce tableau', danger: true, action: async close => {
        if (layout.boards.length === 1) { message.textContent = 'Conservez au moins un tableau de bord.'; return; }
        layout.boards.splice(layout.boards.indexOf(board), 1); layout.activeBoardId = layout.boards[0].id; await call(ctx, 'dashboard.layout.save', { personal, layout }); close(); go({ boardId: layout.activeBoardId });
      } }
    ]);
  }
  function hiddenDialog() {
    const hidden = board.widgets.filter(w => w.hidden);
    const modal = dialog('Widgets masqués', hidden.length ? table(hidden, [{ key: 'title', label: 'Widget', render: w => w.title || w.sourceKey }, { key: 'show', label: 'Action', render: w => command(ctx, 'Réafficher', async () => {
      const widget = board.widgets.find(item => item.id === w.id); if (!widget) throw new Error('Ce widget n’est plus disponible.'); widget.hidden = false; await persist(); modal.close();
    }) }]) : empty('Aucun widget masqué'));
  }
  function filtersDialog() {
    const common = filterEditor(model.catalog, board.filters); const compare = checkbox('Comparer avec un second périmètre indépendant', board.comparisonEnabled);
    const comparison = filterEditor(model.catalog, board.comparisonFilters); const name = input('');
    const preset = select([{ value: '', label: 'Choisir un contexte' }, ...layout.filterPresets.map((p, i) => ({ value: String(i), label: p.name }))]);
    const body = el('div', {}, field('Contexte mémorisé', preset), caption('Charger un contexte applique immédiatement ses filtres communs.'),
      command(ctx, 'Charger ce contexte', async () => { if (preset.value === '') return; board.filters = clone(layout.filterPresets[Number(preset.value)].filters); await persist(); }),
      command(ctx, 'Supprimer ce contexte mémorisé', async () => { if (preset.value === '') return; layout.filterPresets.splice(Number(preset.value), 1); await persist(); }),
      el('h3', { text: 'Périmètre A' }), common.element, compare.element, el('h3', { text: 'Périmètre B' }), comparison.element,
      caption('B reste borné à la source de la vue, y compris pour une personne, et ne reçoit pas les filtres de A.'), field('Nom du contexte à mémoriser (facultatif)', name));
    dialog('Filtres et comparaison', body, [{ label: 'Appliquer', primary: true, action: async close => {
      board.filters = common.read(); board.comparisonFilters = comparison.read(); board.comparisonEnabled = compare.control.checked;
      if (name.value.trim()) layout.filterPresets.push({ name: name.value.trim(), filters: clone(board.filters) });
      await call(ctx, 'dashboard.layout.save', { personal, layout }); close(); go({ crossFilters: [] });
    } }]);
  }
  function catalogDialog(sectionKey = null) {
    const search = input('', { placeholder: 'Rechercher une source ou une définition' }); const kind = select([{ value: '', label: 'Tous les types' }, ...Object.entries(kindNames).map(([value, label]) => ({ value, label }))]);
    const available = checkbox('Disponibles seulement', true); const favorite = checkbox('Favoris uniquement'); const recent = checkbox('Récemment utilisés'); const itemsHost = el('div', { class: 'analytics-catalog' });
    const modal = dialog('Catalogue de widgets', el('div', {}, toolbar(search, kind, available.element, favorite.element, recent.element), itemsHost));
    const entries = catalogEntries(model.catalog);
    const render = () => {
      let entriesShown = entries.filter(e => (!kind.value || e.kind === kind.value) && (!available.control.checked || e.available !== false) && (!search.value || `${e.label} ${e.key}`.toLocaleLowerCase().includes(search.value.toLocaleLowerCase())));
      if (favorite.control.checked) entriesShown = entriesShown.filter(e => layout.catalogFavorites.includes(`${e.kind}:${e.key}`));
      if (recent.control.checked) entriesShown = entriesShown.filter(e => layout.catalogRecent.includes(`${e.kind}:${e.key}`)).sort((a, b) => layout.catalogRecent.indexOf(`${a.kind}:${a.key}`) - layout.catalogRecent.indexOf(`${b.kind}:${b.key}`));
      itemsHost.replaceChildren(...entriesShown.slice(0, 300).map(entry => {
        const token = `${entry.kind}:${entry.key}`; const fav = layout.catalogFavorites.includes(token);
        return el('div', { class: 'analytics-catalog-entry' }, el('div', {}, el('strong', { text: entry.label }), caption(`${kindNames[entry.kind]}${entry.available === false ? ' · source indisponible' : ''}`)),
          button(fav ? '★' : '☆', () => { layout.catalogFavorites = fav ? layout.catalogFavorites.filter(f => f !== token) : [...layout.catalogFavorites, token]; render(); }, { title: 'Favori du catalogue' }),
          command(ctx, 'Ajouter', async () => {
            if (board.widgets.length >= 60) throw new Error('Cette disposition contient déjà 60 widgets.');
            const widget = newWidget(entry.kind, entry.key); widget.section = sectionKey ?? ensureSections(board).find(section => !section.hidden)?.key ?? board.sections[0].key; board.widgets.push(widget);
            layout.catalogRecent = [token, ...layout.catalogRecent.filter(t => t !== token)].slice(0, 30); await persist(); modal.close();
          }));
      }));
      if (entriesShown.length > 300) itemsHost.append(caption(`${entriesShown.length} sources correspondent. Affinez la recherche pour afficher celles au-delà des 300 premières.`));
    };
    search.addEventListener('input', render); kind.addEventListener('change', render); available.control.addEventListener('change', render); favorite.control.addEventListener('change', render); recent.control.addEventListener('change', render); render();
    itemsHost.after(command(ctx, 'Enregistrer les favoris', async () => { await persist(); modal.close(); }));
  }
  function widgetDialog(original) {
    const widget = clone(original); const kind = select(choices(Object.entries(kindNames).map(([key, label]) => ({ key, label }))), widget.kind);
    const source = select([], widget.sourceKey); const title = input(widget.title, { maxLength: 180 }); const section = select(sectionChoices(), widget.section);
    const span = select(Array.from({ length: 10 }, (_, index) => index + 3).map(value => ({ value: String(value), label: `${value} / 12` })), String(widget.span));
    const height = input(widget.height, { type: 'number', min: 160, max: 960, step: 20 }); const top = input(widget.topCount, { type: 'number', min: 3, max: 30 });
    const style = select(namedChoices(model.catalog.styles, styleNames), widget.style); const accent = select(namedChoices(model.catalog.accents, accentNames), widget.accent); const presentation = select(namedChoices(model.catalog.presentations, presentationNames), widget.presentation);
    const stacking = select([{ value: 'percent', label: 'Pourcentage' }, { value: 'volume', label: 'Volume' }], widget.stackedMode); const pareto = input(widget.paretoThresholdPercent, { type: 'number', min: .1, max: 100, step: .1 });
    const automatic = checkbox('Hauteur automatique', widget.autoHeight); const row = checkbox('Commencer une nouvelle ligne', widget.startNewRow);
    const description = checkbox('Afficher les définitions et limites', widget.showDescription); const mini = checkbox('Mini-historique sur l’indicateur', widget.showMiniHistory);
    const locked = checkbox('Verrouiller déplacement et actions groupées', widget.locked); const hidden = checkbox('Masquer ce widget', widget.hidden);
    const period = input(widget.historyPeriodDays, { type: 'number', min: 1, max: 3650 }); const note = textArea(widget.note, 5);
    const target = nullableNumber(widget.targetValue, 'Objectif'); const warning = nullableNumber(widget.warningValue, 'Seuil de vigilance'); const critical = nullableNumber(widget.criticalValue, 'Seuil critique');
    const lower = checkbox('Une valeur plus basse est préférable', widget.lowerIsBetter); const breaks = input(widget.histogramBreaks.join(';'));
    const weatherHelp = caption('');
    const updateWeatherHelp = () => {
      const weather = kind.value === 'metric' && presentation.value === 'weather-volume'; weatherHelp.hidden = !weather;
      weatherHelp.textContent = `Météo des volumes : les deux seuils sont propres à ce widget. La vigilance puis la tension commencent ${lower.control.checked ? 'strictement au-delà' : 'strictement en dessous'} de chaque seuil ; l’égalité reste dans l’état précédent. Sans les deux seuils, le volume reste visible et la météo indique « Seuils à définir ». Pour le stock de tickets ouverts, choisissez la source Actifs.`;
      const weatherOption = presentation.querySelector('option[value="weather-volume"]'); if (weatherOption) weatherOption.disabled = kind.value !== 'metric';
    };
    const filters = filterEditor(model.catalog, widget.filters);
    const roster = participationRosterEditor(widget.participationRoster ?? []);
    const rosterPanel = details('Liste facultative des participants et alias', roster.element);
    const updateRoster = () => { rosterPanel.hidden = kind.value !== 'participation'; };
    kind.addEventListener('change', updateRoster); updateRoster();
    const updateSources = () => {
      const old = source.value || widget.sourceKey; const entries = catalogEntries(model.catalog).filter(e => e.kind === kind.value);
      source.replaceChildren(...entries.map(e => el('option', { value: e.key, text: e.label })));
      if (old && !entries.some(e => e.key === old)) source.append(el('option', { value: old, text: `${old} · indisponible` }));
      source.value = old || entries[0]?.key || '';
    };
    kind.addEventListener('change', () => { widget.sourceKey = ''; source.value = ''; if (kind.value !== 'metric' && presentation.value === 'weather-volume') presentation.value = 'bars'; updateSources(); updateWeatherHelp(); }); updateSources();
    presentation.addEventListener('change', updateWeatherHelp); lower.control.addEventListener('change', updateWeatherHelp); updateWeatherHelp();
    const read = () => Object.assign(widget, { kind: kind.value, sourceKey: source.value, title: title.value, section: section.value,
      span: Number(span.value), height: Number(height.value), topCount: Number(top.value), style: style.value, accent: accent.value, presentation: presentation.value,
      stackedMode: stacking.value, paretoThresholdPercent: Number(pareto.value), autoHeight: automatic.control.checked, startNewRow: row.control.checked,
      showDescription: description.control.checked, showMiniHistory: mini.control.checked, historyPeriodDays: Number(period.value), locked: locked.control.checked,
      hidden: hidden.control.checked, note: note.value, targetValue: target.read(), warningValue: warning.read(), criticalValue: critical.read(), lowerIsBetter: lower.control.checked,
      histogramBreaks: breaks.value.trim() ? breaks.value.split(';').map(value => Number(value.trim().replace(',', '.'))) : [], filters: filters.read(), participationRoster: roster.read() });
    dialog('Configurer le widget', el('div', {}, el('div', { class: 'analytics-form-grid' }, field('Type', kind), field('Source', source), field('Titre facultatif', title), field('Section', section),
      field('Largeur', span), field('Hauteur', height), field('Top catégories', top), field('Style', style), field('Accent', accent), field('Présentation', presentation),
      field('Empilage', stacking), field('Seuil de Pareto (%)', pareto), field('Fenêtre historique (jours)', period), field('Bornes histogramme séparées par ;', breaks), target.element, warning.element, critical.element),
      toolbar(automatic.element, row.element, description.element, mini.element, locked.element, hidden.element, lower.element), weatherHelp, field('Note', note), rosterPanel, details('Filtres propres à ce widget', filters.element)), [
      { label: 'Enregistrer', primary: true, action: async close => {
        read(); if (original.locked && widget.locked && (widget.section !== original.section || widget.span !== original.span || widget.height !== original.height || widget.autoHeight !== original.autoHeight || widget.startNewRow !== original.startNewRow))
          throw new Error('Déverrouillez le widget pour modifier sa position ou ses dimensions.');
        const index = board.widgets.findIndex(item => item.id === original.id); if (index < 0) throw new Error('Ce widget n’est plus disponible.');
        board.widgets[index] = widget; await persist(); close();
      } },
      { label: 'Dupliquer', action: async close => { const copy = read(); copy.id = uuid(); copy.locked = false; board.widgets.push(copy); await persist(); close(); } },
      { label: 'Supprimer', danger: true, action: async close => {
        const index = board.widgets.findIndex(item => item.id === original.id); if (index < 0) throw new Error('Ce widget n’est plus disponible.');
        if (board.widgets[index].locked) throw new Error('Déverrouillez ce widget avant de le supprimer.'); board.widgets.splice(index, 1); await persist(); close();
      } }
    ]);
  }
  function exportDialog() {
    const formats = [{ value: 'html', label: 'Rapport HTML autonome · graphiques, critères, qualité et toutes les lignes' }, { value: 'xlsx', label: 'Bilan Excel' }, { value: 'csv', label: 'Bilan CSV' },
      { value: 'pdf', label: 'PDF visuel de la page' }, { value: 'png', label: 'PNG de la zone visible' }];
    const format = select(formats, 'html');
    dialog('Exporter le résultat actif', el('div', {}, field('Format', format), caption('Le cartouche contient la date, le périmètre, les filtres et la complétude. Le PNG capture la zone visible ; le rapport HTML contient les données complètes du périmètre.')), [
      { label: 'Choisir le fichier et exporter', primary: true, action: async close => {
        const selectedFormat = format.value;
        close();
        const wasOpen = provenance.open;
        if (selectedFormat === 'png' || selectedFormat === 'pdf') provenance.open = true;
        ctx.root.classList.add('analytics-exporting');
        if (selectedFormat === 'png') provenance.scrollIntoView({ block: 'start', behavior: 'instant' });
        try {
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          const result = await call(ctx, 'reports.export', { ...runArgs, format: selectedFormat });
          if (!result.cancelled) ctx.notify(`Export enregistré : ${result.path}`);
        }
        finally { ctx.root.classList.remove('analytics-exporting'); provenance.open = wasOpen; }
      } }
    ]);
  }
  function displayHistory(result) {
    for (const item of result.widgets) {
      const widget = board.widgets.find(w => w.id === item.id); if (!widget) continue;
      const article = [...boardHost.querySelectorAll('[data-widget-id]')].find(node => node.dataset.widgetId === item.id); if (!article) continue;
      const host = widget.kind === 'history' ? article.querySelector('.analytics-history-mount') : article.querySelector('[data-history-widget]');
      const content = el('div', { class: widget.kind === 'history' ? 'analytics-history-mount' : 'analytics-mini-history', 'data-history-widget': item.id });
      const observed = item.history?.points?.some(point => point.comparable && point.value != null && Number.isFinite(point.value));
      if (widget.kind === 'history' || observed) content.append(sparkline(item.history, widget.kind === 'metric'));
      else content.append(caption('Historique : aucune observation comparable.'));
      if (item.comparison && widget.kind === 'metric') {
        const c = item.comparison;
        if (c.delta != null) content.append(caption(`Moyennes : ${number(c.before)} → ${number(c.after)} · Δ ${number(c.delta, item.history?.unit)}`));
        if (widget.showDescription) content.append(details('Observations historiques',
          caption(`Deux fenêtres de ${c.periodDays} j · ${c.beforeSamples}/${c.afterSamples} observations · ${c.breaks} rupture(s)`),
          c.delta == null ? caption('Une fenêtre ne contient pas d’observation exploitable ; la variation reste indisponible.') : null,
          caption(c.note), caption(item.history?.explanation ?? '')));
      }
      if (host) host.replaceWith(content); else article.append(content);
    }
  }
  async function loadHistory() {
    historyController?.abort(); const controller = new AbortController(); historyController = controller;
    status.replaceChildren(caption('Relecture des mêmes filtres et identités sur les archives comparables…'), button('Annuler les mini-historiques', () => controller.abort()));
    try {
      const activeRun = runArgs.runId;
      const result = await ctx.api.call('dashboard.history', { ...runArgs }, { signal: AbortSignal.any([ctx.signal, controller.signal]) });
      if (ctx.signal.aborted || controller !== historyController || activeRun !== runArgs.runId) return; historyData = result; displayHistory(result);
      status.replaceChildren(caption('Mini-historiques recalculés · archives comparables, ruptures conservées.'), command(ctx, 'Recalculer l’historique', loadHistory));
    } catch (reason) {
      if (ctx.signal.aborted || controller !== historyController) return;
      status.replaceChildren(caption(reason.name === 'AbortError' ? 'Calcul historique annulé ; les indicateurs du relevé restent disponibles.' : `Historique indisponible : ${reason.message}`), command(ctx, 'Réessayer l’historique', loadHistory));
    }
  }
  bindDashboardLayout({ host: boardHost, getBoard: () => board, canEdit: () => editing && !saving,
    commit: async focus => { try { await persist(focus); } catch (reason) { error(ctx, reason); } },
    announce: text => { editStatus.textContent = text; }, signal: ctx.signal });
  draw();
  if (board.widgets.some(w => !w.hidden && (w.kind === 'history' || w.kind === 'metric' && w.showMiniHistory))) void loadHistory();
  return () => { historyController?.abort(); cleanup(); };
}

function newWidget(kind, sourceKey) { return { id: uuid(), kind, sourceKey, title: '', span: kind === 'metric' ? 3 : 6, height: kind === 'metric' ? 220 : 380, autoHeight: true,
  startNewRow: false, stackedMode: 'percent', paretoThresholdPercent: 80, showMiniHistory: true, historyPeriodDays: 7, style: 'surface', accent: 'blue', presentation: 'bars', topCount: 8,
  showDescription: true, hidden: false, locked: false, note: '', targetValue: null, warningValue: null, criticalValue: null, lowerIsBetter: true, section: '', filters: [], participationRoster: [], histogramBreaks: [1, 7, 14, 30, 90] }; }
function qualitySummary(quality) {
  if (!quality) return null;
  const observations = [['Avant le transfert', quality.initialSample], ['Après le transfert', quality.finalSample]].filter(([, sample]) => sample);
  const sample = observations.find(([, value]) => value.representationVerified === false)?.[1] ?? observations.at(-1)?.[1];
  const item = (title, value, tone = '') => el('div', { class: `analytics-quality-fact ${tone}` }, el('span', { text: title }), el('strong', { text: value }));
  return el('div', { class: 'analytics-quality-receipt' }, el('div', { class: 'analytics-quality-facts' },
    item('Lignes reçues', `${quality.sourceRows == null ? 'Nombre inconnu' : formatNumber(quality.sourceRows)}${quality.expectedRows == null ? '' : ` / ${formatNumber(quality.expectedRows)}`}`, quality.receptionComplete ? 'success' : 'warning'),
    item('Identifiants de l’échantillon', sample?.identityVerified == null ? 'Contrôle indisponible' : `${sample.sampleSize - sample.missingSampleIds} / ${sample.sampleSize} rapprochés`, quality.sampleIdentityVerified === false ? 'warning' : ''),
    item('Comparaison des valeurs', sample?.representationVerified == null ? 'Contrôle indisponible' : sample.differentCells ? `${sample.differentCells} / ${sample.comparedCells} à vérifier` : `${sample.comparedCells} concordantes`, quality.representationVerified === false ? 'warning' : ''),
    item('Caractères de contrôle', quality.unusualControlCount ? `${formatNumber(quality.unusualControlCount)} conservés dans la source` : 'Aucun détecté', quality.unusualControlCount ? 'warning' : '')),
    observations.length ? details('Détail des deux contrôles', table(observations.map(([phase, value]) => ({ phase,
      ids: value.identityVerified == null ? 'Indisponible' : `${value.sampleSize - value.missingSampleIds} / ${value.sampleSize}`,
      values: value.representationVerified == null ? 'Indisponible' : `${value.differentCells} écarts / ${value.comparedCells}`,
      formats: value.equivalentFormatCells })), [{ key: 'phase', label: 'Contrôle' }, { key: 'ids', label: 'Identifiants rapprochés' }, { key: 'values', label: 'Valeurs comparées' }, { key: 'formats', label: 'Formats équivalents prouvés' }])) : null);
}
function describeFilters(filters = []) { return filters.map(f => `${f.exclude ? 'sauf ' : ''}${f.sourceKey}${f.kind === 'metric' ? '' : ` = ${f.values?.length ? f.values.join(' OU ') : f.value}${f.kind === 'date' ? ` → ${f.value2}` : ''}${f.includeDescendants ? ' (sous-catégories incluses)' : ''}`}`).join(' · '); }
function catalogEntries(catalog) {
  return [...catalog.metrics.map(m => ({ ...m, kind: 'metric' })), ...catalog.distributions.map(m => ({ ...m, kind: 'distribution' })),
    ...catalog.matrices.map(m => ({ ...m, kind: 'matrix' })), ...catalog.numeric.map(m => ({ ...m, kind: 'histogram' })),
    ...catalog.boxes.map(m => ({ ...m, kind: 'boxplot' })), ...catalog.scatters.map(m => ({ ...m, kind: 'scatter' })),
    ...catalog.weather.map(m => ({ ...m, kind: 'weather' })), ...catalog.metrics.map(m => ({ ...m, kind: 'history', label: `${m.label} · observations` })),
    ...(catalog.oldest ?? []).map(m => ({ ...m, kind: 'oldest' })), ...(catalog.participation ?? []).map(m => ({ ...m, kind: 'participation' })),
    ...(catalog.sites ?? []).map(m => ({ ...m, kind: 'sites' })),
    ...['timeline', 'attention', 'quality', 'note'].map(kind => ({ kind, key: '', label: kindNames[kind], available: true })),
    { kind: 'map', key: 'all', label: 'Carte des tickets · tous', available: true }, { kind: 'map', key: 'active', label: 'Carte des tickets · actifs', available: true }];
}
function widgetTitle(widget, catalog) { return widget.settings.title || catalogEntries(catalog).find(e => e.kind === widget.settings.kind && e.key === widget.settings.sourceKey)?.label || widget.content?.label || widget.content?.title || kindNames[widget.settings.kind]; }
function comparisonCard(a, b) {
  const keys = new Set([...a.metrics.map(m => m.key), ...b.metrics.map(m => m.key)]);
  const rows = [...keys].map(key => { const left = a.metrics.find(m => m.key === key); const right = b.metrics.find(m => m.key === key); return { label: left?.label ?? right.label,
    a: number(left?.value, left?.unit), b: number(right?.value, right?.unit), difference: left?.value != null && right?.value != null && left.unit === right.unit ? number(left.value - right.value, left.unit) : 'Indisponible',
    samples: `${left?.sample ?? a.rowCount} / ${right?.sample ?? b.rowCount}` }; });
  return card('Comparer les périmètres', caption(`A : ${a.scopeLabel} · B : ${b.scopeLabel}`), details(`${rows.length} indicateurs comparés avec leurs échantillons`,
    table(rows, [{ key: 'label', label: 'Indicateur' }, { key: 'a', label: 'A' }, { key: 'b', label: 'B' }, { key: 'difference', label: 'A − B' }, { key: 'samples', label: 'Échantillons A / B' }], { maxRows: 512 })));
}
function newBoardDialog(ctx, personal, go) {
  const name = input('Mon tableau de bord'); const preset = select(Object.entries(presetNames).map(([value, label]) => ({ value, label })), 'empty');
  dialog('Créer un tableau de bord', el('div', {}, field('Nom', name), field('Modèle', preset)), [{ label: 'Créer', primary: true, action: async close => {
    const result = await call(ctx, 'dashboard.board.create', { personal, name: name.value, preset: preset.value }); close(); go({ boardId: result.boardId, editing: true });
  } }]);
}
