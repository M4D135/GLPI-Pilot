import { el, button, toolbar, card, empty, badge, input, select, field, table, dialog, command, caption, details,
  checkbox, filterEditor, rowDialog, pager, formatNumber, formatDate, number, sparkline, error, clone, uuid, textArea } from './analytics-ui.js';
import { icon, extractionStatus, isSimulation, replaceChildren } from '../ui.js';
import { importLocalTestFile } from '../local-file-test.js';
import { createWorkspacePanels } from '../workspace-panels.js';
import { navigationVisible } from '../navigation.js';
import { widgetContent } from './analytics-charts.js';
import { comparisonCard } from './analytics-dashboard.js';
import { renderOldestGroups } from './analytics-oldest.js';
import { participationTable } from './analytics-participation.js';
import { sitesTable } from './analytics-sites.js';
import { catalogEntries, newWidget, openWidgetEditor, widgetTitle } from './workspace-widget-editor.js';
import { renderHistory } from './analytics-history.js';
import { renderQueries } from './analytics-queries.js';
import { renderTechnicians, renderPlanning } from './workplace-technicians.js';
import { renderAssistant } from './analytics-assistant.js';

const memories = new WeakMap();
const tools = {
  history: ['Comparer les observations', renderHistory], queries: ['Construire une analyse', renderQueries],
  technicians: ['Charge et disponibilité déclarée', renderTechnicians], planning: ['Organiser les capacités', renderPlanning],
  assistant: ['Assistant', renderAssistant]
};
const sectionIcons = { weather: 'sun', flow: 'chart', attention: 'bell', teams: 'users', analysis: 'layers', people: 'users', actions: 'check' };

