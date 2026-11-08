// Presentation changes only. Business values and persisted validation stay in C#.
export function ensureSections(board) {
  board.sections ??= [];
  for (const key of [...new Set(board.widgets.map(widget => widget.section || ''))])
    if (!board.sections.some(section => section.key === key)) board.sections.push({ key, title: key || 'Vue d’ensemble', description: '', accent: 'blue', hidden: false });
  if (!board.sections.length) board.sections.push({ key: '', title: 'Vue d’ensemble', description: '', accent: 'blue', hidden: false });
  return board.sections;
}

export function moveDashboardWidget(board, id, sectionKey, targetId = null, after = false) {
  const widget = board.widgets.find(item => item.id === id);
  const section = ensureSections(board).find(item => item.key === sectionKey);
  const target = targetId == null ? null : board.widgets.find(item => item.id === targetId);
  if (!widget || widget.locked || !section || targetId != null && (!target || target === widget || target.section !== sectionKey)) return false;
  const before = board.widgets.map(item => item.id).join('|'); const oldSection = widget.section;
  board.widgets.splice(board.widgets.indexOf(widget), 1);
  widget.section = sectionKey;
  let index = target ? board.widgets.indexOf(target) + (after ? 1 : 0) : board.widgets.findLastIndex(item => item.section === sectionKey) + 1;
  if (!target && index === 0) index = board.widgets.length;
  board.widgets.splice(index, 0, widget);
  const changed = oldSection !== sectionKey || before !== board.widgets.map(item => item.id).join('|');
  if (changed) { widget.startNewRow = false; board.collapsedSections = (board.collapsedSections ?? []).filter(key => key !== sectionKey); }
  return changed;
}

export function moveDashboardSection(board, key, targetKey, after = false) {
  const sections = ensureSections(board); const source = sections.find(section => section.key === key);
  const target = sections.find(section => section.key === targetKey);
  if (!source || !target || source === target) return false;
  const before = sections.map(section => section.key).join('|');
  sections.splice(sections.indexOf(source), 1); sections.splice(sections.indexOf(target) + (after ? 1 : 0), 0, source);
  return before !== sections.map(section => section.key).join('|');
}

