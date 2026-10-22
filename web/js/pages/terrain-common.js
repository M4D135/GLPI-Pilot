import { el, button, toolbar, field, input, table, dialog, showError, formatNumber } from '../ui.js';

export const labels = { city: 'Ville', site: 'Site', building: 'Bâtiment' };
export const stateLabels = { draft: 'Brouillon', ready: 'Prête', done: 'Effectuée' };
export const listText = values => (values || []).join(' ; ');
export const readList = value => String(value || '').split(/[;\n]/).map(part => part.trim()).filter(Boolean);
export const numberOrNull = value => {
  if (value === '' || value == null) return null;
  const number = Number(String(value).trim().replace(',', '.'));
  if (!Number.isFinite(number)) throw new Error('La coordonnée doit être un nombre fini.');
  return number;
};
export const text = (value, fallback = '—') => value == null || value === '' ? fallback : String(value);
export const minuteText = value => value == null ? 'Non déclarée' : `${formatNumber(value)} min`;
export const timeText = value => value ? String(value).slice(11, 16) : '—';
export const dayText = value => value ? String(value).slice(0, 10) : '—';
export const guid = () => crypto.randomUUID().replaceAll('-', '');

export function page(ctx, title, detail) {
  const errors = el('div', { class: 'terrain-errors', role: 'alert' });
  const status = el('p', { class: 'terrain-status', role: 'status', 'aria-live': 'polite' });
  const cancel = button('Annuler le traitement', () => active?.abort(), { disabled: true });
  const body = el('div', { class: 'terrain-body' });
  const root = el('section', { class: 'terrain-page' }, el('header', { class: 'terrain-heading' },
    el('div', {}, el('h1', { text: title }), el('p', { text: detail })), cancel), status, errors, body);
  ctx.root.append(root);
  let active = null, busy = false;
  const ownedDialogs = new Set();
  const events = [];
  const api = {
    ctx, root, body, errors,
    call: (op, args = {}) => ctx.api.call(op, args, { signal: active?.signal || ctx.signal }),
    async run(action, errorRoot = errors) {
      if (busy || ctx.signal.aborted) return;
      busy = true; active = new AbortController(); const abort = () => active?.abort(); ctx.signal.addEventListener('abort', abort, { once: true });
      errorRoot.replaceChildren(); cancel.disabled = false; root.setAttribute('aria-busy', 'true');
      const panels = [body, ...[...ownedDialogs].flatMap(item => [...item.element.querySelectorAll('.modal-body, .modal-actions')])];
      panels.forEach(node => { node.inert = true; });
      try { return await action(); }
      catch (error) {
        if (!ctx.signal.aborted) {
          if (active?.signal.aborted || error?.name === 'AbortError') status.textContent = 'Traitement annulé. Aucun résultat partiel n’a été appliqué au brouillon.';
          else showError(errorRoot, error);
        }
      }
      finally {
        panels.forEach(node => { node.inert = false; });
        ctx.signal.removeEventListener('abort', abort); active = null; busy = false; cancel.disabled = true; root.removeAttribute('aria-busy');
      }
    },
    act: action => () => api.run(action),
    message: message => { status.textContent = message; },
    dialog(title, content, actions = []) {
      const item = dialog(title, content, actions); ownedDialogs.add(item);
      item.element.classList.add('terrain-dialog');
      const close = item.close; item.close = () => { ownedDialogs.delete(item); close(); };
      return item;
    },
    on(event, handler) { const stop = ctx.api.on(event, handler); events.push(stop); return stop; },
    cleanup() { active?.abort(); events.forEach(stop => stop()); ownedDialogs.forEach(item => item.close()); ownedDialogs.clear(); }
  };
  return api;
}

export function pager(data, change) {
  const first = data.total ? data.offset + 1 : 0, last = Math.min(data.total, data.offset + data.limit);
  return toolbar(button('Précédent', () => change(Math.max(0, data.offset - data.limit)), { disabled: data.offset <= 0 }),
    el('span', { class: 'terrain-page-count', text: `${formatNumber(first)}–${formatNumber(last)} / ${formatNumber(data.total)}` }),
    button('Suivant', () => change(data.offset + data.limit), { disabled: data.offset + data.limit >= data.total }));
}

/** Search and pagination are server-side, including for relationship pickers. */
export async function lookup(p, kind, chosen, options = {}) {
  const search = input('', { placeholder: 'Rechercher…', maxLength: 120 });
  const results = el('div'); const errors = el('div', { role: 'alert' });
  const body = el('div', { class: 'terrain-picker' }, field('Recherche', search),
    button('Rechercher', () => p.run(() => load(0), errors)), errors, results);
  const popup = p.dialog(options.title || 'Choisir une référence', body);
  async function load(offset) {
    const data = await p.call('terrain.reference.lookup', { kind, search: search.value, offset, limit: 30, saved: !!options.saved, parent: options.parent || '' });
    if (p.ctx.signal.aborted) return;
    results.replaceChildren(table(data.items, [
      { key: 'label', label: 'Nom' }, { key: 'detail', label: 'Rattachement' },
      { key: 'choose', label: '', render: row => button('Choisir', () => { popup.close(); chosen(row); }) }
    ], { key: 'value', maxRows: 30, onSelect: row => { popup.close(); chosen(row); }, emptyText: 'Aucune référence correspondante.' }),
    pager(data, next => p.run(() => load(next), errors)));
  }
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); p.run(() => load(0), errors); } });
  await load(0);
  return popup;
}

export function referenceField(p, label, kind, value = '', name = '', options = {}) {
  let selected = value;
  const output = el('span', { class: 'terrain-picked', text: name || value || options.empty || 'À choisir' });
  const choose = button('Choisir…', p.act(() => lookup(p, kind, row => { selected = row.value; output.textContent = row.label; options.onChange?.(row); },
    { ...options, parent: typeof options.parent === 'function' ? options.parent() : options.parent })));
  const clear = button('Retirer', () => { selected = ''; output.textContent = options.empty || 'Non renseigné'; options.onChange?.({ value: '', label: '' }); });
  return { element: field(label, toolbar(output, choose, clear), options.help || ''), read: () => selected,
    set(next, nextName = '') { selected = next; output.textContent = nextName || next || options.empty || 'À choisir'; } };
}

export function highlighted(title, ranges) {
  const line = el('p', { class: 'terrain-highlight' }); let cursor = 0;
  for (const range of ranges || []) {
    const start = Math.max(cursor, range.start), end = Math.min(title.length, start + range.length);
    if (start > title.length || end <= start) continue;
    if (start > cursor) line.append(document.createTextNode(title.slice(cursor, start)));
    line.append(el('mark', { text: title.slice(start, end) })); cursor = end;
  }
  if (cursor < title.length) line.append(document.createTextNode(title.slice(cursor)));
  return line;
}

export function details(title, ...children) { return el('details', { class: 'terrain-details' }, el('summary', { text: title }), ...children); }
export function facts(pairs) {
  return el('dl', { class: 'terrain-facts' }, ...pairs.flatMap(([label, value]) => [el('dt', { text: label }), el('dd', { text: text(value) })]));
}
