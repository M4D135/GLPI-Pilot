import { el, replaceChildren, button, card, toolbar, input, select, field, table, dialog, confirm, badge, empty, isSimulation } from '../ui.js';
import { call, run, message, note, pre, date, dataTable, pager, ticketButton, checkbox, textArea, toLocalInput, toIso } from './workplace-common.js';
import { renderDraft, renderActions, renderInsights } from './workplace-actions.js';
import { openLibraryPrecedents } from '../library-ticket.js';
import { publishSharedOperation } from './shared-operations.js';
const personalDrafts = new Map();

export async function renderTickets(ctx) {
  const { renderTicketsWorkspace } = await import('./tickets-workspace.js');
  return renderTicketsWorkspace(ctx, { resumeActive: true });
}

// A working panel, not a second ticket page: source facts, personal notes and
// collected observations share one layout. Specialist tools open in context.
export async function renderTicketReader(ctx, { onTicketOpened, onSourceChanged, onTicketClosed } = {}) {
  const simulation = isSimulation(ctx.state.session);
  let state = await call(ctx, 'workplace.get'), current = null, source = null, opened = null;
  let personal = null, tool = null, disposed = false, version = 0;
  const reader = el('section', { class: 'work-ticket-reader tickets-working-panel', 'aria-label': 'Lecture du ticket' });
  ctx.root.replaceChildren(reader);
  const savePosition = async (ticket, patch = {}) => {
    ticket.personal.readingPosition = { ...ticket.personal.readingPosition, pageIndex: 0, ...patch };
    await ctx.api.call('workplace.ticket.position', { ticketKey: ticket.personal.key, position: ticket.personal.readingPosition });
  };
  async function closeTool() {
    if (!tool) return;
    const closing = tool; await closing.close(); if (tool === closing) tool = null;
  }
  async function loadSource(args) {
    if (args?.rowIndex == null) { source = null; return; }
    const data = await call(ctx, 'records.list', { revision: ctx.state.session.revision, indices: [args.rowIndex], limit: 1 });
    source = data.rows[0] ? { headers: data.headers, row: data.rows[0], revision: data.revision } : null;
  }
  async function open(args) {
    const generation = ++version;
    await closeTool(); await personal?.dispose(); personal = null;
    const ticket = await call(ctx, 'workplace.ticket.open', args), next = await call(ctx, 'workplace.get');
    if (disposed || ctx.signal?.aborted || generation !== version) return;
    current = ticket; state = next; opened = args; await loadSource(args);
    await draw();
    if (disposed || generation !== version) return;
    onTicketOpened?.(current, args);
    if (args.section === 'personal') focusPersonal();
    else if (args.section && args.section !== 'reader') await openTool(args.section);
  }
  function focusPersonal() {
    const control = reader.querySelector('.tickets-personal textarea');
    if (control) { control.scrollIntoView({ block: 'nearest' }); control.focus({ preventScroll: true }); }
  }
  function sourceFacts(ticket) {
    const body = el('div', { class: 'tickets-source-facts' });
    if (!source) { body.append(note('Dossier conservé. Les valeurs du relevé ne sont pas déduites de vos notes.')); return body; }
    const normalize = value => String(value).normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase('fr').replace(/[^a-z0-9]+/g, ' ').trim();
    const wanted = [['Statut', 'État'], ['Priorité'], ['Attribué à - Technicien', 'Technicien'], ['Attribué à - Groupe de techniciens', 'Groupe'], ['Catégorie'], ["Date d'ouverture", 'Date d’ouverture']];
    for (const names of wanted) {
      const index = source.headers.findIndex(header => names.some(name => normalize(header) === normalize(name)));
      if (index >= 0) body.append(el('div', {}, el('span', { text: source.headers[index] }), el('strong', { text: source.row.cells[index] || 'Non renseigné' })));
    }
    const frozen = source;
    body.append(button('Toutes les données du relevé', () => {
      const content = dataTable(frozen.headers.map((name, index) => ({ name, index, value: frozen.row.cells[index] || '—' })), [
        { key: 'name', label: 'Champ' }, { label: 'Valeur source', render: row => el('div', {}, pre(row.value), button('Lire la valeur complète', run(ctx, async () => {
          const cell = await call(ctx, 'records.cell', { revision: frozen.revision, row: frozen.row.index, column: row.index });
          dialog(cell.header, pre(cell.raw), [{ label: 'Copier', action: () => ctx.api.call('app.copy', { text: cell.raw }) }]);
        }))) }]);
      dialog(`Ticket ${ticket.personal.id} · données source`, content);
    }, { icon: 'table' }));
    return body;
  }
  async function journal(ticket) {
    const section = ticket.sections?.find(value => value.kind === 'timeline');
    const body = el('div', { class: 'tickets-journal' });
    const heading = el('div', { class: 'tickets-panel-section-title' }, el('h3', { text: 'Journal recueilli' }),
      button('Consulter les observations', () => openTool('reader'), { icon: 'search', disabled: !ticket.loaded, attributes: { 'aria-haspopup': 'dialog' } }));
    body.append(heading);
    if (!section) { body.append(note(ticket.loaded ? 'Aucun suivi ou tâche recueilli dans les rubriques lues.' : 'La fiche n’a pas encore été lue. Actualisez-la explicitement pour recueillir ses rubriques.')); return body; }
    const result = await call(ctx, 'workplace.ticket.section', { ticketKey: ticket.personal.key, sectionIndex: section.index, page: 0 });
    for (const row of result.rows.slice(0, 4)) body.append(el('article', { class: 'tickets-journal-event' },
      toolbar(badge(row.values[0] || 'Événement'), el('small', { text: row.values[1] || 'Date non renseignée' })),
      el('strong', { text: row.values[2] || 'Auteur non renseigné' }), el('p', { text: (row.values[3] || row.values.join(' · ')).slice(0, 420) }),
      (row.values[3] || '').length > 420 || row.truncatedColumns?.length ? button('Lire le texte complet', run(ctx, async () => {
        const cell = await call(ctx, 'workplace.ticket.cell', { ticketKey: ticket.personal.key, sectionIndex: section.index, rowIndex: row.index, columnIndex: Math.min(3, result.headers.length - 1), readAt: result.readAt });
        dialog(cell.section + ' · ' + cell.header, pre(cell.text));
      })) : null));
    if (result.total > 4) body.append(note(`${result.total} événements dans cette rubrique · les quatre premiers sont affichés.`));
    if (!result.complete) body.append(note('Couverture de cette rubrique partielle ou non confirmée.'));
    return body;
  }
  async function draw() {
    const ticket = current, generation = version;
    const refresh = run(ctx, async () => {
      personal?.remember(); const updated = await call(ctx, 'workplace.ticket.read', { ticketKey: ticket.personal.key });
      if (disposed || current?.personal.key !== ticket.personal.key) return;
      await personal?.dispose(); current = updated; await draw();
    });
    const facts = sourceFacts(ticket), journalHost = el('div');
    personal = ticketPersonalEditor(ctx, ticket.personal, state.checklistTemplates, value => { if (current?.personal.key === ticket.personal.key) current.personal = value; }, () => { current = null; onTicketClosed?.(); });
    replaceChildren(reader,
      el('header', { class: 'work-ticket-heading tickets-panel-heading' },
        toolbar(badge(`#${ticket.personal.id}`, 'info'), badge(simulation ? 'Simulation locale' : ticket.loaded ? 'Observation datée' : 'Lecture à demander')),
        el('h2', { text: `#${ticket.personal.id} · ${ticket.title || ticket.personal.title}` }),
        el('p', { class: 'help', text: ticket.loaded ? 'Lu le ' + date(ticket.readAt) : 'Fiche non lue' }),
        toolbar(button('Actualiser la fiche', refresh, { icon: 'refresh', disabled: !ticket.canRead }),
          button(simulation ? 'Afficher la fiche simulée' : 'Afficher dans GLPI', run(ctx, async () => { await call(ctx, 'workplace.ticket.browser', { ticketKey: ticket.personal.key }); if (simulation) await refresh(); }), { disabled: !ticket.canRead }))),
      !ticket.canRead ? note(ticket.readReason) : null, ...(ticket.warnings || []).map(note), facts,
      el('div', { class: 'tickets-actionbar' }, button('Notes et rappels', focusPersonal, { icon: 'log' }),
        button('Brouillon de suivi', () => openTool('draft'), { icon: 'log', attributes: { 'aria-haspopup': 'dialog' } }), button('Actions assistées', () => openTool('actions'), { icon: 'check', primary: true, attributes: { 'aria-haspopup': 'dialog' } }),
        button('Autres outils', moreTools, { icon: 'settings' })),
      button('Chercher des solutions similaires', run(ctx, event => openLibraryPrecedents({ api: ctx.api, signal: ctx.signal, state: ctx.state, navigate: ctx.navigate }, { id: ticket.personal.id, url: ticket.personal.url }, { returnFocus: event.currentTarget })), { icon: 'search' }),
      personal.element, journalHost);
    const content = await journal(ticket);
    if (!disposed && generation === version && current?.personal.key === ticket.personal.key) journalHost.replaceChildren(content);
  }
  function moreTools() {
    let modal; const item = (label, key) => button(label, () => { modal.close(); return openTool(key); });
    modal = dialog('Outils du ticket', el('div', { class: 'tickets-tools-menu' },
      item('Synthèse des observations', 'summary'), item('Chercher dans les rubriques', 'search'), item('Passation', 'handover'),
      item('Rapprochements', 'intelligence'), item('Planification et relations', 'insights')));
  }
  async function openTool(kind) {
    if (!current) return;
    if (kind === 'personal') { focusPersonal(); return; }
    personal?.remember(); await closeTool();
    const ticket = current, titles = { reader: 'Observations du ticket', summary: 'Synthèse du ticket', search: 'Rechercher dans le ticket', handover: 'Passation du ticket', intelligence: 'Rapprochements', insights: 'Planification et relations', draft: 'Brouillon de suivi', actions: 'Actions assistées' };
    if (!titles[kind]) return;
    const content = el('div', { class: 'work-reader-body tickets-tool-content' }), modal = dialog(titles[kind], content);
    modal.element.classList.add('tickets-tool-modal');
    const nativeClose = modal.close;
    const record = { modal, cleanup: null, ready: null, finishing: null, closing: null,
      finish() {
        return this.finishing ||= Promise.resolve(this.ready).catch(() => {}).then(async () => { await this.cleanup?.(); this.cleanup = null; })
          .catch(error => { this.finishing = null; throw error; });
      },
      close() {
        if (this.closing) return this.closing;
        content.inert = true;
        return this.closing = this.finish().then(() => { nativeClose(); if (tool === record) tool = null; })
          .catch(error => { content.inert = false; this.closing = null; throw error; });
      }
    };
    modal.close = () => record.close(); tool = record;
    const requestClose = event => { event.preventDefault(); event.stopImmediatePropagation(); void record.close().catch(error => message(ctx, error)); };
    modal.element.querySelector(':scope > .modal-heading button')?.addEventListener('click', requestClose, { capture: true });
    modal.element.addEventListener('cancel', requestClose, { capture: true });
    record.ready = (async () => {
      const persist = patch => savePosition(ticket, patch);
      if (kind === 'reader') record.cleanup = await renderSections(ctx, content, ticket, state, persist, async updated => {
        if (current?.personal.key === ticket.personal.key) { current = updated; await closeTool(); await personal?.dispose(); await draw(); }
      });
      else if (kind === 'draft') record.cleanup = await renderDraft(ctx, content, ticket);
      else if (kind === 'actions') record.cleanup = await renderActions(ctx, content, ticket, async updated => {
        if (disposed || current?.personal.key !== updated.personal.key) return;
        current = updated; await onSourceChanged?.(updated); await loadSource(opened);
        const facts = reader.querySelector('.tickets-source-facts'); if (facts) facts.replaceWith(sourceFacts(updated));
      });
      else if (kind === 'insights') await renderInsights(ctx, content, ticket);
      else if (kind === 'intelligence') await renderIntelligence(ctx, content, ticket, persist);
      else if (kind === 'summary') content.replaceChildren(dataTable((ticket.summary || []).map(row => ({ label: row[0], value: row[1] })), [{ key: 'label', label: 'Élément' }, { key: 'value', label: 'Observation', render: row => pre(row.value) }]));
      else if (kind === 'handover') { const result = await call(ctx, 'workplace.ticket.handover', { ticketKey: ticket.personal.key }); content.replaceChildren(pre(result.text), button('Copier la passation', run(ctx, async () => { await call(ctx, 'workplace.ticket.handover', { ticketKey: ticket.personal.key, copy: true }); ctx.notify('Passation copiée.'); }))); }
      else if (kind === 'search') {
        const query = input(ticket.personal.readingPosition?.wholeTicketSearch || '', { maxLength: 500, placeholder: 'Dans les rubriques effectivement lues' }), results = el('div');
        const find = run(ctx, async () => {
          await persist({ wholeTicketSearch: query.value }); const hits = await call(ctx, 'workplace.ticket.search', { ticketKey: ticket.personal.key, search: query.value }); let page = 0;
          const paint = () => results.replaceChildren(note(`${hits.length} résultats · documents joints non indexés.`), dataTable(hits.slice(page * 100, page * 100 + 100), [{ key: 'section', label: 'Rubrique' }, { key: 'text', label: 'Extrait', render: hit => pre(hit.text) }, { label: '', render: hit => button('Voir la rubrique', run(ctx, async () => { await persist({ sectionKey: ticket.sections.find(section => section.index === hit.sectionIndex)?.key || '', rowIndex: hit.rowIndex, search: '', type: '', author: '', from: null, to: null }); await closeTool(); await openTool('reader'); })) }]), pager(page, 100, hits.length, value => { page = value; paint(); })); paint();
        });
        content.replaceChildren(toolbar(query, button('Chercher', find, { primary: true })), results); query.addEventListener('keydown', event => { if (event.key === 'Enter') find(); }); if (query.value) await find();
      }
    })();
    try { await record.ready; } catch (error) { content.replaceChildren(empty('Cet outil n’a pas pu être chargé', error.message)); throw error; }
  }
  if (ctx.params?.rowIndex != null) await open({ ...ctx.params, rowIndex: Number(ctx.params.rowIndex), revision: Number(ctx.params.revision) });
  else if (ctx.params?.ticketKey) await open({ ...ctx.params });
  const cleanup = async () => { await closeTool(); await personal?.dispose(); personal = null; disposed = true; version++; };
  cleanup.openTicket = open; cleanup.ticketKey = () => current?.personal.key; return cleanup;
}

