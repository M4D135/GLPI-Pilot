'use strict';

// Browser UI exercises are wired to the real C# terrain dispatcher over an isolated stdio fixture.
// The fixture refuses every network request and every installer launch.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const project = path.resolve(__dirname, '../..');
const { chromium } = require(process.env.GLPI_TEST_PLAYWRIGHT_MODULE || require.resolve('playwright', { paths: [path.join(project, 'tests/DomChecks')] }));
const dll = process.env.GLPI_TERRAIN_FIXTURE || path.join(project, 'tests/TerrainBridgeChecks/bin/Release/net10.0/TerrainBridgeChecks.dll');
const sdk = process.env.GLPI_TEST_DOTNET || 'dotnet';
const child = spawn(sdk, [dll, '--serve'], { cwd: project, stdio: ['pipe', 'pipe', 'pipe'], env: process.env });
const pending = new Map(); let nextId = 1, browser, server, checks = 0, exitCode = 0;
const calls = [], consoleErrors = []; let readyResolve, readyReject;
const ready = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject; });
child.on('error', readyReject);
child.on('exit', code => { for (const item of pending.values()) item.reject(new Error(`C# fixture exited with ${code}`)); pending.clear(); if (code) readyReject(new Error(`C# fixture exited with ${code}`)); });
child.stderr.on('data', data => process.stderr.write(data));
const lines = readline.createInterface({ input: child.stdout });
lines.on('line', line => {
  let response; try { response = JSON.parse(line); } catch { process.stderr.write(line + '\n'); return; }
  if (response.ready) readyResolve(response);
  const request = pending.get(response.id); if (!request) return;
  pending.delete(response.id); if (response.error) request.reject(new Error(response.error.message)); else request.resolve(response.result);
});
function call(op, args = {}) { calls.push(op); return new Promise((resolve, reject) => { const id = nextId++; pending.set(id, { resolve, reject }); child.stdin.write(JSON.stringify({ id, op, args }) + '\n'); }); }
function ok(condition, label) { assert.ok(condition, label); console.log(`PASS ${++checks} ${label}`); }
function timeout(promise, milliseconds, label) { let timer; return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(label)), milliseconds); })]).finally(() => clearTimeout(timer)); }

