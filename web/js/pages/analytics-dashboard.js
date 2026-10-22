import { el, button, card, toolbar, empty, badge, input, select, field, table, dialog, call, command, heading, caption, details, choices, clone, uuid,
  number, formatNumber, formatDate, rowDialog, pager, filterEditor, checkbox, nullableNumber, textArea, sparkline, error } from './analytics-ui.js';
import { widgetContent } from './analytics-charts.js';

const kindNames = { metric: 'Indicateur', distribution: 'Répartition', weather: 'Météo', timeline: 'Chronologie', attention: 'Points d’attention', quality: 'Qualité',
  note: 'Note', matrix: 'Matrice', histogram: 'Histogramme', boxplot: 'Boîtes à moustaches', scatter: 'Nuage de points', history: 'Historique', map: 'Carte locale' };
const presetNames = { empty: 'Vide', overview: 'Pilotage', operations: 'Opérations', personal: 'Personnel', team: 'Équipe', deadlines: 'Échéances', quality: 'Qualité', geography: 'Géographie', analysis: 'Analyse' };

export async function renderDashboard(ctx) {
  const cleanup = heading(ctx, 'Tableaux de bord', 'Indicateurs calculés sur le relevé actif, périmètres reproductibles et observations historiques traçables.');
  let editing = ctx.params?.editing === true;
  const params = { boardId: ctx.params?.boardId, technicianKey: ctx.params?.technicianKey ?? '', crossFilters: ctx.params?.crossFilters ?? [] };
  const loading = empty('Calcul du tableau de bord…', 'Les widgets sont publiés ensemble lorsque leurs périmètres sont prêts.'); ctx.root.append(loading);
  const model = await call(ctx, 'dashboard.get', Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined)));
  if (ctx.signal.aborted) return cleanup;
  loading.remove(); ctx.analyticsRevision = model.revision;
  const layout = model.layout; const board = layout.boards.find(b => b.id === model.boardId); const personal = model.technicianKey.length > 0;
  const runArgs = { runId: model.runId, revision: model.revision }; const selected = new Set();
  const go = extra => ctx.navigate('dashboard', { ...params, boardId: board.id, editing, ...extra });
  const persist = async () => { await call(ctx, 'dashboard.layout.save', { personal, layout }); go({}); };
  const list = select(layout.boards.map(b => ({ value: b.id, label: b.name })), board.id);
  list.addEventListener('change', async () => { try { layout.activeBoardId = list.value; await call(ctx, 'dashboard.layout.save', { personal, layout }); go({ boardId: list.value }); } catch (reason) { error(ctx, reason); } });
  const people = select([{ value: '', label: 'Équipe entière' }, ...model.roster.map(t => ({ value: t.key, label: `${t.name} · ${formatNumber(t.count)} lignes` }))], model.technicianKey);
  people.addEventListener('change', () => go({ technicianKey: people.value, boardId: undefined, crossFilters: [] }));
  const editControl = button(editing ? 'Quitter l’édition' : 'Modifier le dashboard', () => { editing = !editing; draw(); });
  const organization = details('Autres actions', toolbar(command(ctx, 'Actualiser', () => go({})), command(ctx, '+ Tableau de bord', () => newBoardDialog(ctx, personal, go)),
    command(ctx, 'Annuler la modification', async () => { const result = await call(ctx, 'dashboard.layout.undo', { personal }); go({ boardId: result.boardId }); }, { disabled: !model.layoutHistory.undo }),
    command(ctx, 'Rétablir la modification', async () => { const result = await call(ctx, 'dashboard.layout.redo', { personal }); go({ boardId: result.boardId }); }, { disabled: !model.layoutHistory.redo })));
  const controls = toolbar(field('Disposition', list), field('Périmètre personnel', people), editControl,
    command(ctx, 'Filtres et comparaison', () => filtersDialog()), command(ctx, 'Exporter', () => exportDialog()), organization);
  controls.classList.add('analytics-primary-toolbar', 'no-print');
  const provenance = el('details', { class: 'analytics-provenance print-provenance', 'aria-label': 'Provenance du tableau de bord' },
    el('summary', {}, el('strong', { text: `${board.name}${personal ? ` · ${model.technicianKey}` : ''}` }),
      el('span', { text: `${formatNumber(model.snapshot.rowCount)} / ${formatNumber(model.snapshot.sourceRowCount)} lignes · ${formatDate(model.snapshot.extractedAt)}` }),
      badge(model.snapshot.complete ? 'Relevé complet' : 'Incomplet / non confirmé', model.snapshot.complete ? 'success' : 'warning'),
      model.historical ? badge('Archive') : ctx.state.session?.demo ? badge('Démonstration', 'warning') : null,
      model.commonFilters.length ? badge(`${model.commonFilters.length} filtre(s)`) : null),
    el('strong', { text: 'GLPI Pilot · provenance de ce calcul' }),
    caption(`Relevé : ${formatDate(model.snapshot.extractedAt)} · calcul : ${formatDate(model.snapshot.calculatedAt)} · ${formatNumber(model.snapshot.rowCount)} / ${formatNumber(model.snapshot.sourceRowCount)} lignes · ${model.snapshot.complete ? 'extraction complète' : 'extraction incomplète / non confirmée'}`),
    caption(`${model.snapshot.scopeLabel} · ${model.snapshot.sourceUrl || 'source locale'}`),
    caption(`Total annoncé dans la source : ${model.snapshot.sourceExpectedTotal == null ? 'inconnu' : formatNumber(model.snapshot.sourceExpectedTotal)} lignes.`),
    caption(`Filtres communs : ${describeFilters(model.commonFilters) || 'aucun'}`),
    ...board.widgets.filter(w => !w.hidden && w.filters?.length).map(w => caption(`Widget ${w.title || w.sourceKey} : ${describeFilters(w.filters)}`)),
    model.historical ? badge('Archive · règles et dénominateurs conservés') : null);
  const warnings = el('details', { class: 'analytics-warnings no-print', hidden: !model.snapshot.warnings.length },
    el('summary', {}, el('strong', { text: `${model.snapshot.warnings.length} réserve(s)` }), el('span', { text: model.snapshot.warnings[0] ?? '' })),
    ...model.snapshot.warnings.map(text => caption(text, 'warning')));
  const boardHost = el('div', { class: 'analytics-board-host' }); const editorHost = el('div', { class: 'analytics-editor-toolbar no-print' });
  const selectionHost = el('div', { class: 'analytics-selection-toolbar no-print' }); const status = el('div', { class: 'analytics-history-status no-print', role: 'status' });
  const cross = checkbox('Cliquer une catégorie pour filtrer les widgets', board.crossFilterOnClick);
  cross.control.addEventListener('change', () => { board.crossFilterOnClick = cross.control.checked; persist().catch(reason => error(ctx, reason)); });
  ctx.root.append(controls, provenance, warnings, toolbar(cross.element,
    command(ctx, 'Voir les lignes du périmètre', () => rowDialog(ctx, 'dashboard.rows', { ...runArgs, kind: 'scope' })),
    command(ctx, 'Effacer les filtres', async () => { if (board.filters.length) { board.filters = []; await call(ctx, 'dashboard.layout.save', { personal, layout }); } go({ crossFilters: [] }); }, { disabled: model.commonFilters.length === 0 }),
    command(ctx, 'Effectifs datés', () => ctx.navigate('populations')), command(ctx, 'Assistant sur ce périmètre', () => ctx.navigate('assistant', runArgs))),
    editorHost, selectionHost);
  if (model.comparison) ctx.root.append(comparisonCard(model.snapshot, model.comparison));
  ctx.root.append(status, boardHost);
  let historyController = null; let historyData = null;

  async function activate(widget, selection) {
    if (board.crossFilterOnClick && ['metric', 'distribution', 'map'].includes(selection.kind)) {
      const filter = selection.kind === 'metric' ? { kind: 'metric', sourceKey: selection.key, value: '', value2: '', exclude: false }
        : { kind: 'distribution', sourceKey: selection.kind === 'map' ? `geography-points-${selection.active ? 'active' : 'all'}` : selection.key, value: selection.label, value2: '', exclude: false };
      if (model.commonFilters.some(f => JSON.stringify(f) === JSON.stringify(filter))) return;
      if (model.commonFilters.length >= 12) throw new Error('Douze filtres sont déjà appliqués. Retirez une condition.');
      go({ crossFilters: [...params.crossFilters, filter] });
    } else await rowDialog(ctx, 'dashboard.rows', { ...runArgs, widgetId: widget.id, ...selection }, `${widget.settings.title || widget.settings.sourceKey} · lignes contributrices`);
  }
  async function more(widget, kind, key) {
    const content = el('div'); const modal = dialog('Toutes les valeurs', content);
    const load = async offset => {
      const data = await call(ctx, 'dashboard.data', { ...runArgs, widgetId: widget.id, kind, key, offset, limit: 100 });
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
    editorHost.replaceChildren(); selectionHost.replaceChildren(); boardHost.replaceChildren();
    editorHost.hidden = !editing; selectionHost.hidden = !editing;
    if (editing) {
      editorHost.append(toolbar(command(ctx, '+ Widget', () => catalogDialog()), command(ctx, 'Nom et organisation', () => boardDialog()),
        command(ctx, 'Importer une disposition', async () => {
          const preview = await call(ctx, 'dashboard.layout.import'); if (preview.cancelled) return;
          dialog('Vérifier la disposition importée', el('div', {}, caption(`${preview.layout.boards.length} tableaux de bord remplaceraient la disposition ${personal ? 'personnelle' : 'globale'}.`),
            table(preview.layout.boards, [{ key: 'name', label: 'Tableau' }, { key: 'widgets', label: 'Widgets', render: b => String(b.widgets.length) }])),
          [{ label: 'Remplacer cette disposition', primary: true, action: async close => { await call(ctx, 'dashboard.layout.save', { personal, layout: preview.layout }); close(); go({ boardId: preview.layout.activeBoardId }); } }]);
        }), command(ctx, 'Exporter la disposition', async () => { const r = await call(ctx, 'dashboard.layout.export', { personal }); if (!r.cancelled) ctx.notify(`Disposition exportée : ${r.path}`); }),
        command(ctx, 'Widgets masqués', () => hiddenDialog())));
      drawBulk();
    }
    const groups = [...new Set(board.widgets.filter(w => !w.hidden).map(w => w.section || ''))];
    for (const section of groups) {
      const widgets = board.widgets.filter(w => !w.hidden && (w.section || '') === section);
      const grid = el('div', { class: 'analytics-board', 'aria-label': section || board.name });
      for (const setting of widgets) {
        const widget = model.widgets.find(w => w.id === setting.id); if (!widget) continue;
        if (setting.startNewRow) grid.append(el('div', { class: 'analytics-new-row', 'aria-hidden': 'true' }));
        const header = el('div', { class: 'analytics-widget-heading' }, el('h2', { text: widgetTitle(widget, model.catalog) }));
        const controls = el('div', { class: 'analytics-widget-controls no-print' });
        if (editing) {
          const check = checkbox('Sélectionner', selected.has(setting.id)); check.control.setAttribute('aria-label', `Sélectionner ${widgetTitle(widget, model.catalog)}`);
          check.control.addEventListener('change', () => { if (check.control.checked) selected.add(setting.id); else selected.delete(setting.id); drawBulk(); });
          controls.append(check.element, command(ctx, 'Régler', () => widgetDialog(setting)),
            command(ctx, '↑', () => moveWidget(setting, -1), { disabled: setting.locked, title: 'Déplacer avant le widget précédent' }),
            command(ctx, '↓', () => moveWidget(setting, 1), { disabled: setting.locked, title: 'Déplacer après le widget suivant' }));
        } else controls.append(command(ctx, 'Lignes', () => rowDialog(ctx, 'dashboard.rows', { ...runArgs, widgetId: setting.id, kind: 'scope' }, widgetTitle(widget, model.catalog))));
        header.append(controls);
        const article = el('article', { class: `analytics-widget style-${setting.style} accent-${setting.accent}${setting.locked ? ' locked' : ''}`,
          style: { '--span': setting.span, '--widget-height': `${setting.height}px` }, 'data-widget-id': setting.id, 'data-auto-height': setting.autoHeight,
          draggable: editing && !setting.locked }, header,
          caption(`${formatNumber(widget.rowCount)} lignes · ${setting.filters?.length ? describeFilters(setting.filters) : widget.scopeLabel}`, 'analytics-widget-scope'),
          widgetContent(ctx, widget, selection => activate(widget, selection).catch(reason => error(ctx, reason)), (kind, key) => more(widget, kind, key)));
        article.addEventListener('dragstart', event => { event.dataTransfer.setData('text/plain', setting.id); event.dataTransfer.effectAllowed = 'move'; });
        article.addEventListener('dragover', event => { if (editing && !setting.locked) event.preventDefault(); });
        article.addEventListener('drop', async event => {
          if (!editing || setting.locked) return; event.preventDefault();
          const id = event.dataTransfer.getData('text/plain'); const source = board.widgets.find(w => w.id === id);
          if (!source || source.locked || source === setting) return;
          board.widgets.splice(board.widgets.indexOf(source), 1); source.section = setting.section;
          board.widgets.splice(board.widgets.indexOf(setting), 0, source); try { await persist(); } catch (reason) { error(ctx, reason); }
        });
        grid.append(article);
      }
      if (groups.length === 1 && section === '') boardHost.append(grid);
      else {
        const expanded = !board.collapsedSections.includes(section);
        const fold = el('details', { class: 'analytics-board-section', open: expanded }, el('summary', {}, el('strong', { text: `${section || 'Général'} · ${widgets.length}` })), grid);
        if (editing) fold.firstElementChild.append(toolbar(command(ctx, 'Section ↑', async () => moveSection(section, -1)), command(ctx, 'Section ↓', async () => moveSection(section, 1))));
        fold.addEventListener('toggle', () => {
          const wasOpen = !board.collapsedSections.includes(section); if (wasOpen === fold.open) return;
          board.collapsedSections = board.collapsedSections.filter(s => s !== section); if (!fold.open) board.collapsedSections.push(section);
          call(ctx, 'dashboard.layout.save', { personal, layout }).then(() => go({})).catch(reason => error(ctx, reason));
        }); boardHost.append(fold);
      }
    }
    if (!groups.length) boardHost.append(empty('Ce dashboard ne contient aucun widget visible', 'Passez en édition pour choisir des indicateurs dans le catalogue.'));
    if (historyData) displayHistory(historyData);
  }
  async function moveWidget(widget, delta) {
    const index = board.widgets.indexOf(widget); const next = index + delta; if (next < 0 || next >= board.widgets.length || board.widgets[next].locked) return;
    [board.widgets[index], board.widgets[next]] = [board.widgets[next], board.widgets[index]]; await persist();
  }
  async function moveSection(section, delta) {
    const sections = [...new Set(board.widgets.map(w => w.section || ''))]; const index = sections.indexOf(section); const next = index + delta;
    if (next < 0 || next >= sections.length) return;
    if (board.widgets.some(w => w.locked && [section, sections[next]].includes(w.section || ''))) throw new Error('Déverrouillez les widgets de ces sections avant de les déplacer.');
    [sections[index], sections[next]] = [sections[next], sections[index]];
    board.widgets.sort((a, b) => sections.indexOf(a.section || '') - sections.indexOf(b.section || '')); await persist();
  }
  function drawBulk() {
    const span = select([3, 4, 6, 8, 9, 12].map(v => ({ value: String(v), label: `${v} / 12 colonnes` })), '4');
    const height = input(320, { type: 'number', min: 160, max: 960, step: 20 }); const section = input('');
    const style = select([{ value: '', label: 'Conserver les styles' }, ...model.catalog.styles.map(value => ({ value, label: value }))]);
    const accent = select([{ value: '', label: 'Conserver les accents' }, ...model.catalog.accents.map(value => ({ value, label: value }))]);
    selectionHost.replaceChildren(toolbar(caption(`${selected.size} widgets sélectionnés`), field('Largeur', span), field('Hauteur', height), field('Section', section), field('Style commun', style), field('Accent commun', accent),
      command(ctx, 'Aligner la sélection', async () => {
        const chosen = board.widgets.filter(w => selected.has(w.id) && !w.locked); let sum = 0;
        for (const widget of chosen) {
          widget.span = Number(span.value); widget.height = Number(height.value); widget.section = section.value;
          if (style.value) widget.style = style.value; if (accent.value) widget.accent = accent.value;
          widget.startNewRow = sum === 0 || sum + widget.span > 12; sum = widget.startNewRow ? widget.span : sum + widget.span;
        }
        await persist();
      }, { disabled: selected.size === 0 }), command(ctx, 'Masquer la sélection', async () => { for (const widget of board.widgets) if (selected.has(widget.id) && !widget.locked) widget.hidden = true; await persist(); }, { disabled: selected.size === 0 })));
  }
  function boardDialog() {
    const name = input(board.name); const message = el('div', { role: 'alert' });
    dialog('Organiser ce tableau de bord', el('div', {}, field('Nom', name), message), [
      { label: 'Renommer', primary: true, action: async close => { board.name = name.value; await persist(); close(); } },
      { label: 'Dupliquer', action: async close => { const copy = clone(board); copy.id = uuid(); copy.name = `${name.value} · copie`.slice(0, 120); copy.widgets.forEach(w => w.id = uuid()); layout.boards.push(copy); layout.activeBoardId = copy.id; await call(ctx, 'dashboard.layout.save', { personal, layout }); close(); go({ boardId: copy.id }); } },
      { label: 'Supprimer ce tableau', danger: true, action: async close => {
        if (layout.boards.length === 1) { message.textContent = 'Conservez au moins un tableau de bord.'; return; }
        layout.boards.splice(layout.boards.indexOf(board), 1); layout.activeBoardId = layout.boards[0].id; await call(ctx, 'dashboard.layout.save', { personal, layout }); close(); go({ boardId: layout.activeBoardId });
      } }
    ]);
  }
  function hiddenDialog() {
    const hidden = board.widgets.filter(w => w.hidden);
    dialog('Widgets masqués', hidden.length ? table(hidden, [{ key: 'title', label: 'Widget', render: w => w.title || w.sourceKey }, { key: 'show', label: 'Action', render: w => command(ctx, 'Réafficher', async () => { w.hidden = false; await persist(); }) }]) : empty('Aucun widget masqué'));
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
  function catalogDialog() {
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
            const widget = newWidget(entry.kind, entry.key); board.widgets.push(widget);
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
    const source = select([], widget.sourceKey); const title = input(widget.title); const section = input(widget.section);
    const span = select([3, 4, 6, 8, 9, 12].map(value => ({ value: String(value), label: `${value} / 12` })), String(widget.span));
    const height = input(widget.height, { type: 'number', min: 160, max: 960, step: 20 }); const top = input(widget.topCount, { type: 'number', min: 3, max: 30 });
    const style = select(choices(model.catalog.styles), widget.style); const accent = select(choices(model.catalog.accents), widget.accent); const presentation = select(choices(model.catalog.presentations), widget.presentation);
    const stacking = select([{ value: 'percent', label: 'Pourcentage' }, { value: 'volume', label: 'Volume' }], widget.stackedMode); const pareto = input(widget.paretoThresholdPercent, { type: 'number', min: .1, max: 100, step: .1 });
    const automatic = checkbox('Hauteur automatique', widget.autoHeight); const row = checkbox('Commencer une nouvelle ligne', widget.startNewRow);
    const description = checkbox('Afficher les définitions et limites', widget.showDescription); const mini = checkbox('Mini-historique sur l’indicateur', widget.showMiniHistory);
    const locked = checkbox('Verrouiller déplacement et actions groupées', widget.locked); const hidden = checkbox('Masquer ce widget', widget.hidden);
    const period = input(widget.historyPeriodDays, { type: 'number', min: 1, max: 3650 }); const note = textArea(widget.note, 5);
    const target = nullableNumber(widget.targetValue, 'Objectif'); const warning = nullableNumber(widget.warningValue, 'Seuil de vigilance'); const critical = nullableNumber(widget.criticalValue, 'Seuil critique');
    const lower = checkbox('Une valeur plus basse est préférable', widget.lowerIsBetter); const breaks = input(widget.histogramBreaks.join(';'));
    const filters = filterEditor(model.catalog, widget.filters);
    const updateSources = () => {
      const old = source.value || widget.sourceKey; const entries = catalogEntries(model.catalog).filter(e => e.kind === kind.value);
      source.replaceChildren(...entries.map(e => el('option', { value: e.key, text: e.label })));
      if (old && !entries.some(e => e.key === old)) source.append(el('option', { value: old, text: `${old} · indisponible` }));
      source.value = old || entries[0]?.key || '';
    };
    kind.addEventListener('change', () => { widget.sourceKey = ''; source.value = ''; updateSources(); }); updateSources();
    const read = () => Object.assign(widget, { kind: kind.value, sourceKey: source.value, title: title.value, section: section.value,
      span: Number(span.value), height: Number(height.value), topCount: Number(top.value), style: style.value, accent: accent.value, presentation: presentation.value,
      stackedMode: stacking.value, paretoThresholdPercent: Number(pareto.value), autoHeight: automatic.control.checked, startNewRow: row.control.checked,
      showDescription: description.control.checked, showMiniHistory: mini.control.checked, historyPeriodDays: Number(period.value), locked: locked.control.checked,
      hidden: hidden.control.checked, note: note.value, targetValue: target.read(), warningValue: warning.read(), criticalValue: critical.read(), lowerIsBetter: lower.control.checked,
      histogramBreaks: breaks.value.trim() ? breaks.value.split(';').map(value => Number(value.trim().replace(',', '.'))) : [], filters: filters.read() });
    dialog('Configurer le widget', el('div', {}, el('div', { class: 'analytics-form-grid' }, field('Type', kind), field('Source', source), field('Titre facultatif', title), field('Section', section),
      field('Largeur', span), field('Hauteur', height), field('Top catégories', top), field('Style', style), field('Accent', accent), field('Présentation', presentation),
      field('Empilage', stacking), field('Seuil de Pareto (%)', pareto), field('Fenêtre historique (jours)', period), field('Bornes histogramme séparées par ;', breaks), target.element, warning.element, critical.element),
      toolbar(automatic.element, row.element, description.element, mini.element, locked.element, hidden.element, lower.element), field('Note', note), details('Filtres propres à ce widget', filters.element)), [
      { label: 'Enregistrer', primary: true, action: async close => { board.widgets[board.widgets.indexOf(original)] = read(); await persist(); close(); } },
      { label: 'Dupliquer', action: async close => { const copy = read(); copy.id = uuid(); copy.locked = false; board.widgets.push(copy); await persist(); close(); } },
      { label: 'Supprimer', danger: true, action: async close => { if (original.locked) throw new Error('Déverrouillez ce widget avant de le supprimer.'); board.widgets.splice(board.widgets.indexOf(original), 1); await persist(); close(); } }
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
      const content = el('div', { class: widget.kind === 'history' ? 'analytics-history-mount' : 'analytics-mini-history', 'data-history-widget': item.id }, sparkline(item.history, widget.kind === 'metric'));
      if (item.comparison && widget.kind === 'metric') {
        const c = item.comparison;
        content.append(caption(`Deux fenêtres de ${c.periodDays} j · ${c.beforeSamples}/${c.afterSamples} observations · ${c.breaks} rupture(s)`),
          caption(c.delta == null ? 'Comparaison indisponible : une fenêtre ne contient pas d’observation exploitable.' : `Moyennes observées : ${number(c.before)} → ${number(c.after)} · Δ ${number(c.delta, item.history?.unit)}`),
          details('Méthode', caption(c.note), caption(item.history?.explanation ?? '')));
      }
      if (host) host.replaceWith(content); else article.append(content);
    }
  }
  async function loadHistory() {
    historyController?.abort(); historyController = new AbortController();
    status.replaceChildren(caption('Relecture des mêmes filtres et identités sur les archives comparables…'), button('Annuler les mini-historiques', () => historyController.abort()));
    try {
      const result = await ctx.api.call('dashboard.history', runArgs, { signal: AbortSignal.any([ctx.signal, historyController.signal]) });
      if (ctx.signal.aborted) return; historyData = result; displayHistory(result);
      status.replaceChildren(caption('Mini-historiques recalculés · archives comparables, ruptures conservées.'), command(ctx, 'Recalculer l’historique', loadHistory));
    } catch (reason) {
      if (ctx.signal.aborted) return;
      status.replaceChildren(caption(reason.name === 'AbortError' ? 'Calcul historique annulé ; les indicateurs du relevé restent disponibles.' : `Historique indisponible : ${reason.message}`), command(ctx, 'Réessayer l’historique', loadHistory));
    }
  }
  draw();
  if (board.widgets.some(w => !w.hidden && (w.kind === 'history' || w.kind === 'metric' && w.showMiniHistory))) void loadHistory();
  return () => { historyController?.abort(); cleanup(); };
}

function newWidget(kind, sourceKey) { return { id: uuid(), kind, sourceKey, title: '', span: kind === 'metric' ? 3 : 6, height: kind === 'metric' ? 220 : 380, autoHeight: true,
  startNewRow: false, stackedMode: 'percent', paretoThresholdPercent: 80, showMiniHistory: true, historyPeriodDays: 7, style: 'surface', accent: 'blue', presentation: 'bars', topCount: 8,
  showDescription: true, hidden: false, locked: false, note: '', targetValue: null, warningValue: null, criticalValue: null, lowerIsBetter: true, section: '', filters: [], histogramBreaks: [1, 7, 14, 30, 90] }; }
function describeFilters(filters = []) { return filters.map(f => `${f.exclude ? 'sauf ' : ''}${f.sourceKey}${f.kind === 'metric' ? '' : ` = ${f.value}${f.kind === 'date' ? ` → ${f.value2}` : ''}`}`).join(' · '); }
function catalogEntries(catalog) {
  return [...catalog.metrics.map(m => ({ ...m, kind: 'metric' })), ...catalog.distributions.map(m => ({ ...m, kind: 'distribution' })),
    ...catalog.matrices.map(m => ({ ...m, kind: 'matrix' })), ...catalog.numeric.map(m => ({ ...m, kind: 'histogram' })),
    ...catalog.boxes.map(m => ({ ...m, kind: 'boxplot' })), ...catalog.scatters.map(m => ({ ...m, kind: 'scatter' })),
    ...catalog.weather.map(m => ({ ...m, kind: 'weather' })), ...catalog.metrics.map(m => ({ ...m, kind: 'history', label: `${m.label} · observations` })),
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
