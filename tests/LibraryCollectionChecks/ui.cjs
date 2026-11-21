'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const project = path.resolve(__dirname, '../..');
const { chromium } = require(process.env.GLPI_TEST_PLAYWRIGHT_MODULE);
let count = 0, browser;
function ok(value, text) { assert.ok(value, text); console.log(`PASS ${++count} ${text}`); }
(async () => {
  try {
    browser = await chromium.launch({ headless: true, executablePath: process.env.GLPI_TEST_CHROMIUM, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } }); const errors = []; let external = 0; page.setDefaultTimeout(6000);
    page.on('pageerror', e => errors.push(e.message));
    const origin = 'https://glpi-collection.test';
    await page.route('**/*', route => {
      const url = new URL(route.request().url()); if (url.origin !== origin) { external++; return route.abort(); }
      if (url.pathname === '/') {
        const html = fs.readFileSync(path.join(project, 'web/index.html'), 'utf8').replaceAll('href="css/', 'href="/web/css/').replaceAll('href="vendor/', 'href="/web/vendor/').replace('src="vendor/', 'src="/web/vendor/').replace('<script type="module" src="js/app.js"></script>', '').replace('<div id="app" class="app-loading" role="status">Ouverture de GLPI Pilot…</div>', '<main class="workspace" id="root"></main>');
        return route.fulfill({ contentType: 'text/html', body: html });
      }
      const file = path.resolve(project, '.' + url.pathname);
      if (!file.startsWith(path.join(project, 'web') + path.sep) || !fs.existsSync(file)) return route.fulfill({ status: 404 });
      return route.fulfill({ contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'application/octet-stream', body: fs.readFileSync(file) });
    });
    if (process.env.GLPI_TEST_OFFLINE_BLOBS === '1') await require('../TicketControlChecks/offline.cjs')(page);
    else { await page.goto(origin); await page.evaluate(() => { window.importProduction = name => import(name); }); }
    await page.evaluate(async () => {
      const root = document.querySelector('#root'); Object.assign(root.style, { height: '100dvh', overflow: 'auto', padding: '16px' });
      const { renderLibraryCollection } = await importProduction('/web/js/pages/library-collection.js');
      window.calls = []; window.callbacks = new Map(); window.state = { revision: 1, state: 'idle', runId: '', nextAt: null, options: { version: 1, automatic: false, intervalMinutes: 120, delayMilliseconds: 3000, batchSize: 25, batchPauseSeconds: 30, maximumMinutes: 60, consecutiveErrorLimit: 3, startMinute: 0, endMinute: 1440, timeZone: 'UTC' } }; window.activity = { state: 'idle' }; window.collectionEvents = []; window.reportOverride = null;
      window.collectionScope = { version: 1, enabled: true, dateRole: 'created', fromDate: '', toDate: '', ticketIds: [], text: '', similarSubject: '', fields: [] };
      window.localAvailability = null; window.retryEntries = []; window.collectionPreflight = null; window.contentReadiness = null; window.notifications = []; window.navigations = [];
      window.collectionCatalog = { sourceFingerprint: 'fixture-source-a', sourceUrl: 'https://fixture.invalid/glpi/front/ticket.php', observedAt: '2026-11-18T10:00:00Z', inventoryComplete: true, inventoryCount: 180000, titleAvailable: true,
        dateRoles: [{ role: 'created', label: 'Date de création', column: 'Date de création', available: true }, { role: 'updated', label: 'Dernière modification', column: 'Dernière modification', available: true }],
        fields: [{ column: 'Groupe attribué', role: 'group', label: 'Groupe attribué', valuesLoaded: true, truncated: false, values: [{ value: 'Support', count: 10000 }, { value: 'Réseau', count: 5000 }] },
          { column: 'Demandeur', role: 'requester', label: 'Demandeur', valuesLoaded: true, truncated: false, values: [{ value: 'Utilisateur de test', count: 12 }] }] };
      window.collectionInventory = { sourceFingerprint: 'fixture-source-a', sourceUrl: collectionCatalog.sourceUrl, observedAt: collectionCatalog.observedAt, count: 180000, complete: true };
      window.scopeCoverage = { selected: 50, complete: 30, missing: 15, partial: 3, changed: 2, unconfirmed: 0, toRead: 20 };
      window.scopeModel = () => ({ scope: structuredClone(collectionScope), catalog: structuredClone(collectionCatalog), available: true, inventory: structuredClone(collectionInventory), preflight: structuredClone(collectionPreflight),
        teams: [{ key: 'team:test', label: 'Assistance', groups: ['Support', 'Réseau'] }] });
      const signal = new AbortController().signal;
      const api = { on: (name, callback) => { callbacks.set(name, callback); return () => callbacks.delete(name); }, call: async (op, args = {}) => {
        calls.push({ op, args: structuredClone(args) });
        if (op === 'library.collection.status') return structuredClone(activity);
        if (op === 'library.collection.get') return { ...scopeModel(), state: structuredClone(state), report: reportOverride || (state.runId ? { runId: state.runId, read: 3, reused: 7, retryCount: 2, message: '10 tickets conservés' } : null), activity: structuredClone(activity), installation: 'https://fixture.invalid/glpi', coverage: state.runId ? structuredClone(scopeCoverage) : null, availability: structuredClone(localAvailability), readiness: structuredClone(contentReadiness) };
        if (op === 'library.collection.scope') return scopeModel();
        if (op === 'library.collection.preview') return { ...scopeModel(), scope: structuredClone(args.scope), previewToken: 'f'.repeat(32), coverage: structuredClone(scopeCoverage), warnings: [] };
        if (op === 'library.collection.events') return { runId: args.runId, total: collectionEvents.length, offset: args.offset, limit: args.limit, persisted: true, items: structuredClone(collectionEvents.slice(args.offset, args.offset + args.limit)) };
        if (op === 'library.capture') { if (args.mode === 'incremental') { if (!args.previewToken || !args.scope?.enabled) throw new Error('A targeted preview is required'); state.scope = collectionScope = structuredClone(args.scope); } state.runId ||= 'a'.repeat(32); collectionEvents.unshift({ sequence: collectionEvents.length + 1, at: new Date().toISOString(), level: 'info', stage: 'Lecture de l’index', code: 'index.started', message: 'Lecture de la liste accessible démarrée.' }); activity = { state: 'running', sessionId: 'b'.repeat(32), runId: state.runId, total: 50, processed: 10, reused: 7, requests: 12, phase: 'Lecture', updatedAt: new Date().toISOString(), coverage: structuredClone(scopeCoverage), coverageVerifiedAt: '2026-11-18T11:00:00Z' }; callbacks.get('library.collection.progress')(activity); return new Promise(resolve => { window.finishCollection = resolve; }); }
        if (op === 'library.collection.control') { assertSession(args.sessionId); state.state = args.action === 'pause' ? 'paused' : 'stopped'; activity = { ...activity, state: state.state }; callbacks.get('library.collection.progress')(activity); finishCollection({ collectionState: state.state }); return { requested: true }; }
        if (op === 'library.collection.settings') { state.options = args.options; state.revision++; return state; }
        if (op === 'library.capture.entries') return { total: retryEntries.length, items: structuredClone(retryEntries) };
        if (op === 'library.ticket') return { key: args.key, installation: 'https://fixture.invalid/glpi', seed: { ticket: { id: '42', url: 'https://fixture.invalid/glpi/front/ticket.form.php?id=42' }, title: 'Fiche conservée' },
          detailComplete: false, detailsCurrent: false, detail: { readAt: '2026-11-20T10:00:00Z', sections: [{ name: 'Description', data: { headers: ['Texte'], rows: [['Description déjà enregistrée <script>texte littéral</script>']], complete: true } }] } };
        if (op === 'cache.state') return { available: false };
        if (op === 'library.get') return { stats: { ticketCount: 10 } };
        if (op === 'library.bootstrap.preview') return { cancelled: false, token: 'c'.repeat(32), preview: { manifest: { records: 10, exportedAt: new Date().toISOString(), installation: 'https://fixture.invalid/glpi' }, complete: 9 }, warning: 'Paquet interne de test.' };
        if (op === 'library.bootstrap.cancel') return { cancelled: true };
        throw new Error('Unexpected operation ' + op);
      } };
      function assertSession(id) { if (id !== 'b'.repeat(32)) throw new Error('Wrong collection identity'); }
      window.collectionContext = { root, api, signal, state: { session: {}, settings: { sources: [], interface: {} } }, notify: (...args) => notifications.push(args), navigate: (...args) => navigations.push(args) };
      window.cleanup = await renderLibraryCollection(collectionContext);
    });
    ok(await page.evaluate(() => calls.every(c => c.op !== 'library.capture')), 'Opening the dedicated menu never starts a collection');
    ok(await page.locator('.collection-actions .primary:visible').count() === 1 && await page.locator('.collection-disclosure[open]').count() === 0, 'One primary action and optional settings remain collapsed on entry');
    ok(await page.getByRole('button', { name: 'Mettre à jour', exact: true }).isDisabled(), 'A source with 180000 tickets requires a reviewed scope before starting');
    await page.getByLabel('Historique à partir du', { exact: true }).fill('2026-01-01');
    await page.getByRole('button', { name: 'Ajouter un filtre', exact: true }).click();
    await page.getByLabel('Groupe attribué : Support', { exact: true }).check();
    ok(await page.evaluate(() => !calls.some(c => ['library.capture', 'library.collection.preview'].includes(c.op))), 'Changing history and group filters performs no collection and no unsolicited preview');
    await page.getByRole('button', { name: 'Prévisualiser le périmètre', exact: true }).click();
    ok(await page.getByRole('button', { name: 'Mettre à jour', exact: true }).isEnabled() && await page.locator('.collection-scope-coverage-heading').innerText().then(text => text.includes('60 %')), 'The reviewed scope displays 30 of 50 complete tickets rather than using the global inventory size');
    ok(await page.evaluate(() => { const scope = calls.find(c => c.op === 'library.collection.preview').args.scope; return scope.fromDate === '2026-01-01' && scope.fields[0].column === 'Groupe attribué' && scope.fields[0].values[0] === 'Support'; }), 'The preview applies the exact history and group selection');
    for (const [width, height] of [[1280, 800], [1093, 614], [390, 800]]) {
      await page.setViewportSize({ width, height });
      ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Scope fields and coverage remain bounded at ${width}x${height}`);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    if (process.env.GLPI_TEST_SCREENSHOTS) { fs.mkdirSync(process.env.GLPI_TEST_SCREENSHOTS, { recursive: true }); await page.locator('.collection-scope').screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-scope-preview.png') }); }
    await page.getByRole('button', { name: 'Mettre à jour', exact: true }).click();
    await page.waitForFunction(() => !!window.finishCollection);
    ok(await page.getByRole('button', { name: 'Pause', exact: true }).isEnabled() && await page.getByRole('button', { name: 'Arrêter', exact: true }).isEnabled(), 'Pause and stop remain available during the long capture call');
    ok(await page.getByLabel('Historique à partir du', { exact: true }).isDisabled() && await page.getByRole('button', { name: 'Prévisualiser le périmètre', exact: true }).isDisabled(), 'The accepted scope stays frozen during collection');
    ok(await page.getByLabel('Contenus complets dans le périmètre', { exact: true }).evaluate(node => node.value === 30 && node.max === 50)
      && await page.getByLabel('Tickets examinés pendant ce passage', { exact: true }).evaluate(node => node.value === 10), 'Coverage and attempt progress remain separate meters');
    await page.locator('.collection-event').first().waitFor({ state: 'visible' });
    ok(await page.locator('[data-collection-panel=journal][open]').count() === 1 && await page.evaluate(() => calls.some(c => c.op === 'library.collection.events') && !calls.some(c => c.op === 'library.capture.entries')), 'The operational journal opens and remains readable during capture independently of ticket retry entries');
    await page.evaluate(() => collectionEvents.unshift({ sequence: 2, at: new Date().toISOString(), level: 'info', stage: 'Lecture de l’index', code: 'index.progress', message: 'Deuxième page de l’index reçue.' }));
    await page.getByText('Deuxième page de l’index reçue.', { exact: true }).waitFor({ state: 'visible' });
    ok(await page.locator('.collection-event time[datetime]').count() === 2, 'Live journal polling shows new timestamped stages before the capture operation finishes');
    await page.getByRole('button', { name: 'Pause', exact: true }).click();
    await page.getByRole('button', { name: 'Reprendre', exact: true }).waitFor({ state: 'visible' });
    await page.waitForFunction(() => state.state === 'paused' && !document.querySelector('button')?.disabled);
    ok(await page.getByRole('button', { name: 'Mettre à jour', exact: true }).count() === 0, 'A held collection cannot be accidentally restarted as a new initial harvest');
    await page.getByRole('button', { name: 'Reprendre', exact: true }).click();
    ok(await page.evaluate(() => calls.filter(c => c.op === 'library.capture').at(-1).args.mode === 'resume' && calls.filter(c => c.op === 'library.capture').at(-1).args.runId === 'a'.repeat(32)), 'Resume sends the original durable run identity');
    await page.getByRole('button', { name: 'Arrêter', exact: true }).click();
    await page.waitForFunction(() => state.state === 'stopped');
    ok(await page.evaluate(() => calls.filter(c => c.op === 'library.collection.control').every(c => c.args.sessionId === 'b'.repeat(32))), 'Control requests target only the active collection session');
    await page.getByText('Rythme et programmation', { exact: true }).click();
    await page.getByLabel('Délai minimal entre navigations (millisecondes)', { exact: true }).fill('5000');
    await page.getByRole('button', { name: 'Enregistrer le rythme', exact: true }).click();
    await page.waitForFunction(() => state.options.delayMilliseconds === 5000);
    ok(await page.evaluate(() => calls.filter(c => c.op === 'library.capture').length === 2 && !state.options.automatic), 'Saving the pace neither resumes a stop nor enables automatic collection');
    await page.getByText('Transférer une bibliothèque préparée', { exact: true }).click();
    await page.getByRole('button', { name: 'Importer une bibliothèque préparée', exact: true }).click();
    await page.getByRole('button', { name: 'Annuler', exact: true }).click();
    await page.waitForFunction(() => calls.some(c => c.op === 'library.bootstrap.cancel'));
    ok(await page.evaluate(() => !calls.some(c => c.op === 'library.bootstrap.apply')), 'Cancelling a bootstrap preview never imports or starts a GLPI collection');
    ok(await page.locator('.collection-tuning[open]').count() === 0, 'Advanced pacing is not mixed into everyday controls');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    ok(await page.locator('.collection-state-mark').evaluate(node => getComputedStyle(node).transitionDuration === '0s'), 'Reduced-motion preference keeps status feedback static');
    await page.evaluate(async () => {
      cleanup(); window.savedCollectionScenario = structuredClone({ state, activity, collectionEvents });
      state.state = 'attention';
      activity = { state: 'attention', sessionId: 'b'.repeat(32), runId: state.runId, message: 'Le délai de lecture de l’index a été dépassé.', updatedAt: new Date().toISOString() };
      reportOverride = { runId: state.runId, read: 0, reused: 0, observed: 0, retryCount: 0, message: 'Passage interrompu avant lecture des fiches.', limitations: ['La lecture de l’index n’a pas abouti. La reprise vérifiera de nouveau la liste accessible.'] };
      collectionEvents = [
        { sequence: 3, at: '2026-11-18T14:32:09Z', level: 'error', stage: 'Lecture de l’index', code: 'index.timeout', message: 'Le délai de lecture de l’index a été dépassé. Aucune fiche individuelle n’a encore pu être identifiée.' },
        { sequence: 2, at: '2026-11-18T14:31:09Z', level: 'info', stage: 'Lecture de l’index', code: 'index.started', message: 'Lecture de la liste accessible démarrée.' },
        { sequence: 1, at: '2026-11-18T14:31:00Z', level: 'info', stage: 'Préparation', code: 'collection.started', message: 'Passage démarré.' }
      ];
      const { renderLibraryCollection } = await importProduction('/web/js/pages/library-collection.js');
      window.cleanup = await renderLibraryCollection(collectionContext);
    });
    await page.locator('.collection-event[data-level=error]').waitFor({ state: 'visible' });
    ok(await page.locator('.collection-event').count() === 3 && await page.locator('.collection-phase').textContent().then(text => text.includes('délai de lecture de l’index')), 'An initial zero-ticket failure displays its cause and operational history immediately');
    ok(await page.getByRole('button', { name: 'Reprendre', exact: true }).isEnabled() && await page.getByRole('button', { name: 'Reprendre les erreurs', exact: true }).count() === 0, 'Index recovery remains available even when there are no identified ticket errors');
    await page.locator('[data-collection-panel=retry] > summary').click();
    await page.getByText('Aucune fiche individuelle à reprendre dans ce passage. Une erreur de lecture de l’index peut néanmoins nécessiter « Reprendre » : consultez le journal.', { exact: true }).waitFor({ state: 'visible' });
    ok(await page.getByRole('button', { name: 'Reprendre les erreurs', exact: true }).isDisabled(), 'An empty ticket retry list explicitly refers index errors to the separate operational journal');
    for (const [width, height] of [[1280, 800], [1366, 768], [1093, 614], [390, 800]]) {
      await page.setViewportSize({ width, height });
      ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Collection controls remain bounded at ${width}x${height}`);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    if (process.env.GLPI_TEST_SCREENSHOTS) {
      fs.mkdirSync(process.env.GLPI_TEST_SCREENSHOTS, { recursive: true });
      await page.locator('[data-collection-panel=journal]').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-journal-initial-failure.png') });
    }
    await page.evaluate(async () => {
      cleanup(); state.state = 'completed'; activity = { state: 'idle' };
      reportOverride = { runId: state.runId, status: 'completed', observed: 800, targeted: 800, processed: 800, read: 800, reused: 0,
        failed: 0, partial: 0, conflicts: 0, unconfirmed: 800, retryCount: 0, finalScopeVerified: true, limitations: [] };
      contentReadiness = { available: true, tickets: 800, eligibleTickets: 0, incomplete: 0, missingContent: 0, unconfirmed: 800, conflicts: 0, isReady: false };
      retryEntries = [{ ticketId: '42', stateLabel: 'Contenu à confirmer', message: 'La date, son format ou le fuseau reste ambigu.' }];
      collectionPreflight = { state: 'blocked', checked: 25, reliableDates: 0, unreliableDates: 25, sourceTimeZone: '', sourceDateOrder: '',
        messages: ['Aucune date de modification fiable dans les dates examinées.'] };
      const { renderLibraryCollection } = await importProduction('/web/js/pages/library-collection.js'); cleanup = await renderLibraryCollection(collectionContext);
    });
    const incompleteStatus = page.locator('#collection-state-title');
    await incompleteStatus.waitFor({ state: 'visible' });
    assert.equal(await incompleteStatus.textContent(), 'Passage à compléter');
    assert.equal(await incompleteStatus.getAttribute('role'), 'status');
    assert.equal(await incompleteStatus.getAttribute('aria-live'), 'polite');
    ok(await page.locator('.collection-quality').textContent().then(text => text.includes('800Versions non confirmées'))
      && await page.locator('.collection-readiness-count').textContent().then(text => text.includes('0 / 800 fiches')), 'A completed 800-ticket pass with no retries exposes zero currently confirmed contents');
    ok(await page.locator('[data-collection-panel=retry]').evaluate(node => node.open)
      && await page.locator('.collection-retry-entry').textContent().then(text => text.includes('format ou le fuseau reste ambigu'))
      && await page.evaluate(() => calls.filter(c => c.op === 'library.capture.entries').at(-1).args.retryOnly === false), 'The nonretryable historical entries remain visible with their recorded causes');
    await page.getByRole('button', { name: 'Prévisualiser le périmètre', exact: true }).click();
    ok(await page.getByRole('button', { name: 'Mettre à jour', exact: true }).isEnabled()
      && await page.locator('.collection-preflight').textContent().then(text => text.includes('25 non fiables') && text.includes('avant toute lecture de fiche')),
      'An old date verdict permits a fresh index check and does not claim permission to read unqualified contents');
    await page.locator('.collection-preflight').getByRole('button', { name: 'Régler les dates GLPI', exact: true }).click();
    ok(await page.evaluate(() => navigations.at(-1)[0] === 'library' && navigations.at(-1)[1].view === 'connection' && navigations.at(-1)[1].focus === 'source-dates'), 'The preflight action leads to GLPI source-date settings, separately from the scheduler timezone');
    for (const [width, height] of [[1280, 800], [390, 800]]) {
      await page.setViewportSize({ width, height });
      ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Quality counts and blocked date preflight remain bounded at ${width}x${height}`);
    }
    if (process.env.GLPI_TEST_SCREENSHOTS) await page.locator('.collection-status').screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-unconfirmed-quality.png') });
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.evaluate(async () => {
      cleanup(); state.state = 'completed'; activity = { state: 'idle' }; collectionContext.state.session = { cachedSource: true }; contentReadiness = null; collectionPreflight = null;
      reportOverride = { runId: state.runId, observed: 8, read: 7, reused: 0, retryCount: 8, message: 'Passage terminé, versions à confirmer.' };
      localAvailability = { runId: state.runId, observed: 8, stored: 7, complete: 1, partial: 6, missing: 1, unconfirmed: 7, checkedAt: '2026-11-20T10:00:00Z' };
      retryEntries = [{ ticketKey: 'e'.repeat(64), ticketId: '42', stateLabel: 'Lecture partielle', message: 'Version à confirmer.', hasContent: true, detailLimitations: ['La rubrique Tâches reste incomplète.'] }];
      const { renderLibraryCollection } = await importProduction('/web/js/pages/library-collection.js'); cleanup = await renderLibraryCollection(collectionContext);
    });
    await page.getByRole('button', { name: 'Vérifier les contenus conservés', exact: true }).click();
    ok(await page.locator('.collection-local-status').textContent().then(text => text.includes('7 contenus présents') && text.includes('aucune nouvelle collecte'))
      && await page.getByRole('button', { name: 'Mettre à jour', exact: true }).isDisabled(), 'Offline local verification reports saved contents independently of unconfirmed freshness and disables online collection');
    if (!await page.locator('[data-collection-panel=retry]').evaluate(node => node.open)) await page.locator('[data-collection-panel=retry] > summary').click();
    await page.getByRole('button', { name: 'Lire le contenu conservé', exact: true }).click();
    await page.locator('.library-offline-notice').waitFor({ state: 'visible' });
    ok(await page.locator('.library-ticket-content').textContent().then(text => text.includes('Lecture partielle conservée') && text.includes('Description déjà enregistrée <script>texte littéral</script>'))
      && await page.getByRole('button', { name: 'Ouvrir dans GLPI', exact: true }).count() === 0, 'The retry list opens its saved partial body as literal text and exposes no online action from the offline dialog');
    ok(await page.evaluate(() => calls.filter(c => c.op === 'library.capture').length === 2 && !calls.some(c => c.op === 'library.open')
      && calls.some(c => c.op === 'library.ticket' && c.args.key === 'e'.repeat(64))), 'Local inspection and ticket reading neither repeat capture nor open GLPI');
    if (process.env.GLPI_TEST_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-offline-saved-ticket.png') });
    await page.locator('.library-ticket-modal').getByRole('button', { name: 'Fermer', exact: true }).click();
    if (process.env.GLPI_TEST_SCREENSHOTS) await page.locator('.collection-local').screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-local-availability.png') });
    await page.evaluate(async () => { cleanup(); ({ state, activity, collectionEvents } = savedCollectionScenario); reportOverride = null; localAvailability = null; retryEntries = []; collectionContext.state.session = {}; collectionContext.root.replaceChildren(); const { renderDataWorkspace } = await importProduction('/web/js/pages/data-workspace.js'); window.workspaceCleanup = await renderDataWorkspace(collectionContext); });
    await page.getByRole('button', { name: 'Choisir les tickets à collecter', exact: true }).click();
    ok(await page.locator('[data-workspace-panel=collection] .collection-workspace').count() === 1 && await page.getByRole('button', { name: 'Reprendre', exact: true }).isVisible()
      && await page.evaluate(() => calls.filter(c => c.op === 'library.capture').length === 2), 'The production data workspace collection entry opens the saved scope without starting a global collection');
    await page.locator('[data-workspace-panel=collection] > .modal-heading button').click();
    await page.locator('[data-workspace-panel=collection]').waitFor({ state: 'detached' });
    await page.locator('[data-open-panel=collection]').click();
    ok(await page.getByRole('button', { name: 'Reprendre', exact: true }).isVisible() && await page.evaluate(() => calls.filter(c => c.op === 'library.capture').length === 2), 'Closing and reopening the collection panel neither resets the journal nor restarts acquisition');
    ok(errors.length === 0, 'No JavaScript exception from production collection modules: ' + errors.join('; '));
    ok(external === 0, 'Synthetic collection interface tests make no external requests');
    if (process.env.GLPI_TEST_SCREENSHOTS) {
      fs.mkdirSync(process.env.GLPI_TEST_SCREENSHOTS, { recursive: true });
      for (const [width, height] of [[1280, 800], [1366, 768], [1093, 614]]) {
        await page.setViewportSize({ width, height });
        for (const theme of ['dark', 'light']) {
          await page.evaluate(value => { document.documentElement.dataset.theme = value; document.querySelectorAll('.collection-disclosure').forEach(d => d.open = false); document.querySelector('#root').scrollTop = 0; }, theme);
          await page.screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, 'collection-' + theme + '-' + width + 'x' + height + '.png') });
        }
      }
    }
    await page.evaluate(() => workspaceCleanup()); console.log(`${count}/${count} collection interface checks passed.`);
  } catch (e) { console.error(e); process.exitCode = 1; }
  finally { await browser?.close(); }
})();