// Native drag handles leave selection, charts and form controls usable. Keyboard
// movement and exact-size dialogs provide the same operations without dragging.
export function bindDashboardLayout({ host, getBoard, canEdit, commit, announce, signal }) {
  let drag = null; let resize = null; let suppressResizeClick = false;
  const listen = (node, event, handler) => node.addEventListener(event, handler, { signal });
  const clearTargets = () => host.querySelectorAll('[data-drop-side]').forEach(node => node.removeAttribute('data-drop-side'));
  const clearDrag = () => { drag = null; clearTargets(); host.classList.remove('is-dragging'); host.querySelectorAll('.is-drag-source').forEach(node => node.classList.remove('is-drag-source')); };
  const widgetNode = node => node?.closest('[data-widget-id]');
  const sectionNode = node => node?.closest('[data-section-key]');
  const save = focus => Promise.resolve(commit(focus)).catch(() => {});
  listen(host, 'dragstart', event => {
    const handle = event.target.closest('[data-drag-kind]'); if (!handle || !canEdit()) { event.preventDefault(); return; }
    const board = getBoard(); const kind = handle.dataset.dragKind;
    const source = kind === 'widget' ? widgetNode(handle) : sectionNode(handle);
    const key = kind === 'widget' ? source?.dataset.widgetId : source?.dataset.sectionKey;
    if (!source || kind === 'widget' && board.widgets.find(widget => widget.id === key)?.locked) { event.preventDefault(); return; }
    drag = { kind, key }; event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', key || 'general');
    source.classList.add('is-drag-source'); host.classList.add('is-dragging'); announce(kind === 'widget' ? 'Choisissez une position ou une section.' : 'Choisissez la position de la section.');
  });
  function destination(event) {
    const section = sectionNode(event.target); if (!section) return null;
    if (drag.kind === 'section') {
      if (section.dataset.sectionKey === drag.key) return null;
      const rect = section.getBoundingClientRect();
      return { node: section, key: section.dataset.sectionKey, after: event.clientY > rect.top + rect.height / 2 };
    }
    const widget = widgetNode(event.target);
    if (widget?.dataset.widgetId === drag.key) return null;
    if (!widget) return { node: section.querySelector('.analytics-drop-zone') || section, key: section.dataset.sectionKey, after: false };
    const rect = widget.getBoundingClientRect(); const y = (event.clientY - rect.top) / rect.height;
    return { node: widget, key: section.dataset.sectionKey, target: widget.dataset.widgetId, after: y > .75 || y >= .25 && event.clientX > rect.left + rect.width / 2 };
  }
  listen(host, 'dragover', event => {
    if (!drag || !canEdit()) return; const target = destination(event); clearTargets(); if (!target) return;
    event.preventDefault(); event.dataTransfer.dropEffect = 'move'; target.node.dataset.dropSide = target.after ? 'after' : 'before';
  });
  listen(host, 'drop', event => {
    if (!drag || !canEdit()) return; event.preventDefault(); const target = destination(event); const source = drag; clearDrag(); if (!target) return;
    const changed = source.kind === 'widget' ? moveDashboardWidget(getBoard(), source.key, target.key, target.target, target.after)
      : moveDashboardSection(getBoard(), source.key, target.key, target.after);
    if (changed) save({ kind: source.kind, key: source.key, action: 'move' });
  });
  listen(host, 'dragend', clearDrag);
  listen(host, 'keydown', event => {
    const handle = event.target.closest('[data-drag-kind]'); if (!handle || !canEdit() || !['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    event.preventDefault(); const board = getBoard(); const sections = ensureSections(board); const delta = ['ArrowUp', 'ArrowLeft'].includes(event.key) ? -1 : 1;
    if (handle.dataset.dragKind === 'section') {
      const key = sectionNode(handle).dataset.sectionKey; const next = sections[sections.findIndex(section => section.key === key) + delta];
      if (next && moveDashboardSection(board, key, next.key, delta > 0)) save({ kind: 'section', key, action: 'move' });
    } else {
      const id = widgetNode(handle).dataset.widgetId; const widget = board.widgets.find(item => item.id === id); if (!widget || widget.locked) return;
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        const next = sections[sections.findIndex(section => section.key === widget.section) + delta];
        if (next && moveDashboardWidget(board, id, next.key)) save({ kind: 'widget', key: id, action: 'move' });
      } else {
        const peers = board.widgets.filter(item => !item.hidden && item.section === widget.section); const target = peers[peers.indexOf(widget) + delta];
        if (target && moveDashboardWidget(board, id, widget.section, target.id, delta > 0)) save({ kind: 'widget', key: id, action: 'move' });
      }
    }
  });
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const applySize = (node, span, height) => { node.style.setProperty('--span', String(span)); node.style.setProperty('--widget-height', `${height}px`); node.dataset.autoHeight = 'false'; };
  function finishResize(cancel) {
    if (!resize) return; const current = resize; resize = null;
    suppressResizeClick = current.moved;
    current.node.classList.remove('is-resizing'); host.classList.remove('is-resizing'); current.handle.releasePointerCapture?.(current.pointerId);
    if (cancel || !current.moved) { applySize(current.node, current.widget.span, current.widget.height); current.node.dataset.autoHeight = String(current.widget.autoHeight); announce(cancel ? 'Redimensionnement annulé.' : ''); return; }
    Object.assign(current.widget, { span: current.span, height: current.height, autoHeight: false });
    save({ kind: 'widget', key: current.widget.id, action: 'resize' });
  }
  listen(host, 'pointerdown', event => {
    const handle = event.target.closest('[data-resize-widget]'); if (!handle || !canEdit() || event.button !== 0) return;
    const node = widgetNode(handle); const widget = getBoard().widgets.find(item => item.id === node.dataset.widgetId); if (!widget || widget.locked) return;
    event.preventDefault(); suppressResizeClick = false; handle.focus(); const rect = node.getBoundingClientRect(); const grid = node.closest('.analytics-board');
    const gap = parseFloat(getComputedStyle(grid).columnGap) || 16;
    resize = { handle, node, widget, pointerId: event.pointerId, x: event.clientX, y: event.clientY, span: widget.span,
      height: clamp(Math.round(rect.height / 20) * 20, 160, 960), startHeight: rect.height, step: (grid.getBoundingClientRect().width + gap) / 12, moved: false };
    handle.setPointerCapture(event.pointerId); node.classList.add('is-resizing'); host.classList.add('is-resizing');
  });
  listen(host, 'pointermove', event => {
    if (!resize || resize.pointerId !== event.pointerId) return; event.preventDefault();
    resize.moved ||= Math.abs(event.clientX - resize.x) + Math.abs(event.clientY - resize.y) > 4;
    if (!resize.moved) return;
    resize.span = clamp(resize.widget.span + Math.round((event.clientX - resize.x) / resize.step), 3, 12);
    resize.height = clamp(Math.round((resize.startHeight + event.clientY - resize.y) / 20) * 20, 160, 960);
    applySize(resize.node, resize.span, resize.height); announce(`${resize.span} colonnes sur 12 · ${resize.height} pixels`);
  });
  listen(host, 'pointerup', event => { if (resize?.pointerId === event.pointerId) finishResize(false); });
  listen(host, 'pointercancel', event => { if (resize?.pointerId === event.pointerId) finishResize(true); });
  listen(host, 'lostpointercapture', event => { if (resize?.pointerId === event.pointerId) finishResize(true); });
  host.addEventListener('click', event => {
    if (suppressResizeClick && event.target.closest('[data-resize-widget]')) { event.preventDefault(); event.stopImmediatePropagation(); suppressResizeClick = false; }
  }, { capture: true, signal });
  listen(host, 'keydown', event => {
    const handle = event.target.closest('[data-resize-widget]'); if (!handle || !canEdit()) return;
    if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    event.preventDefault(); const widget = getBoard().widgets.find(item => item.id === handle.dataset.resizeWidget); if (!widget || widget.locked) return;
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') widget.span = clamp(widget.span + (event.key === 'ArrowLeft' ? -1 : 1), 3, 12);
    else widget.height = clamp(widget.height + (event.key === 'ArrowUp' ? -1 : 1) * (event.shiftKey ? 100 : 20), 160, 960);
    widget.autoHeight = false; save({ kind: 'widget', key: widget.id, action: 'resize' });
  });
  listen(document, 'keydown', event => { if (event.key === 'Escape') { if (resize) { event.preventDefault(); finishResize(true); } clearDrag(); } });
  signal.addEventListener('abort', () => { finishResize(true); clearDrag(); }, { once: true });
}