export async function renderPilotWorkspace(ctx) {
  const { root, signal, api, state, notify, navigate } = ctx;
  const panels = createWorkspacePanels(ctx);
  root.classList.add('analytics', 'analytics-business', 'pilot-workspace');
  root.dataset.workspace = 'pilot';
  let disposed = false, serial = 0, historyController = null, historyData = null, model = null, board = null, savedLayout = null, bootstrap = null, editing = false, busy = false;
  let memory = memories.get(state); if (!memory) { memory = new Map(); memories.set(state, memory); }
  const memoryKey = isSimulation(state.session) ? 'simulation' : 'normal';
  const previous = memory.get(memoryKey) || {};
  const scope = { focusKeys: ctx.params?.focusKeys ?? previous.focusKeys ?? [], technicianKey: ctx.params?.technicianKey ?? previous.technicianKey ?? '',
    crossFilters: ctx.params?.crossFilters ?? previous.crossFilters ?? [], composition: ctx.params?.boardId ?? previous.composition ?? 'service' };
  const call = (operation, args = {}, options = {}) => api.call(operation, args, { signal, ...options });
  const run = () => ({ runId: model.runId, revision: model.revision });
  const boardArgs = () => scope.composition === 'service' ? { businessView: 'workspace' } : { boardId: scope.composition };
  const personal = () => scope.technicianKey.length > 0;
  const hero = el('section', { class: 'hero workspace-intro pilot-intro' });
  const commands = el('div', { class: 'workspace-commandbar no-print' });
  const scopeLabel = el('div', { class: 'workspace-scope-summary', role: 'status', 'aria-live': 'polite' });
  const provenance = el('details', { class: 'workspace-provenance analytics-provenance print-provenance' });
  const navigation = el('nav', { class: 'pilot-section-index no-print', 'aria-label': 'Repères dans le pilotage' });
  const content = el('div', { class: 'pilot-composition analytics-board-host' });
  const status = el('div', { class: 'workspace-operation-status', role: 'status' });
  root.append(hero, commands, scopeLabel, provenance, navigation, status, content);

  function panelButton(label, name, symbol) {
    return command(ctx, label, () => openTool(name), { icon: symbol, attributes: { 'data-open-panel': name } });
  }
  async function openTool(name) {
    const tool = tools[name]; if (!tool) return;
    const allowed = name !== 'assistant' || navigationVisible('assistant', state.settings.interface);
    if (!allowed) return;
    const modal = await panels.open(tool[0], tool[1], { panel: name, ...(model ? run() : {}), technicianKey: scope.technicianKey });
    // Queries and personal planning can change source interpretation. Rebuild
    // this single scope after closing their editor, not an obsolete old page.
    modal?.element.addEventListener('pilot:closed', () => { if (!disposed) void load().catch(reason => error(ctx, reason)); }, { once: true });
  }
  function focusText() {
    const labels = new Map([...(bootstrap?.focus || []).map(item => [item.key, item.label]), ...(bootstrap?.groups || []).map(label => [`group:${label}`, label])]);
    return scope.focusKeys.length ? scope.focusKeys.map(key => labels.get(key) || key).join(' + ') : 'Toutes les équipes';
  }
  function drawHeader() {
    const receipt = extractionStatus(state.session);
    replaceChildren(hero, el('div', {}, el('p', { class: 'workspace-eyebrow', text: 'PILOTAGE DU SERVICE' }),
      el('h2', { text: 'Comprendre. Prioriser. Agir.' }),
      el('p', { text: 'La météo, la charge et les tickets à traiter se lisent ensemble. Chaque chiffre ouvre les lignes qui le composent.' }),
      el('div', { class: 'workspace-intro-meta' }, badge(isSimulation(state.session) ? 'Simulation active' : model?.historical ? 'Observation archivée' : 'Relevé actif', 'info'),
        el('span', { text: state.session.loaded ? `Observé le ${formatDate(model?.snapshot?.extractedAt || state.session.date)}` : 'Prêt à accueillir vos données' }))),
      el('div', { class: 'hero-icon', 'aria-hidden': 'true' }, icon('chart')));
    if (!model) {
      replaceChildren(commands, toolbar(panelButton('Comparer les observations', 'history', 'history'), panelButton('Construire une analyse', 'queries', 'filter'),
        command(ctx, 'Régler la météo', () => navigate('settings', { panel: 'pilot-settings' }), { icon: 'sun' })));
      return;
    }
    const userBoards = (personal() ? state.settings.dashboard.personalLayout : state.settings.dashboard.layout)?.boards || [];
    const composition = select([{ value: 'service', label: 'Pilotage du service' }, ...userBoards.map(item => ({ value: item.id, label: item.name }))], scope.composition);
    composition.setAttribute('aria-label', 'Composition du pilotage');
    composition.addEventListener('change', () => { scope.composition = composition.value; void load().catch(reason => error(ctx, reason)); });
    const technician = select([{ value: '', label: 'Tous les techniciens' }, ...model.roster.map(person => ({ value: person.key, label: person.name })),
      ...(scope.technicianKey && !model.roster.some(person => person.key === scope.technicianKey) ? [{ value: scope.technicianKey, label: 'Identité conservée · absente de ce relevé' }] : [])], scope.technicianKey);
    technician.setAttribute('aria-label', 'Technicien du périmètre');
    technician.addEventListener('change', () => { scope.technicianKey = technician.value; if (scope.composition !== 'service') scope.composition = 'service'; void load().catch(reason => error(ctx, reason)); });
    const customize = button(editing ? 'Terminer la composition' : 'Composer', () => { editing = !editing; drawHeader(); draw(); },
      { icon: editing ? 'check' : 'layers', attributes: { 'aria-pressed': String(editing), 'data-compose': 'pilot' } });
    const teamsButton = button('Équipes', null, { icon: 'users', attributes: { 'data-filter-teams': '', 'aria-haspopup': 'dialog' } });
    teamsButton.addEventListener('click', () => chooseTeams(teamsButton));
    const more = el('details', { class: 'workspace-menu' }, el('summary', { class: 'button', text: 'Analyser et organiser' }),
      el('div', { class: 'workspace-menu-content' }, panelButton('Comparer les observations', 'history', 'history'), panelButton('Construire une analyse', 'queries', 'filter'),
        panelButton('Charge des techniciens', 'technicians', 'users'), panelButton('Capacités et planning', 'planning', 'calendar'),
        navigationVisible('assistant', state.settings.interface) ? panelButton('Assistant', 'assistant', 'spark') : null,
        command(ctx, 'Seuils et équipes', () => navigate('settings', { panel: 'pilot-settings' }), { icon: 'settings' }),
        command(ctx, 'Nouvelle composition', createComposition, { icon: 'plus' }),
        command(ctx, 'Importer une composition', importComposition, { icon: 'folder' }),
        command(ctx, 'Exporter la composition', () => call('dashboard.layout.export', { ...boardArgs(), personal: personal() }), { icon: 'download' })));
    replaceChildren(commands, el('div', { class: 'workspace-filter-cluster' }, composition,
      teamsButton, technician,
      button(board.filters.length + scope.crossFilters.length ? `Filtres · ${board.filters.length + scope.crossFilters.length}` : 'Filtres', chooseFilters, { icon: 'filter' })),
      el('div', { class: 'workspace-action-cluster' }, customize, button('Exporter', exportReport, { icon: 'download' }), more));
    replaceChildren(scopeLabel, el('span', { class: 'scope-dot', 'aria-hidden': 'true' }), el('strong', { text: focusText() }),
      el('span', { text: `${formatNumber(model.snapshot.rowCount)} ${model.snapshot.grain || 'lignes'} dans le périmètre` }),
      scope.focusKeys.length || scope.crossFilters.length || board.filters.length || scope.technicianKey ? command(ctx, 'Réinitialiser', async () => { scope.focusKeys = []; scope.crossFilters = []; scope.technicianKey = ''; if (board.filters.length) await persist(draft => { draft.filters = []; }); else await load(); }) : null);
    replaceChildren(provenance, el('summary', {}, badge(receipt.receipt, receipt.tone), el('span', { text: 'Source et fiabilité du calcul' })),
      caption(receipt.details), caption(`${model.snapshot.rowCount} / ${model.snapshot.sourceRowCount} lignes · calcul ${formatDate(model.snapshot.calculatedAt)}`),
      isSimulation(state.session) ? caption('Simulation locale : couverture GLPI non vérifiée.') : null,
      ...model.snapshot.warnings.map(text => caption(text, 'warning')),
      command(ctx, 'Examiner les données', () => navigate('data', { panel: 'quality' }), { attributes: { 'data-open-panel': 'quality' } }));
    if (receipt.attention) replaceChildren(status, el('p', { class: 'notice warning', text: receipt.attention }));
    else replaceChildren(status);
    const elapsedHours = (Date.now() - Date.parse(model.snapshot.extractedAt)) / 3600000;
    if (!model.historical && board.freshnessWarningHours > 0 && Number.isFinite(elapsedHours) && elapsedHours > board.freshnessWarningHours)
      status.append(caption(`Relevé à actualiser : ${Math.floor(elapsedHours)} h depuis l’observation, rappel après ${board.freshnessWarningHours} h. Les volumes et couleurs restent ceux du relevé.`, 'workspace-freshness warning'));
    if (bootstrap.usingWeatherDefaults) status.append(caption('Seuils de départ à adapter dans Réglages → Seuils, équipes et règles météo.', 'workspace-weather-defaults'));
    const resolved = model.snapshot.metrics.find(item => item.key === 'resolved')?.value, closed = model.snapshot.metrics.find(item => item.key === 'closed')?.value;
    if (resolved != null && closed != null) scopeLabel.append(el('span', { class: 'workspace-closed-stock', text: `${formatNumber(resolved + closed)} résolus ou clos hors du stock actif` }));
  }

  async function load() {
    const ticket = ++serial; root.setAttribute('aria-busy', 'true'); historyController?.abort();
    try {
      bootstrap = await call('dashboard.business.bootstrap'); if (signal.aborted || ticket !== serial) return;
      if (!bootstrap.hasSource) {
        drawHeader();
        replaceChildren(content, card('Un seul espace pour lire votre activité', caption('Importez un CSV ou un classeur : la météo, les équipes et les priorités seront calculées sur les mêmes tickets.'),
          toolbar(command(ctx, 'Importer en simulation', () => importLocalTestFile(ctx), { primary: true, icon: 'folder' }),
            command(ctx, 'Essayer avec des tickets fictifs', async () => { await call('app.demo'); await navigate('pilot'); }, { icon: 'spark' }),
            button('Connecter GLPI', () => navigate('data'), { icon: 'download' }))));
        root.dataset.ready = 'true'; return;
      }
      const valid = new Set([...bootstrap.focus.map(item => item.key), ...bootstrap.groups.map(item => `group:${item}`)]);
      const previousCount = scope.focusKeys.length; scope.focusKeys = scope.focusKeys.filter(key => valid.has(key));
      if (previousCount !== scope.focusKeys.length) notify('Le périmètre a été actualisé : certaines équipes ne sont plus présentes dans ce relevé.', 'warning');
      const layouts = personal() ? state.settings.dashboard.personalLayout : state.settings.dashboard.layout;
      if (scope.composition !== 'service' && !layouts?.boards?.some(item => item.id === scope.composition)) {
        scope.composition = 'service'; notify('Cette composition n’est plus disponible. Le pilotage du service est affiché.', 'warning');
      }
      const candidate = await call('dashboard.get', { ...boardArgs(), focusKeys: scope.focusKeys, technicianKey: scope.technicianKey, crossFilters: scope.crossFilters });
      if (disposed || signal.aborted || ticket !== serial) return;
      model = candidate; historyData = null; savedLayout = clone(model.layout); board = model.layout.boards.find(item => item.id === model.boardId); ctx.analyticsRevision = model.revision;
      memory.set(memoryKey, clone(scope)); drawHeader(); draw(); root.dataset.ready = 'true';
      if (board.widgets.some(item => !item.hidden && (item.kind === 'history' || item.kind === 'metric' && item.showMiniHistory))) void loadHistory(ticket);
    } finally { if (ticket === serial) root.removeAttribute('aria-busy'); }
  }

  function draw() {
    root.dataset.businessEditing = String(editing); root.dataset.layoutEditing = String(editing);
    replaceChildren(content); replaceChildren(navigation);
    content.dataset.density = board.density || 'comfortable';
    if (model.comparison) content.append(comparisonCard(model.snapshot, model.comparison));
    const widgets = new Map(model.widgets.map(item => [item.id, item]));
    if (editing) content.append(el('div', { class: 'workspace-composition-tools no-print' },
      caption('Réglez les cartes, leur ordre et leur largeur. Les nombres et leurs périmètres restent ceux du moteur de calcul.'),
      toolbar(command(ctx, 'Ajouter un widget', addWidget, { icon: 'plus' }), command(ctx, 'Ajouter une section', () => sectionEditor(), { icon: 'plus' }),
        command(ctx, 'Nom et organisation', organizeComposition), command(ctx, 'Cartes et sections visibles', chooseVisibility),
        command(ctx, 'Annuler', async () => { await call('dashboard.layout.undo', { ...boardArgs(), personal: personal() }); await load(); }, { disabled: !model.layoutHistory.undo }),
        command(ctx, 'Rétablir', async () => { await call('dashboard.layout.redo', { ...boardArgs(), personal: personal() }); await load(); }, { disabled: !model.layoutHistory.redo }))));
    let rendered = 0;
    for (const section of board.sections) {
      if (section.hidden && !editing) continue;
      const items = board.widgets.filter(item => item.section === section.key && !item.hidden && (editing || !board.hideUnavailableWidgets || widgets.get(item.id)?.available !== false));
      if (!items.length && !editing) continue;
      const block = el('section', { class: `pilot-block accent-${section.accent}`, 'data-section-key': section.key, 'aria-label': section.title });
      const shortTitle = { weather: 'Météo', flow: 'Stock', attention: 'À traiter', teams: 'Équipes', analysis: 'Demandes', people: 'Contributions', actions: 'Notes' }[section.key] || section.title;
      const go = button(shortTitle, () => block.scrollIntoView({ block: 'start', behavior: document.documentElement.dataset.motion === 'reduce' ? 'instant' : 'smooth' }), { icon: sectionIcons[section.key] || 'chart', title: section.title, attributes: { 'aria-label': section.title } });
      navigation.append(go);
      block.append(el('header', { class: 'pilot-block-heading' }, el('div', {},
        el('span', { class: 'workspace-eyebrow', text: String(++rendered).padStart(2, '0') }),
        el('h2', { text: section.title }), section.description ? caption(section.description) : null),
        toolbar(editing ? command(ctx, 'Régler la section', () => sectionEditor(section)) : null, section.key === 'teams' ? button('Voir sur la carte', () => navigate('terrain-map', { focusKeys: scope.focusKeys, technicianKeys: scope.technicianKey ? [scope.technicianKey] : [] }), { icon: 'map' }) : null)));
      const collapsed = !editing && (board.collapsedSections || []).includes(section.key);
      const toggle = button(collapsed ? 'Déplier' : 'Replier', async () => { await persist(draft => { const selected = new Set(draft.collapsedSections || []); if (selected.has(section.key)) selected.delete(section.key); else selected.add(section.key); draft.collapsedSections = [...selected]; }); },
        { attributes: { 'aria-label': `${collapsed ? 'Déplier' : 'Replier'} · ${section.title}`, 'aria-expanded': String(!collapsed) } });
      if (!editing) block.querySelector(':scope>header>.toolbar').append(toggle);
      const grid = el('div', { class: 'pilot-widget-grid analytics-widget-grid', hidden: collapsed });
      for (const setting of items) {
        const widget = widgets.get(setting.id); if (!widget) continue;
        const weather = setting.presentation === 'weather-volume';
        const title = widgetTitle(widget, model.catalog);
        const mainWeather = weather && setting.sourceKey === 'active' && !setting.filters.length;
        const article = el('article', { class: `analytics-widget pilot-widget style-${setting.style} accent-${setting.accent}${weather ? ' business-weather-card' : ''}${mainWeather ? ' business-weather-hero' : ''}${setting.kind === 'metric' && !weather ? ' analytics-kpi-card' : ''}`,
          'data-widget-id': setting.id, 'data-span': setting.span, 'data-widget-kind': setting.kind, 'data-auto-height': setting.autoHeight, 'data-available': widget.available,
          'data-weather-state': weather ? widget.content?.volumeWeather?.state || 'unknown' : undefined,
          style: { '--span': setting.span, '--widget-height': `${setting.height}px` } });
        const controls = editing ? toolbar(
          command(ctx, 'Régler', () => openWidgetEditor(ctx, { model, board, persist }, setting)),
          command(ctx, '↑', () => move(setting, -1), { disabled: setting.locked, title: 'Déplacer la carte vers le début' }),
          command(ctx, '↓', () => move(setting, 1), { disabled: setting.locked, title: 'Déplacer la carte vers la fin' }))
          : button('Détail', () => activate(widget, { kind: 'scope' }).catch(reason => error(ctx, reason)), { title: `Voir les lignes de ${title}` });
        article.append(el('div', { class: 'analytics-widget-heading' }, el('h3', { text: title }), controls),
          widgetContent(ctx, widget, selection => activate(widget, selection).catch(reason => error(ctx, reason)), (kind, key) => more(widget, kind, key),
            { snapshot: model.snapshot, historical: model.historical, compactWeather: !mainWeather, preserveWidgetAccent: editing || scope.composition !== 'service',
              onControls: () => navigate('data', { panel: 'quality' }), copyIds: async selection => { const result = await call('dashboard.copyIds', { ...run(), widgetId: widget.id, ...selection }); notify(`${formatNumber(result.count)} identifiants copiés.`); } }));
        if (!setting.autoHeight) article.classList.add('is-fixed-height');
        if (setting.startNewRow) grid.append(el('div', { class: 'analytics-new-row', 'aria-hidden': 'true' }));
        grid.append(article);
      }
      block.append(grid); content.append(block);
    }
    if (!rendered) content.append(empty('Aucune carte visible', 'Ouvrez Composer pour choisir les widgets et sections à afficher.'));
    if (historyData) displayHistory(historyData);
  }
  async function persist(mutate) {
    if (busy) throw new Error('La composition est déjà en cours d’enregistrement.');
    busy = true;
    try {
      const candidate = clone(savedLayout || model.layout);
      const currentBoard = candidate.boards.find(item => item.id === model.boardId);
      if (!currentBoard) throw new Error('Cette composition n’est plus disponible.');
      mutate?.(currentBoard, candidate);
      await call('dashboard.layout.save', { ...boardArgs(), personal: personal(), layout: candidate });
      savedLayout = clone(candidate); await load();
    } finally { busy = false; }
  }
  async function move(widget, direction) {
    const peers = board.widgets.filter(item => item.section === widget.section && !item.hidden);
    const position = peers.indexOf(widget), target = peers[position + direction]; if (!target) return;
    const a = board.widgets.indexOf(widget), b = board.widgets.indexOf(target);
    await persist(draft => { [draft.widgets[a], draft.widgets[b]] = [draft.widgets[b], draft.widgets[a]]; });
  }
  function chooseTeams(trigger) {
    const selected = new Set(scope.focusKeys), search = input('', { type: 'search', placeholder: 'Chercher une équipe ou un groupe', 'aria-label': 'Rechercher une équipe ou un groupe' });
    const fold = value => value.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');
    const entries = [...bootstrap.focus.map(item => ({ key: item.key, label: item.label, category: 'Équipe' })), ...bootstrap.groups.map(label => ({ key: `group:${label}`, label, category: 'Groupe GLPI' }))];
    const host = el('div', { class: 'workspace-choice-grid' });
    const drawChoices = () => replaceChildren(host, ...entries.filter(item => fold(item.label).includes(fold(search.value))).map(item => {
      const choice = checkbox(item.label, selected.has(item.key)); choice.element.append(el('small', { text: item.category }));
      choice.control.setAttribute('aria-label', `${item.category} · ${item.label}`);
      choice.control.addEventListener('change', () => choice.control.checked ? selected.add(item.key) : selected.delete(item.key)); return choice.element;
    }));
    search.addEventListener('input', drawChoices); drawChoices();
    const modal = dialog('Choisir plusieurs équipes', el('div', {}, caption('Les choix se cumulent ; chaque ticket reste compté une seule fois dans le périmètre.'),
      toolbar(search, button('Toutes', () => { selected.clear(); drawChoices(); })), host),
      [{ label: 'Annuler', action: close => close() }, { label: 'Appliquer', primary: true, action: async close => {
        if (selected.size > 64) throw new Error('Choisissez au maximum 64 équipes ou groupes.'); scope.focusKeys = [...selected]; close(); await load();
      } }], { returnFocus: trigger }); modal.element.dataset.workspacePanel = 'teams';
  }
  function chooseFilters() {
    const editor = filterEditor(model.catalog, [...board.filters, ...scope.crossFilters]);
    const comparison = filterEditor(model.catalog, board.comparisonFilters);
    const enabled = checkbox('Comparer avec un second périmètre indépendant', board.comparisonEnabled);
    const name = input('', { maxLength: 120 });
    const presets = model.layout.filterPresets || [];
    const preset = select([{ value: '', label: 'Choisir un contexte enregistré' }, ...presets.map((item, index) => ({ value: String(index), label: item.name }))]);
    const modal = dialog('Périmètre commun à toutes les analyses', el('div', {},
      field('Contexte enregistré', preset), toolbar(button('Charger le contexte', async () => {
        if (preset.value === '') return;
        const previousFilters = scope.crossFilters; scope.crossFilters = [];
        try { await persist(draft => { draft.filters = clone(presets[Number(preset.value)].filters); }); modal.close(); }
        catch (reason) { scope.crossFilters = previousFilters; throw reason; }
      }), button('Supprimer le contexte', async () => {
        if (preset.value === '') return;
        await persist((draft, layout) => { layout.filterPresets.splice(Number(preset.value), 1); }); modal.close();
      })),
      el('h3', { text: 'Périmètre A · toutes les cartes' }), editor.element,
      caption('Ces conditions sont enregistrées dans la composition. Les équipes et le technicien choisis en haut restent visibles et s’y ajoutent.'),
      field('Nom du contexte à conserver (facultatif)', name),
      details('Comparer deux périmètres', enabled.element, el('h3', { text: 'Périmètre B' }), comparison.element,
        caption('B utilise la même source et la même identité personnelle éventuelle, mais ne reçoit ni les filtres ni la sélection d’équipes de A.'))),
      [{ label: 'Appliquer', primary: true, action: async close => {
        const filters = editor.read(), previousFilters = scope.crossFilters; scope.crossFilters = [];
        try { await persist((draft, layout) => {
          draft.filters = filters; draft.comparisonFilters = comparison.read(); draft.comparisonEnabled = enabled.control.checked;
          if (name.value.trim()) {
            if (layout.filterPresets.length >= 30) throw new Error('Supprimez un contexte avant d’en ajouter : 30 maximum.');
            layout.filterPresets.push({ name: name.value.trim(), filters: clone(filters) });
          }
        }); close(); } catch (reason) { scope.crossFilters = previousFilters; throw reason; }
      } }]); modal.element.dataset.workspacePanel = 'filters';
  }
  function sectionEditor(original = null) {
    const section = clone(original || { key: uuid(), title: 'Nouvelle section', description: '', accent: 'blue', hidden: false });
    const title = input(section.title, { maxLength: 120 }), description = textArea(section.description, 3);
    const colours = { blue: 'Bleu', teal: 'Turquoise', green: 'Vert', orange: 'Ambre', red: 'Rouge', purple: 'Violet', slate: 'Neutre' };
    const accent = select(model.catalog.accents.map(value => ({ value, label: colours[value] || value })), section.accent);
    const hidden = checkbox('Masquer cette section et ses cartes', section.hidden);
    const next = original ? board.sections[board.sections.findIndex(item => item.key === section.key) + 1]?.key : null;
    const position = select([{ value: '', label: 'À la fin' }, ...board.sections.filter(item => item.key !== section.key).map(item => ({ value: item.key, label: `Avant ${item.title}` }))], next || '');
    const body = el('div', {}, field('Titre de la section', title), field('Description de la section', description),
      toolbar(field('Couleur', accent), field('Position', position)), hidden.element);
    const actions = [{ label: 'Enregistrer la section', primary: true, action: async close => {
      if (!title.value.trim()) throw new Error('Donnez un titre à cette section.');
      await persist(draft => {
        Object.assign(section, { title: title.value.trim(), description: description.value, accent: accent.value, hidden: hidden.control.checked });
        if (!original && draft.sections.length >= 60) throw new Error('La composition contient déjà 60 sections.');
        draft.sections = draft.sections.filter(item => item.key !== section.key);
        const index = position.value ? draft.sections.findIndex(item => item.key === position.value) : draft.sections.length;
        draft.sections.splice(index < 0 ? draft.sections.length : index, 0, section);
      }); close();
    } }];
    if (original) {
      const destination = select(board.sections.filter(item => item.key !== section.key).map(item => ({ value: item.key, label: item.title })));
      body.append(details('Retirer la section', field('Conserver ses cartes dans', destination),
        button('Supprimer la section', async () => {
          await persist(draft => {
            if (draft.sections.length < 2) throw new Error('Conservez au moins une section.');
            if (draft.widgets.some(item => item.section === section.key && item.locked)) throw new Error('Déverrouillez les cartes de cette section avant de les déplacer.');
            for (const item of draft.widgets.filter(item => item.section === section.key)) item.section = destination.value;
            draft.sections = draft.sections.filter(item => item.key !== section.key);
            draft.collapsedSections = draft.collapsedSections.filter(key => key !== section.key);
          }); modal.close();
        }, { danger: true })));
    }
    const modal = dialog(original ? 'Modifier la section' : 'Créer une section', body, actions);
  }
  function organizeComposition() {
    const name = input(board.name, { maxLength: 120 }), description = textArea(board.description || '', 3);
    const density = select([{ value: 'comfortable', label: 'Aérée' }, { value: 'compact', label: 'Compacte' }], board.density);
    const freshness = input(board.freshnessWarningHours ?? '', { type: 'number', min: 1, max: 8760 });
    dialog('Organiser la composition', el('div', {}, field('Nom de la composition', name), field('Description', description), field('Densité', density),
      field('Rappel après un relevé ancien · heures', freshness, 'Vide : aucun rappel. Les couleurs météo ne changent pas.')),
      [{ label: 'Enregistrer', primary: true, action: async close => {
        if (!name.value.trim()) throw new Error('Donnez un nom à cette composition.');
        await persist(draft => { draft.name = name.value.trim(); draft.description = description.value; draft.density = density.value;
          draft.freshnessWarningHours = freshness.value.trim() ? Number(freshness.value) : null; }); close();
      } }, { label: 'Dupliquer', action: async close => {
        if (board.widgets.length > 60) throw new Error('Cette composition dépasse les 60 cartes autorisées par tableau personnalisé. Exportez sa disposition complète ou répartissez les cartes avant de la dupliquer.');
        const layout = await call('dashboard.layout.get', { personal: personal() });
        if (layout.boards.length >= 12) throw new Error('La disposition contient déjà 12 compositions.');
        const copy = clone(board); copy.id = uuid(); copy.name = `${name.value.trim() || board.name} · copie`.slice(0, 120); copy.widgets.forEach(item => item.id = uuid());
        layout.boards.push(copy); layout.activeBoardId = copy.id;
        await call('dashboard.layout.save', { personal: personal(), layout }); close(); await navigate('pilot', { boardId: copy.id, technicianKey: scope.technicianKey });
      } }, ...(scope.composition === 'service' ? [{ label: 'Rétablir la composition du service', danger: true, action: async close => {
        await call('dashboard.business.reset', { businessView: 'workspace' }); close(); await load();
      } }] : [{ label: 'Supprimer cette composition', danger: true, action: async close => {
        const layout = clone(model.layout);
        if (layout.boards.length < 2) throw new Error('Conservez au moins une composition personnalisée.');
        layout.boards = layout.boards.filter(item => item.id !== board.id); layout.activeBoardId = layout.boards[0].id;
        await call('dashboard.layout.save', { personal: personal(), layout }); close(); await navigate('pilot', { boardId: 'service', technicianKey: scope.technicianKey });
      } }])]);
  }
  async function importComposition() {
    const preview = await call('dashboard.layout.import', { personal: personal() }); if (preview.cancelled) return;
    dialog('Vérifier les compositions importées', el('div', {}, caption(`${preview.layout.boards.length} composition(s) dans le fichier · ${preview.existingBoards} déjà enregistrée(s).`),
      preview.canAppend ? table(preview.addedBoards, [{ key: 'originalName', label: 'Nom dans le fichier' }, { key: 'name', label: 'Nom après ajout' }, { key: 'widgetCount', label: 'Cartes' }]) : caption(preview.appendReason, 'warning'),
      caption('Ajouter conserve vos compositions. Remplacer retire toutes les compositions personnalisées de cette disposition ; le pilotage du service reste disponible.')),
      [{ label: 'Ajouter à mes compositions', primary: true, disabled: !preview.canAppend, action: async close => {
        const result = await call('dashboard.layout.append', { personal: personal(), layout: preview.layout, baseFingerprint: preview.baseFingerprint });
        close(); await navigate('pilot', { boardId: result.boardId, technicianKey: scope.technicianKey });
      } }, { label: 'Remplacer la disposition personnalisée', danger: true, action: async close => {
        await call('dashboard.layout.save', { personal: personal(), layout: preview.layout, baseFingerprint: preview.baseFingerprint });
        close(); await navigate('pilot', { boardId: preview.layout.activeBoardId, technicianKey: scope.technicianKey });
      } }]);
  }
  function chooseVisibility() {
    const draft = clone(board);
    const showUnavailable = checkbox('Conserver les cartes sans donnée calculable', !draft.hideUnavailableWidgets);
    const body = el('div', {}, showUnavailable.element, ...draft.sections.map(section => {
      const visible = checkbox(section.title, !section.hidden); visible.control.addEventListener('change', () => section.hidden = !visible.control.checked);
      return el('section', { class: 'workspace-visibility-group' }, visible.element, ...draft.widgets.filter(item => item.section === section.key).map(item => {
        const choice = checkbox(item.title || item.sourceKey, !item.hidden); choice.control.addEventListener('change', () => item.hidden = !choice.control.checked); return choice.element;
      }));
    }));
    dialog('Choisir les informations visibles', body, [{ label: 'Appliquer', primary: true, action: async close => {
      draft.hideUnavailableWidgets = !showUnavailable.control.checked; await persist(current => Object.assign(current, draft)); close();
    } }]);
  }
  function addWidget() {
    const search = input('', { type: 'search', placeholder: 'Indicateur, répartition ou méthode…', 'aria-label': 'Rechercher un widget' });
    const list = el('div', { class: 'workspace-catalog' }), section = select(board.sections.map(item => ({ value: item.key, label: item.title })));
    const favorites = new Set(model.layout.catalogFavorites || []), recent = model.layout.catalogRecent || [];
    const onlyFavorites = checkbox('Favoris uniquement'), onlyAvailable = checkbox('Disponibles seulement', true), onlyRecent = checkbox('Récemment utilisés');
    const entries = catalogEntries(model.catalog);
    const kind = select([{ value: '', label: 'Tous les types' }, ...[...new Set(entries.map(item => item.kind))].map(value => ({ value, label: ({ metric: 'Indicateurs', distribution: 'Répartitions', matrix: 'Matrices', weather: 'Météo', history: 'Historique', participation: 'Contribution', oldest: 'Plus anciens', sites: 'Sites', map: 'Carte', note: 'Notes' })[value] || value }))]);
    const modal = dialog('Ajouter une information', el('div', {}, toolbar(field('Destination', section), field('Rechercher', search), field('Type de widget', kind)),
      toolbar(onlyAvailable.element, onlyFavorites.element, onlyRecent.element), list,
      button('Enregistrer les favoris', async () => { await persist((draft, layout) => { layout.catalogFavorites = [...favorites]; }); modal.close(); })));
    function paint() {
      let shown = entries.filter(item => (!kind.value || item.kind === kind.value) && (!onlyAvailable.control.checked || item.available !== false)
        && (!onlyFavorites.control.checked || favorites.has(`${item.kind}:${item.key}`)) && (!onlyRecent.control.checked || recent.includes(`${item.kind}:${item.key}`))
        && `${item.label} ${item.key}`.toLocaleLowerCase('fr').includes(search.value.toLocaleLowerCase('fr')));
      if (onlyRecent.control.checked) shown.sort((a, b) => recent.indexOf(`${a.kind}:${a.key}`) - recent.indexOf(`${b.kind}:${b.key}`));
      replaceChildren(list, ...shown.slice(0, 300).map(item => {
        const token = `${item.kind}:${item.key}`, favorite = favorites.has(token);
        return el('div', {}, el('span', {}, el('strong', { text: item.label }), el('small', { text: item.available === false ? 'Source non disponible dans ce relevé' : 'Calculé à partir du relevé' })),
          toolbar(button(favorite ? '★' : '☆', () => { if (favorite) favorites.delete(token); else favorites.add(token); paint(); }, { title: 'Favori du catalogue', attributes: { 'aria-label': `Favori · ${item.label}`, 'aria-pressed': String(favorite) } }),
            button('Ajouter', async () => {
              if (board.widgets.length >= (board.id === 'business-workspace' ? 120 : 60)) throw new Error('La composition a atteint sa capacité.');
              const widget = newWidget(item.kind, item.key); widget.section = section.value; widget.showMiniHistory = false;
              await persist((draft, layout) => { draft.widgets.push(widget); layout.catalogFavorites = [...favorites]; layout.catalogRecent = [token, ...recent.filter(value => value !== token)].slice(0, 30); }); modal.close();
            }, { icon: 'plus' })));
      }));
      if (shown.length > 300) list.append(caption(`${shown.length} informations correspondent. Affinez la recherche pour voir celles au-delà des 300 premières.`));
      if (!shown.length) list.append(empty('Aucune information pour ces filtres'));
    }
    search.addEventListener('input', paint); kind.addEventListener('change', paint);
    for (const item of [onlyFavorites, onlyAvailable, onlyRecent]) item.control.addEventListener('change', paint);
    paint();
  }
  function createComposition() {
    const name = input('', { placeholder: 'Ex. Revue hebdomadaire' }); const preset = select([{ value: 'empty', label: 'Vide' }, { value: 'overview', label: 'Indicateurs essentiels' }, { value: 'service', label: 'Météo et activité' }, { value: 'analysis', label: 'Analyses détaillées' }]);
    dialog('Créer une composition', el('div', {}, field('Nom', name), field('Point de départ', preset)), [{ label: 'Créer', primary: true, action: async close => {
      if (!name.value.trim()) throw new Error('Donnez un nom à cette composition.'); const result = await call('dashboard.board.create', { personal: personal(), name: name.value.trim(), preset: preset.value });
      close(); await navigate('pilot', { ...scope, boardId: result.boardId, technicianKey: scope.technicianKey });
    } }]);
  }
  async function activate(widget, selection) {
    if (selection.kind === 'record') return navigate('records', { rowIndex: selection.rowIndex, revision: model.revision });
    return rowDialog(ctx, 'dashboard.rows', { ...run(), widgetId: widget.id, ...selection }, widgetTitle(widget, model.catalog));
  }
  async function more(widget, kind, key) {
    const body = el('div'), modal = dialog(widgetTitle(widget, model.catalog), body);
    async function page(offset) {
      const data = await call('dashboard.data', { ...run(), widgetId: widget.id, kind, key, offset, limit: 100 });
      if (signal.aborted || !modal.element.isConnected) return;
      let view;
      if (kind === 'oldest') view = renderOldestGroups(ctx, data, selection => activate(widget, selection));
      else if (kind === 'participation') view = participationTable(ctx, data.participants, widget.settings.sourceKey, selection => activate(widget, selection), data.maximumCount);
      else if (kind === 'sites') view = sitesTable(ctx, data.sites, selection => activate(widget, selection), async selection => { const result = await call('dashboard.copyIds', { ...run(), widgetId: widget.id, ...selection }); notify(`${result.count} identifiants copiés.`); });
      else {
        const columns = kind === 'distribution' ? [{ key: 'label', label: 'Catégorie' }, { key: 'count', label: 'Lignes' }, { key: 'value', label: 'Valeur', render: row => number(row.isAggregate ? row.value : row.value ?? row.count, row.unit) }]
          : kind === 'matrix' ? [{ key: 'rowLabel', label: 'Ligne' }, { key: 'columnLabel', label: 'Colonne' }, { key: 'value', label: 'Valeur' }]
          : kind === 'boxplot' ? ['label', 'sample', 'minimum', 'firstQuartile', 'median', 'thirdQuartile', 'p90', 'maximum'].map(key => ({ key, label: key }))
          : kind === 'scatter' ? [{ key: 'label', label: 'Ticket' }, { key: 'x', label: 'X' }, { key: 'y', label: 'Y' }]
          : [{ key: 'id', label: 'Ticket' }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'Statut' }];
        view = table(data.rows, columns, { maxRows: 100, onSelect: row => { modal.close();
          if (kind === 'attention' || kind === 'scatter') return navigate('records', { rowIndex: row.index ?? row.row, revision: model.revision });
          return activate(widget, { kind, key, label: row.label ?? row.rowLabel, columnLabel: row.columnLabel });
        } });
      }
      replaceChildren(body, view, pager(ctx, data.total, data.offset, 100, page));
    }
    await page(0);
  }
  function exportReport() {
    root.querySelector('.analytics-error')?.remove();
    const format = select([{ value: 'html', label: 'Rapport HTML complet' }, { value: 'xlsx', label: 'Bilan Excel' }, { value: 'csv', label: 'Bilan CSV' }, { value: 'pdf', label: 'PDF de la composition' }, { value: 'png', label: 'PNG de la zone visible' }]);
    dialog('Exporter ce périmètre', field('Format', format), [{ label: 'Exporter', primary: true, action: async close => {
      const selected = format.value; close(); const wasOpen = provenance.open; provenance.open = true;
      try { await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); const result = await call('reports.export', { ...run(), format: selected }); if (!result.cancelled) notify('Export enregistré.'); }
      catch (reason) { error(ctx, reason); }
      finally { provenance.open = wasOpen; }
    } }]);
  }
  async function loadHistory(ticket) {
    historyController = new AbortController(); const local = historyController;
    try {
      const result = await call('dashboard.history', run(), { signal: AbortSignal.any([signal, local.signal]) });
      if (disposed || local.signal.aborted || ticket !== serial) return;
      historyData = result; displayHistory(result);
    } catch (reason) { if (!signal.aborted && !local.signal.aborted && ticket === serial) status.append(caption(`Historique : ${reason.message}`, 'warning')); }
  }
  function displayHistory(result) {
    for (const item of result.widgets) {
      const setting = board.widgets.find(widget => widget.id === item.id);
      const article = [...content.querySelectorAll('[data-widget-id]')].find(node => node.dataset.widgetId === item.id);
      if (!setting || !article) continue;
      const host = article.querySelector(setting.kind === 'history' ? '.analytics-history-mount' : '[data-history-widget]');
      const history = el('div', { class: setting.kind === 'history' ? 'analytics-history-mount' : 'analytics-mini-history', 'data-history-widget': item.id });
      const observed = item.history?.points?.some(point => point.comparable && point.value != null && Number.isFinite(point.value));
      history.append(setting.kind === 'history' || observed ? sparkline(item.history, setting.kind === 'metric') : caption('Historique : aucune observation comparable.'));
      if (item.comparison && setting.kind === 'metric') {
        const comparison = item.comparison;
        if (comparison.delta != null) history.append(caption(`Moyennes : ${number(comparison.before)} → ${number(comparison.after)} · Δ ${number(comparison.delta, item.history?.unit)}`));
        if (setting.showDescription) history.append(details('Observations historiques',
          caption(`Deux fenêtres de ${comparison.periodDays} j · ${comparison.beforeSamples}/${comparison.afterSamples} observations · ${comparison.breaks} rupture(s)`),
          comparison.delta == null ? caption('Une fenêtre ne contient pas d’observation exploitable ; la variation reste indisponible.') : null,
          caption(comparison.note), caption(item.history?.explanation || '')));
      }
      if (host) host.replaceWith(history); else article.append(history);
    }
  }
  await load();
  if (!signal.aborted) {
    if (ctx.params?.focusSection) [...content.querySelectorAll('[data-section-key]')].find(node => node.dataset.sectionKey === ctx.params.focusSection)?.scrollIntoView({ block: 'start' });
    if (tools[ctx.params?.panel]) await openTool(ctx.params.panel);
  }
  return async () => {
    disposed = true; serial++; historyController?.abort();
    try { await panels.dispose(); } catch (reason) { disposed = false; throw reason; }
    root.classList.remove('analytics', 'analytics-business', 'pilot-workspace');
  };
}

export const pages = [{ id: 'pilot', title: 'Pilotage', icon: 'chart', group: 'Espaces', render: renderPilotWorkspace }];
