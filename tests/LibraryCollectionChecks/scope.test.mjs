import assert from 'node:assert/strict';
import test from 'node:test';
import { fixture, options, deferred, runA, sessionA } from './fixture.mjs';

const scope = () => ({ version: 1, enabled: true, dateRole: 'created', fromDate: '', toDate: '', ticketIds: [], text: '', similarSubject: '', fields: [] });
const catalog = () => ({ sourceFingerprint: 'source-a', sourceUrl: 'https://fixture.invalid/glpi/front/ticket.php', observedAt: '2026-11-18T10:00:00Z',
  inventoryComplete: true, inventoryCount: 180000, titleAvailable: true,
  dateRoles: [{ role: 'created', label: 'Date de création', column: 'Date de création', available: true }, { role: 'updated', label: 'Dernière modification', column: 'Dernière modification', available: true }],
  fields: [{ column: 'Groupe attribué', role: 'group', label: 'Groupe attribué', valuesLoaded: true, truncated: true, values: [{ value: 'Support', count: 600 }, { value: 'Réseau', count: 200 }] },
    { column: 'Demandeur', role: 'requester', label: 'Demandeur', valuesLoaded: false, truncated: false, values: [] }] });
const scopeModel = () => ({ scope: scope(), catalog: catalog(), available: true, coverage: null,
  inventory: { sourceFingerprint: 'source-a', sourceUrl: 'https://fixture.invalid/glpi/front/ticket.php', observedAt: '2026-11-18T10:00:00Z', count: 180000, complete: true },
  teams: [{ key: 'team:one', label: 'Assistance', groups: ['Support', 'Réseau'] }] });
const coverage = (patch = {}) => ({ selected: 10, complete: 6, missing: 2, partial: 1, changed: 1, unconfirmed: 0, toRead: 4, ...patch });
const idle = (patch = {}) => ({ state: { revision: 1, state: 'idle', runId: '', nextAt: null, options, scope: scope() }, report: null, events: [], scopeModel: scopeModel(), ...patch });
const byLabel = (f, label) => f.find(node => node['aria-label'] === label || node.attributes['aria-label'] === label);
async function fill(node, value) { node.value = value; await node.emit('input'); }
async function preview(f) { await f.button('Prévisualiser le périmètre').emit('click'); await f.flush(); }
const countCalls = (f, operation) => f.calls.filter(call => call.operation === operation).length;

test('a 180000-ticket source opens in targeted mode without reading any remote ticket or calculating an unsolicited preview', async () => {
  const f = await fixture(idle());
  assert.match(f.byClass('collection-scope-source').textContent, /180000.*2026-11-18.*https:\/\/fixture.invalid/);
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.equal(f.fieldControl('Parcourir toute la liste GLPI accessible').checked, false);
  assert.equal(countCalls(f, 'library.capture'), 0);
  assert.equal(countCalls(f, 'library.collection.preview'), 0);
  await fill(f.fieldControl('Historique à partir du'), '2026-01-01');
  await fill(f.fieldControl('Sujet contenant'), 'imprimante');
  assert.equal(countCalls(f, 'library.collection.preview'), 0);
  assert.equal(countCalls(f, 'library.capture'), 0);
  f.dispose();
});

test('an explicit preview combines history, tickets and group values and uses the filtered denominator', async () => {
  const f = await fixture(idle());
  await fill(f.fieldControl('Historique à partir du'), '2026-01-01');
  await fill(f.fieldControl('Jusqu’au'), '2025-12-31');
  await preview(f); assert.equal(countCalls(f, 'library.collection.preview'), 0);
  assert.match(f.byClass('collection-scope-status').textContent, /date de fin.*postérieure/);
  await fill(f.fieldControl('Jusqu’au'), '2026-10-31');
  await fill(f.fieldControl('Numéros de tickets'), '42, 43;42');
  await f.button('Ajouter un filtre').emit('click');
  const group = byLabel(f, 'Groupe attribué : Support'); group.checked = true; await group.emit('change');
  await preview(f);
  const args = f.calls.find(call => call.operation === 'library.collection.preview').args.scope;
  assert.equal(args.enabled, true);
  assert.equal(args.fromDate, '2026-01-01'); assert.equal(args.toDate, '2026-10-31');
  assert.deepEqual(args.ticketIds, ['42', '43']);
  assert.deepEqual(args.fields, [{ column: 'Groupe attribué', values: ['Support'] }]);
  assert.match(f.byClass('collection-scope-coverage-heading').textContent, /60 %/);
  assert.match(f.byClass('collection-scope-coverage-detail').textContent, /6 \/ 10 contenus complets/);
  assert.equal(byLabel(f, 'Contenus complets dans le périmètre').max, 10);
  assert.equal(f.button('Mettre à jour').disabled, false);
  assert.equal(countCalls(f, 'library.capture'), 0);
  await fill(f.fieldControl('Sujet contenant'), 'autre sujet');
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.equal(f.byClass('collection-scope-coverage').hidden, true);
  f.dispose();
});