function ticketPersonalEditor(ctx, personal, templates, onSaved, onRemoved) {
  const simulation = isSimulation(ctx.state.session), draftKey = JSON.stringify([simulation ? ctx.state.settings.exportFolder : 'normal', personal.key]);
  const draft = personalDrafts.get(draftKey), notes = textArea(draft?.notes ?? personal.notes, 8000, 4);
  const pinned = checkbox(draft?.pinned ?? personal.pinned, 'Épingler ce ticket'), queued = checkbox(draft?.queued ?? personal.queueOrder != null, 'Dans ma file personnelle'), offline = checkbox(draft?.offline ?? personal.offline, 'Conserver les rubriques lues hors connexion');
  const reminder = input(toLocalInput(draft?.reminderAt ?? personal.reminderAt), { type: 'datetime-local' }), snooze = input(toLocalInput(draft?.snoozedUntil ?? personal.snoozedUntil), { type: 'datetime-local' }), reason = input(draft?.snoozeReason ?? personal.snoozeReason ?? '', { maxLength: 1200 });
  const checklist = (draft?.checklist || personal.checklist || []).map(item => ({ ...item })), list = el('div', { class: 'work-checklist' }), status = el('span', { class: 'tickets-save-status', text: draft ? 'Brouillon personnel · à enregistrer' : simulation ? 'Notes privées de simulation' : 'Notes privées sur ce poste' });
  let dirty = !!draft, disposed = false, saving = Promise.resolve();
  const patch = () => ({ ticketKey: personal.key, notes: notes.value, pinned: pinned.control.checked, queued: queued.control.checked, offline: offline.control.checked, checklist: checklist.filter(item => item.text.trim()), reminderAt: toIso(reminder.value), snoozedUntil: toIso(snooze.value), snoozeReason: reason.value });
  const remember = () => { if (dirty) { personalDrafts.set(draftKey, structuredClone(patch())); while (personalDrafts.size > 24) personalDrafts.delete(personalDrafts.keys().next().value); } else personalDrafts.delete(draftKey); };
  const save = (announce = false) => {
    saving = saving.catch(() => {}).then(async () => {
      if (!dirty && !announce) return;
      const value = patch(), signature = JSON.stringify(value); const result = await ctx.api.call('workplace.personal.save', value);
      if (signature === JSON.stringify(patch())) dirty = false;
      remember();
      onSaved?.(result); if (!disposed) status.textContent = dirty ? 'Modifications en cours…' : simulation ? 'Enregistré dans la simulation' : 'Enregistré sur ce poste';
      if (announce) ctx.notify(simulation ? 'Travail personnel enregistré dans la simulation.' : 'Travail personnel enregistré.', 'success');
    });
    return saving;
  };
  const changed = () => { if (disposed) return; dirty = true; status.textContent = 'Brouillon personnel · à enregistrer'; remember(); };
  const renderList = () => list.replaceChildren(...checklist.map((item, index) => {
    const done = checkbox(item.done, ''); done.control.setAttribute('aria-label', `Terminé : ${item.text}`); const text = input(item.text, { maxLength: 500, 'aria-label': 'Action personnelle' });
    done.control.addEventListener('change', () => { item.done = done.control.checked; changed(); }); text.addEventListener('input', () => { item.text = text.value; changed(); });
    return toolbar(done.element, text, button('Retirer', () => { checklist.splice(index, 1); renderList(); changed(); }));
  })); renderList();
  const template = select(Object.keys(templates || {}).map(name => ({ value: name, label: name })), Object.keys(templates || {})[0] || '');
  const advanced = el('details', { class: 'tickets-personal-options' }, el('summary', { text: 'Checklist, rappels et conservation' }), list,
    toolbar(button('Ajouter une action', () => { if (checklist.length >= 40) throw new Error('La liste est limitée à 40 actions.'); checklist.push({ text: '', done: false }); renderList(); }), template,
      button('Reprendre le modèle', () => { checklist.splice(0, checklist.length, ...(templates[template.value] || []).map(text => ({ text, done: false }))); renderList(); changed(); })),
    field('Rappel personnel', reminder), field('Reporter jusqu’au', snooze), field('Motif du report', reason), offline.element,
    button('Rappel traité', run(ctx, async () => { await call(ctx, 'workplace.personal.save', { ticketKey: personal.key, acknowledge: true }); ctx.notify('Rappel marqué comme traité.'); })),
    button('Retirer le dossier conservé…', run(ctx, async () => { if (!await confirm('Retirer ce dossier ?', 'Les notes, le brouillon, la liste et la copie hors connexion de ce dossier seront retirés. Les reçus d’actions restent conservés.', 'Retirer le dossier')) return; await saving; await call(ctx, 'workplace.personal.remove', { ticketKey: personal.key, confirmed: true }); dirty = false; personalDrafts.delete(draftKey); onRemoved?.(); await ctx.navigate('records'); }), { danger: true }));
  const element = el('section', { class: 'tickets-personal' }, el('div', { class: 'tickets-panel-section-title' }, el('h3', { text: 'Mon suivi' }), status),
    toolbar(pinned.element, queued.element), field('Mes notes', notes), advanced,
    toolbar(button('Enregistrer mon travail', () => save(true), { primary: true, icon: 'check' }), button('Enregistrer et partager la note', run(ctx, async () => { await save(); await publishSharedOperation(ctx, 'workspace.publishPersonalNote', { ticketKey: personal.key }); }), { icon: 'users' }), button('Voir le travail partagé', () => ctx.navigate('library', { view: 'team' }))));
  for (const control of [notes, pinned.control, queued.control, offline.control, reminder, snooze, reason]) { control.addEventListener('input', changed); control.addEventListener('change', changed); }
  return { element, save, remember, async dispose() { await saving; remember(); disposed = true; } };
}

