import { el, button, select, caption } from './analytics-ui.js';
import { attachWidgetReading } from './workspace-widget-reading.js';
import { GRID_COLUMNS, GRID_STEP, GRID_GAP, GRID_MAX_ROW, WIDGET_SIZES,
  MIN_WIDGET_HEIGHT, MIN_RESIZE_HEIGHT, MAX_WIDGET_HEIGHT,
  arrangeWorkspaceWidgets, effectiveGridSpan, planWorkspaceDrop, presetForWidget,
  gridHeight, gridRows, widgetRows, snapGridHeight, previewMoveRect, previewResizeRect } from './workspace-grid.js';

// One controller per rendered composition. It owns its observers, gestures and
// preview; the existing C# save path owns validation, undo and disk persistence.
export function bindWorkspaceGrid({ host, getBoard, canEdit, persist, notify, signal }) {
  const lifetime = new AbortController();
  const on = (node, event, fn, options = {}) => node.addEventListener(event, fn, { ...options, signal: lifetime.signal });
  const grids = [...host.querySelectorAll('.pilot-widget-grid')];
  const nodes = new Map([...host.querySelectorAll('.pilot-widget[data-widget-id]')].map(node => [node.dataset.widgetId, node]));
  const sections = new Map(grids.map(grid => [grid, grid.closest('[data-section-key]').dataset.sectionKey]));
  const placements = new Map();
  const readers = [];
  const live = el('div', { class: 'workspace-grid-status', role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' });
  host.append(live);
  let gesture = null, pending = false, frame = 0, scrolling = 0, disposed = false, suppressClick = false;
  const editable = () => !disposed && !pending && canEdit();
  const announce = text => { live.textContent = text; };
  const setting = id => getBoard()?.widgets.find(w => w.id === id);
  const cards = grid => [...grid.children].filter(node => node.matches('.pilot-widget[data-widget-id]'));
  const width = grid => grid.getBoundingClientRect().width;
  const unit = grid => (width(grid) + GRID_GAP) / GRID_COLUMNS;
  const observer = new ResizeObserver(() => schedule());

  function layout() {
    frame = 0; if (disposed || gesture || pending) return;
    for (const grid of grids) {
      if (grid.hidden || width(grid) < 1) continue;
      grid.dataset.workspaceGrid = '';
      const items = cards(grid), settings = items.map(node => setting(node.dataset.widgetId)).filter(Boolean);
      const step = unit(grid), heights = new Map();
      // Only composition positions cards on absolute rows: reading mode keeps its
      // deliberate natural height and its saved cap.
      const composing = canEdit(), maximum = composing ? MAX_WIDGET_HEIGHT : GRID_MAX_ROW * GRID_STEP;
      for (const node of items) {
        const widget = setting(node.dataset.widgetId); if (!widget) continue;
        const span = effectiveGridSpan(widget.span, width(grid));
        node.style.width = `${span * step - GRID_GAP}px`;
        // Auto height fits its own content, bounded by its configured height.
        // Explicit sizes are exact. Neither mode inherits a neighbour's height.
        // In composition the painted height is the one the row span reserves, so a
        // card lands exactly where the preview showed it and keeps the grid gap.
        const measured = () => Math.max(MIN_WIDGET_HEIGHT, node.getBoundingClientRect().height);
        if (!composing) { node.style.setProperty('--widget-height', `${widget.height}px`); heights.set(widget.id, measured()); continue; }
        const rows = widgetRows(widget.autoHeight === false ? widget.height : measured(), maximum);
        node.style.setProperty('--widget-height', `${gridHeight(rows)}px`);
        heights.set(widget.id, gridHeight(rows));
      }
      try {
        const arranged = arrangeWorkspaceWidgets(settings, width(grid), heights, maximum);
        placements.set(grid, arranged);
        for (const rect of arranged) {
          const node = nodes.get(rect.id); if (!node) continue;
          node.style.left = `${rect.x * step}px`; node.style.top = `${rect.y * GRID_STEP}px`;
          node.dataset.gridColumn = String(rect.x); node.dataset.gridRow = String(rect.y);
        }
        const bottom = Math.max(0, ...arranged.map(rect => rect.y * GRID_STEP + heights.get(rect.id)));
        grid.style.height = `${Math.max(80, bottom + (canEdit() ? 120 : 0))}px`;
        grid.dataset.gridReady = 'true';
      } catch (error) { announce(error.message); }
    }
  }
  function schedule() { if (!frame && !disposed) frame = requestAnimationFrame(layout); }

  async function save(plan, id, targetGrid, size) {
    if (!editable() || !plan) return;
    pending = true; host.setAttribute('aria-busy', 'true');
    const all = new Map([...placements].map(([grid, rects]) => [grid, rects.map(rect => ({ ...rect }))]));
    all.set(targetGrid, plan);
    const destination = sections.get(targetGrid);
    try {
      await persist(draft => {
        const moving = draft.widgets.find(w => w.id === id);
        if (!moving || moving.locked) throw new Error('Déverrouillez cette carte avant de la déplacer ou de la redimensionner.');
        moving.section = destination;
        if (size) Object.assign(moving, size, { autoHeight: false });
        for (const [grid, rects] of all) {
          for (const rect of rects) {
            if (rect.id === id && grid !== targetGrid) continue;
            const widget = draft.widgets.find(w => w.id === rect.id);
            if (!widget || widget.section !== sections.get(grid)) continue;
            widget.gridX = rect.x; widget.gridY = rect.y; widget.startNewRow = false;
          }
        }
      });
      if (!signal?.aborted) {
        const fresh = [...host.querySelectorAll('.pilot-widget')].find(node => node.dataset.widgetId === id);
        fresh?.querySelector(size ? '[data-grid-size]' : '[data-grid-move]')?.focus({ preventScroll: true });
      }
    } catch (error) {
      if (error.name !== 'AbortError') notify(error.message || String(error), 'error');
    } finally {
      pending = false;
      if (!disposed) { host.removeAttribute('aria-busy'); schedule(); }
    }
  }

  function sizePlan(grid, widget, size) {
    const rects = placements.get(grid) || [];
    const current = rects.find(rect => rect.id === widget.id); if (!current) return null;
    return planWorkspaceDrop(rects, { ...current, x: Math.min(current.x, GRID_COLUMNS - size.span), w: size.span,
      h: gridRows(size.height) });
  }

  for (const [id, node] of nodes) {
    observer.observe(node);
    if (!canEdit()) { const widget = setting(id); if (widget) readers.push(attachWidgetReading(node, widget, signal)); continue; }
    const widget = setting(id); if (!widget) continue;
    const controls = node.querySelector('.analytics-widget-heading > .toolbar'); if (!controls) continue;
    const handle = button('Déplacer', null, { disabled: widget.locked, attributes: { 'data-grid-move': id,
      'aria-label': `Déplacer ${node.querySelector('h3')?.textContent || 'la carte'}` },
      title: 'Glissez vers une case libre ou une autre section. Flèches : déplacer ; Maj + flèche : cinq cases ; Échap : annuler.' });
    handle.classList.add('workspace-grid-handle');
    const preset = presetForWidget(widget);
    const sizes = select([...(preset ? [] : [{ value: '', label: 'Personnalisé' }]),
      ...Object.entries(WIDGET_SIZES).map(([value, size]) => ({ value, label: size.label }))], preset);
    sizes.dataset.gridSize = id; sizes.classList.add('workspace-grid-size'); sizes.disabled = widget.locked;
    sizes.setAttribute('aria-label', `Taille de ${node.querySelector('h3')?.textContent || 'la carte'}`);
    sizes.title = 'Petit : 4 colonnes · Moyen : 6 · Grand : 12. Chaque carte conserve sa propre hauteur.';
    if (!preset) sizes.options[0].disabled = true;
    on(sizes, 'change', () => {
      const size = WIDGET_SIZES[sizes.value]; if (!size || !editable() || widget.locked) return;
      const grid = node.parentElement;
      const height = snapGridHeight(size.height, MIN_RESIZE_HEIGHT);
      const plan = sizePlan(grid, widget, { span: size.span, height });
      if (!plan) { sizes.value = preset; announce('Une carte verrouillée occupe cette place. Déplacez d’abord la carte.'); return; }
      void save(plan, id, grid, { span: size.span, height });
    });
    controls.prepend(handle); controls.append(sizes);
    const grip = button('↘', null, { disabled: widget.locked, attributes: { 'data-grid-resize': id,
      'aria-label': `Redimensionner ${node.querySelector('h3')?.textContent || 'la carte'}` },
      title: 'Glissez pour choisir largeur et hauteur. Les flèches règlent la taille ; Échap annule.' });
    grip.classList.add('workspace-grid-resize'); node.append(grip);
  }
  for (const grid of grids) observer.observe(grid);

  function clearPreview(current) {
    current?.preview?.remove(); current?.ghost?.remove();
    for (const grid of grids) grid.classList.remove('is-grid-target');
    current?.node?.classList.remove('is-grid-moving');
    host.classList.remove('is-grid-gesture');
  }
  function finish(cancel) {
    const current = gesture; if (!current) return;
    gesture = null; cancelAnimationFrame(scrolling); scrolling = 0;
    clearPreview(current); suppressClick = current.moved;
    try { if (current.handle.hasPointerCapture(current.pointer)) current.handle.releasePointerCapture(current.pointer); } catch { /* Removed handle. */ }
    if (!cancel && current.moved && current.plan && current.target) {
      void save(current.plan, current.id, current.target, current.kind === 'resize' ? current.size : null);
    } else {
      announce(cancel ? 'Modification annulée.' : current.moved ? 'Emplacement occupé par une carte verrouillée.' : ''); schedule();
    }
  }

  function previewAt(x, y) {
    const current = gesture; if (!current) return;
    const under = document.elementFromPoint(x, y);
    const target = current.kind === 'resize' ? current.grid : under?.closest('.pilot-widget-grid')
      || under?.closest('.pilot-block')?.querySelector('.pilot-widget-grid');
    current.plan = null; current.target = null;
    grids.forEach(grid => grid.classList.remove('is-grid-target'));
    if (!target || !grids.includes(target) || target.hidden) { current.preview.remove(); return; }
    const bounds = target.getBoundingClientRect(), step = unit(target);
    let moving;
    if (current.kind === 'resize') {
      const resized = previewResizeRect({ rect: current.rect, span: current.widget.span, width: bounds.width,
        startX: current.x, startY: current.y, pointerX: x, pointerY: y, startHeight: current.startHeight });
      moving = resized.moving; current.size = resized.size;
    } else {
      moving = previewMoveRect({ rect: current.rect, span: current.widget.span, width: bounds.width,
        pointerX: x - bounds.left, pointerY: y - bounds.top,
        offsetRatio: current.offsetRatio, offsetY: current.offsetY });
    }
    let plan;
    try { plan = planWorkspaceDrop(placements.get(target) || [], moving); } catch { plan = null; }
    const preview = current.preview;
    target.append(preview); target.classList.add('is-grid-target');
    preview.dataset.valid = String(!!plan);
    // The preview is painted from the very rows the committed card will occupy.
    Object.assign(preview.style, { left: `${moving.x * step}px`, top: `${moving.y * GRID_STEP}px`,
      width: `${moving.w * step - GRID_GAP}px`, height: `${gridHeight(moving.h)}px` });
    preview.textContent = plan ? `${moving.w} / 12 · ${current.kind === 'resize' ? gridHeight(moving.h) + ' px' : 'ligne ' + (moving.y + 1)}` : 'Carte verrouillée';
    current.ghost.style.left = `${x + 14}px`; current.ghost.style.top = `${y + 14}px`;
    if (plan) { current.plan = plan; current.target = target; }
    announce(plan ? `Colonne ${moving.x + 1}, ligne ${moving.y + 1}.` : 'Une carte verrouillée occupe cet emplacement.');
  }

  function scrollTick() {
    scrolling = 0; if (!gesture?.moved) return;
    const scroller = host.closest('.workspace') || document.scrollingElement;
    const rect = scroller.getBoundingClientRect();
    const speed = gesture.lastY < Math.max(0, rect.top) + 48 ? -14 : gesture.lastY > Math.min(innerHeight, rect.bottom) - 48 ? 14 : 0;
    if (speed) {
      const before = scroller.scrollTop; scroller.scrollTop += speed;
      if (before !== scroller.scrollTop) previewAt(gesture.lastX, gesture.lastY);
    }
    scrolling = requestAnimationFrame(scrollTick);
  }
  on(host, 'pointerdown', event => {
    const handle = event.target.closest('[data-grid-move],[data-grid-resize]');
    if (!handle || !editable() || event.button !== 0 || gesture) return;
    const id = handle.dataset.gridMove || handle.dataset.gridResize, widget = setting(id), node = nodes.get(id);
    if (!node || !widget || widget.locked) return;
    layout(); const grid = node.parentElement, rect = placements.get(grid)?.find(r => r.id === id); if (!rect) return;
    const box = node.getBoundingClientRect();
    event.preventDefault(); handle.focus({ preventScroll: true }); suppressClick = false;
    gesture = { id, widget, node, grid, handle, pointer: event.pointerId, rect: { ...rect }, kind: handle.hasAttribute('data-grid-resize') ? 'resize' : 'move',
      x: event.clientX, y: event.clientY, lastX: event.clientX, lastY: event.clientY, offsetRatio: (event.clientX - box.left) / box.width,
      offsetY: event.clientY - box.top, startHeight: box.height, moved: false, plan: null, target: null,
      preview: el('div', { class: 'workspace-grid-preview', 'aria-hidden': 'true' }),
      ghost: el('div', { class: 'workspace-grid-ghost', text: node.querySelector('h3')?.textContent || 'Carte', 'aria-hidden': 'true' }) };
    handle.setPointerCapture(event.pointerId);
  });
  on(host, 'pointermove', event => {
    if (!gesture || gesture.pointer !== event.pointerId) return;
    gesture.lastX = event.clientX; gesture.lastY = event.clientY;
    if (!gesture.moved && Math.abs(event.clientX - gesture.x) + Math.abs(event.clientY - gesture.y) <= 5) return;
    event.preventDefault();
    if (!gesture.moved) {
      gesture.moved = true; host.classList.add('is-grid-gesture'); gesture.node.classList.add('is-grid-moving');
      document.body.append(gesture.ghost); scrolling = requestAnimationFrame(scrollTick);
    }
    previewAt(event.clientX, event.clientY);
  });
  on(host, 'pointerup', event => { if (gesture?.pointer === event.pointerId) { if (gesture.moved) previewAt(event.clientX, event.clientY); finish(false); } });
  on(host, 'pointercancel', event => { if (gesture?.pointer === event.pointerId) finish(true); });
  on(host, 'lostpointercapture', event => { if (gesture?.pointer === event.pointerId) finish(true); });
  on(document, 'keydown', event => { if (event.key === 'Escape' && gesture) { event.preventDefault(); finish(true); } });
  on(host, 'click', event => {
    if (suppressClick && event.target.closest('[data-grid-move],[data-grid-resize]')) {
      event.preventDefault(); event.stopImmediatePropagation(); suppressClick = false;
    }
  }, { capture: true });
  on(host, 'keydown', event => {
    const handle = event.target.closest('[data-grid-move],[data-grid-resize]');
    if (!handle || !editable() || gesture || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault(); const id = handle.dataset.gridMove || handle.dataset.gridResize, widget = setting(id), node = nodes.get(id);
    if (!node || !widget || widget.locked) return;
    const grid = node.parentElement, rects = placements.get(grid) || [], rect = rects.find(r => r.id === id); if (!rect) return;
    const horizontal = event.key === 'ArrowLeft' || event.key === 'ArrowRight';
    const direction = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 1;
    let plan, size = null;
    if (handle.hasAttribute('data-grid-resize')) {
      size = { span: Math.max(3, Math.min(GRID_COLUMNS, widget.span + (horizontal ? direction : 0))),
        height: snapGridHeight(widget.height + (horizontal ? 0 : direction * (event.shiftKey ? 100 : 20)), MIN_RESIZE_HEIGHT) };
      plan = sizePlan(grid, widget, size);
    } else {
      const delta = direction * (event.shiftKey ? 5 : 1);
      plan = planWorkspaceDrop(rects, { ...rect, x: Math.max(0, Math.min(12 - rect.w, rect.x + (horizontal ? delta : 0))),
        y: Math.max(0, Math.min(GRID_MAX_ROW, rect.y + (horizontal ? 0 : delta))) });
    }
    if (!plan) { announce('Emplacement occupé par une carte verrouillée.'); return; }
    void save(plan, id, grid, size);
  });
  on(window, 'resize', schedule);
  if (canEdit()) host.prepend(caption('Glissez les poignées pour placer vos cartes, y compris entre sections. Petit, Moyen ou Grand : les hauteurs restent indépendantes. Sur un écran étroit, faites défiler la grille horizontalement.', 'workspace-grid-help no-print'));
  function dispose() {
    if (disposed) return;
    disposed = true; finish(true); host.removeAttribute('aria-busy'); observer.disconnect(); readers.forEach(reader => reader.dispose()); lifetime.abort(); cancelAnimationFrame(frame); cancelAnimationFrame(scrolling);
    live.remove(); signal?.removeEventListener('abort', dispose);
  }
  signal?.addEventListener('abort', dispose, { once: true });
  layout();
  return { refresh: schedule, dispose };
}