test('a late preview cannot authorize a different draft and closing the editor ignores pending data', async () => {
  const f = await fixture(idle()), delayed = deferred();
  f.handlers.set('library.collection.preview', args => delayed.promise);
  const pending = f.button('Prévisualiser le périmètre').emit('click'); await f.flush();
  await fill(f.fieldControl('Historique à partir du'), '2026-11-01');
  delayed.resolve({ ...scopeModel(), scope: scope(), previewToken: 'old-token', coverage: coverage() });
  await pending; await f.flush();
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.equal(f.byClass('collection-scope-coverage').hidden, true);
  assert.match(f.byClass('collection-scope-status').textContent, /Filtres modifiés/);
  const afterClose = deferred(); f.handlers.set('library.collection.preview', () => afterClose.promise);
  const closing = f.button('Prévisualiser le périmètre').emit('click'); await f.flush(); f.dispose();
  const before = f.root.textContent;
  afterClose.resolve({ ...scopeModel(), previewToken: 'closed-token', coverage: coverage() });
  await closing; await f.flush(); assert.equal(f.root.textContent, before);
});

test('a preview from a replaced source requires another explicit review instead of authorizing the new inventory', async () => {
  const f = await fixture(idle());
  f.handlers.set('library.collection.preview', args => ({ ...scopeModel(), scope: args.scope, catalog: { ...catalog(), sourceFingerprint: 'source-b' }, previewToken: 'new-source-token', coverage: coverage() }));
  await preview(f);
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.match(f.byClass('collection-scope-status').textContent, /relevé a changé.*prévisualiser à nouveau/);
  assert.equal(f.byClass('collection-scope-coverage').hidden, true);
  assert.equal(countCalls(f, 'library.capture'), 0);
  await preview(f);
  assert.equal(f.button('Mettre à jour').disabled, false);
  f.dispose();
});

test('starting uses the accepted preview and live coverage never comes from attempt progress or read counts', async () => {
  const f = await fixture(idle()), capture = deferred(); let sent;
  f.handlers.set('library.capture', args => { sent = args; f.emit({ state: 'running', sessionId: sessionA, runId: runA,
    processed: 10, total: 10, requests: 2, reused: 99, coverage: coverage({ complete: 7, missing: 1, toRead: 3 }), coverageVerifiedAt: '2026-11-18T12:00:00Z' }); return capture.promise; });
  await preview(f);
  const pending = f.button('Mettre à jour').emit('click'); await f.flush();
  assert.equal(sent.scope.enabled, true); assert.equal(sent.previewToken, 'preview-token'); assert.equal(sent.confirmedGlobal, false);
  assert.equal(f.byClass('collection-scope-controls').disabled, true);
  assert.equal(f.button('Prévisualiser le périmètre').disabled, true);
  assert.equal(byLabel(f, 'Tickets examinés pendant ce passage').value, 10);
  assert.equal(byLabel(f, 'Contenus complets dans le périmètre').value, 7);
  assert.match(f.byClass('collection-scope-coverage-heading').textContent, /70 %/);
  assert.match(f.byClass('collection-scope-verification').textContent, /2026-11-18T12:00:00Z/);
  assert.equal(f.button('Pause').disabled, false); assert.equal(f.button('Arrêter').disabled, false);
  f.data.state = { ...f.data.state, state: 'paused', runId: runA };
  f.emit({ state: 'paused', sessionId: sessionA, runId: runA, coverage: coverage({ complete: 7, missing: 1, toRead: 3 }) });
  capture.resolve({ collectionState: 'paused' }); await pending; await f.flush();
  assert.equal(f.byClass('collection-scope-controls').disabled, true);
  assert.equal(f.button('Reprendre').hidden, false);
  f.dispose();
});