(async () => {
  try {
    const fixture = await timeout(ready, 15000, 'C# fixture startup timed out');
    server = http.createServer((request, response) => {
      const pathname = new URL(request.url, 'http://localhost').pathname;
      if (pathname === '/') { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end('<!doctype html><html lang="fr"><head><meta charset="utf-8"><link rel="stylesheet" href="/web/css/app.css"><link rel="stylesheet" href="/web/css/terrain.css"></head><body><main id="root"></main></body></html>'); return; }
      const file = path.resolve(project, '.' + pathname);
      if (!file.startsWith(path.join(project, 'web') + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.statusCode = 404; response.end(); return; }
      response.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript; charset=utf-8' : 'text/css; charset=utf-8'); response.end(fs.readFileSync(file));
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true, executablePath: process.env.GLPI_TEST_CHROMIUM || undefined, args: process.platform === 'linux' ? ['--no-sandbox', '--disable-gpu'] : ['--disable-gpu'] });
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    await page.route('**/*', route => route.request().url().startsWith(origin + '/') ? route.continue() : route.abort());
    page.on('pageerror', error => consoleErrors.push(error.message));
    await page.exposeFunction('terrainCall', call);
    await page.goto(origin);
    await page.evaluate(async revision => {
      const { pages } = await import('/web/js/pages/terrain.js');
      let controller, cleanup;
      window.renderTerrain = async (id, params = {}) => {
        controller?.abort(); cleanup?.(); controller = new AbortController(); document.querySelector('#root').replaceChildren();
        const current = await window.terrainCall('terrain.reference.state', {});
        const state = { session: { loaded: true, revision: current.revision }, settings: {}, version: 'fixture' };
        const ctx = { root: document.querySelector('#root'), state, signal: controller.signal, params,
          api: { call: (op, args = {}) => window.terrainCall(op, args), on: () => () => {} },
          navigate: (next, args) => { window.lastNavigation = { id: next, params: args }; }, notify: () => {}, refresh: () => window.renderTerrain(id, params) };
        cleanup = await pages.find(page => page.id === id).render(ctx); window.currentTerrain = id;
      };
    }, fixture.revision);
    const render = id => page.evaluate(id => window.renderTerrain(id), id);
    const lastDialog = () => page.getByRole('dialog').last();

    await render('terrain-reference');
    ok(await page.getByRole('heading', { name: 'Référentiel géographique', exact: true }).count() === 1, 'reference page renders through the real C# API');
    await page.getByRole('button', { name: 'Ajouter une référence', exact: true }).click();
    await lastDialog().getByLabel('Nom', { exact: true }).fill('Gamma');
    await lastDialog().getByRole('button', { name: 'Ajouter au brouillon', exact: true }).click();
    await page.getByText('Gamma', { exact: true }).waitFor();
    ok(await page.getByText('Brouillon modifié', { exact: true }).count() === 1, 'city form creates a server-side draft without JSON editing');
    await page.getByRole('button', { name: 'Simuler et enregistrer le référentiel', exact: true }).click();
    await lastDialog().getByRole('button', { name: 'Enregistrer et appliquer', exact: true }).click();
    await page.getByText('Référentiel enregistré et relevé recalculé.', { exact: true }).waitFor();
    ok(calls.includes('terrain.reference.preview') && calls.includes('terrain.reference.apply'), 'saving the form goes through full preview and explicit apply');
    await page.getByRole('textbox', { name: 'Titre à tester', exact: true }).fill('Incident Gamma');
    await page.getByRole('button', { name: 'Tester le brouillon', exact: true }).click();
    await page.locator('mark').filter({ hasText: 'Gamma' }).waitFor();
    ok(await page.locator('mark').filter({ hasText: 'Gamma' }).count() === 1, 'matched terms are highlighted using detector offsets');
    await page.getByRole('button', { name: 'Importer un CSV…', exact: true }).click();
    await lastDialog().getByRole('button', { name: 'Fusionner dans le brouillon', exact: true }).click();
    await page.getByText('Import fusionné dans le brouillon. Simulez maintenant le référentiel avant de l’enregistrer.', { exact: true }).waitFor();
    ok(calls.includes('terrain.reference.importApply'), 'CSV compare dialog invokes the real merge operation');

    // A new render receives the updated source revision just as the shell does after session.changed.
    await render('terrain-review');
    await page.getByRole('button', { name: 'Examiner', exact: true }).first().click();
    await lastDialog().getByRole('heading', { name: 'Correction locale', exact: true }).waitFor();
    ok(await lastDialog().getByRole('heading', { name: 'Correction locale', exact: true }).count() === 1, 'review dialog exposes real correction and promotion controls');
    await lastDialog().getByRole('button', { name: 'Fermer', exact: true }).first().click();

    await render('tours');
    await page.getByRole('button', { name: 'Ajouter la page au panier', exact: true }).click();
    await page.getByText('3 tickets dans le panier', { exact: true }).waitFor();
    ok(true, 'basket page selection updates the C# selection');
    await page.getByLabel('Nom de la tournée', { exact: true }).fill('Tournée interface');
    await page.getByLabel('Premier jour', { exact: true }).fill('2030-01-07');
    await page.getByRole('button', { name: 'Créer une tournée avec ce panier', exact: true }).click();
    await page.getByRole('heading', { name: 'Tournée interface', exact: true }).waitFor();
    await page.getByRole('heading', { name: 'Planning calculé', exact: true }).waitFor();
    ok(calls.includes('terrain.tours.calculate') && calls.includes('terrain.tours.stops'), 'dated planning renders computed schedule and stage records');
    await page.getByRole('button', { name: 'Modifier', exact: true }).first().click();
    await lastDialog().getByLabel('Verrouiller l’étape contre les réorganisations', { exact: true }).check();
    await lastDialog().getByRole('button', { name: 'Appliquer à la tournée', exact: true }).click();
    await page.getByRole('button', { name: 'Verrouillée · modifier', exact: true }).waitFor();
    ok(true, 'stage form persists a real lock and displays it');
    await page.getByRole('button', { name: 'Enregistrer la tournée', exact: true }).click();
    await page.getByText('Tournée enregistrée localement.', { exact: true }).waitFor();
    ok(calls.includes('terrain.tours.save'), 'local tour save is wired through the store');
    await page.getByRole('button', { name: 'Routes et météo', exact: true }).click();
    const beforeNetwork = calls.filter(value => value === 'terrain.tours.route').length;
    await page.getByRole('button', { name: 'Calculer les routes de la tournée', exact: true }).click();
    await page.getByText('Autorisez l’envoi des coordonnées et dates avant ce calcul.', { exact: true }).waitFor();
    ok(calls.filter(value => value === 'terrain.tours.route').length === beforeNetwork, 'unchecked provider consent blocks the request in the UI');

    await render('updates');
    ok(await page.getByRole('heading', { name: 'Mise à jour et retour arrière', exact: true }).count() === 1, 'update assistant renders without network access');
    ok(await page.getByRole('button', { name: 'Télécharger et vérifier le pack officiel', exact: true }).isDisabled(), 'official download stays disabled until a verified release exists');
    ok(await page.getByRole('button', { name: 'Fermer GLPI Pilot et installer la version vérifiée', exact: true }).isDisabled(), 'installation stays disabled without a prepared pack');
    await page.getByRole('textbox', { name: /SHA-256 du certificat/ }).fill('A'.repeat(64));
    await page.getByLabel('Exiger une signature Authenticode de l’éditeur approuvé', { exact: true }).check();
    await page.getByRole('button', { name: 'Enregistrer la politique de confiance', exact: true }).click();
    await page.getByText('Politique enregistrée. Vérifiez de nouveau le pack avant de l’installer.', { exact: true }).waitFor();
    ok((await call('update.state')).policy.requireSignature, 'signature policy form persists through the real C# validator');
    await page.setViewportSize({ width: 640, height: 900 });
    ok(await page.evaluate(() => document.querySelector('.terrain-page').getBoundingClientRect().width <= innerWidth + 1), 'terrain page remains inside a compact viewport');

    await page.setViewportSize({ width: 1440, height: 1000 });
    await render('terrain-reference');
    await page.getByLabel('Type', { exact: true }).selectOption('site');
    await page.getByRole('button', { name: 'Ajouter une référence', exact: true }).click();
    await lastDialog().getByText('Commune non renseignée', { exact: true }).waitFor();
    await lastDialog().getByLabel('Nom', { exact: true }).fill('Atelier indépendant');
    await lastDialog().getByLabel('Alias et variantes', { exact: true }).fill('AI');
    await lastDialog().getByRole('button', { name: 'Ajouter au brouillon', exact: true }).click();
    await page.getByText('Atelier indépendant', { exact: true }).waitFor();
    const independent = (await call('terrain.reference.list', { kind: 'site', search: 'Atelier indépendant' })).items[0];
    ok(independent.parent === 'Commune non renseignée', 'site form accepts an explicit missing commune without manufacturing a city');
    await page.getByRole('textbox', { name: 'Titre à tester', exact: true }).fill('AI imprimante');
    await page.getByRole('button', { name: 'Tester le brouillon', exact: true }).click();
    await page.locator('mark').filter({ hasText: 'AI' }).waitFor();
    ok(await page.getByText(/Site unique « Atelier indépendant »/).count() > 0, 'standalone simulation presents the recognized site and honest missing-commune explanation');
    await page.getByRole('row').filter({ hasText: 'Atelier indépendant' }).getByRole('button', { name: 'Modifier', exact: true }).click();
    await lastDialog().getByRole('button', { name: 'Choisir…', exact: true }).click();
    await lastDialog().getByRole('row').filter({ hasText: 'Alpha' }).getByRole('button', { name: 'Choisir', exact: true }).click();
    await lastDialog().getByRole('button', { name: 'Ajouter au brouillon', exact: true }).click();
    await page.getByRole('row').filter({ hasText: 'Atelier indépendant' }).getByText('Alpha', { exact: true }).waitFor();
    const attached = (await call('terrain.reference.list', { kind: 'site', search: 'Atelier indépendant' })).items[0];
    ok(attached.id === independent.id && attached.parent === 'Alpha', 'site editor attaches a commune later while preserving the original site identity');
    ok(consoleErrors.length === 0, `no unhandled JavaScript errors: ${consoleErrors.join('; ')}`);
    ok(!calls.some(value => ['update.findOfficial', 'update.downloadOfficial', 'update.install', 'terrain.tours.route', 'terrain.tours.weather'].includes(value)), 'all UI tests completed offline without provider or installer execution');
    console.log(`${checks}/${checks} terrain WebView page checks passed.`);
  } catch (error) { exitCode = 1; console.error(error.stack || error); }
  finally {
    await browser?.close(); await new Promise(resolve => server ? server.close(resolve) : resolve());
    const childExit = child.exitCode !== null ? Promise.resolve() : new Promise(resolve => child.once('exit', resolve));
    child.stdin.end(); await timeout(childExit, 10000, 'Fixture cleanup timed out').catch(() => child.kill('SIGTERM'));
    lines.close(); process.exitCode = exitCode;
  }
})();
