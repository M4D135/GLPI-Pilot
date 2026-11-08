import { el, caption } from './analytics-ui.js';
import { passiveWidget, slideCanvas, widgetIds } from './presentation-layout.js';
export { preparePilotPresentation } from './presentation-editor.js';

/** Print the exact reviewed slide geometry from the same frozen calculation. */
export function presentationPdfDocument(preview, ids, sources) {
  if (!preview?.selectionToken || JSON.stringify(preview.widgets?.map(widget => widget.id)) !== JSON.stringify(ids))
    throw new Error('Le bilan d’export ne correspond plus à la sélection. Reprenez la présentation.');
  if (!preview.presentation) return legacyPresentationPdfDocument(preview, ids, sources);
  if (JSON.stringify(widgetIds(preview.presentation)) !== JSON.stringify(ids))
    throw new Error('La composition du PDF ne correspond plus aux widgets sélectionnés.');
  const values = new Map((preview.context || []).map(item => [item.key, item.value]));
  const context = { boardName: values.get('Tableau') || preview.title, sourceKind: values.get('Source de données') || 'Relevé GLPI',
    observedLabel: values.get('Date du relevé') || 'Date inconnue', scopeText: values.get('Filtres communs') || 'Périmètre du tableau',
    filterText: [values.get('Recherche / contexte source'), values.get('Périmètre personnel')].filter(Boolean).join(' · ') };
  const detail = ['Recherche : ' + (values.get('Recherche / contexte source') || 'aucune recherche supplémentaire'),
    'Personnel : ' + (values.get('Périmètre personnel') || 'Tous'), 'Filtres : ' + (values.get('Filtres communs') || 'Aucun'),
    ...(preview.warnings?.length ? ['Réserves : ' + preview.warnings.join(' · ')] : [])].join(' | ');
  const catalog = new Map(preview.widgets.map(widget => {
    const node = sources?.get(widget.id);
    if (!node) throw new Error('Une information sélectionnée n’est plus disponible dans le tableau.');
    return [widget.id, { id: widget.id, title: widget.title, node }];
  }));
  const root = el('main', { class: 'presentation-export-document analytics analytics-business',
    'data-presentation-export': preview.selectionToken, 'data-presentation-layout': 'slides' });
  preview.presentation.slides.forEach((slide, index) => {
    const canvas = slideCanvas(slide, catalog, context, { number: index + 1, total: preview.presentation.slides.length });
    canvas.querySelectorAll('[data-presented-widget]').forEach(node => node.dataset.exportWidget = node.dataset.presentedWidget);
    const footer = canvas.querySelector('.presentation-canvas-footer');
    footer.classList.add('presentation-export-footer');
    const widgetFilters = specificWidgetFilters(preview).filter(widget => slide.items.some(item => item.widgetId === widget.id))
      .map(widget => `${widget.title} : ${widget.filters}`);
    footer.append(el('p', { class: 'presentation-export-detail', text: [detail, ...widgetFilters].join(' | ') }));
    root.append(el('section', { class: 'presentation-export-page', 'data-export-slide': slide.id }, canvas));
  });
  // Measure only the dedicated provenance area. Long context receives readable
  // appendix pages; it never reduces or covers the saved widget geometry.
  root.classList.add('presentation-export-measuring'); document.body.append(root);
  const details = [...root.querySelectorAll('.presentation-export-detail')];
  const overflow = details.some(node => node.scrollHeight > node.clientHeight + 1);
  if (overflow) {
    details.forEach(node => node.textContent = 'Contexte détaillé et réserves : voir les pages de contexte à la fin du document.');
    appendContextPages(root, preview, context);
  }
  root.classList.remove('presentation-export-measuring'); document.body.classList.add('presentation-exporting');
  return () => { root.remove(); document.body.classList.remove('presentation-exporting'); };
}

