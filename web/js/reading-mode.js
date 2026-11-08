import { el, dialog } from './ui.js';

// Move the live nodes, never a clone: event handlers, selection and unsaved inputs
// survive the enlarged view. This does not write a layout or business document.
export function openReadingView(content, { title, signal, className = '', returnFocus, onOpen, onClose } = {}) {
  if (!content?.isConnected || signal?.aborted) return null;
  const anchor = el('div', { class: 'reading-view-placeholder', 'aria-hidden': 'true' });
  const bounds = content.getBoundingClientRect(), style = getComputedStyle(content);
  Object.assign(anchor.style, { height: `${bounds.height}px`, flex: '0 0 auto', marginTop: style.marginTop, marginBottom: style.marginBottom });
  const trigger = returnFocus || document.activeElement;
  const positions = [];
  for (let node = content.parentElement; node; node = node.parentElement)
    positions.push([node, node.scrollLeft, node.scrollTop]);
  const localScroll = [content.scrollLeft, content.scrollTop];
  content.before(anchor);
  const host = el('div', { class: 'reading-focus-content' });
  const modal = dialog(title || 'Vue agrandie', host, [], { returnFocus: trigger });
  modal.element.classList.add('reading-focus');
  if (className) modal.element.classList.add(className);
  host.append(content);
  let closed = false;
  const close = () => modal.close();
  const restore = () => {
    if (closed) return;
    closed = true;
    signal?.removeEventListener('abort', close);
    if (anchor.isConnected) anchor.replaceWith(content);
    else content.remove();
    onClose?.();
    content.scrollLeft = localScroll[0]; content.scrollTop = localScroll[1];
    for (const [node, left, top] of positions) if (node.isConnected) { node.scrollLeft = left; node.scrollTop = top; }
    if (trigger?.isConnected) trigger.focus({ preventScroll: true });
  };
  modal.element.addEventListener('pilot:closed', restore, { once: true });
  signal?.addEventListener('abort', close, { once: true });
  try { onOpen?.(); }
  catch (error) { close(); throw error; }
  return { close, element: modal.element };
}

// Secondary controls stay in the normal document flow. No absolute-positioned
// menu, nested scrollbar, focus trap or cloned form controls.
export function foldControls(label, ...children) {
  return el('details', { class: 'reading-options' },
    el('summary', { text: label }), el('div', { class: 'reading-options-body' }, ...children));
}
