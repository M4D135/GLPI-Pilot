import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const production = readFileSync(new URL('../../web/js/pages/library-collection.js', import.meta.url), 'utf8')
  .replace(/^import[^\n]+\n/, '').replace('export async function renderLibraryCollection', 'async function renderLibraryCollection');
const runA = 'a'.repeat(32), runB = 'b'.repeat(32), sessionA = 'c'.repeat(32), sessionB = 'd'.repeat(32);
const options = { automatic: false, intervalMinutes: 120, delayMilliseconds: 3000, batchSize: 25, batchPauseSeconds: 30,
  maximumMinutes: 60, consecutiveErrorLimit: 3, startMinute: 0, endMinute: 1440, timeZone: 'UTC' };
const event = (sequence, patch = {}) => ({ sequence, at: '2026-11-18T14:32:09Z', level: 'info', stage: 'Lecture de l’index',
  code: 'index.progress', message: `Étape conservée ${sequence}`, ...patch });
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };

// Run the entire production page and its bridge/timer lifecycle in a small DOM
// double. These checks neither start a browser nor duplicate collection policy.
async function fixture(patch = {}) {
  const queued = [], timers = new Map(), listeners = new Map(), calls = [], handlers = new Map(); let timerId = 0;
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; this.attributes = {}; this.dataset = {}; this.listeners = new Map(); this.className = ''; this._text = ''; this._open = false; this.hidden = false; this.disabled = false;
      this.classList = { add: value => { this.className += ` ${value}`; }, contains: value => this.className.split(/\s+/).includes(value) }; }
    set textContent(value) { this._text = String(value ?? ''); this.children = []; }
    get textContent() { return this._text + this.children.map(child => typeof child === 'string' ? child : child.textContent).join(''); }
    set open(value) { const changed = this._open !== !!value; this._open = !!value; if (changed) queued.push(() => this.emit('toggle')); }
    get open() { return this._open; }
    append(...children) { for (const child of children) { if (child instanceof Element) child.parentNode = this; this.children.push(child instanceof Element ? child : String(child)); } }
    replaceChildren(...children) { this.children = []; this._text = ''; this.append(...children); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    removeAttribute(name) { delete this.attributes[name]; }
    addEventListener(name, callback) { const callbacks = this.listeners.get(name) || []; callbacks.push(callback); this.listeners.set(name, callbacks); }
    async emit(name) { for (const callback of this.listeners.get(name) || []) await callback({ type: name, target: this, preventDefault() {} }); }
    reportValidity() { return true; }
  }
  const el = (tag, attributes = {}, ...children) => {
    const node = new Element(tag);
    for (const [key, value] of Object.entries(attributes || {})) {
      if (value === undefined || value === null) continue;
      if (key === 'text') node.textContent = value;
      else if (key === 'class') node.className = value;
      else if (key === 'dataset') Object.assign(node.dataset, value);
      else node[key] = value;
    }
    node.append(...children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false)); return node;
  };
  const button = (label, handler, settings = {}) => {
    const node = el('button', { disabled: !!settings.disabled, class: `button ${settings.primary ? 'primary' : ''}`, ...settings.attributes }, el('span', { text: label }));
    if (handler) node.addEventListener('click', async () => {
      if (node.disabled) return; const disabled = node.disabled; node.disabled = true;
      try { await handler(); } finally { node.disabled = disabled; }
    });
    return node;
  };
  const data = { state: { revision: 1, state: 'attention', runId: runA, nextAt: null, options },
    report: { runId: runA, observed: 0, read: 0, reused: 0, retryCount: 0, message: 'Passage interrompu avant lecture des fiches.',
      limitations: ['Le délai de lecture de l’index a été dépassé. La reprise relira la liste.'], startedAt: '2026-11-18T14:31:00Z' },
    activity: { state: 'idle' }, persisted: true,
    events: [event(3, { level: 'error', code: 'index.timeout', message: 'Le délai de lecture de l’index a été dépassé.' }), event(2), event(1, { stage: 'Préparation', code: 'collection.started', message: 'Passage démarré.' })], ...patch };
  const api = {
    on: (name, callback) => { listeners.set(name, callback); return () => listeners.delete(name); },
    call: async (operation, args = {}) => {
      calls.push({ operation, args: structuredClone(args) });
      if (handlers.has(operation)) return handlers.get(operation)(args);
      if (operation === 'library.collection.status') return structuredClone(data.activity);
      if (operation === 'library.collection.get') {
        if (data.modelReadFailure) throw new Error(data.modelReadFailure);
        return structuredClone({ state: data.state, report: data.report, activity: data.activity });
      }
      if (operation === 'library.collection.events') return { runId: args.runId, total: data.events.length, offset: args.offset, limit: args.limit,
        persisted: data.persisted, items: structuredClone(data.events.slice(args.offset, args.offset + args.limit)) };
      if (operation === 'library.capture.entries') return { total: 0, items: [] };
      throw new Error(`Unexpected operation: ${operation}`);
    }
  };
  const root = el('main'), controller = new AbortController();
  const context = vm.createContext({
    el, button, icon: name => el('svg', { name }), toolbar: (...nodes) => el('div', { class: 'toolbar' }, nodes),
    input: (value, attributes) => el('input', { value, ...attributes }), field: (label, node) => el('label', {}, label, node),
    confirm: async () => true, formatNumber: value => String(value ?? 0), formatDate: value => value || '—',
    showError: (host, error) => host.append(el('p', { text: error.message })), isSimulation: session => !!session.simulation, isFileSource: session => !!session.fileSource,
    document: { visibilityState: 'visible' },
    setInterval: callback => { const id = ++timerId; timers.set(id, callback); return id; }, clearInterval: id => timers.delete(id)
  });
  vm.runInContext(production + '\nglobalThis.render = renderLibraryCollection;', context);
  const dispose = await context.render({ root, api, signal: controller.signal, state: { session: {} }, notify() {}, navigate() {} });
  function all(node = root) { return [node, ...node.children.filter(child => child instanceof Element).flatMap(child => all(child))]; }
  const find = predicate => all().find(predicate);
  async function flush() {
    for (let attempt = 0; attempt < 5; attempt++) {
      for (const callback of queued.splice(0)) await callback();
      await new Promise(resolve => setImmediate(resolve));
    }
  }
  await flush();
  return { data, calls, handlers, root, dispose, timers, listeners, all, find, flush,
    byClass: value => find(node => node.classList.contains(value)),
    panel: value => find(node => node.dataset.collectionPanel === value),
    button: label => find(node => node.tagName === 'button' && node.textContent === label),
    emit: activity => { data.activity = structuredClone(activity); listeners.get('library.collection.progress')?.(activity); },
    async tick() { for (const callback of timers.values()) callback(); await flush(); }
  };
}

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
  assert.match(f.byClass('collection-recovery').textContent, /L’index.*vérifie d’abord l’index/);
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