function specificWidgetFilters(preview) {
  const common = (preview.context || []).find(item => item.key === 'Filtres communs')?.value?.trim();
  return preview.widgets.filter(widget => {
    const filters = widget.filters?.trim();
    return filters && filters !== 'Aucun' && filters !== common;
  });
}

function appendContextPages(root, preview, context) {
  // Conservative fixed-width wrapping is also deterministic in the offline
  // Windows runtime. Split long words as well as paragraphs, without dropping text.
  const lines = [];
  const entries = [...(preview.context || []), ...specificWidgetFilters(preview)
    .map(widget => ({ key: `Filtres du widget « ${widget.title} »`, value: widget.filters }))];
  for (const item of entries) {
    let text = `${item.key} : ${item.value}`;
    for (const paragraph of text.split(/\r?\n/)) {
      let rest = paragraph;
      while (rest.length > 120) {
        const blank = rest.lastIndexOf(' ', 120), end = blank > 60 ? blank : 120;
        lines.push(rest.slice(0, end)); rest = rest.slice(end).replace(/^ /, '');
      }
      lines.push(rest);
    }
    lines.push('');
  }
  // Paragraph separators belong between entries. A trailing separator at a
  // page boundary must not create an appendix with only a header and footer.
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  const count = Math.max(1, Math.ceil(lines.length / 26));
  for (let index = 0; index < count; index++) {
    const canvas = slideCanvas({ id: `export-context-${index}`, title: 'Source, filtres et réserves', items: [] }, new Map(), context,
      { number: index + 1, total: count });
    canvas.querySelector('[data-presentation-area]').replaceChildren(el('pre', { class: 'presentation-context-text', text: lines.slice(index * 26, (index + 1) * 26).join('\n') }));
    canvas.querySelector('.presentation-canvas-footer>span').textContent = `Contexte de la présentation · ${index + 1} / ${count}`;
    root.append(el('section', { class: 'presentation-export-page', 'data-export-context-page': String(index + 1) }, canvas));
  }
}

/** Legacy selected reports retain their complete tables, outside the slide format. */
function legacyPresentationPdfDocument(preview, ids, sources) {
  if (!preview?.selectionToken || JSON.stringify(preview.widgets?.map(widget => widget.id)) !== JSON.stringify(ids))
    throw new Error('Le bilan d’export ne correspond plus à la sélection. Reprenez la présentation.');
  const documentRoot = el('main', { class: 'presentation-export-document analytics analytics-business', 'data-presentation-export': preview.selectionToken },
    el('h1', { text: preview.title || 'Présentation GLPI Pilot' }),
    el('dl', { class: 'presentation-export-context' }, ...preview.context.flatMap(item => [el('dt', { text: item.key }), el('dd', { text: item.value })])));
  for (const widget of preview.widgets) {
    const source = sources.get(widget.id); if (!source) throw new Error('Une information sélectionnée n’est plus disponible dans le tableau.');
    const visual = passiveWidget(source);
    // Full backend tables replace the bounded on-screen previews; charts retain the same real rendering.
    if (widget.tables?.length) visual.querySelectorAll('.table-scroll').forEach(node => node.remove());
    const section = el('section', { class: 'presentation-export-section', 'data-export-widget': widget.id },
      el('h2', { text: widget.title }), caption(widget.scope || ''), widget.filters ? caption(widget.filters) : null, visual,
      ...(widget.notes || []).map(note => caption(note)));
    for (const data of widget.tables || []) section.append(el('section', { class: 'presentation-export-table' },
      el('h3', { text: data.title }), el('table', { class: 'data-table' },
        el('thead', {}, el('tr', {}, ...data.headers.map(header => el('th', { scope: 'col', text: header })))),
        el('tbody', {}, ...data.rows.map(row => el('tr', {}, ...row.map(value => el('td', { text: value }))))))));
    documentRoot.append(section);
  }
  document.body.append(documentRoot); document.body.classList.add('presentation-exporting');
  return () => { documentRoot.remove(); document.body.classList.remove('presentation-exporting'); };
}
