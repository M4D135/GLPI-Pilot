import { el, button, card, toolbar, input, select, field, table, dialog, confirm, formatDate } from '../ui.js';

export const call = (ctx, op, args = {}, options = {}) => ctx.api.call(op, args, { signal: ctx.signal, ...options });
export const revision = ctx => ctx.state.session?.revision ?? 0;
export const message = (ctx, error) => { if (error?.name !== 'AbortError' && !ctx.signal?.aborted) ctx.notify(error?.message || String(error), 'error'); };
export const run = (ctx, action) => async (...args) => { try { return await action(...args); } catch (error) { message(ctx, error); } };
export const note = text => el('p', { class: 'work-note', text });
export const pre = text => el('pre', { class: 'work-text', text: text || 'Aucune donnée recueillie.' });
export const label = value => value == null || value === '' ? '—' : String(value);
export function ticketIdLabel(value) {
  const text = String(value ?? '').trim();
  // GLPI may display thousands separators in a numeric ID. This is only a
  // label: preserve leading zeroes, other identifiers and the original row.
  return /^\d{1,3}(?:[ \u00a0\u202f]\d{3})+$/.test(text) ? text.replace(/[ \u00a0\u202f]/g, '') : text;
}
export function ticketButton(value, handler, options = {}) {
  const text = ticketIdLabel(value);
  const control = button(text || 'Ouvrir', handler, { title: text ? 'Ouvrir le ticket ' + text : 'Ouvrir le ticket', ...options });
  control.classList.add('work-ticket-id'); return control;
}
export const date = value => value ? formatDate(value) : '—';
export function heading(title, detail = '') { return el('header', { class: 'work-heading' }, el('h1', { text: title }), detail ? note(detail) : null); }
export function checkbox(value, text) { const control = el('input', { type: 'checkbox' }); control.checked = !!value; return { control, element: el('label', { class: 'work-check' }, control, el('span', { text })) }; }
export function textArea(value = '', max = 20000, rows = 8) { const control = el('textarea', { class: 'input work-editor', maxLength: max, rows }); control.value = value; return control; }
export function dataTable(rows, columns, options = {}) { return table(rows, columns.map(c => ({ ...c, render: c.render ?? (row => label(row[c.key])) })), { maxRows: 100, ...options }); }
export function pager(page, pageSize, total, change) {
  const last = Math.max(0, Math.ceil(total / pageSize) - 1);
  return toolbar(button('Précédent', () => change(page - 1), { disabled: page === 0 }), el('span', { class: 'work-page-count', text: `${total.toLocaleString('fr-FR')} résultat(s) · page ${page + 1} / ${last + 1}` }), button('Suivant', () => change(page + 1), { disabled: page >= last }));
}
export function tabs(items, current, choose) {
  const navigation = el('nav', { class: 'work-tabs', 'aria-label': 'Pages de cet espace' }, ...items.map(item => button(item.label, () => choose(item.key), { primary: item.key === current, attributes: { 'aria-current': item.key === current ? 'page' : null } })));
  requestAnimationFrame(() => {
    if (!navigation.isConnected) return;
    const active = navigation.querySelector('[aria-current="page"]'); if (!active) return;
    const bounds = navigation.getBoundingClientRect(), item = active.getBoundingClientRect();
    const left = bounds.left + navigation.clientLeft, right = left + navigation.clientWidth;
    // Reveal only this horizontal navigation; the reader/page scroll positions
    // belong to the user's saved reading state and must not move with it.
    if (item.left < left) navigation.scrollLeft += item.left - left;
    else if (item.right > right) navigation.scrollLeft += item.right - right;
  });
  return navigation;
}
export function toLocalInput(value) {
  if (!value) return '';
  const d = new Date(value); if (!Number.isFinite(d.getTime())) return '';
  const n = v => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${n(d.getMonth() + 1)}-${n(d.getDate())}T${n(d.getHours())}:${n(d.getMinutes())}`;
}
export const toIso = value => value ? new Date(value).toISOString() : '';

/** A real local editor. Changes are submitted as an allowlisted patch, never as a replacement of read evidence. */
export function personalEditor(ctx, personal, templates, onSaved) {
  const notes = textArea(personal.notes, 8000, 8);
  const pinned = checkbox(personal.pinned, 'Épingler ce ticket'); const queued = checkbox(personal.queueOrder != null, 'Dans ma file personnelle');
  const offline = checkbox(personal.offline, 'Conserver les rubriques lues hors connexion');
  const reminder = input(toLocalInput(personal.reminderAt), { type: 'datetime-local' }); const snooze = input(toLocalInput(personal.snoozedUntil), { type: 'datetime-local' });
  const reason = input(personal.snoozeReason || '', { maxLength: 1200 });
  const checklist = (personal.checklist || []).map(item => ({ ...item })); const listRoot = el('div', { class: 'work-checklist' });
  const renderList = () => listRoot.replaceChildren(...checklist.map((item, index) => {
    const done = checkbox(item.done, ''); done.control.setAttribute('aria-label', `Terminé : ${item.text}`);
    const text = input(item.text, { maxLength: 500 }); text.setAttribute('aria-label', 'Action personnelle');
    done.control.addEventListener('change', () => item.done = done.control.checked); text.addEventListener('input', () => item.text = text.value);
    return toolbar(done.element, text, button('Retirer', () => { checklist.splice(index, 1); renderList(); }));
  })); renderList();
  const template = select(Object.keys(templates || {}).map(name => ({ value: name, label: name })), Object.keys(templates || {})[0] || '');
  const save = run(ctx, async () => {
    const result = await call(ctx, 'workplace.personal.save', { ticketKey: personal.key, notes: notes.value, pinned: pinned.control.checked, queued: queued.control.checked, offline: offline.control.checked, checklist, reminderAt: toIso(reminder.value), snoozedUntil: toIso(snooze.value), snoozeReason: reason.value });
    ctx.notify('Travail personnel enregistré.', 'success'); if (onSaved) await onSaved(result);
  });
  return el('div', { class: 'work-personal-editor' },
    note('Notes, rappels et listes restent sur ce poste. La copie hors connexion contient les seules rubriques effectivement lues.'),
    toolbar(pinned.element, queued.element, offline.element), field('Mes notes', notes),
    card('Liste de vérification', listRoot, toolbar(button('Ajouter une action', () => { if (checklist.length >= 40) { ctx.notify('La liste est limitée à 40 actions.', 'error'); return; } checklist.push({ text: '', done: false }); renderList(); }), template, button('Reprendre le modèle', () => { checklist.splice(0, checklist.length, ...(templates[template.value] || []).map(text => ({ text, done: false }))); renderList(); }))),
    el('div', { class: 'work-form-grid' }, field('Rappel personnel', reminder), field('Reporter jusqu’au', snooze), field('Motif du report', reason)),
    toolbar(button('Enregistrer mon travail', save, { primary: true }), button('Rappel traité', run(ctx, async () => { await call(ctx, 'workplace.personal.save', { ticketKey: personal.key, acknowledge: true }); ctx.notify('Rappel marqué comme traité.'); })), button('Retirer le dossier conservé…', run(ctx, async () => { if (!await confirm('Retirer ce dossier ?', 'Les notes, le brouillon, la liste et la copie hors connexion de ce dossier seront retirés. Les reçus d’actions restent conservés.', 'Retirer le dossier')) return; await call(ctx, 'workplace.personal.remove', { ticketKey: personal.key, confirmed: true }); ctx.navigate('tickets'); }), { danger: true })));
}
export function textDialog(title, content, actions = []) { return dialog(title, pre(content), actions); }
