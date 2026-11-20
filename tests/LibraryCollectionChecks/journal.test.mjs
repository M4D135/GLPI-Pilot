import assert from 'node:assert/strict';
import test from 'node:test';
import { fixture, runA, runB, sessionA, sessionB, options, event, deferred } from './fixture.mjs';

const storedPass = () => ({
  state: { revision: 1, state: 'completed', runId: runA, options },
  report: { runId: runA, observed: 8, read: 7, reused: 0, retryCount: 8, message: 'Passage terminé, versions à confirmer.' },
  availability: { runId: runA, observed: 8, stored: 7, complete: 1, partial: 6, missing: 1, unconfirmed: 7, confirmed: 0, invalid: 0, checkedAt: '2026-11-20T10:00:00Z' }
});

test('local verification exposes saved partial contents separately from freshness and never starts a capture', async () => {
  const f = await fixture(storedPass());
  assert.match(f.byClass('collection-local').textContent, /7Contenus conservés.*1Complets conservés.*6Partiels conservés.*1Sans contenu.*7Version à confirmer/);
  const before = structuredClone(f.data);
  await f.button('Vérifier les contenus conservés').emit('click'); await f.flush();
  assert.deepEqual(f.data, before);
  assert.equal(f.calls.filter(call => call.operation === 'library.collection.get').length, 2);
  assert.equal(f.calls.some(call => /capture$|\.preview$|\.settings$|\.save$/.test(call.operation)), false);
  assert.match(f.notifications.at(-1)[0], /Aucune lecture GLPI.*aucune modification/);
  assert.match(f.byClass('collection-local-status').textContent, /7 contenus présents.*aucune nouvelle collecte/);
  assert.equal(f.button('Vérifier les contenus conservés').disabled, false);
  f.dispose();
});

test('restored cache offers local verification while explaining and blocking every online capture control', async () => {
  const f = await fixture({ ...storedPass(), session: { cachedSource: true } });
  assert.match(f.byClass('collection-launch-hint').textContent, /Cache local.*sans recollecte.*désactivées/);
  for (const name of ['Mettre à jour', 'Reprendre', 'Reprendre les erreurs', 'Compléter seulement les contenus absents']) {
    assert.equal(f.button(name).disabled, true, name);
    await f.button(name).emit('click');
  }
  await f.button('Vérifier les contenus conservés').emit('click');
  await f.button('Consulter les contenus').emit('click'); await f.flush();
  assert.deepEqual(f.navigations, [['library']]);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  assert.equal(f.button('Vérifier les contenus conservés').disabled, false);
  f.dispose();
});

test('missing-content retry keeps the original run and sends an explicit filter without a new scope', async () => {
  const f = await fixture(storedPass());
  f.handlers.set('library.capture', () => ({ collectionState: 'completed' }));
  assert.equal(f.button('Compléter seulement les contenus absents').disabled, false);
  await f.button('Compléter seulement les contenus absents').emit('click'); await f.flush();
  const call = f.calls.find(call => call.operation === 'library.capture');
  assert.equal(call.args.mode, 'retry');
  assert.equal(call.args.runId, runA);
  assert.equal(call.args.missingOnly, true);
  assert.equal(call.args.scope, undefined);
  assert.equal(call.args.previewToken, undefined);
  // An explicit full retry remains available for truly incomplete sections.
  await f.button('Reprendre les erreurs').emit('click'); await f.flush();
  assert.equal(f.calls.filter(call => call.operation === 'library.capture').at(-1).args.missingOnly, undefined);
  f.dispose();
});

