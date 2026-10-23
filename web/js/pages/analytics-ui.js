import { el as baseElement, button, card, toolbar, empty, badge, input, select, field, table, dialog, formatNumber, formatDate, showError } from '../ui.js';
export { button, card, toolbar, empty, badge, input, select, field, table, dialog, formatNumber, formatDate };
export function el(tag, attrs = {}, ...children) {
  const { style, ...safe } = attrs ?? {}; const element = baseElement(tag, safe, ...children);
  if (style != null) {
    if (typeof style !== 'object') throw new TypeError('Utiliser des propriétés CSSOM pour les styles calculés.');
    for (const [name, value] of Object.entries(style)) {
      if (name.startsWith('--') || name.includes('-')) element.style.setProperty(name, String(value));
      else element.style[name] = String(value);
    }
  }
  return element;
}

export const call = (ctx, op, args = {}) => ctx.api.call(op, args, { signal: ctx.signal });
export const uuid = () => crypto.randomUUID().replaceAll('-', '');
export const clone = value => structuredClone(value);
export const number = (value, unit = '') => value == null || !Number.isFinite(value) ? 'Indisponible' : `${formatNumber(value)}${unit ? ` ${unit}` : ''}`;
export const caption = (text, cls = '') => el('p', { class: `analytics-caption ${cls}`, text });
export const choices = values => values.map(value => typeof value === 'string' ? { value, label: value } : { value: value.key ?? value.id, label: value.label ?? value.name });
export const columnStateLabel = column => column.stateLabel || ({ auto: 'Reconnue automatiquement', configured: 'Définie manuellement',
  ambiguous: 'Plusieurs colonnes possibles', 'missing-override': 'Colonne configurée absente', missing: 'Aucune colonne reconnue' })[column.state] || 'Correspondance non déterminée';
