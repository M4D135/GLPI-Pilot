import { el, button, toolbar, input, select, field, dialog, choices, clone, uuid, checkbox, nullableNumber, textArea, filterEditor, details, caption } from './analytics-ui.js';
import { participationRosterEditor } from './analytics-participation.js';

const kindNames = { metric: 'Indicateur', distribution: 'Répartition', weather: 'Météo', timeline: 'Chronologie', attention: 'Points d’attention', quality: 'Qualité',
  note: 'Note', matrix: 'Matrice', histogram: 'Histogramme', boxplot: 'Boîtes à moustaches', scatter: 'Nuage de points', history: 'Historique', map: 'Carte locale',
  oldest: 'Plus anciens', participation: 'Participation des agents', sites: 'Stock des sites' };
const accentNames = { blue: 'Bleu', teal: 'Turquoise', green: 'Vert', orange: 'Ambre', red: 'Rouge', purple: 'Violet', slate: 'Neutre' };
const styleNames = { surface: 'Carte', outline: 'Contour', accent: 'Accent', minimal: 'Sans cadre' };
const presentationNames = { bars: 'Barres', list: 'Liste', compact: 'Compacte', donut: 'Anneau', gauge: 'Jauge', pareto: 'Pareto', columns: 'Colonnes', heatmap: 'Carte de chaleur', stacked: 'Empilage', 'weather-volume': 'Météo des volumes' };
const namedChoices = (values, names) => values.map(value => ({ value, label: names[value] || value }));

export function openWidgetEditor(ctx, { model, board, persist }, original) {
  const sectionChoices = () => board.sections.map(section => ({ value: section.key, label: section.title }));
    const widget = clone(original); const kind = select(choices(Object.entries(kindNames).map(([key, label]) => ({ key, label }))), widget.kind);
    const source = select([], widget.sourceKey); const title = input(widget.title, { maxLength: 180 }); const section = select(sectionChoices(), widget.section);
    const span = select(Array.from({ length: 10 }, (_, index) => index + 3).map(value => ({ value: String(value), label: `${value} / 12` })), String(widget.span));
    const height = input(widget.height, { type: 'number', min: 160, max: 960, step: 20 }); const top = input(widget.topCount, { type: 'number', min: 3, max: 30 });
    const style = select(namedChoices(model.catalog.styles, styleNames), widget.style); const accent = select(namedChoices(model.catalog.accents, accentNames), widget.accent); const presentation = select(namedChoices(model.catalog.presentations, presentationNames), widget.presentation);
    const stacking = select([{ value: 'percent', label: 'Pourcentage' }, { value: 'volume', label: 'Volume' }], widget.stackedMode); const pareto = input(widget.paretoThresholdPercent, { type: 'number', min: .1, max: 100, step: .1 });
    const automatic = checkbox('Hauteur automatique', widget.autoHeight); const row = checkbox('Commencer une nouvelle ligne', widget.startNewRow);
    const description = checkbox('Afficher les définitions et limites', widget.showDescription); const mini = checkbox('Mini-historique sur l’indicateur', widget.showMiniHistory);
    const locked = checkbox('Verrouiller l’ordre et les dimensions', widget.locked); const hidden = checkbox('Masquer ce widget', widget.hidden);
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
        await persist(draft => { const target = draft.widgets.findIndex(item => item.id === original.id); if (target < 0) throw new Error('Ce widget n’est plus disponible.'); draft.widgets[target] = clone(widget); }); close();
      } },
      { label: 'Dupliquer', action: async close => { const copy = clone(read()); copy.id = uuid(); copy.locked = false; await persist(draft => draft.widgets.push(copy)); close(); } },
      { label: 'Supprimer', danger: true, action: async close => {
        const index = board.widgets.findIndex(item => item.id === original.id); if (index < 0) throw new Error('Ce widget n’est plus disponible.');
        if (board.widgets[index].locked) throw new Error('Déverrouillez ce widget avant de le supprimer.'); await persist(draft => { const target = draft.widgets.findIndex(item => item.id === original.id); if (target < 0) throw new Error('Ce widget n’est plus disponible.'); if (draft.widgets[target].locked) throw new Error('Déverrouillez ce widget avant de le supprimer.'); draft.widgets.splice(target, 1); }); close();
      } }
    ]);
  }

export function newWidget(kind, sourceKey) { return { id: uuid(), kind, sourceKey, title: '', span: kind === 'metric' ? 3 : 6, height: kind === 'metric' ? 220 : 380, autoHeight: true,
  startNewRow: false, stackedMode: 'percent', paretoThresholdPercent: 80, showMiniHistory: true, historyPeriodDays: 7, style: 'surface', accent: 'blue', presentation: 'bars', topCount: 8,
  showDescription: true, hidden: false, locked: false, note: '', targetValue: null, warningValue: null, criticalValue: null, lowerIsBetter: true, section: '', filters: [], participationRoster: [], histogramBreaks: [1, 7, 14, 30, 90] }; }

export function catalogEntries(catalog) {
  return [...catalog.metrics.map(m => ({ ...m, kind: 'metric' })), ...catalog.distributions.map(m => ({ ...m, kind: 'distribution' })),
    ...catalog.matrices.map(m => ({ ...m, kind: 'matrix' })), ...catalog.numeric.map(m => ({ ...m, kind: 'histogram' })),
    ...catalog.boxes.map(m => ({ ...m, kind: 'boxplot' })), ...catalog.scatters.map(m => ({ ...m, kind: 'scatter' })),
    ...catalog.weather.map(m => ({ ...m, kind: 'weather' })), ...catalog.metrics.map(m => ({ ...m, kind: 'history', label: `${m.label} · observations` })),
    ...(catalog.oldest ?? []).map(m => ({ ...m, kind: 'oldest' })), ...(catalog.participation ?? []).map(m => ({ ...m, kind: 'participation' })),
    ...(catalog.sites ?? []).map(m => ({ ...m, kind: 'sites' })),
    ...['timeline', 'attention', 'quality', 'note'].map(kind => ({ kind, key: '', label: kindNames[kind], available: true })),
    { kind: 'map', key: 'all', label: 'Carte des tickets · tous', available: true }, { kind: 'map', key: 'active', label: 'Carte des tickets · actifs', available: true }];
}
export function widgetTitle(widget, catalog) { return widget.settings.title || catalogEntries(catalog).find(e => e.kind === widget.settings.kind && e.key === widget.settings.sourceKey)?.label || widget.content?.label || widget.content?.title || kindNames[widget.settings.kind]; }