test('resuming a frozen run sends its identity without the draft, while another scope requires preview and a new-cycle flag', async () => {
  const saved = idle({ state: { revision: 1, state: 'paused', runId: runA, options, scope: scope() } });
  const f = await fixture(saved); let resumed;
  f.handlers.set('library.capture', args => { resumed = args; return { collectionState: 'paused' }; });
  await f.button('Reprendre').emit('click'); await f.flush();
  assert.equal(resumed.mode, 'resume'); assert.equal(resumed.runId, runA);
  assert.equal(resumed.scope, undefined); assert.equal(resumed.previewToken, undefined);
  assert.equal(countCalls(f, 'library.collection.preview'), 0);
  await f.button('Préparer un autre périmètre').emit('click');
  assert.equal(f.byClass('collection-scope-controls').disabled, false);
  assert.equal(f.button('Mettre à jour').disabled, true);
  await fill(f.fieldControl('Historique à partir du'), '2026-10-01');
  await f.button('Revenir à la reprise').emit('click');
  assert.equal(f.byClass('collection-scope-controls').disabled, true);
  assert.equal(f.fieldControl('Historique à partir du').value, '');
  assert.equal(f.button('Reprendre').hidden, false);
  await f.button('Préparer un autre périmètre').emit('click');
  await fill(f.fieldControl('Historique à partir du'), '2026-11-01');
  await preview(f);
  await f.button('Mettre à jour').emit('click'); await f.flush();
  assert.equal(resumed.mode, 'incremental'); assert.equal(resumed.runId, '');
  assert.equal(resumed.confirmedNewCycle, true);
  assert.equal(resumed.scope.fromDate, '2026-11-01');
  f.dispose();
});

test('an empty scope cannot start or report zero as one hundred percent', async () => {
  const f = await fixture(idle({ previewCoverage: coverage({ selected: 0, complete: 0, missing: 0, partial: 0, changed: 0, toRead: 0 }) }));
  await preview(f);
  assert.equal(f.button('Mettre à jour').disabled, true);
  await f.button('Mettre à jour').emit('click');
  assert.equal(countCalls(f, 'library.capture'), 0);
  assert.match(f.byClass('collection-scope-coverage-heading').textContent, /Aucun ticket/);
  assert.doesNotMatch(f.byClass('collection-scope-coverage-heading').textContent, /100 %/);
  f.dispose();
});

test('one hundred percent known coverage still permits an explicit version check without starting from the preview', async () => {
  const f = await fixture(idle({ previewCoverage: coverage({ complete: 10, missing: 0, partial: 0, changed: 0, toRead: 0 }) }));
  await preview(f);
  assert.equal(f.button('Mettre à jour').disabled, false);
  assert.equal(countCalls(f, 'library.capture'), 0);
  assert.match(f.byClass('collection-scope-status').textContent, /contenus connus.*vérifiera les versions dans GLPI/);
  f.handlers.set('library.capture', args => ({ collectionState: 'completed' }));
  await f.button('Mettre à jour').emit('click'); await f.flush();
  assert.equal(countCalls(f, 'library.capture'), 1);
  assert.equal(f.calls.find(call => call.operation === 'library.capture').args.scope.enabled, true);
  f.dispose();
});

test('remote facet search can select a rare user outside initial values without collecting tickets and configured teams reuse their group sets', async () => {
  const f = await fixture(idle());
  byLabel(f, 'Colonne à filtrer').value = 'Demandeur'; await f.button('Ajouter un filtre').emit('click');
  await fill(byLabel(f, 'Rechercher des valeurs : Demandeur'), 'Utilisateur rare');
  f.handlers.set('library.collection.scope', args => ({ ...scopeModel(), catalog: { ...catalog(), fields: [{ column: args.column, role: 'requester', label: 'Demandeur', values: [{ value: 'Utilisateur rare', count: 3 }], truncated: false }] } }));
  await f.button('Rechercher les valeurs').emit('click'); await f.flush();
  const requested = f.calls.find(call => call.operation === 'library.collection.scope').args;
  assert.equal(requested.column, 'Demandeur'); assert.equal(requested.search, 'Utilisateur rare');
  const user = byLabel(f, 'Demandeur : Utilisateur rare'); user.checked = true; await user.emit('change');
  await f.button('Assistance').emit('click');
  await preview(f);
  const selected = f.calls.find(call => call.operation === 'library.collection.preview').args.scope.fields;
  assert.deepEqual(selected, [{ column: 'Demandeur', values: ['Utilisateur rare'] }, { column: 'Groupe attribué', values: ['Support', 'Réseau'] }]);
  assert.equal(countCalls(f, 'library.capture'), 0);
  f.dispose();
});

