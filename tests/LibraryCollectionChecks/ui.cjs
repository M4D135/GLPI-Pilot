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
      const signal = new AbortController().signal;
      const api = { on: (name, callback) => { callbacks.set(name, callback); return () => callbacks.delete(name); }, call: async (op, args = {}) => {
        calls.push({ op, args: structuredClone(args) });
        if (op === 'library.collection.status') return structuredClone(activity);
        if (op === 'library.collection.get') return { state: structuredClone(state), report: reportOverride || (state.runId ? { runId: state.runId, read: 3, reused: 7, retryCount: 2, message: '10 tickets conservés' } : null), activity: structuredClone(activity), installation: 'https://fixture.invalid/glpi' };
        if (op === 'library.collection.events') return { runId: args.runId, total: collectionEvents.length, offset: args.offset, limit: args.limit, persisted: true, items: structuredClone(collectionEvents.slice(args.offset, args.offset + args.limit)) };
        if (op === 'library.capture') { state.runId ||= 'a'.repeat(32); collectionEvents.unshift({ sequence: collectionEvents.length + 1, at: new Date().toISOString(), level: 'info', stage: 'Lecture de l’index', code: 'index.started', message: 'Lecture de la liste accessible démarrée.' }); activity = { state: 'running', sessionId: 'b'.repeat(32), runId: state.runId, total: 50, processed: 10, reused: 7, requests: 12, phase: 'Lecture', updatedAt: new Date().toISOString() }; callbacks.get('library.collection.progress')(activity); return new Promise(resolve => { window.finishCollection = resolve; }); }
        if (op === 'library.collection.control') { assertSession(args.sessionId); state.state = args.action === 'pause' ? 'paused' : 'stopped'; activity = { ...activity, state: state.state }; callbacks.get('library.collection.progress')(activity); finishCollection({ collectionState: state.state }); return { requested: true }; }
        if (op === 'library.collection.settings') { state.options = args.options; state.revision++; return state; }
        if (op === 'library.capture.entries') return { total: 0, items: [] };
        if (op === 'cache.state') return { available: false };
        if (op === 'library.get') return { stats: { ticketCount: 10 } };
        if (op === 'library.bootstrap.preview') return { cancelled: false, token: 'c'.repeat(32), preview: { manifest: { records: 10, exportedAt: new Date().toISOString(), installation: 'https://fixture.invalid/glpi' }, complete: 9 }, warning: 'Paquet interne de test.' };
        if (op === 'library.bootstrap.cancel') return { cancelled: true };
        throw new Error('Unexpected operation ' + op);
      } };
      function assertSession(id) { if (id !== 'b'.repeat(32)) throw new Error('Wrong collection identity'); }
      window.collectionContext = { root, api, signal, state: { session: {}, settings: { sources: [], interface: {} } }, notify: () => {}, navigate: () => {} };
      window.cleanup = await renderLibraryCollection(collectionContext);
    });
    ok(await page.evaluate(() => calls.every(c => c.op !== 'library.capture')), 'Opening the dedicated menu never starts a collection');
    ok(await page.locator('.collection-actions .primary:visible').count() === 1 && await page.locator('.collection-disclosure[open]').count() === 0, 'One primary action and optional settings remain collapsed on entry');
    await page.getByRole('button', { name: 'Mettre à jour', exact: true }).click();
    await page.waitForFunction(() => !!window.finishCollection);
    ok(await page.getByRole('button', { name: 'Pause', exact: true }).isEnabled() && await page.getByRole('button', { name: 'Arrêter', exact: true }).isEnabled(), 'Pause and stop remain available during the long capture call');
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
    await page.evaluate(async () => { cleanup(); ({ state, activity, collectionEvents } = savedCollectionScenario); reportOverride = null; collectionContext.root.replaceChildren(); const { renderDataWorkspace } = await importProduction('/web/js/pages/data-workspace.js'); window.workspaceCleanup = await renderDataWorkspace(collectionContext); });
    await page.locator('[data-open-panel=collection]').click();
    ok(await page.locator('[data-workspace-panel=collection] .collection-workspace').count() === 1 && await page.getByRole('button', { name: 'Reprendre', exact: true }).isVisible(), 'The production data workspace opens the collector with its saved resumable state');
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