async function renderSections(ctx, root, ticket, state, savePosition, replaceTicket) {
  const key = ticket.personal.key; const position = ticket.personal.readingPosition || {}; const ordered = [...(ticket.sections || [])].sort((a, b) => {
    const rank = s => { const i = state.sectionOrder.indexOf(s.key); return i < 0 ? 100000 + s.index : i; }; return rank(a) - rank(b);
  });
  let selected = ordered.find(s => s.key === position.sectionKey) || ordered.find(s => s.key === state.defaultSection || s.kind === state.defaultSection || ticket.tabs?.some(t => t.category === state.defaultSection && t.key === s.tabKey)) || ordered[0];
  const sectionList = select(ordered.map(s => ({ value: s.key, label: (state.favoriteSections.includes(s.key) ? '★ ' : '') + s.name + ` (${s.rows})` })), selected?.key || '');
  const search = input(position.search || '', { placeholder: 'Filtrer cette rubrique', maxLength: 500 }); const author = input(position.author || '', { maxLength: 200 }); const type = input(position.type || '', { maxLength: 100 });
  const from = input(position.from?.slice(0, 10) || '', { type: 'date' }); const to = input(position.to?.slice(0, 10) || '', { type: 'date' });
  const mode = select([{ value: '0', label: 'Lecture chronologique' }, { value: '1', label: 'Tableau' }], String(position.viewMode || 0));
  const grouped = el('input', { type: 'checkbox' }); grouped.checked = state.groupTimelineByDay;
  const list = el('div', { class: 'work-section-content', tabIndex: 0 }); let page = Math.floor(Math.max(0, position.rowIndex || 0) / 50); let selectedRow = position.rowIndex ?? -1; let restoreFocus = selectedRow >= 0; let disposed = false;
  const persist = () => savePosition({ sectionKey: selected?.key || '', search: search.value, type: type.value, author: author.value, from: from.value ? from.value + 'T00:00:00' : null, to: to.value ? to.value + 'T00:00:00' : null, viewMode: Number(mode.value), rowIndex: selectedRow, scrollOffset: list.scrollTop });
  async function load() {
    if (!selected) { list.replaceChildren(empty('Aucune rubrique collectée', 'Les onglets non lus peuvent être chargés séparément ci-dessus.')); return; }
    const result = await call(ctx, 'workplace.ticket.section', { ticketKey: key, sectionIndex: selected.index, page, focusRowIndex: restoreFocus ? selectedRow : undefined, search: search.value, type: type.value, author: author.value, from: from.value, to: to.value }); if (disposed) return;
    restoreFocus = false; page = result.page;
    const fullText = (row, column) => button('Lire le texte complet', run(ctx, async () => {
      const cell = await call(ctx, 'workplace.ticket.cell', { ticketKey: key, sectionIndex: result.sectionIndex, rowIndex: row.index, columnIndex: column, readAt: result.readAt });
      dialog(cell.section + ' · ' + cell.header, pre(cell.text), [{ label: 'Fermer', action: close => close() }]);
    }));
    const marker = () => el('div', { class: 'work-reading-marker', text: 'DERNIÈRE LECTURE · ' + date(result.previousReadAt) + ' · les lignes surlignées ont changé depuis ce repère.' });
    const renderTimeline = () => {
      const items = result.rows.map(row => {
        const values = row.values; const item = el('article', { class: 'work-timeline-event' + (row.changed ? ' work-changed' : '') },
          toolbar(badge(values[0] || 'Événement'), el('time', { text: values[1] || 'Date non recueillie' }), el('strong', { text: values[2] || 'Auteur non recueilli' }), row.changed ? badge('Nouveau ou modifié', 'warning') : null), pre(values[3] || values.join('\n')), ...(row.truncatedColumns || []).map(column => fullText(row, column)));
        item.addEventListener('click', () => { selectedRow = row.index; run(ctx, persist)(); }); return { date: values[1] || 'Date inconnue', occurredAt: row.occurredAt ? Date.parse(row.occurredAt) : null, item };
      });
      if (result.previousReadAt) {
        const baseline = Date.parse(result.previousReadAt); const dated = items.filter(item => item.occurredAt != null);
        const descending = dated.every((item, index) => index === 0 || item.occurredAt <= dated[index - 1].occurredAt);
        const ascending = dated.every((item, index) => index === 0 || item.occurredAt >= dated[index - 1].occurredAt);
        // An unknown date stays unknown; place the marker only against an ordered, parsed sequence.
        let boundary = descending ? items.findIndex(item => item.occurredAt != null && item.occurredAt <= baseline)
          : ascending ? items.findIndex(item => item.occurredAt != null && item.occurredAt > baseline) : 0;
        if (boundary < 0) boundary = dated.length ? items.lastIndexOf(dated.at(-1)) + 1 : 0;
        items.splice(boundary, 0, { date: items[boundary]?.date || items[boundary - 1]?.date || '', item: marker() });
      }
      if (!grouped.checked) return el('div', { class: 'work-timeline' }, ...items.map(i => i.item));
      const groups = new Map(); for (const item of items) { const match = item.date.match(/\d{1,2}\/\d{1,2}\/\d{4}|\d{4}-\d{2}-\d{2}/); const day = match?.[0] || 'Sans date lisible'; if (!groups.has(day)) groups.set(day, []); groups.get(day).push(item.item); }
      return el('div', { class: 'work-timeline' }, ...[...groups.entries()].map(([day, entries]) => el('details', { open: true }, el('summary', { text: day + ` · ${entries.length} événement(s)` }), ...entries)));
    };
    const grid = () => table(result.rows, result.headers.map((header, index) => ({ key: String(index), label: header, render: row => el('span', { class: row.changed ? 'work-cell-changed' : '' }, el('span', { text: row.values[index] || '' }), row.truncatedColumns?.includes(index) ? fullText(row, index) : null) })), { key: 'index', selected: selectedRow, onSelect: row => { selectedRow = row.index; run(ctx, persist)(); }, maxRows: 100 });
    replaceChildren(list, result.previousReadAt ? result.kind === 'timeline' && mode.value === '0' ? null : marker() : note('Première observation mémorisée.'),
      result.unreadableDates ? note(`${result.unreadableDates} événement(s) exclus du filtre de dates : date illisible.`) : null,
      result.kind === 'timeline' && mode.value === '0' ? renderTimeline() : grid(),
      pager(result.page, result.pageSize, result.total, run(ctx, async value => { page = value; selectedRow = page * 50; await persist(); await load(); })),
      ...(result.warnings || []).map(w => note(w)), !result.complete ? note('Rubrique partielle ou exhaustivité non confirmée.') : null);
  }
  const apply = run(ctx, async () => { page = 0; selectedRow = -1; await persist(); await load(); });
  sectionList.addEventListener('change', run(ctx, async () => { selected = ordered.find(s => s.key === sectionList.value); page = 0; selectedRow = -1; await persist(); await load(); }));
  mode.addEventListener('change', apply);
  grouped.addEventListener('change', run(ctx, async () => { state.groupTimelineByDay = grouped.checked; await call(ctx, 'workplace.preferences.save', { groupTimelineByDay: grouped.checked }); await load(); }));
  async function move(direction) { if (!selected) return; const index = ordered.indexOf(selected); const target = Math.max(0, Math.min(ordered.length - 1, index + direction)); [ordered[index], ordered[target]] = [ordered[target], ordered[index]]; state.sectionOrder = ordered.map(s => s.key); await call(ctx, 'workplace.preferences.save', { sectionOrder: state.sectionOrder }); sectionList.replaceChildren(...ordered.map(s => el('option', { value: s.key, text: s.name }))); sectionList.value = selected.key; }
  replaceChildren(root, ticket.tabs?.some(t => !t.loaded) ? card('Onglets GLPI à lire', note('Chaque bouton charge seulement l’onglet de consultation découvert. Une lecture impossible conserve ses observations précédentes.'), toolbar(...ticket.tabs.filter(t => !t.loaded).map(tab => button(tab.name, run(ctx, async () => { const result = await call(ctx, 'workplace.ticket.tab', { ticketKey: key, tabKey: tab.key }); if (!result.loaded) ctx.notify(result.warnings.join('\n') || 'Cet onglet n’a pas pu être lu.', 'warning'); await replaceTicket(result.ticket); }))))) : null,
    toolbar(sectionList, button('★ Favori', run(ctx, async () => { if (!selected) return; state.favoriteSections = state.favoriteSections.includes(selected.key) ? state.favoriteSections.filter(k => k !== selected.key) : [...state.favoriteSections, selected.key]; await call(ctx, 'workplace.preferences.save', { favoriteSections: state.favoriteSections }); ctx.notify(state.favoriteSections.includes(selected.key) ? 'Rubrique ajoutée aux favoris.' : 'Rubrique retirée des favoris.'); })), button('Ouvrir ici par défaut', run(ctx, async () => { if (selected) { state.defaultSection = selected.key; await call(ctx, 'workplace.preferences.save', { defaultSection: selected.key }); ctx.notify('Rubrique d’ouverture mémorisée.'); } })), button('↑', run(ctx, () => move(-1)), { title: 'Monter la rubrique' }), button('↓', run(ctx, () => move(1)), { title: 'Descendre la rubrique' })),
    toolbar(search, mode, el('label', { class: 'work-check' }, grouped, el('span', { text: 'Regrouper par jour' }))),
    el('details', {}, el('summary', { text: 'Filtres de chronologie' }), el('div', { class: 'work-form-grid' }, field('Type d’événement', type), field('Auteur', author), field('Du', from), field('Au', to))),
    toolbar(button('Appliquer les filtres', apply, { primary: true }), button('Effacer les filtres', run(ctx, async () => { search.value = author.value = type.value = from.value = to.value = ''; await apply(); }))), list);
  await load(); list.scrollTop = position.scrollOffset || 0;
  return async () => { try { await persist(); disposed = true; } catch (error) { ctx.notify('Position de lecture non enregistrée : ' + error.message, 'error'); throw error; } };
}