test('missing dates and titles are unavailable, a partial inventory is named, and global discovery is only an explicit advanced choice', async () => {
  const model = scopeModel(); model.catalog = { ...catalog(), dateRoles: [], titleAvailable: false, inventoryComplete: false }; model.inventory.complete = false;
  const f = await fixture(idle({ scopeModel: model }));
  assert.equal(f.fieldControl('Historique à partir du').disabled, true);
  assert.equal(f.fieldControl('Sujet proche').disabled, true);
  assert.equal(f.fieldControl('Sujet contenant').disabled, true);
  assert.match(f.byClass('collection-scope-note').textContent, /Relevé partiel.*tickets connus/);
  const global = f.fieldControl('Parcourir toute la liste GLPI accessible'); global.checked = true; await global.emit('change');
  assert.equal(countCalls(f, 'library.capture'), 0);
  f.handlers.set('library.collection.preview', args => ({ scope: args.scope, available: true, previewToken: 'global-token', coverage: null }));
  await preview(f);
  assert.equal(f.byClass('collection-scope-coverage').hidden, true);
  assert.match(f.byClass('collection-scope-status').textContent, /volume reste inconnu/);
  let captured; f.handlers.set('library.capture', args => { captured = args; return { collectionState: 'completed' }; });
  await f.button('Mettre à jour').emit('click'); await f.flush();
  assert.equal(captured.scope.enabled, false); assert.equal(captured.confirmedGlobal, true); assert.equal(captured.previewToken, 'global-token');
  f.dispose();
});

test('coverage rejects corrupt totals and automatic scheduling cannot adopt an unlaunched draft', async () => {
  const f = await fixture(idle({ state: { revision: 1, state: 'idle', runId: '', options, scope: { enabled: false } } }));
  assert.equal(f.fieldControl('Ce poste effectue les mises à jour automatiques').disabled, true);
  assert.equal(f.coveragePolicy({ selected: 10, complete: 11 }), null);
  assert.equal(f.coveragePolicy({ selected: -1, complete: 0 }), null);
  assert.equal(f.coveragePolicy({ selected: 10, complete: NaN }), null);
  assert.equal(f.coveragePolicy({ selected: 0, complete: 0 }).percent, null);
  assert.equal(f.coveragePolicy({ selected: 3, complete: 1 }).percent, 33.3);
  f.dispose();
});

test('a rejected capture clears the old preview token rather than offering an endless stale retry', async () => {
  const f = await fixture(idle()); await preview(f);
  f.handlers.set('library.capture', () => { throw new Error('L’aperçu a changé ou a expiré.'); });
  await f.button('Mettre à jour').emit('click'); await f.flush();
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.match(f.byClass('collection-scope-status').textContent, /Prévisualiser à nouveau/);
  assert.equal(countCalls(f, 'library.capture'), 1);
  f.dispose();
});

test('preparing a new run never borrows another scope’s saved coverage', async () => {
  const model = scopeModel(); model.coverage = coverage({ selected: 180000, complete: 170000, missing: 10000, partial: 0, changed: 0, toRead: 10000 });
  const f = await fixture(idle({ scopeModel: model, state: { revision: 1, state: 'completed', runId: runA, options, scope: scope() } }));
  await preview(f);
  f.emit({ state: 'running', sessionId: sessionA, runId: '', phase: 'Préparation du nouveau passage', coverage: null });
  assert.equal(byLabel(f, 'Contenus complets dans le périmètre').max, 10);
  assert.equal(byLabel(f, 'Contenus complets dans le périmètre').value, 6);
  assert.doesNotMatch(f.byClass('collection-scope-coverage').textContent, /170000/);
  f.dispose();
});

test('an older held global run is shown as global and switching to a new draft does not silently preserve that mode', async () => {
  const f = await fixture(idle({ state: { revision: 1, state: 'paused', runId: runA, options, scope: { ...scope(), enabled: false } } }));
  assert.equal(f.fieldControl('Parcourir toute la liste GLPI accessible').checked, true);
  assert.match(f.byClass('collection-scope-source').textContent, /Toute la liste GLPI accessible/);
  assert.equal(f.byClass('collection-scope-controls').disabled, true);
  await f.button('Préparer un autre périmètre').emit('click');
  assert.equal(f.fieldControl('Parcourir toute la liste GLPI accessible').checked, false);
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.equal(countCalls(f, 'library.capture'), 0);
  f.dispose();
});
