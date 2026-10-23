import { el, replaceChildren, button, card, toolbar, input, select, field, table, dialog, confirm, badge, empty } from '../ui.js';
import { call, run, message, revision, note, pre, date, label, heading, dataTable, pager, tabs, personalEditor, ticketButton } from './workplace-common.js';
import { renderDraft, renderActions, renderInsights } from './workplace-actions.js';
import { openLibraryPrecedents } from '../library-ticket.js';

const readerPages = [
  { key: 'reader', label: 'Fiche GLPI' }, { key: 'summary', label: 'Synthèse' }, { key: 'personal', label: 'Mon travail' },
  { key: 'search', label: 'Chercher partout' }, { key: 'handover', label: 'Passation' }, { key: 'intelligence', label: 'Rapprochements' },
  { key: 'insights', label: 'Planification et relations' }, { key: 'draft', label: 'Brouillon de suivi' }, { key: 'actions', label: 'Actions assistées' }
];

export async function renderTickets(ctx) {
  let state = await call(ctx, 'workplace.get'); let current = null; let sectionCleanup = null; let readerPage = ctx.params?.section || 'reader'; let sourcePage = 0;
  let searchText = ''; let sourceSaved = !ctx.state.session?.loaded; let disposed = false; let sectionVersion = 0;
  const top = el('div'); const openTabs = el('div', { class: 'work-open-tabs', 'aria-label': 'Tickets ouverts' });
  const browser = el('div', { class: 'work-ticket-browser' }); const reader = el('section', { class: 'work-ticket-reader', 'aria-label': 'Lecture du ticket' });
  const sourceSearch = input('', { placeholder: 'ID, titre ou texte du relevé', maxLength: 300 }); const savedChoice = select([{ value: 'source', label: 'Relevé courant' }, { value: 'saved', label: 'Dossiers conservés' }], sourceSaved ? 'saved' : 'source');
  ctx.root.replaceChildren(heading('Tickets', 'Une fiche dédiée par ticket, des observations datées et votre travail conservé sur ce poste.'), top, openTabs, el('div', { class: 'work-ticket-layout' }, browser, reader));
  top.replaceChildren(toolbar(sourceSearch, savedChoice, button('Rechercher', run(ctx, async () => { searchText = sourceSearch.value; sourceSaved = savedChoice.value === 'saved'; sourcePage = 0; await loadSource(); })), button('Ma journée', () => ctx.navigate('day')), button('Lectures ciblées', () => ctx.navigate('enrichment'))));
  sourceSearch.addEventListener('keydown', e => { if (e.key === 'Enter') run(ctx, async () => { searchText = sourceSearch.value; sourcePage = 0; await loadSource(); })(); });
  savedChoice.addEventListener('change', run(ctx, async () => { sourceSaved = savedChoice.value === 'saved'; sourcePage = 0; await loadSource(); }));
  function drawTabs() {
    openTabs.replaceChildren(...state.openTickets.map(p => el('div', { class: 'work-open-ticket' },
      button((p.pinned ? '★ ' : '') + p.id + ' · ' + p.title, run(ctx, () => open({ ticketKey: p.key })), { primary: current?.personal.key === p.key, title: p.title }),
      button('×', run(ctx, async () => { if (current?.personal.key === p.key) { await sectionCleanup?.(); sectionCleanup = null; } state = await call(ctx, 'workplace.ticket.close', { ticketKey: p.key }); if (current?.personal.key === p.key) { current = null; reader.replaceChildren(empty('Choisissez un ticket', 'Ses notes restent disponibles dans les dossiers conservés.')); if (state.activeTicketKey) await open({ ticketKey: state.activeTicketKey }, false); } drawTabs(); }), { title: 'Fermer l’onglet ' + p.id }))));
  }
  async function loadSource() {
    const result = await call(ctx, 'workplace.ticket.list', { revision: revision(ctx), saved: sourceSaved, search: searchText, page: sourcePage });
    if (disposed) return;
    browser.replaceChildren(dataTable(result.rows, [{ key: 'id', label: 'Ticket', width: '1%', render: row => ticketButton(row.id, run(ctx, () => open(row.ticketKey ? { ticketKey: row.ticketKey } : { rowIndex: row.rowIndex, revision: result.revision }))) }, { key: 'title', label: 'Titre' }, { key: 'status', label: 'État du relevé' }], { key: sourceSaved ? 'ticketKey' : 'rowIndex' }), pager(result.page, result.pageSize, result.total, run(ctx, async page => { sourcePage = page; await loadSource(); })));
  }
  async function open(args, allowAutoRead = true) {
    await sectionCleanup?.(); sectionCleanup = null;
    current = await call(ctx, 'workplace.ticket.open', args); state = await call(ctx, 'workplace.get');
    if (!ctx.params?.section) readerPage = readerPages[current.personal.readingPosition?.pageIndex || 0]?.key || 'reader';
    if (allowAutoRead && state.autoRead && !current.loaded && current.canRead) {
      reader.replaceChildren(note('Lecture de la fiche dans la session GLPI…'));
      try { current = await call(ctx, 'workplace.ticket.read', { ticketKey: current.personal.key }); }
      catch (error) { message(ctx, error); }
    }
    drawTabs(); await showReader();
  }
  async function savePosition(patch = {}) {
    if (!current) return;
    current.personal.readingPosition = { ...current.personal.readingPosition, pageIndex: Math.max(0, readerPages.findIndex(p => p.key === readerPage)), ...patch };
    await ctx.api.call('workplace.ticket.position', { ticketKey: current.personal.key, position: current.personal.readingPosition });
  }
  async function showReader() {
    await sectionCleanup?.(); sectionCleanup = null; const generation = ++sectionVersion;
    if (!current || disposed) return;
    const ticket = current; const body = el('div', { class: 'work-reader-body' }); const key = ticket.personal.key;
    replaceChildren(reader, el('div', { class: 'work-ticket-heading' }, el('h2', { text: `#${ticket.personal.id} · ${ticket.title || ticket.personal.title}` }),
      toolbar(badge(ticket.loaded ? 'Lu le ' + date(ticket.readAt) : 'Fiche non lue'), ticket.personal.offline ? badge('Copie conservée') : null,
        button('Actualiser la fiche', run(ctx, async () => { current = await call(ctx, 'workplace.ticket.read', { ticketKey: key }); await showReader(); }), { disabled: !ticket.canRead }),
        button('Afficher dans GLPI', run(ctx, async () => { await call(ctx, 'workplace.ticket.browser', { ticketKey: key }); }), { disabled: !ticket.canRead }),
        button('Chercher des solutions similaires', run(ctx, event => openLibraryPrecedents({api: ctx.api, signal: ctx.signal}, {id: ticket.personal.id, url: ticket.personal.url}, {returnFocus: event.currentTarget})), { icon: 'search' }))),
      !ticket.canRead ? note(ticket.readReason) : null, ...(ticket.warnings || []).map(w => note(w)),
      ticket.changes ? note(ticket.changes.summary + (ticket.changes.rowComparisonComplete ? '' : ' Comparaison de lignes incomplète.')) : null,
      tabs(readerPages, readerPage, run(ctx, async page => { readerPage = page; await savePosition(); await showReader(); })), body);
    if (!ticket.loaded && !['personal', 'draft', 'actions', 'handover'].includes(readerPage)) { body.replaceChildren(empty('Lire la fiche', 'Lisez ce ticket pour afficher les rubriques. Aucune donnée n’est déduite d’une fiche non collectée.')); return; }
    try {
      let cleanup;
      if (readerPage === 'personal') body.replaceChildren(personalEditor(ctx, ticket.personal, state.checklistTemplates, async p => { current.personal = p; state = await call(ctx, 'workplace.get'); drawTabs(); }));
      else if (readerPage === 'summary') body.replaceChildren(dataTable((ticket.summary || []).map(row => ({ label: row[0], value: row[1] })), [{ key: 'label', label: 'Élément' }, { key: 'value', label: 'Observation', render: row => pre(row.value) }]));
      else if (readerPage === 'reader') cleanup = await renderSections(ctx, body, ticket, state, savePosition, async updated => { current = updated; await showReader(); });
      else if (readerPage === 'search') {
        const search = input(ticket.personal.readingPosition?.wholeTicketSearch || '', { maxLength: 500, placeholder: 'Rechercher dans toutes les rubriques lues' }); const results = el('div');
        const find = run(ctx, async () => { await savePosition({ wholeTicketSearch: search.value }); const hits = await call(ctx, 'workplace.ticket.search', { ticketKey: key, search: search.value });
          results.replaceChildren(note(`${hits.length} résultat(s), limite 2 000. Les documents joints ne sont pas indexés.`), dataTable(hits.slice(0, 100), [{ key: 'section', label: 'Rubrique' }, { key: 'text', label: 'Texte recueilli', render: hit => el('details', {}, el('summary', { text: hit.text.slice(0, 200) }), pre(hit.text)) }, { key: 'open', label: 'Lire', render: hit => button('Voir la rubrique', run(ctx, async () => { readerPage = 'reader'; await savePosition({ sectionKey: ticket.sections.find(s => s.index === hit.sectionIndex)?.key || '', rowIndex: hit.rowIndex, search: '', type: '', author: '', from: null, to: null }); await showReader(); })) }]));
          // Results remain bounded in the DOM; subsequent pages are available without repeating the engine search.
          if (hits.length > 100) { let page = 0; const pageHost = el('div'); const draw = () => { pageHost.replaceChildren(dataTable(hits.slice(page * 100, page * 100 + 100), [{ key: 'section', label: 'Rubrique' }, { key: 'text', label: 'Extrait', render: hit => pre(hit.text) }, { key: 'open', label: 'Lire', render: hit => button('Ouvrir', run(ctx, async () => { readerPage = 'reader'; await savePosition({ sectionKey: ticket.sections.find(s => s.index === hit.sectionIndex)?.key || '', rowIndex: hit.rowIndex, search: '', type: '', author: '', from: null, to: null }); await showReader(); })) }]), pager(page, 100, hits.length, value => { page = value; draw(); })); }; draw(); results.replaceChildren(note(`${hits.length} résultats, limite 2 000.`), pageHost); }
        }); body.replaceChildren(toolbar(search, button('Chercher', find, { primary: true })), results); search.addEventListener('keydown', e => { if (e.key === 'Enter') find(); }); if (search.value) await find();
      }
      else if (readerPage === 'handover') { const result = await call(ctx, 'workplace.ticket.handover', { ticketKey: key }); body.replaceChildren(toolbar(button('Copier la passation', run(ctx, async () => { await call(ctx, 'workplace.ticket.handover', { ticketKey: key, copy: true }); ctx.notify('Passation copiée.'); }))), pre(result.text)); }
      else if (readerPage === 'intelligence') await renderIntelligence(ctx, body, ticket, savePosition);
      else if (readerPage === 'insights') await renderInsights(ctx, body, ticket);
      else if (readerPage === 'draft') cleanup = await renderDraft(ctx, body, ticket);
      else if (readerPage === 'actions') cleanup = await renderActions(ctx, body, ticket);
      if (generation !== sectionVersion || disposed) await cleanup?.(); else sectionCleanup = cleanup;
    } catch (error) { message(ctx, error); if (!ctx.signal?.aborted) body.replaceChildren(empty('Cet espace n’est pas disponible', error.message)); }
  }
  await loadSource(); drawTabs();
  if (ctx.params?.rowIndex != null) await open({ rowIndex: Number(ctx.params.rowIndex), revision: Number(ctx.params.revision) });
  else if (ctx.params?.ticketKey) await open({ ticketKey: ctx.params.ticketKey });
  else if (state.activeTicketKey) await open({ ticketKey: state.activeTicketKey }, false);
  else reader.replaceChildren(empty('Choisissez un ticket', 'Le lecteur conserve jusqu’à douze fiches ouvertes, leurs notes et leurs positions de lecture.'));
  return async () => { await sectionCleanup?.(); disposed = true; sectionVersion++; };
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