test('retry entries open saved partial content by its exact key even in offline cache', async () => {
  const f = await fixture({ ...storedPass(), session: { cachedSource: true } });
  const key = 'e'.repeat(64);
  f.handlers.set('library.capture.entries', () => ({ total: 2, items: [
    { ticketKey: key, ticketId: '42', stateLabel: 'Lecture partielle', message: 'Date non confirmée.', hasContent: true,
      detailLimitations: ['La rubrique Tâches reste incomplète.'] },
    { ticketKey: 'f'.repeat(64), ticketId: '43', stateLabel: 'Non encore lu', message: 'Aucun contenu.', hasContent: false }
  ] }));
  f.handlers.set('library.ticket', args => ({ key: args.key }));
  f.panel('retry').open = true; await f.flush();
  assert.match(f.panel('retry').textContent, /rubrique Tâches reste incomplète/);
  assert.equal(f.all().filter(node => node.tagName === 'button' && node.textContent === 'Lire le contenu conservé').length, 1);
  await f.button('Lire le contenu conservé').emit('click');
  assert.deepEqual(f.calls.find(call => call.operation === 'library.ticket').args, { key });
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('a failed retry scrolls its error into view and a disabled start explains its missing preview', async () => {
  const f = await fixture(storedPass());
  assert.equal(f.button('Mettre à jour').disabled, true);
  assert.match(f.byClass('collection-launch-hint').textContent, /Prévisualisez le périmètre/);
  f.handlers.set('library.capture', () => { throw new Error('La source a changé. Les contenus sont conservés.'); });
  await f.button('Reprendre les erreurs').emit('click'); await f.flush();
  assert.equal(f.byClass('collection-errors').scrolledIntoView, true);
  assert.match(f.byClass('collection-errors').textContent, /source a changé.*conservés/);
  assert.equal(f.button('Reprendre les erreurs').disabled, false);
  f.dispose();
});

test('an unsuccessful local check keeps the previous data and does not announce a successful verification', async () => {
  const f = await fixture(storedPass());
  f.data.modelReadFailure = 'Private storage failure';
  await f.button('Vérifier les contenus conservés').emit('click'); await f.flush();
  assert.equal(f.notifications.length, 0);
  assert.equal(f.byClass('collection-model-error').hidden, false);
  assert.equal(f.byClass('collection-errors').scrolledIntoView, true);
  assert.match(f.byClass('collection-errors').textContent, /vérification locale n’a pas abouti.*conservés/);
  assert.doesNotMatch(f.root.textContent, /Private storage failure/);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('a collection starting during a local check cannot produce an unperformed audit success', async () => {
  const f = await fixture(storedPass());
  const modelReads = f.calls.filter(call => call.operation === 'library.collection.get').length;
  f.handlers.set('library.collection.status', () => ({ state: 'running', sessionId: sessionA, runId: runA }));
  await f.button('Vérifier les contenus conservés').emit('click'); await f.flush();
  assert.equal(f.calls.filter(call => call.operation === 'library.collection.get').length, modelReads);
  assert.equal(f.notifications.some(args => args[0].includes('Vérification locale terminée')), false);
  assert.match(f.notifications.at(-1)[0], /Une collecte est en cours/);
  assert.equal(f.button('Vérifier les contenus conservés').disabled, true);
  f.dispose();
});

test('a restored source with no saved run cannot report that its nonexistent availability was checked', async () => {
  const f = await fixture({ state: { revision: 0, state: 'idle', runId: '', options }, report: null, availability: null,
    session: { cachedSource: true } });
  await f.button('Vérifier les contenus conservés').emit('click'); await f.flush();
  assert.equal(f.notifications.some(args => args[0].includes('Vérification locale terminée')), false);
  assert.match(f.notifications.at(-1)[0], /Aucun bilan.*Consulter les contenus/);
  assert.match(f.byClass('collection-local-status').textContent, /Aucun bilan local/);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('an initial index failure with zero tickets opens dated operational events separately from retry entries', async () => {
  const f = await fixture();
  assert.equal(f.panel('journal').open, true);
  assert.equal(f.panel('retry').open, false);
  assert.match(f.byClass('collection-journal-rows').textContent, /Erreur.*Lecture de l’index.*Le délai de lecture/);
  assert.match(f.byClass('collection-journal-rows').textContent, /\d{2}:\d{2}:09/);
  assert.equal(f.find(node => node.tagName === 'time').dateTime, '2026-11-18T14:32:09Z');
  assert.equal(f.calls.filter(call => call.operation === 'library.capture.entries').length, 0);
  assert.equal(f.button('Reprendre les erreurs').disabled, true);
  assert.equal(f.button('Reprendre').disabled, false);
  assert.match(f.byClass('collection-recovery').textContent, /Vérifier la cause.*La reprise vérifie l’index/);
  f.panel('retry').open = true; await f.flush();
  assert.match(f.panel('retry').textContent, /Aucune fiche individuelle.*erreur de lecture de l’index/);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('an older failed pass without detailed events preserves its limitation instead of implying success', async () => {
  const f = await fixture({ events: [] });
  assert.match(f.byClass('collection-journal-empty').textContent, /Aucun événement détaillé enregistré/);
  assert.match(f.byClass('collection-journal-report').textContent, /Bilan conservé.*délai de lecture de l’index/);
  assert.equal(f.find(node => node.id === 'collection-state-title').textContent, 'À vérifier avant de reprendre');
  f.dispose();
});

test('an active pass refreshes logs through the read-only event API without queuing model or retry operations', async () => {
  const f = await fixture({ activity: { state: 'running', sessionId: sessionA, runId: runA, phase: 'Lecture de l’index' } });
  assert.equal(f.panel('journal').open, true);
  assert.equal(f.calls.some(call => ['library.collection.get', 'library.capture.entries'].includes(call.operation)), false);
  f.data.events.unshift(event(4, { message: 'Deuxième page de l’index reçue.' }));
  await f.tick();
  assert.match(f.byClass('collection-journal-rows').textContent, /Deuxième page de l’index reçue/);
  assert.equal(f.button('Pause').disabled, false);
  f.panel('retry').open = true; await f.flush();
  assert.equal(f.calls.some(call => call.operation === 'library.capture.entries'), false);
  assert.match(f.panel('retry').textContent, /journal reste consultable pendant la collecte/);
  f.dispose();
});

test('a preparation failure before opening a durable run does not display totals from the previous successful pass', async () => {
  const f = await fixture({ state: { revision: 1, state: 'completed', runId: runA, options },
    report: { runId: runA, read: 99, reused: 11, retryCount: 0, message: 'Ancien passage terminé.' },
    activity: { state: 'attention', sessionId: sessionB, runId: '', message: 'Le poste collecteur est déjà occupé.', phase: 'Préparation' },
    persisted: false, events: [event(1, { level: 'error', stage: 'Préparation', message: 'Le poste collecteur est déjà occupé.' })] });
  assert.match(f.byClass('collection-phase').textContent, /poste collecteur est déjà occupé/);
  assert.doesNotMatch(f.byClass('collection-counters').textContent, /99|11/);
  assert.equal(f.byClass('collection-journal-report').hidden, true);
  assert.match(f.byClass('collection-journal-status').textContent, /mémoire uniquement/);
  assert.equal(f.button('Mettre à jour').hidden, false);
  assert.equal(f.button('Reprendre').hidden, true);
  assert.equal(f.calls.filter(call => call.operation === 'library.collection.events').at(-1).args.runId, '');
  f.dispose();
});

test('a failed local model read still displays the preparation cause and in-memory events without raw storage errors', async () => {
  const f = await fixture({ activity: { state: 'attention', sessionId: sessionA, runId: '', phase: 'Préparation de la collecte',
    message: 'La bibliothèque locale ne peut pas être ouverte. Vérifiez son dossier.' },
    modelReadFailure: 'Raw storage path C:\\Users\\private-fixture-user\\secret-library; Cookie=private-fixture-secret',
    persisted: false, events: [event(1, { stage: 'Préparation de la collecte', code: 'local-storage', level: 'error', message: 'Le journal de cette tentative est conservé en mémoire.' })] });
  assert.equal(f.panel('journal').open, true);
  assert.equal(f.byClass('collection-model-error').hidden, false);
  assert.match(f.byClass('collection-phase').textContent, /bibliothèque locale ne peut pas être ouverte/);
  assert.match(f.byClass('collection-journal-rows').textContent, /Erreur.*Préparation de la collecte.*conservé en mémoire/);
  assert.match(f.byClass('collection-journal-status').textContent, /mémoire uniquement/);
  assert.equal(f.calls.some(call => call.operation === 'library.collection.events' && call.args.runId === ''), true);
  assert.doesNotMatch(f.root.textContent, /private-fixture-user|private-fixture-secret|Raw storage path/);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('older log pages stay still during live collection and returning to the newest page resumes the feed', async () => {
  const f = await fixture({ activity: { state: 'running', sessionId: sessionA, runId: runA }, events: Array.from({ length: 60 }, (_, index) => event(60 - index)) });
  await f.button('Plus anciens').emit('click'); await f.flush();
  assert.equal(f.calls.filter(call => call.operation === 'library.collection.events').at(-1).args.offset, 25);
  const requests = f.calls.filter(call => call.operation === 'library.collection.events').length;
  const before = f.byClass('collection-journal-rows').textContent;
  f.data.events.unshift(event(61, { message: 'Nouvel événement du passage.' }));
  await f.tick();
  assert.equal(f.calls.filter(call => call.operation === 'library.collection.events').length, requests);
  assert.equal(f.byClass('collection-journal-rows').textContent, before);
  await f.button('Revenir au suivi').emit('click'); await f.flush();
  assert.match(f.byClass('collection-journal-rows').textContent, /Nouvel événement du passage/);
  assert.equal(f.button('Plus récents').disabled, true);
  f.dispose();
});

test('a late event page cannot overwrite a new run or a resumed attempt of the same durable run', async () => {
  for (const nextRun of [runB, runA]) {
    const f = await fixture({ activity: { state: 'running', sessionId: sessionA, runId: runA } });
    const delayed = deferred(); let reads = 0;
    f.handlers.set('library.collection.events', () => ++reads === 1 ? delayed.promise : { runId: nextRun, total: 1, offset: 0, limit: 25, persisted: true, items: [event(10, { message: 'Nouvelle tentative uniquement.' })] });
    const refreshing = f.button('Actualiser le journal').emit('click'); await f.flush();
    f.emit({ state: 'running', sessionId: sessionB, runId: nextRun }); await f.tick();
    delayed.resolve({ runId: runA, total: 1, offset: 0, limit: 25, persisted: true, items: [event(9, { message: 'Réponse ancienne à ignorer.' })] });
    await refreshing; await f.flush();
    assert.match(f.byClass('collection-journal-rows').textContent, /Nouvelle tentative uniquement/);
    assert.doesNotMatch(f.byClass('collection-journal-rows').textContent, /Réponse ancienne/);
    f.dispose();
  }
});

test('a status response begun before a new progress event cannot restore the former run', async () => {
  const f = await fixture({ activity: { state: 'running', sessionId: sessionA, runId: runA, phase: 'Ancienne phase' } });
  const delayed = deferred(); f.handlers.set('library.collection.status', () => delayed.promise);
  await f.tick();
  f.emit({ state: 'running', sessionId: sessionB, runId: runB, phase: 'Nouvelle phase' }); await f.flush();
  delayed.resolve({ state: 'running', sessionId: sessionA, runId: runA, phase: 'Ancienne phase' }); await f.flush();
  assert.equal(f.byClass('collection-phase').textContent, 'Nouvelle phase');
  f.dispose();
});

test('a transient log read failure preserves prior events and does not expose raw exception details', async () => {
  const f = await fixture();
  const before = f.byClass('collection-journal-rows').textContent;
  f.handlers.set('library.collection.events', () => { throw new Error('Cookie=private-fixture-secret; private ticket body'); });
  await f.button('Actualiser le journal').emit('click'); await f.flush();
  assert.equal(f.byClass('collection-journal-rows').textContent, before);
  assert.equal(f.byClass('collection-journal-error').hidden, false);
  assert.match(f.byClass('collection-journal-error').textContent, /n’a pas pu être actualisé/);
  assert.doesNotMatch(f.root.textContent, /private-fixture-secret|private ticket body/);
  f.dispose();
});

test('only explicit safe event fields are rendered as text, without dumping extra payload metadata', async () => {
  const f = await fixture({ events: [event(1, { level: 'warning', stage: 'Contrôle final', message: '<script>Texte de diagnostic</script>',
    ticketId: '42', privateBody: 'must-never-be-rendered', cookies: 'must-never-be-rendered' })] });
  assert.match(f.byClass('collection-journal-rows').textContent, /<script>Texte de diagnostic<\/script>/);
  assert.match(f.byClass('collection-journal-rows').textContent, /Avertissement.*Contrôle final.*Fiche #42/);
  assert.equal(f.all().some(node => node.tagName === 'script'), false);
  assert.doesNotMatch(f.root.textContent, /must-never-be-rendered/);
  f.dispose();
});

test('terminal events from an automatic collection reload its final report without starting another pass', async () => {
  const f = await fixture({ activity: { state: 'running', sessionId: sessionA, runId: runA } });
  f.data.state.state = 'attention';
  f.data.report.limitations = ['La confirmation finale n’a pas abouti.'];
  f.emit({ state: 'attention', sessionId: sessionA, runId: runA, message: 'La confirmation finale n’a pas abouti.' });
  await f.flush();
  assert.match(f.byClass('collection-journal-report').textContent, /confirmation finale/);
  assert.equal(f.button('Reprendre').disabled, false);
  assert.equal(f.calls.some(call => call.operation === 'library.capture'), false);
  f.dispose();
});

test('closing the page removes polling and ignores a pending log result', async () => {
  const f = await fixture(), delayed = deferred();
  f.handlers.set('library.collection.events', () => delayed.promise);
  const refreshing = f.button('Actualiser le journal').emit('click'); await f.flush();
  f.dispose(); const before = f.byClass('collection-journal-rows').textContent;
  delayed.resolve({ runId: runA, total: 1, offset: 0, limit: 25, persisted: true, items: [event(99, { message: 'Arrivé après fermeture.' })] });
  await refreshing; await f.flush();
  assert.equal(f.byClass('collection-journal-rows').textContent, before);
  assert.equal(f.timers.size, 0); assert.equal(f.listeners.size, 0);
});