export const getRevision = ctx => ctx.state.session?.revision ?? 0;
export function error(ctx, reason) {
  if (ctx.signal.aborted || reason?.name === 'AbortError') return;
  let box = ctx.root.querySelector('.analytics-error');
  if (!box) { box = el('div', { class: 'analytics-error', role: 'alert' }); ctx.root.prepend(box); }
  showError(box, reason); box.scrollIntoView({ block: 'nearest' });
}
export function command(ctx, label, action, options = {}) {
  let control;
  control = button(label, async () => {
    control.disabled = true;
    try { await action(); } catch (reason) { error(ctx, reason); }
    finally { if (control.isConnected && !options.disabled) control.disabled = false; }
  }, options);
  return control;
}
export function heading(ctx, title, detail) {
  ctx.root.classList.add('analytics');
  ctx.root.setAttribute('aria-label', title);
  ctx.root.replaceChildren();
  if (!ctx.root.closest('.workspace')?.querySelector('.page-heading'))
    ctx.root.append(el('header', { class: 'analytics-heading' }, el('h1', { text: title }), caption(detail)));
  return () => ctx.root.classList.remove('analytics');
}
export function details(title, ...children) { return el('details', { class: 'analytics-details' }, el('summary', { text: title }), ...children); }
export function pager(ctx, total, offset, size, change) {
  return toolbar(command(ctx, 'Précédent', () => change(Math.max(0, offset - size)), { disabled: offset === 0 }),
    caption(`${total === 0 ? 0 : offset + 1}–${Math.min(total, offset + size)} sur ${formatNumber(total)}`),
    command(ctx, 'Suivant', () => change(offset + size), { disabled: offset + size >= total }));
}
export async function rowDialog(ctx, op, args, title = 'Lignes du relevé', explain = null) {
  const body = el('div', { class: 'analytics-row-dialog' }); const modal = dialog(title, body);
  let columnOffset = 0;
  const load = async offset => {
    const page = await call(ctx, op, { ...args, offset, limit: 50, columnOffset });
    if (ctx.signal.aborted || !modal.element.isConnected) return;
    const columns = [{ key: 'index', label: 'Ligne', render: row => String(row.index + 1) },
      ...page.columns.map((label, index) => ({ key: `c${index}`, label, render: row => command(ctx, row.values[index] || '—', async () => {
        const cell = await call(ctx, 'records.cell', { revision: page.revision, row: row.index, column: columnOffset + index });
        dialog(`Cellule · ${cell.header}`, el('div', {}, caption(`Ligne ${row.index + 1} · rôle ${cell.role || 'non attribué'}`),
          el('h3', { text: 'Valeur brute' }), el('pre', { class: 'analytics-preview', text: cell.raw }),
          el('h3', { text: 'Interprétation' }), el('pre', { class: 'analytics-preview', text: cell.interpreted ?? 'Non interprétable' })));
      }, { class: 'analytics-cell-value', title: 'Voir la valeur brute complète et son interprétation' }) }))];
    if (!ctx.state.session?.testMode) columns.push({ key: 'open', label: 'Consulter', render: row => command(ctx, 'Ticket',
      () => { modal.close(); ctx.navigate('tickets', { rowIndex: row.index, revision: page.revision }); }) });
    if (explain) columns.push({ key: 'explain', label: 'Décision', render: row => command(ctx, 'Pourquoi ?', async () => {
      const result = await call(ctx, 'queries.explain', { ...explain, rowIndex: row.index });
      dialog('Décision de la requête pour cette ligne', el('div', {}, badge(result.decision), caption(result.logic), caption(result.message),
        table(result.conditions, [{ key: 'number', label: 'N°' }, { key: 'field', label: 'Champ' }, { key: 'comparison', label: 'Comparaison' }, { key: 'decision', label: 'Décision' }, { key: 'explanation', label: 'Explication' }])));
    }) });
    body.replaceChildren(caption(`Cellules affichées jusqu’à ${page.cellLimit} caractères. Les exports conservent les données complètes.`),
      table(page.rows, columns, { key: 'index', maxRows: 50 }), pager(ctx, page.total, page.offset, 50, load),
      toolbar(command(ctx, 'Colonnes précédentes', async () => { columnOffset = Math.max(0, columnOffset - 20); await load(offset); }, { disabled: columnOffset === 0 }),
        caption(`Colonnes ${page.columns.length ? columnOffset + 1 : 0}–${columnOffset + page.columns.length} sur ${page.totalColumns}`),
        command(ctx, 'Colonnes suivantes', async () => { columnOffset += 20; await load(offset); }, { disabled: columnOffset + page.columns.length >= page.totalColumns })));
  };
  try { await load(0); } catch (reason) { modal.close(); throw reason; }
}
export function textArea(value = '', rows = 4) { return el('textarea', { value, rows, class: 'analytics-textarea' }); }
export function checkbox(label, value = false) {
  const control = el('input', { type: 'checkbox', checked: value });
  return { control, element: el('label', { class: 'analytics-check' }, control, el('span', { text: label })) };
}
export function nullableNumber(value, label) {
  const control = input(value ?? '', { type: 'number', min: 0, step: 'any' });
  return { element: field(label, control), read: () => control.value.trim() === '' ? null : Number(control.value) };
}
export function filterEditor(catalog, initial = []) {
  const root = el('div', { class: 'analytics-filter-editor' }); const rows = [];
  const add = value => {
    if (rows.length >= 12) throw new Error('Douze conditions au maximum par périmètre.');
    const kind = select([{ value: 'distribution', label: 'Catégorie' }, { value: 'matrix-row', label: 'Groupe ou ligne de tableau' }, { value: 'metric', label: 'Population' }, { value: 'date', label: 'Période datée' }], value.kind ?? 'distribution');
    const source = select([], value.sourceKey ?? ''); const first = input(value.value ?? ''); const last = input(value.value2 ?? '', { type: 'date' });
    const alternatives = textArea(value.values?.length ? value.values.join('\n') : value.value ?? '', 3);
    const alternativesField = field('Valeurs acceptées · une par ligne', alternatives);
    const firstField = field('Début inclus', first); const lastField = field('Fin incluse', last);
    const exclude = checkbox('Exclure', value.exclude ?? false);
    const descendants = checkbox('Inclure les sous-catégories', value.includeDescendants ?? false);
    const row = el('div', { class: 'analytics-filter-row' });
    const categoryBranchAvailable = () => kind.value === 'distribution' && ['category', 'active-category'].includes(source.value);
    const refreshDescendants = () => { descendants.element.hidden = !categoryBranchAvailable(); };
    const refresh = () => {
      const previous = source.value || value.sourceKey;
      const available = kind.value === 'metric' ? catalog.metrics : kind.value === 'date' ? catalog.dates : kind.value === 'matrix-row' ? catalog.matrices : catalog.distributions;
      source.replaceChildren(...(available ?? []).map(item => el('option', { value: item.key, text: `${item.label}${item.available === false ? ' · indisponible' : ''}` })));
      if (previous && ![...source.options].some(option => option.value === previous)) source.append(el('option', { value: previous, text: `${previous} · indisponible` }));
      if (previous) source.value = previous;
      first.type = 'date'; firstField.hidden = lastField.hidden = kind.value !== 'date';
      alternativesField.hidden = kind.value !== 'distribution' && kind.value !== 'matrix-row';
      refreshDescendants();
    };
    kind.addEventListener('change', refresh); source.addEventListener('change', refreshDescendants); refresh();
    const item = { read: () => {
      const selections = [...new Set(alternatives.value.split(/\r?\n/).map(item => item.trim()).filter(Boolean))];
      if ((kind.value === 'distribution' || kind.value === 'matrix-row') && selections.length > 32) throw new Error('Une condition accepte au maximum 32 valeurs.');
      return { kind: kind.value, sourceKey: source.value,
        value: kind.value === 'date' ? first.value : kind.value === 'metric' ? '' : selections.length === 1 ? selections[0] : '',
        values: kind.value === 'distribution' || kind.value === 'matrix-row' ? selections.length > 1 ? selections : [] : [],
        value2: kind.value === 'date' ? last.value : '', exclude: exclude.control.checked,
        includeDescendants: categoryBranchAvailable() && descendants.control.checked };
    } };
    row.append(field('Type', kind), field('Source', source), alternativesField, firstField, lastField, descendants.element, exclude.element,
      button('Retirer', () => { row.remove(); rows.splice(rows.indexOf(item), 1); addButton.disabled = false; }));
    item.row = row; rows.push(item); list.append(row); addButton.disabled = rows.length >= 12;
  };
  const list = el('div'); const message = el('div', { role: 'alert' });
  const addButton = button('+ Condition', () => { try { add({}); } catch (reason) { message.textContent = reason.message; } });
  root.append(list, addButton, caption('Les valeurs d’une condition sont combinées par OU, sans compter deux fois le même ticket. Les conditions sont combinées par ET. Les filtres des widgets s’ajoutent au périmètre commun.'), message);
  for (const value of initial) add(value);
  return { element: root, read: () => rows.map(row => row.read()) };
}
export function svg(tag, attrs = {}, ...children) {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, value] of Object.entries(attrs)) if (value != null) node.setAttribute(key, String(value));
  for (const child of children) node.append(typeof child === 'string' ? document.createTextNode(child) : child);
  return node;
}
export function sparkline(series, compact = false) {
  if (!series) return empty('Historique non calculé', 'Lancez le calcul des observations pour ce périmètre.');
  const points = series.points ?? []; const good = points.filter(p => p.comparable && p.value != null && Number.isFinite(p.value));
  const panel = el('div', { class: `analytics-history ${compact ? 'compact' : ''}` });
  if (!good.length) { panel.append(caption('Aucune observation comparable.', 'warning'), details('Disponibilité de cette série', caption(series.explanation))); return panel; }
  const dates = points.map(p => Date.parse(p.capturedAt)); const minDate = Math.min(...dates); const maxDate = Math.max(...dates);
  const low = Math.min(...good.map(p => p.value)); const high = Math.max(...good.map(p => p.value));
  const x = p => 15 + 570 * (Date.parse(p.capturedAt) - minDate) / Math.max(1, maxDate - minDate);
  const y = p => 115 - 95 * (p.value - low) / Math.max(1, high - low);
  const drawing = svg('svg', { viewBox: '0 0 600 140', role: 'img', 'aria-label': `${series.title}. ${good.length} observations comparables. Les ruptures ne sont pas interpolées.` });
  let segment = [];
  const flush = () => { if (segment.length > 1) drawing.append(svg('polyline', { points: segment.join(' '), fill: 'none', stroke: 'currentColor', 'stroke-width': 2.5 })); segment = []; };
  for (const point of points) {
    if (!point.comparable || point.value == null) { flush(); continue; }
    segment.push(`${x(point)},${y(point)}`);
    drawing.append(svg('circle', { cx: x(point), cy: y(point), r: 3 }, svg('title', {}, `${formatDate(point.capturedAt)} : ${number(point.value, series.unit)} · ${point.note}`)));
  }
  flush(); panel.append(drawing, caption(`${formatDate(points[0]?.capturedAt)} → ${formatDate(points.at(-1)?.capturedAt)} · ${good.length} observations · ${points.length - good.length} ruptures / inconnues`));
  if (!compact) panel.append(details('Observations et limites', caption(series.explanation), table(points, [
    { key: 'capturedAt', label: 'Observation', render: p => formatDate(p.capturedAt) }, { key: 'value', label: 'Valeur', render: p => number(p.value, series.unit) },
    { key: 'rows', label: 'Lignes' }, { key: 'comparable', label: 'Comparable', render: p => p.comparable ? 'Oui' : 'Non' }, { key: 'note', label: 'Limites' }
  ], { maxRows: 250 })));
  return panel;
}