async function renderIntelligence(ctx, root, ticket, savePosition) {
  const key = ticket.personal.key; const result = await call(ctx, 'workplace.ticket.intelligence', { ticketKey: key });
  const search = input(ticket.personal.readingPosition?.intelligenceSearch || '', { maxLength: 300, placeholder: 'Rechercher une solution recueillie' }); const related = el('div'); const recurrence = el('div');
  const draw = () => { const rows = result.related.filter(row => !search.value || (row.title + ' ' + row.solution).toLocaleLowerCase('fr').includes(search.value.toLocaleLowerCase('fr'))); related.replaceChildren(dataTable(rows, [{ key: 'id', label: 'Ticket', render: row => ticketButton(row.id, () => ctx.navigate('tickets', { rowIndex: row.sourceIndex, revision: result.revision })) }, { key: 'title', label: 'Titre' }, { key: 'relationship', label: 'Rapprochement' }, { key: 'evidence', label: 'Critères' }, { key: 'solution', label: 'Solution effectivement recueillie', render: row => row.hasSolution ? el('details', {}, el('summary', { text: row.solutionSource + ' · ' + date(row.observedAt) }), pre(row.solution)) : note(row.solutionSource) }])); };
  search.addEventListener('input', () => { draw(); }); search.addEventListener('change', run(ctx, () => savePosition({ intelligenceSearch: search.value })));
  const equipment = input('', { placeholder: 'Nom exact d’une colonne équipement, ou détection automatique', maxLength: 500 });
  root.replaceChildren(note(result.message), toolbar(search), related, card('Récurrences dans les relevés compatibles', note('Un identifiant observé dans plusieurs relevés reste un seul dossier. L’analyse est limitée à 20 relevés et 500 000 lignes.'), field('Colonne équipement', equipment), button('Analyser les récurrences', run(ctx, async () => {
    const data = await call(ctx, 'workplace.ticket.recurrence', { ticketKey: key, equipmentColumn: equipment.value });
    let page = 0; const list = el('div'); const drawRecurrences = () => list.replaceChildren(dataTable(data.result.incidents.slice(page * 50, page * 50 + 50), [{ key: 'id', label: 'Ticket' }, { key: 'title', label: 'Titre' }, { key: 'equipment', label: 'Équipement' }, { key: 'category', label: 'Catégorie' }, { key: 'observations', label: 'Observations' }, { key: 'firstSeen', label: 'Première observation', render: r => date(r.firstSeen) }, { key: 'lastSeen', label: 'Dernière observation', render: r => date(r.lastSeen) }, { key: 'evidence', label: 'Critères' }]), pager(page, 50, data.result.incidents.length, p => { page = p; drawRecurrences(); })); drawRecurrences();
    recurrence.replaceChildren(note(data.result.summary), ...data.warnings.map(w => note(w)), list);
  }), { primary: true }), recurrence)); draw();
}
