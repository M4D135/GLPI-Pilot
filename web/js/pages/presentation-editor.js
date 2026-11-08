import { el, toolbar, input, field, select, dialog, caption, formatNumber, formatDate } from './analytics-ui.js';
import { extractionStatus, isSimulation, confirm, icon } from '../ui.js';
import { copy, newId, geometry, positionItem, arrangeSlide, initialDeck, groupedDeck, widgetIds, slideCanvas, slideThumbnail, fitCanvas } from './presentation-layout.js';

/** Saved slide composition over one frozen, explicitly scoped calculation. */
export function preparePilotPresentation({ ctx, model, board, content, scopeText, filterText, exportSelection, onSaved }) {
  const visible = new Map([...content.querySelectorAll('.pilot-widget[data-widget-id]')].map(node => [node.dataset.widgetId, node]));
  const available = board.widgets.filter(setting => !setting.hidden && visible.has(setting.id)
    && !board.sections.find(section => section.key === setting.section)?.hidden)
    .map(setting => ({ id: setting.id, setting, node: visible.get(setting.id).cloneNode(true),
      title: visible.get(setting.id).querySelector('h3')?.textContent || setting.title || setting.sourceKey,
      section: board.sections.find(section => section.key === setting.section)?.title || 'Analyse',
      kind: ['metric', 'weather'].includes(setting.kind) ? 'Chiffre clé' : ['attention', 'oldest', 'participation', 'sites'].includes(setting.kind) ? 'Tableau' : setting.kind === 'note' ? 'Texte' : 'Graphique' }));
  const catalog = new Map(available.map(item => [item.id, item]));
  const receipt = extractionStatus(ctx.state.session), observed = model.snapshot.extractedAt || ctx.state.session.date;
  const sourceKind = ctx.state.session.cachedSource ? 'Dernier relevé conservé' : isSimulation(ctx.state.session) ? 'Démonstration · données fictives'
    : model.historical ? 'Observation archivée' : ctx.state.session.fileSource ? 'Source CSV / Excel' : 'Relevé GLPI';
  const freshness = (Date.now() - Date.parse(observed)) / 3600000;
  const stale = Number.isFinite(freshness) && board.freshnessWarningHours > 0 && freshness > board.freshnessWarningHours;
  const warnings = [...new Set([receipt.attention, ...(model.snapshot.warnings || []), stale ? `Relevé à actualiser : ${Math.floor(freshness)} h depuis l’observation.` : ''].filter(Boolean))];
  const context = { boardName: board.name || 'Pilotage du service', sourceKind, observed, scopeText, filterText };
  let deck = initialDeck(available), fingerprint = '', current = 0, selectedId = '', dirty = true, ready = false, disposed = false, saving = false;
  let fit = null, viewer = null, selectedNode = null, activePointerCancel = null;
  const undo = [], redo = [], aborter = new AbortController();
  const abort = () => aborter.abort(); ctx.signal.addEventListener('abort', abort, { once: true });
  const call = (op, args = {}) => ctx.api.call(op, { runId: model.runId, revision: model.revision, ...args }, { signal: aborter.signal });
  const errors = el('div', { class: 'presentation-editor-errors', role: 'alert' });
  const saveStatus = el('span', { class: 'presentation-save-status', role: 'status' });
  const thumbnails = el('div', { class: 'presentation-thumbnails', 'aria-label': 'Diapos de la présentation' });
  const stageHost = el('div', { class: 'presentation-stage-host' });
  const stageViewport = el('div', { class: 'presentation-stage-viewport' }); stageHost.append(stageViewport);
  const inspector = el('section', { class: 'presentation-inspector', 'aria-label': 'Disposition du widget' });
  const catalogList = el('div', { class: 'presentation-widget-library' });
  const search = input('', { type: 'search', placeholder: 'Rechercher un widget…', 'aria-label': 'Rechercher un widget à présenter' });
  const slideTitle = input('', { maxLength: 160, 'aria-label': 'Titre de la diapo', placeholder: 'Titre de la diapo' });
  const snap = input('on', { type: 'checkbox', checked: true, 'aria-label': 'Aligner les déplacements sur la grille' });
  const undoButton = button('Annuler', () => history(-1), { disabled: true, attributes: { 'aria-label': 'Annuler la modification de présentation' } });
  const redoButton = button('Rétablir', () => history(1), { disabled: true, attributes: { 'aria-label': 'Rétablir la modification de présentation' } });
  const saveButton = button('Enregistrer', () => safely(saveDeck), { icon: 'check', attributes: { 'aria-label': 'Enregistrer la présentation' }, disabled: true });
  const presentButton = button('Présenter', () => safely(async () => { validateLaunch(); await saveDeck(); openViewer(); }), { primary: true, icon: 'chart', disabled: true });
  const body = el('div', { class: 'presentation-editor', 'data-presentation-editor': '', 'data-ready': 'false' },
    el('div', { class: 'presentation-editor-top' },
      el('div', {}, el('p', { class: 'presentation-editor-eyebrow', text: 'COMPOSER VOTRE RÉUNION' }),
        el('p', { class: 'presentation-editor-context', text: scopeText }), provenance()),
      toolbar(undoButton, redoButton, saveStatus, saveButton, presentButton)), errors,
    el('div', { class: 'presentation-editor-workspace' },
      el('aside', { class: 'presentation-slide-rail' }, toolbar(el('h3', { text: 'Diapos' }), button('+', () => addSlide(), { title: 'Ajouter une diapo', attributes: { 'aria-label': 'Ajouter une diapo' } })),
        thumbnails, button('Composer automatiquement', () => safely(async () => {
          if (!await confirm('Réorganiser la présentation', 'Répartir les widgets du tableau sur plusieurs diapos ? Vous pourrez annuler cette modification.', 'Composer')) return;
          change(() => { deck = groupedDeck(available); current = 0; selectedId = ''; });
        }), { attributes: { 'data-presentation-auto': '' } })),
      el('section', { class: 'presentation-stage-column' }, toolbar(slideTitle,
        button('Dupliquer', () => duplicateSlide(), { attributes: { 'aria-label': 'Dupliquer la diapo' } }),
        button('Supprimer', () => removeSlide(), { attributes: { 'aria-label': 'Supprimer la diapo' } })),
        stageHost, el('div', { class: 'presentation-stage-tools' },
          el('label', { class: 'presentation-grid-option' }, snap, el('span', { text: 'Grille magnétique' })),
          button('Répartir les widgets', () => change(() => arrangeSlide(activeSlide(), catalog)), { attributes: { 'aria-label': 'Répartir les widgets de cette diapo' } }),
          caption('Glisser pour placer · poignées pour redimensionner · flèches pour ajuster'))),
      el('aside', { class: 'presentation-library-rail' }, inspector, el('h3', { text: 'Widgets du tableau' }), search,
        caption('Ajouter par clic ou glisser sur la page.'), catalogList)),
    el('footer', { class: 'presentation-editor-footer' }, caption('Format 16:9 · la composition est mémorisée avec ce tableau. Les chiffres restent ceux du périmètre affiché.'),
      caption('Les tableaux peuvent afficher un aperçu. Les exports HTML, CSV et Excel conservent leurs données détaillées.')));
  const editor = dialog('Préparer la présentation', body);
  editor.element.classList.add('presentation-editor-modal'); editor.element.querySelector('.modal-actions')?.remove();
  const originalClose = editor.close;
  const requestClose = async () => {
    if (saving || disposed) return;
    if (dirty && ready && !await confirm('Fermer la préparation', 'La présentation comporte des modifications non enregistrées.', 'Fermer sans enregistrer')) return;
    disposeEditor(); originalClose();
  };
  const headingClose = editor.element.querySelector('.modal-heading > button');
  headingClose?.addEventListener('click', event => { event.preventDefault(); event.stopImmediatePropagation(); void requestClose(); }, true);
  editor.element.addEventListener('cancel', event => { event.preventDefault(); event.stopImmediatePropagation(); void requestClose(); }, true);

  // These controls derive their disabled state from the editor, including after
  // asynchronous saves; the generic button helper restores its previous state.
  function button(label, action, options = {}) {
    const control = el('button', { type: 'button', class: `button ${options.primary ? 'primary' : ''} ${options.danger ? 'danger' : ''}`,
      disabled: !!options.disabled, title: options.title || '', ...options.attributes }, options.icon ? icon(options.icon) : null, el('span', { text: label }));
    control.addEventListener('click', event => { if (!control.disabled) void safely(() => action(event)); });
    return control;
  }
  function provenance() {
    return el('details', { class: 'presentation-provenance' },
      el('summary', { text: `${sourceKind} · ${formatDate(observed)} · ${formatNumber(model.snapshot.rowCount)} lignes${warnings.length ? ` · ${warnings.length} point${warnings.length > 1 ? 's' : ''} à vérifier` : ''}` }),
      el('div', {}, caption(filterText), caption(receipt.details), ...warnings.map(text => caption(text, 'warning'))));
  }
  function activeSlide() { return deck.slides[current]; }
  function selected() { return activeSlide()?.items.find(item => item.id === selectedId); }
  function remember() { undo.push(copy(deck)); if (undo.length > 40) undo.shift(); redo.length = 0; }
  function change(action) {
    if (!ready || saving || disposed) return;
    remember(); action(); dirty = true; paint();
  }
  function history(direction) {
    if (saving || !ready) return;
    const source = direction < 0 ? undo : redo, target = direction < 0 ? redo : undo;
    if (!source.length) return; target.push(copy(deck)); deck = source.pop();
    current = Math.min(current, deck.slides.length - 1); selectedId = ''; dirty = true; paint();
  }
  async function safely(action) {
    errors.replaceChildren();
    try { return await action(); }
    catch (error) { if (!disposed && error?.name !== 'AbortError') errors.replaceChildren(caption(error.message || String(error), 'warning')); }
  }
  function paintStatus() {
    saveStatus.textContent = !ready ? 'Chargement…' : saving ? 'Enregistrement…' : dirty ? 'Modifications à enregistrer' : 'Présentation enregistrée';
    saveButton.disabled = !ready || saving || !dirty; presentButton.disabled = !ready || saving || !widgetIds(deck).length;
    undoButton.disabled = !undo.length || saving; redoButton.disabled = !redo.length || saving;
    body.dataset.dirty = String(dirty); body.dataset.ready = String(ready);
  }
  function paint() {
    if (disposed) return;
    current = Math.max(0, Math.min(current, deck.slides.length - 1));
    thumbnails.replaceChildren(...deck.slides.map((slide, index) => {
      const controls = toolbar(button('↑', () => moveSlide(index, -1), { disabled: index === 0, attributes: { 'aria-label': `Monter la diapo ${index + 1}` } }),
        button('↓', () => moveSlide(index, 1), { disabled: index === deck.slides.length - 1, attributes: { 'aria-label': `Descendre la diapo ${index + 1}` } }));
      return el('div', { class: 'presentation-thumb-entry' }, slideThumbnail(slide, index, index === current, () => { current = index; selectedId = ''; paint(); }), controls);
    }));
    slideTitle.value = activeSlide().title; paintCanvas(); paintInspector(); paintCatalog(); paintStatus();
  }
  function paintCanvas() {
    activePointerCancel?.(); fit?.dispose();
    const canvas = slideCanvas(activeSlide(), catalog, context, { editable: true, number: current + 1, total: deck.slides.length });
    stageViewport.replaceChildren(canvas); fit = fitCanvas(stageHost, stageViewport, canvas);
    const area = canvas.querySelector('[data-presentation-area]');
    area.addEventListener('pointerdown', event => {
      if (!ready || saving || disposed) return;
      const frame = event.target.closest('[data-placement-id]');
      if (!frame) { selectedId = ''; markSelection(); paintInspector(); return; }
      selectedId = frame.dataset.placementId; markSelection(); paintInspector();
      const handle = event.target.closest('[data-presentation-resize]')?.dataset.presentationResize;
      if (event.button !== 0 || (!handle && !event.target.closest('[data-presentation-drag]'))) return;
      event.preventDefault(); frame.focus({ preventScroll: true }); beginMove(event, frame, handle || '', area);
    });
    area.addEventListener('keydown', event => {
      if (!selected() || event.target.closest('input,textarea,select')) return;
      if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); removeItem(); return; }
      const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
      if (!direction) return; event.preventDefault(); const step = event.shiftKey ? 10 : 1;
      const id = selectedId; change(() => Object.assign(selected(), geometry({ ...selected(), x: selected().x + direction[0] * step, y: selected().y + direction[1] * step })));
      [...stageViewport.querySelectorAll('[data-placement-id]')].find(node => node.dataset.placementId === id)?.focus({ preventScroll: true });
    });
    area.addEventListener('dragover', event => { if (event.dataTransfer.types.includes('application/x-glpi-widget')) event.preventDefault(); });
    area.addEventListener('drop', event => {
      const id = event.dataTransfer.getData('application/x-glpi-widget'); if (!catalog.has(id)) return;
      event.preventDefault(); const rect = area.getBoundingClientRect();
      addItem(id, (event.clientX - rect.left) / rect.width * 1000, (event.clientY - rect.top) / rect.height * 1000);
    });
    markSelection();
  }
  function markSelection() {
    selectedNode = null;
    stageViewport.querySelectorAll('[data-placement-id]').forEach(node => {
      const active = node.dataset.placementId === selectedId; node.classList.toggle('is-selected', active); if (active) selectedNode = node;
    });
  }
  function beginMove(event, node, handle, area) {
    if (!ready || saving || disposed) return;
    const item = selected(), initial = copy(item), before = copy(deck), rect = area.getBoundingClientRect();
    const startX = event.clientX, startY = event.clientY, pointer = event.pointerId;
    const quantize = value => Math.round(value / (snap.checked ? 10 : 1)) * (snap.checked ? 10 : 1);
    node.setPointerCapture(pointer); node.classList.add('is-moving'); body.classList.add('is-manipulating');
    const move = currentEvent => {
      if (currentEvent.pointerId !== pointer) return;
      const dx = (currentEvent.clientX - startX) / rect.width * 1000, dy = (currentEvent.clientY - startY) / rect.height * 1000;
      if (!handle) Object.assign(item, geometry({ ...initial, x: quantize(initial.x + dx), y: quantize(initial.y + dy) }));
      else {
        let left = initial.x, top = initial.y, right = initial.x + initial.width, bottom = initial.y + initial.height;
        if (handle.includes('w')) left = Math.max(0, Math.min(right - 80, quantize(initial.x + dx)));
        if (handle.includes('e')) right = Math.min(1000, Math.max(left + 80, quantize(initial.x + initial.width + dx)));
        if (handle.includes('n')) top = Math.max(0, Math.min(bottom - 80, quantize(initial.y + dy)));
        if (handle.includes('s')) bottom = Math.min(1000, Math.max(top + 80, quantize(initial.y + initial.height + dy)));
        Object.assign(item, { x: left, y: top, width: right - left, height: bottom - top });
      }
      positionItem(node, item);
    };
    const finish = cancelled => {
      node.removeEventListener('pointermove', move); node.removeEventListener('pointerup', done); node.removeEventListener('pointercancel', cancel);
      if (node.hasPointerCapture(pointer)) node.releasePointerCapture(pointer);
      node.classList.remove('is-moving'); body.classList.remove('is-manipulating'); activePointerCancel = null;
      if (cancelled) { Object.assign(item, initial); positionItem(node, item); }
      else if (JSON.stringify(item) !== JSON.stringify(initial)) { undo.push(before); if (undo.length > 40) undo.shift(); redo.length = 0; dirty = true; }
      paintInspector(); paintStatus();
      // Refresh the tiny previews without replacing the captured drag surface.
      thumbnails.querySelectorAll('.presentation-miniature-content').forEach((area, index) => {
        area.replaceChildren(...deck.slides[index].items.map(value => { const block = el('i'); positionItem(block, value); return block; }));
      });
    };
    const done = currentEvent => { if (currentEvent.pointerId === pointer) finish(false); };
    const cancel = () => finish(true); activePointerCancel = cancel;
    node.addEventListener('pointermove', move); node.addEventListener('pointerup', done); node.addEventListener('pointercancel', cancel);
  }
  function paintInspector() {
    const item = selected(); inspector.replaceChildren();
    if (!item) { inspector.append(el('h3', { text: 'Placement libre' }), caption('Sélectionnez un widget pour ajuster sa position et sa taille.')); return; }
    inspector.append(el('h3', { text: catalog.get(item.widgetId)?.title || 'Widget indisponible' }));
    const values = el('div', { class: 'presentation-geometry-fields' });
    for (const [key, label] of [['x', 'Gauche'], ['y', 'Haut'], ['width', 'Largeur'], ['height', 'Hauteur']]) {
      const control = input(String(item[key] / 10), { type: 'number', min: key === 'width' || key === 'height' ? 8 : 0, max: 100, step: .1, 'aria-label': `${label} du widget (%)` });
      control.addEventListener('change', () => {
        if (!control.reportValidity() || !Number.isFinite(control.valueAsNumber)) return;
        change(() => Object.assign(item, geometry({ ...item, [key]: control.valueAsNumber * 10 })));
      }); values.append(field(`${label} (%)`, control));
    }
    inspector.append(values, toolbar(button('Premier plan', () => change(() => { const items = activeSlide().items; items.splice(items.indexOf(item), 1); items.push(item); })),
      button('Arrière-plan', () => change(() => { const items = activeSlide().items; items.splice(items.indexOf(item), 1); items.unshift(item); }))),
      toolbar(button('Retirer le widget', removeItem, { danger: true })));
  }
  function paintCatalog() {
    const fold = value => value.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr');
    const needle = fold(search.value), items = available.filter(item => fold(`${item.title} ${item.section} ${item.kind}`).includes(needle));
    catalogList.replaceChildren(...items.map(item => {
      const node = el('button', { type: 'button', class: 'presentation-library-item', draggable: true, 'data-presentation-add': item.id,
        'aria-label': `Ajouter · ${item.title}`, disabled: activeSlide().items.some(value => value.widgetId === item.id), onClick: () => addItem(item.id) }, el('strong', { text: item.title }),
        el('small', { text: `${item.kind} · ${item.section}` }), el('span', { class: 'presentation-library-plus', text: '+', 'aria-hidden': 'true' }));
      node.addEventListener('dragstart', event => { event.dataTransfer.setData('application/x-glpi-widget', item.id); event.dataTransfer.effectAllowed = 'copy'; });
      return node;
    }));
    if (!items.length) catalogList.append(caption('Aucun widget ne correspond à cette recherche.'));
  }
  function addItem(widgetId, x = 35, y = 35, original = null) {
    if (!ready || saving || !catalog.has(widgetId) || activeSlide().items.some(item => item.widgetId === widgetId)) return;
    if (activeSlide().items.length >= 20 || deck.slides.reduce((sum, slide) => sum + slide.items.length, 0) >= 300) { errors.replaceChildren(caption('Une diapo accepte 20 widgets et une présentation 300 emplacements au maximum.', 'warning')); return; }
    change(() => { const item = geometry({ id: newId(), widgetId, x, y, width: original?.width || 480, height: original?.height || 460 }); activeSlide().items.push(item); selectedId = item.id; });
  }
  function removeItem() { if (selected()) change(() => { activeSlide().items = activeSlide().items.filter(item => item.id !== selectedId); selectedId = ''; }); }
  function addSlide() {
    if (deck.slides.length >= 40) { errors.replaceChildren(caption('Une présentation accepte 40 diapos au maximum.', 'warning')); return; }
    change(() => { deck.slides.splice(current + 1, 0, { id: newId(), title: `Diapo ${deck.slides.length + 1}`, items: [] }); current++; selectedId = ''; });
  }
  function duplicateSlide() {
    if (deck.slides.length >= 40 || deck.slides.reduce((sum, slide) => sum + slide.items.length, 0) + activeSlide().items.length > 300) { errors.replaceChildren(caption('La duplication dépasse les limites de la présentation.', 'warning')); return; }
    change(() => { const duplicate = copy(activeSlide()); duplicate.id = newId(); duplicate.title = `${duplicate.title} · copie`.slice(0, 160); duplicate.items.forEach(item => item.id = newId()); deck.slides.splice(current + 1, 0, duplicate); current++; selectedId = ''; });
  }
  function removeSlide() { change(() => { deck.slides.splice(current, 1); if (!deck.slides.length) deck.slides.push({ id: newId(), title: 'Vue d’ensemble', items: [] }); current = Math.min(current, deck.slides.length - 1); selectedId = ''; }); }
  function moveSlide(index, delta) { change(() => { const activeId = activeSlide().id; [deck.slides[index], deck.slides[index + delta]] = [deck.slides[index + delta], deck.slides[index]]; current = deck.slides.findIndex(slide => slide.id === activeId); }); }
  slideTitle.addEventListener('change', () => change(() => activeSlide().title = slideTitle.value.trim() || `Diapo ${current + 1}`));
  search.addEventListener('input', paintCatalog);
  function validateLaunch() {
    if (!widgetIds(deck).length) throw new Error('Ajoutez au moins un widget à la présentation.');
    if (deck.slides.some(slide => !slide.items.length)) throw new Error('Une diapo est vide. Ajoutez un widget ou supprimez cette page.');
    if (widgetIds(deck).some(id => !catalog.has(id))) throw new Error('Un widget a été retiré ou masqué. Corrigez les emplacements signalés avant de présenter.');
  }
  async function saveDeck() {
    if (!ready || saving || !dirty) return;
    saving = true; paintStatus();
    try {
      const saved = await call('dashboard.presentation.save', { deck, baseFingerprint: fingerprint });
      if (disposed) return;
      deck = copy(saved.deck); fingerprint = saved.fingerprint; dirty = false;
      if (saved.settings) ctx.state.settings = saved.settings;
      onSaved?.(copy(deck), saved.settings); paint();
    } finally { saving = false; paintStatus(); }
  }
  function openViewer() {
    viewer?.close(); const shown = copy(deck); let index = 0, viewerFit = null;
    const host = el('div', { class: 'presentation-viewer-stage' }), viewport = el('div', { class: 'presentation-stage-viewport' }); host.append(viewport);
    const progress = el('span', { class: 'presentation-progress', role: 'status', 'aria-live': 'polite' });
    const choose = select(shown.slides.map((slide, i) => ({ value: String(i), label: `${i + 1}. ${slide.title}` })), '0', { 'aria-label': 'Choisir une diapo' });
    choose.setAttribute('aria-label', 'Choisir une diapo');
    const previous = button('Précédent', () => show(index - 1), { attributes: { 'aria-label': 'Diapo précédente' } });
    const next = button('Suivant', () => show(index + 1), { attributes: { 'aria-label': 'Diapo suivante' } });
    const ids = widgetIds(shown), snapshots = new Map(ids.map(id => [id, catalog.get(id).node.cloneNode(true)]));
    const exportSlides = () => exportSelection({ runId: model.runId, revision: model.revision, selectedWidgetIds: ids, includeOverview: false, presentation: shown }, snapshots);
    const viewerBody = el('div', { class: 'presentation-body analytics analytics-business' },
      el('header', { class: 'presentation-viewer-toolbar' }, el('strong', { text: board.name || 'Présentation' }),
        toolbar(previous, progress, next, choose), toolbar(button('Exporter la présentation', exportSlides, { icon: 'download' }),
          button('Revenir à la préparation', () => viewer.close(), { attributes: { 'data-presentation-exit': '' } }))),
      provenance(), host);
    const modal = dialog('Présentation du pilotage', viewerBody); viewer = modal;
    modal.element.classList.add('pilot-presentation'); modal.element.querySelector('.modal-heading')?.remove(); modal.element.querySelector('.modal-actions')?.remove();
    modal.element.dataset.presentationRun = model.runId; modal.element.dataset.presentationSelection = JSON.stringify(ids); modal.element.dataset.presentationCached = String(!!ctx.state.session.cachedSource);
    function show(value) {
      index = Math.max(0, Math.min(shown.slides.length - 1, value)); viewerFit?.dispose();
      const canvas = slideCanvas(shown.slides[index], catalog, context, { number: index + 1, total: shown.slides.length });
      viewport.replaceChildren(canvas); viewerFit = fitCanvas(host, viewport, canvas);
      progress.textContent = `${index + 1} / ${shown.slides.length}`; previous.disabled = index === 0; next.disabled = index === shown.slides.length - 1; choose.value = String(index);
      modal.element.dataset.presentationSlide = String(index);
    }
    choose.addEventListener('change', () => show(Number(choose.value)));
    modal.element.addEventListener('keydown', event => {
      if (document.querySelector('dialog[open]:last-of-type') !== modal.element || event.target.closest('input,select,textarea') || event.altKey || event.ctrlKey || event.metaKey) return;
      if (event.key === 'ArrowRight' || event.key === 'PageDown') { event.preventDefault(); show(index + 1); }
      if (event.key === 'ArrowLeft' || event.key === 'PageUp') { event.preventDefault(); show(index - 1); }
    });
    modal.element.addEventListener('pilot:closed', () => { viewerFit?.dispose(); if (viewer === modal) viewer = null; }, { once: true });
    show(0); modal.element.querySelector('[data-presentation-exit]').focus();
  }
  function disposeEditor() {
    if (disposed) return; disposed = true; activePointerCancel?.(); fit?.dispose(); viewer?.close(); aborter.abort(); ctx.signal.removeEventListener('abort', abort);
  }
  editor.element.addEventListener('pilot:closed', disposeEditor, { once: true });
  paint();
  void safely(async () => {
    const saved = await call('dashboard.presentation.get'); if (disposed) return;
    fingerprint = saved.fingerprint; if (saved.deck?.slides?.length) { deck = copy(saved.deck); dirty = false; }
    else if (saved.deck) { deck = { version: 1, slides: [{ id: newId(), title: 'Vue d’ensemble', items: [] }] }; dirty = true; }
    ready = true; paint();
  });
  return () => { disposeEditor(); originalClose(); };
}
