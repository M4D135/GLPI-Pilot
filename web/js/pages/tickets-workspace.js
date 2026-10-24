import { el, replaceChildren, button, toolbar, badge, empty, dialog, input, isSimulation, formatNumber, extractionStatus } from '../ui.js';
import { importLocalTestFile } from '../local-file-test.js';
import { renderRecordList } from './records.js';
import { renderTicketReader } from './workplace-tickets.js';

// One visible workspace owns the list and its adjacent reader. The historical
// route IDs are only entry points into this same list/selection state.
export async function renderTicketsWorkspace(ctx, { resumeActive = false } = {}) {
  ctx.root.dataset.workspace = 'records';
  const shell = el('div', { class: 'tickets-workspace', dataset: { view: 'list' } });
  const list = el('section', { class: 'tickets-list-pane', 'aria-label': 'Liste des tickets' });
  const detail = el('section', { class: 'tickets-detail-pane', 'aria-label': 'Fiche et suivi du ticket', hidden: true });
  const path = el('nav', { class: 'tickets-path', 'aria-label': 'Parcours Tickets' });
  const readerRoot = el('div', { class: 'tickets-reader-host' });
  const resumeHost = el('div', { class: 'tickets-resume' });
  const source = ctx.state.session || {};
  const receipt = extractionStatus(source);
  const heading = el('header', { class: 'tickets-workspace-heading' },
    el('div', { class: 'tickets-source-summary' },
      el('p', { text: source.loaded ? `${formatNumber(source.rows)} lignes · ${isSimulation(source) ? 'simulation locale' : source.historical ? 'relevé archivé' : 'relevé courant'}` : 'Votre liste, les informations du ticket et votre travail au même endroit.' }),
      source.loaded ? el('div', { class: 'tickets-receipt' }, badge(receipt.receipt, receipt.tone),
        receipt.attention ? el('span', { class: 'tickets-receipt-attention', text: receipt.attention }) : null) : null),
    toolbar(button('Mes dossiers', () => savedDossiers(), { icon: 'folder' }), button('Importer un fichier', () => importLocalTestFile(ctx), { icon: 'folder' }),
      button(isSimulation(source) ? 'Simuler une collecte' : 'Collecter', () => ctx.navigate('collect'), { icon: 'refresh' })));
  detail.append(path, readerRoot);
  shell.append(heading, resumeHost, el('div', { class: 'tickets-split' }, list, detail));
  ctx.root.replaceChildren(shell);

  const listParams = {};
  for (const key of ['indices', 'rows', 'revision']) if (ctx.params?.[key] != null) listParams[key] = ctx.params[key];
  let listCleanup = null, readerCleanup = null, listRevision = null;
  let listPosition = { top: 0, left: 0 }, lastFocus = null, currentTicket = null, currentArgs = null;
  let sequence = null, activeRow = null, disposed = false, queue = Promise.resolve(), showingDetail = false;
  const wide = matchMedia('(min-width: 1240px)');
  const scrollHost = ctx.root.closest('#content') || ctx.root.closest('.page-content') || ctx.root;
  const perform = action => { const next = queue.then(() => !disposed && !ctx.signal?.aborted ? action() : undefined); queue = next.catch(() => {}); return next; };
  const sameRow = args => args.rowIndex != null && args.rowIndex === currentArgs?.rowIndex && args.revision === currentArgs?.revision;
  const sameTicket = args => sameRow(args) || args.ticketKey && args.ticketKey === readerCleanup?.ticketKey();
  function updateLayout() {
    shell.classList.toggle('has-detail', showingDetail); shell.dataset.view = showingDetail ? 'detail' : 'list';
    list.hidden = showingDetail && !wide.matches; detail.hidden = !showingDetail;
    if (list.hidden) listCleanup?.suspend?.(); else listCleanup?.resume?.();
  }
  const onWidth = () => { if (!disposed) updateLayout(); };
  wide.addEventListener('change', onWidth);
  const rememberList = () => {
    if (list.hidden) return;
    listPosition = { top: scrollHost.scrollTop, left: scrollHost.scrollLeft };
    const active = document.activeElement;
    if (active && list.contains(active)) lastFocus = active.dataset.focusKey || lastFocus;
    listCleanup?.remember?.();
  };
  const focusReader = () => {
    const target = readerRoot.querySelector('.work-ticket-heading h2');
    if (target) { target.tabIndex = -1; target.focus({ preventScroll: true }); }
  };
  const returnToList = () => perform(showList);
  function drawPath() {
    const position = sequence?.revision === ctx.state.session?.revision ? sequence.rows.indexOf(activeRow) : -1;
    const move = delta => perform(() => showTicket({ rowIndex: sequence.rows[position + delta], revision: sequence.revision }));
    replaceChildren(path, button('Retour à la liste', returnToList, { icon: 'arrow', attributes: { class: 'button tickets-back' } }),
      position >= 0 ? toolbar(badge(`${position + 1} / ${sequence.rows.length}`),
        button('Ticket précédent', () => move(-1), { disabled: position === 0, icon: 'arrow', attributes: { class: 'button tickets-back' } }),
        button('Ticket suivant', () => move(1), { disabled: position === sequence.rows.length - 1, icon: 'arrow' })) : null);
  }
  function drawResume() {
    resumeHost.replaceChildren();
    if (!currentTicket || showingDetail) return;
    resumeHost.append(el('div', { class: 'tickets-resume-card' },
      el('div', {}, badge('Fiche en cours'), el('strong', { text: `#${currentTicket.personal.id} · ${currentTicket.title || currentTicket.personal.title}` })),
      button('Reprendre la fiche', () => perform(() => showTicket({ ticketKey: currentTicket.personal.key })), { icon: 'ticket' })));
  }
  const readerNavigate = (id, args = {}) => {
    if (id === 'tickets' || id === 'records' && (args.rowIndex != null || args.ticketKey)) return args.rowIndex != null || args.ticketKey ? perform(() => showTicket(args)) : returnToList();
    if (id === 'records' && !args.indices && !args.rows && args.revision == null) return returnToList();
    return ctx.navigate(id, args);
  };
  async function ensureList() {
    if (listCleanup && listRevision !== ctx.state.session?.revision) {
      await listCleanup(); listCleanup = null; list.replaceChildren();
      delete listParams.indices; delete listParams.rows; delete listParams.revision;
      sequence = null; listPosition = { top: 0, left: 0 }; lastFocus = null;
      ctx.notify('Le relevé a évolué. La liste a été rechargée ; ses anciennes sélections de lignes ont été retirées.', 'info');
    }
    if (!listCleanup) {
      list.replaceChildren(); listRevision = ctx.state.session?.revision;
      listCleanup = await renderRecordList({ ...ctx, root: list, params: listParams }, {
        onOpenTicket: (args, rows, focusKey) => { if (focusKey) lastFocus = focusKey; return perform(() => showTicket(args, rows)); },
        onRows: rows => { sequence = rows; }
      });
      listCleanup?.highlight?.(activeRow);
    }
  }
  async function mountReader(args) {
    readerCleanup = await renderTicketReader({ ...ctx, root: readerRoot, params: args, navigate: readerNavigate }, {
      onTicketOpened: (ticket, opened) => { currentTicket = ticket; currentArgs = opened; activeRow = opened.rowIndex ?? null; listCleanup?.highlight?.(activeRow); drawPath(); drawResume(); },
      onSourceChanged: async () => { await ensureList(); updateLayout(); drawPath(); },
      onTicketClosed: () => { currentTicket = currentArgs = null; activeRow = null; drawPath(); drawResume(); }
    });
  }
  async function showTicket(args, rows) {
    if (rows) sequence = rows;
    const reused = readerCleanup && sameTicket(args) && !args.section;
    rememberList(); showingDetail = true; updateLayout(); resumeHost.replaceChildren();
    try {
      if (!readerCleanup) await mountReader(args);
      else if (!reused) await readerCleanup.openTicket(args);
      if (disposed || ctx.signal?.aborted) return;
      drawPath(); detail.scrollTop = 0; if (!wide.matches) scrollHost.scrollTop = 0; focusReader();
    } catch (error) {
      if (!currentTicket) readerRoot.replaceChildren(empty('Cette fiche ne peut pas être ouverte', error.message));
      drawPath(); throw error;
    }
  }
  async function showList() {
    await ensureList(); if (disposed || ctx.signal?.aborted) return;
    showingDetail = false; updateLayout(); drawResume();
    requestAnimationFrame(() => {
      if (disposed || showingDetail || ctx.signal?.aborted) return;
      listCleanup?.resume?.(); scrollHost.scrollTop = listPosition.top; scrollHost.scrollLeft = listPosition.left;
      const target = lastFocus ? [...list.querySelectorAll('[data-focus-key]')].find(node => node.dataset.focusKey === lastFocus) : null;
      (target || list.querySelector('[aria-label="Recherche du relevé"]'))?.focus({ preventScroll: true });
    });
  }
  async function savedDossiers() {
    const search = input('', { 'aria-label': 'Rechercher dans les dossiers conservés', placeholder: 'ID, titre ou note personnelle', maxLength: 300 });
    const body = el('div', { class: 'tickets-saved-list' }); let page = 0, serial = 0;
    const modal = dialog('Mes dossiers', el('div', { class: 'stack' }, el('p', { class: 'help', text: 'Vos dossiers et vos notes restent disponibles même si le ticket n’est plus dans le relevé courant.' }), toolbar(search, button('Rechercher', () => { page = 0; return load(); })), body));
    const load = async () => {
      const version = ++serial; const data = await ctx.api.call('workplace.ticket.list', { saved: true, search: search.value, page }, { signal: ctx.signal });
      if (version !== serial || !modal.element.isConnected) return;
      replaceChildren(body, ...data.rows.map(row => el('article', { class: 'tickets-saved-item' }, el('div', {}, badge(`#${row.id}`), el('strong', { text: row.title }), el('p', { class: 'help', text: row.status })),
        button('Ouvrir le dossier', () => { modal.close(); return perform(() => showTicket({ ticketKey: row.ticketKey })); }, { icon: 'ticket' }))),
        data.total ? toolbar(badge(`${formatNumber(data.total)} dossiers`), button('Précédent', () => { page--; return load(); }, { disabled: page === 0 }), button('Suivant', () => { page++; return load(); }, { disabled: (page + 1) * data.pageSize >= data.total })) : empty('Aucun dossier conservé', 'Ouvrez un ticket ou ajoutez-le à votre file pour le retrouver ici.'));
    };
    search.addEventListener('keydown', event => { if (event.key === 'Enter') { page = 0; load().catch(error => ctx.notify(error.message, 'error')); } });
    await load();
  }

  await showList();
  if (ctx.params?.rowIndex != null || ctx.params?.ticketKey) await showTicket({ ...ctx.params, rowIndex: ctx.params.rowIndex == null ? undefined : Number(ctx.params.rowIndex), revision: ctx.params.revision == null ? undefined : Number(ctx.params.revision) });
  else if (resumeActive || ctx.params?.resumeTicket) { const work = await ctx.api.call('workplace.get', {}, { signal: ctx.signal }); if (work.activeTicketKey) await showTicket({ ticketKey: work.activeTicketKey, section: ctx.params?.section }); }
  return async () => { await queue; await readerCleanup?.(); await listCleanup?.(); disposed = true; wide.removeEventListener('change', onWidth); if (ctx.root.dataset.workspace === 'records') delete ctx.root.dataset.workspace; };
}
