import { el, button } from '../ui.js';
import { openReadingView } from '../reading-mode.js';

// Presentation only: keep the original rows and actions. No count, denominator,
// filter, order or persisted layout is changed to make a smaller preview.
export function attachWidgetReading(node, setting, signal, { controls = null, onContentChange = () => {} } = {}) {
  const body = node.querySelector(':scope > .analytics-widget-body');
  if (!body) return { dispose() {} };
  const size = setting.height <= 240 ? 'small' : setting.height <= 400 ? 'medium' : 'large';
  const limit = { small: 3, medium: 6, large: 10 }[size];
  const hidden = new Set();
  let expanded = null, disposed = false, scheduled = false;
  node.dataset.readingSize = size;
  const label = el('span', { class: 'workspace-reading-caption' });
  const expand = button('Agrandir', () => {
    if (expanded || disposed) return;
    showAll();
    expanded = openReadingView(body, { title: node.querySelector('h3')?.textContent || 'Widget', signal,
      className: 'workspace-widget-focus', returnFocus: expand, onClose: () => { expanded = null; compact(); onContentChange(); } });
  }, { attributes: { 'data-widget-expand': setting.id }, title: 'Lire tout le contenu chargé sans changer la disposition' });
  const footer = el('div', { class: 'workspace-reading-footer no-print' }, ...(controls ? [controls] : [label, expand]));
  node.append(footer);
  function describe() {
    if (disposed || expanded) return;
    const clipped = body.scrollHeight > body.clientHeight + 1;
    label.textContent = hidden.size ? `Aperçu · ${hidden.size} élément(s) supplémentaire(s) chargé(s)`
      : clipped ? 'Aperçu · agrandir pour tout lire' : 'Vue complète';
    node.dataset.readingLimited = String(hidden.size > 0 || clipped);
  }
  function showAll() {
    for (const child of hidden) { child.hidden = false; child.removeAttribute('data-reading-hidden'); }
    hidden.clear();
  }
  function compact() {
    if (disposed || expanded) return;
    showAll();
    // Tabular/bars previews only. Do not truncate warnings, quantities, chart
    // scales, pie slices or prose, and never hide content inside a disclosure.
    const groups = [...body.querySelectorAll('tbody')].filter(group => !group.closest('details'));
    const bars = [...body.querySelectorAll('.analytics-distribution')].filter(group => !group.closest('details'));
    for (const group of [...groups, ...bars]) {
      const children = [...group.children].filter(child => !child.hidden &&
        (group.tagName === 'TBODY' ? child.tagName === 'TR' : child.matches('.analytics-bar-row')));
      for (const child of children.slice(limit)) { child.hidden = true; child.dataset.readingHidden = 'true'; hidden.add(child); }
    }
    describe();
  }
  const summaryKeys = event => {
    if (expanded || disposed || event.key !== 'ArrowDown' || !event.target.matches?.('tr')) return;
    if (event.target.nextElementSibling?.dataset.readingHidden !== 'true') return;
    event.preventDefault(); event.stopImmediatePropagation();
    (controls?.querySelector('button') || expand).focus({ preventScroll: true });
  };
  body.addEventListener('keydown', summaryKeys, true);
  const observer = new MutationObserver(() => {
    if (scheduled || disposed || expanded) return;
    scheduled = true; queueMicrotask(() => { scheduled = false; if (disposed) return; compact(); onContentChange(); });
  });
  observer.observe(body, { childList: true, subtree: true });
  const resizeObserver = new ResizeObserver(describe); resizeObserver.observe(body);
  body.addEventListener('toggle', onContentChange, true);
  compact();
  const dispose = () => {
    if (disposed) return;
    disposed = true; observer.disconnect(); resizeObserver.disconnect(); body.removeEventListener('keydown', summaryKeys, true);
    body.removeEventListener('toggle', onContentChange, true); expanded?.close(); showAll(); footer.remove();
    delete node.dataset.readingSize; delete node.dataset.readingLimited;
    signal?.removeEventListener('abort', dispose);
  };
  signal?.addEventListener('abort', dispose, { once: true });
  return { dispose };
}
