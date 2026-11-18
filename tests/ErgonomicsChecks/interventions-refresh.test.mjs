import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const planning = readFileSync(new URL('../../web/js/pages/interventions-planning.js', import.meta.url), 'utf8');
const workspace = readFileSync(new URL('../../web/js/pages/interventions-workspace.js', import.meta.url), 'utf8');
function block(source, from, until) {
  const start = source.indexOf(from), end = source.indexOf(until, start);
  assert.ok(start >= 0 && end > start, `Production lifecycle block exists: ${from}`);
  return source.slice(start, end);
}

// Execute the actual closed-over handler and event wiring. A deterministic clock
// and a revision-checking bridge reproduce the failure without a browser or a
// second implementation of the scheduling policy.
function fixture({ linked = true } = {}) {
  const listeners = new Map(), timers = new Map(), requests = [], notifications = [];
  let timerId = 0;
  const query = { revision: 7, search: '4101', indices: [0], selectedRows: [0] };
  const state = { session: { revision: 7 } };
  const notice = { children: [] }, controller = new AbortController();
  const openForm = { value: 'Saisie du référentiel conservée', connected: true };
  const context = vm.createContext({
    disposed: false, refreshPending: false, refreshTimer: null, editor: null, mutation: false,
    signal: controller.signal, initial: linked ? { ticketSearch: query } : {}, state, notice,
    ctx: { refresh: async () => { openForm.connected = false; } },
    navigate: async () => { openForm.connected = false; },
    stops: [],
    api: { on: (event, handler) => { listeners.set(event, handler); return () => listeners.delete(event); } },
    el: (tag, attributes = {}, ...children) => ({ tag, ...attributes, children }),
    replaceChildren: (node, ...children) => { node.children = children; },
    showIssue: message => { notice.children = [{ text: message }]; },
    notify: (message, tone) => notifications.push({ message, tone }),
    setTimeout: handler => { const id = ++timerId; timers.set(id, handler); return id; },
    clearTimeout: id => timers.delete(id),
    load: async () => {
      const args = linked ? { ticketSearch: structuredClone(query) } : {};
      requests.push(args);
      if (args.ticketSearch && args.ticketSearch.revision !== state.session.revision)
        throw new Error('Le relevé a changé. Actualisez cet écran avant de poursuivre.');
    }
  });
  vm.runInContext(block(planning, ' const action=', ' const call=')
    + block(planning, ' function changed()', '  const abort='), context);
  return {
    context, state, query, notice, requests, notifications, timers, openForm,
    emit: event => { assert.ok(listeners.has(event)); listeners.get(event)(); },
    async flush() {
      const pending = [...timers.values()]; timers.clear();
      for (const callback of pending) callback();
      await Promise.resolve(); await Promise.resolve();
    }
  };
}

test('a new observation suspends the stale linked search without a danger toast or closing an open form', async () => {
  const f = fixture(), original = structuredClone(f.query);
  f.state.session.revision = 8;
  f.emit('session.changed');
  await f.flush();
  assert.deepEqual(f.notifications, []);
  assert.deepEqual(f.requests, [], 'calendar.get must not receive indices from the previous revision');
  assert.match(JSON.stringify(f.notice), /Actualisez cet espace/);
  assert.deepEqual(f.query, original, 'old row coordinates must not be relabelled with a new revision');
  assert.deepEqual(f.openForm, { value: 'Saisie du référentiel conservée', connected: true });
});

test('a session change cancels a calendar refresh already queued for the old search', async () => {
  const f = fixture();
  f.emit('calendar.changed');
  assert.equal(f.timers.size, 1);
  f.state.session.revision = 8;
  f.emit('session.changed');
  assert.equal(f.timers.size, 0);
  await f.flush();
  assert.deepEqual(f.requests, []);
  assert.deepEqual(f.notifications, []);
});

test('calendar, tour and library changes still coalesce into a refresh for a current search', async () => {
  const f = fixture();
  for (const event of ['calendar.changed', 'tours.changed', 'library.changed', 'session.changed']) f.emit(event);
  assert.equal(f.timers.size, 1);
  await f.flush();
  assert.deepEqual(f.requests, [{ ticketSearch: f.query }]);
  assert.deepEqual(f.notifications, []);
});

test('an unlinked calendar still refreshes after a source revision changes', async () => {
  const f = fixture({ linked: false });
  f.state.session.revision = 8;
  f.emit('session.changed');
  await f.flush();
  assert.deepEqual(f.requests, [{}]);
  assert.deepEqual(f.notifications, []);
});

test('a dirty editor cancels a previously queued refresh and keeps its content', async () => {
  const f = fixture();
  f.emit('calendar.changed');
  f.context.editor = { dirty: true, draft: { title: 'Intervention en cours de saisie' } };
  f.emit('tours.changed');
  await f.flush();
  assert.deepEqual(f.requests, []);
  assert.equal(f.context.editor.draft.title, 'Intervention en cours de saisie');
  assert.match(JSON.stringify(f.notice), /Saisie conservée/);
});

test('an active mutation or a disposed page never starts a background refresh', async () => {
  const f = fixture();
  f.context.mutation = true; f.emit('calendar.changed'); await f.flush();
  assert.deepEqual(f.requests, []);
  f.context.mutation = false; f.context.disposed = true; f.emit('calendar.changed');
  assert.equal(f.timers.size, 0);
});

test('the existing workspace refresh rebuilds a stale search without replaying its row indices or panel', async () => {
  const navigations = [], reads = [];
  const state = { session: { revision: 8 } }, params = { ticketSearch: { revision: 7, indices: [0] }, panel: 'reference' };
  const context = vm.createContext({
    dispose: {}, params, active: 'map',
    host: { querySelector: selector => selector === '.interventions-workspace' ? { dataset: { tab: 'map' } } : null },
    ctx: { state, navigate: async (...args) => navigations.push(args) },
    cleanup: { refresh: async () => reads.push('refresh') }
  });
  vm.runInContext(block(workspace, 'dispose.refresh=async()=>{', 'return dispose;'), context);
  await context.dispose.refresh();
  assert.equal(navigations.length, 1);
  assert.equal(navigations[0][0], 'interventions');
  assert.equal(navigations[0][1].tab, 'map');
  assert.equal(Object.hasOwn(navigations[0][1], 'ticketSearch'), false);
  assert.equal(Object.hasOwn(navigations[0][1], 'panel'), false);
  assert.deepEqual(reads, []);
  params.ticketSearch.revision = state.session.revision;
  await context.dispose.refresh();
  assert.deepEqual(reads, ['refresh']);
});
