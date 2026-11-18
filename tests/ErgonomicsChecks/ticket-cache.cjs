'use strict';
module.exports = async ({ page, reset, ok, capture }) => {
  await page.setViewportSize({ width: 1280, height: 900 }); await reset();
  await page.evaluate(async () => {
    const { renderTicketsWorkspace } = await window.importProduction('/web/js/pages/tickets-workspace.js');
    const now = Date.now(), sourceModifiedAt = new Date(now - 3600000).toISOString();
    window.cacheCalls = []; window.cacheNotices = []; window.cacheHideId = false; window.cacheDelayJournal = false;
    const verified = { state: 'current', hasContent: true, autoRead: false, sourceToken: 'same-source-version',
      sourceModifiedAt, cachedModifiedAt: sourceModifiedAt, sourceObservedAt: new Date(now - 600000).toISOString(), readAt: new Date(now - 60000).toISOString() };
    const caches = [
      { ...verified, state: 'missing', hasContent: false, autoRead: true, readAt: null, cachedModifiedAt: null, reason: 'Aucune fiche conservée.' },
      { ...verified },
      { ...verified, sourceModifiedAt: new Date(now - 9 * 24 * 3600000).toISOString(), cachedModifiedAt: new Date(now - 9 * 24 * 3600000).toISOString(), readAt: new Date(now - 8 * 24 * 3600000).toISOString() },
      { ...verified, state: 'recent', sourceModifiedAt: null, cachedModifiedAt: null, reason: 'Date de modification non reconnue.' },
      { ...verified, state: 'stale', autoRead: true, cachedModifiedAt: new Date(now - 86400000).toISOString(), reason: 'Modification détectée dans le relevé.' },
      undefined
    ];
    const source = caches.map((cache, index) => ({ index, cells: [String(index + 1), `Cache fixture ${index + 1}`, 'Nouveau'], cache }));
    const headers = ['ID', 'Titre', 'Statut'];
    const state = { session: { loaded: true, revision: 7, rows: source.length }, settings: { dashboard: {}, interface: {} }, capabilities: [] };
    const ticket = index => ({ personal: { key: `cache-ticket-${index}`, id: String(index + 1), title: source[index].cells[1], notes: '', checklist: [], readingPosition: {} },
      title: source[index].cells[1], canRead: true, loaded: !!source[index].cache?.hasContent, sections: [], warnings: [], readAt: source[index].cache?.readAt, cache: structuredClone(source[index].cache) });
    const api = { on: () => () => {}, call: async (op, args = {}) => {
      cacheCalls.push({ op, args: structuredClone(args) });
      if (op === 'records.list') {
        const rows = source.filter(row => (!args.indices || args.indices.includes(row.index)) && (!args.search || row.cells.join(' ').includes(args.search)));
        return { rows: structuredClone(rows.slice(args.offset || 0, (args.offset || 0) + (args.limit || 100))), headers, total: rows.length, sourceTotal: source.length,
          revision: 7, offset: args.offset || 0, limit: args.limit || 100, columns: headers.map((_, sourceIndex) => ({ sourceIndex, displayIndex: sourceIndex, visible: !(cacheHideId && sourceIndex === 0) })), session: state.session };
      }
      if (op === 'records.schema') return { available: false };
      if (op === 'workplace.scope') return { technicians: [], profile: {}, personalTechnician: '' };
      if (op === 'workplace.get') return { checklistTemplates: {} };
      if (op === 'workplace.ticket.open') return ticket(args.rowIndex ?? Number(args.ticketKey.split('-').at(-1)));
      if (op === 'workplace.ticket.read') {
        const index = Number(args.ticketKey.split('-').at(-1));
        source[index].cache = { ...verified, readAt: new Date().toISOString() };
        const result = ticket(index);
        if (cacheDelayJournal) result.sections = [{ kind: 'timeline', key: 'timeline', index: 0 }];
        return result;
      }
      if (op === 'workplace.ticket.section') return new Promise(resolve => { window.releaseCacheJournal = () => { cacheDelayJournal = false; resolve({ rows: [], total: 0, complete: true }); }; });
      if (op === 'workplace.ticket.position') return {};
      throw Error('Unexpected cache fixture operation ' + op);
    } };
    window.cacheContext = { root: document.querySelector('#root'), state, params: {}, api, signal: controller.signal, navigate: () => {}, notify: text => cacheNotices.push(text) };
    window.cacheCleanup = await renderTicketsWorkspace(cacheContext);
  });
  const icons = page.locator('.tickets-record-grid .ticket-cache-indicator');
  ok(await icons.count() === 6, 'Every compact search result has one database cache indicator');
  ok(await icons.evaluateAll(nodes => nodes.map(n => n.dataset.cacheTone).join(',') === 'missing,current,warning,warning,warning,unknown'), 'Absent, verified, old, unverified and unavailable metadata have distinct honest cache states');
  ok(await icons.evaluateAll(nodes => nodes.every(n => n.getAttribute('role') === 'img' && n.tabIndex === 0 && n.getAttribute('aria-label') === n.title && n.querySelector('.ticket-cache-mark')?.textContent)), 'Each cache indicator has a focusable textual explanation and a shape cue in addition to colour');
  await icons.first().focus(); await page.keyboard.press('Enter');
  ok(await page.evaluate(() => !cacheCalls.some(c => c.op === 'workplace.ticket.open' || c.op === 'workplace.ticket.read')), 'Inspecting cache metadata never opens or reads a ticket');
  await page.getByRole('button', { name: 'Outils du tableau', exact: true }).click();
  await page.getByRole('button', { name: 'Vue des colonnes', exact: true }).click();
  ok(await icons.count() === 6 && await page.locator('.tickets-record-grid thead tr:first-child th').count() === 5, 'Column view keeps the source-column positions and adds no cache column');
  await page.evaluate(() => { cacheHideId = true; });
  await page.getByLabel('Recherche du relevé', { exact: true }).fill('Cache');
  await page.waitForFunction(() => document.querySelectorAll('.tickets-record-grid thead tr:first-child th').length === 4);
  ok(await icons.count() === 6 && await page.locator('.tickets-record-grid tbody td:last-child .ticket-cache-indicator').count() === 6, 'Hiding the ID column retains one cache indicator in the existing open-action cell');
  await page.getByLabel('Sélectionner la ligne 2', { exact: true }).check();
  await page.evaluate(() => { window.cacheDomBeforeRead = { grid: document.querySelector('.tickets-record-grid .table-scroll'), opener: document.querySelector('[data-focus-key="open-1"]'), search: document.querySelector('[aria-label="Recherche du relevé"]'), filter: document.querySelector('[aria-label="Filtrer Statut"]') }; });
  await page.locator('[data-focus-key="open-1"]').click();
  await page.locator('.tickets-detail-pane .work-ticket-heading h2').waitFor();
  ok(await page.getByRole('heading', { name: '#2 · Cache fixture 2', exact: true }).count() === 1 && await page.locator('.tickets-detail-pane .ticket-cache-indicator[data-cache-tone=current]').count() === 1, 'Ticket detail shows the same cache indicator beside an unchanged accessible heading');
  ok(await page.locator('.ticket-cache-dates').evaluate(n => n.textContent.includes('Dernière modification (relevé)') && n.textContent.includes('Dernière lecture / mise en cache') && !n.textContent.includes('non connue')), 'Ticket detail shows known source modification and cache reading dates directly under its title');
  await page.waitForTimeout(650); // Beyond the real 500 ms dwell, with a verified unchanged cache.
  ok(await page.evaluate(() => !cacheCalls.some(c => c.op === 'workplace.ticket.read')), 'An unchanged backend cache decision suppresses the detail auto-read');
  await page.evaluate(() => { cacheDelayJournal = true; });
  await page.getByRole('button', { name: 'Actualiser la fiche', exact: true }).click();
  await page.waitForFunction(() => typeof releaseCacheJournal === 'function');
  await page.locator('.work-ticket-reader').evaluate(node => node.dispatchEvent(new FocusEvent('focusout', { bubbles: true })));
  ok(await page.locator('.ticket-live-read').getAttribute('data-state') === 'reading', 'Focus resumed while applying an unchanged cache does not cancel the pending journal redraw');
  await page.evaluate(() => releaseCacheJournal());
  await page.locator('.ticket-live-read[data-state=fresh]').waitFor();
  ok(await page.evaluate(() => { const reads = cacheCalls.filter(c => c.op === 'workplace.ticket.read'); return reads.length === 1 && reads[0].args.automatic === false && reads[0].args.ticketKey === 'cache-ticket-1'; }), 'Manual refresh remains explicit and sends the original ticket identity with automatic false');
  await page.getByRole('button', { name: 'Retour à la liste', exact: true }).click();
  ok(await page.getByLabel('Recherche du relevé', { exact: true }).inputValue() === 'Cache' && await page.getByLabel('Sélectionner la ligne 2', { exact: true }).isChecked(), 'Refreshing a ticket keeps the search and explicit selection on return');
  ok(await page.evaluate(() => cacheDomBeforeRead.grid === document.querySelector('.tickets-record-grid .table-scroll') && cacheDomBeforeRead.opener === document.querySelector('[data-focus-key="open-1"]') && cacheDomBeforeRead.search === document.querySelector('[aria-label="Recherche du relevé"]') && cacheDomBeforeRead.filter === document.querySelector('[aria-label="Filtrer Statut"]')), 'A refreshed cache preserves the existing grid, opener, search and named filter DOM');
  await page.locator('[data-focus-key="open-0"]').click();
  await page.waitForFunction(() => cacheCalls.filter(c => c.op === 'workplace.ticket.read').length === 2 && document.querySelector('.tickets-detail-pane .ticket-cache-indicator')?.dataset.cacheTone === 'current');
  ok(await page.evaluate(() => cacheCalls.filter(c => c.op === 'workplace.ticket.read')[1].args.automatic === true), 'A missing copy can auto-read through the guarded automatic API contract');
  await page.getByRole('button', { name: 'Retour à la liste', exact: true }).click();
  ok(await page.locator('[data-focus-key="row-0"] .ticket-cache-indicator').getAttribute('data-cache-tone') === 'current' && await page.getByLabel('Sélectionner la ligne 2', { exact: true }).isChecked(), 'A newly read cache updates the hidden list row without losing the other selected ticket');
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    ok(await icons.first().evaluate(n => n.getBoundingClientRect().width === 16 && n.getBoundingClientRect().height === 16) && await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Cache indicators stay discrete and search stays bounded at ${width}px`);
  }
  await capture('ticket-cache-states');
  ok(await page.evaluate(() => cacheNotices.length === 0), 'Cache metadata and refresh interactions emit no fixture errors');
  await page.evaluate(() => cacheCleanup());
};
