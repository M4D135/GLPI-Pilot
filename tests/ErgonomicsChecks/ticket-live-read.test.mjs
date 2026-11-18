import assert from 'node:assert/strict';
import test from 'node:test';
import { createTicketLiveRead, TICKET_LIVE_READ_DELAY_MS } from '../../web/js/pages/ticket-live-read.js';

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

test('default delay stays at 500 ms; hovering never calls the reader', async () => {
  assert.equal(TICKET_LIVE_READ_DELAY_MS, 500);
  const calls = [];
  const reader = createTicketLiveRead({ read: async key => calls.push(key), delayMs: 25 });
  reader.schedule('ticket-1');
  await pause(7);
  assert.deepEqual(calls, []);
  reader.cancel(); // pointer left the ticket before the dwell threshold
  await pause(30);
  assert.deepEqual(calls, []);
  reader.dispose();
});

test('only the final ticket is read after fast navigation', async () => {
  const calls = [], states = [];
  const reader = createTicketLiveRead({
    delayMs: 15, read: async key => { calls.push(key); return { key }; },
    onState: state => states.push(state)
  });
  reader.schedule('A');
  await pause(3);
  reader.schedule('B');
  await pause(30);
  assert.deepEqual(calls, ['B']);
  assert.equal(states.at(-1), 'fresh');
  reader.dispose();
});

test('leaving the page aborts an in-flight browser read', async () => {
  const wait = deferred(), calls = [];
  const reader = createTicketLiveRead({
    delayMs: 5,
    read: async (key, signal) => { calls.push({ key, signal }); return wait.promise; }
  });
  reader.schedule('A');
  await pause(14);
  assert.equal(calls.length, 1);
  reader.cancel();
  assert.equal(calls[0].signal.aborted, true);
  wait.resolve({ key: 'A' });
  await pause(2);
  reader.dispose();
});

test('recently read tickets reuse the observation; manual refresh bypasses it', async () => {
  const calls = [];
  const reader = createTicketLiveRead({
    delayMs: 5, freshnessMs: 30000,
    read: async key => { calls.push(key); return key; }
  });
  reader.schedule('A');
  await pause(16);
  reader.schedule('A');
  await pause(16);
  assert.deepEqual(calls, ['A']);
  await reader.refresh('A');
  assert.deepEqual(calls, ['A', 'A']);
  reader.dispose();
});

test('offline/unreadable tickets never trigger a background read', async () => {
  const calls = [];
  const reader = createTicketLiveRead({ delayMs: 4, read: key => { calls.push(key); } });
  reader.schedule('offline', false);
  await pause(10);
  assert.deepEqual(calls, []);
  reader.dispose();
});

test('opening a ticket tool cancels auto-reading and closing it can resume safely', async () => {
  const calls = [];
  const reader = createTicketLiveRead({ delayMs: 10, read: async key => { calls.push(key); return key; } });
  reader.schedule('ticket');
  reader.cancel(); // modal editor opens before the dwell threshold
  await pause(20);
  assert.deepEqual(calls, []);
  reader.schedule('ticket'); // same ticket, editor closed
  await pause(20);
  assert.deepEqual(calls, ['ticket']);
  reader.dispose();
});

test('the manual refresh button starts immediately without a duplicate delayed read', async () => {
  const calls = [];
  const reader = createTicketLiveRead({ delayMs: 20, read: async key => { calls.push(key); return key; } });
  reader.schedule('manual');
  await reader.refresh('manual');
  await pause(26);
  assert.deepEqual(calls, ['manual']);
  reader.dispose();
});

test('failed automatic reading preserves the cached view and offers manual retry', async () => {
  const states = [];
  let attempts = 0;
  const reader = createTicketLiveRead({
    delayMs: 3,
    read: async () => { if (++attempts === 1) throw new Error('Connexion temporaire'); return 'fresh'; },
    onState: status => states.push(status)
  });
  reader.schedule('ticket');
  await pause(12);
  assert.equal(states.at(-1), 'error');
  assert.equal(await reader.refresh('ticket'), 'fresh');
  assert.equal(states.at(-1), 'fresh');
  reader.dispose();
});

test('focus resumption while applying a successful response does not read or repaint twice', async t => {
  const started = deferred(), response = deferred(), painting = deferred(), finishPainting = deferred();
  const calls = [], views = ['cached'], states = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    onState: status => states.push(status),
    read: async (key, signal) => {
      calls.push({ key, signal });
      if (calls.length === 1) { started.resolve(); await response.promise; }
      if (signal.aborted) return;
      views.push('fresh ' + key);
      if (views.length === 2) {
        // The reader replaces its focused heading, then awaits the journal.
        // Its queued focusout handler resumes reading before draw() completes.
        queueMicrotask(() => reader.schedule(key));
        painting.resolve();
        await finishPainting.promise;
      }
      return key;
    }
  });
  t.after(() => { reader.dispose(); finishPainting.resolve(); });
  reader.schedule('A');
  await started.promise;
  response.resolve();
  await painting.promise;
  await pause(16);
  assert.deepEqual(calls.map(call => call.key), ['A']);
  assert.deepEqual(views, ['cached', 'fresh A']);
  assert.equal(calls[0].signal.aborted, false);
  finishPainting.resolve();
  await pause(1);
  assert.equal(states.at(-1), 'fresh');
  assert.equal(states.filter(state => state === 'reading').length, 1);
  reader.schedule('A');
  await pause(10);
  assert.equal(calls.length, 1);
});

test('focus resumption joins a manual read instead of cancelling its result', async t => {
  const response = deferred(), calls = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    read: async (key, signal) => { calls.push({ key, signal }); return response.promise; }
  });
  t.after(() => { reader.dispose(); response.resolve('fresh'); });
  const manual = reader.refresh('A');
  reader.schedule('A');
  await pause(12);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].signal.aborted, false);
  response.resolve('fresh');
  assert.equal(await manual, 'fresh');
});

test('navigation to another ticket still cancels an in-flight read and rejects its late freshness', async t => {
  const started = deferred(), response = deferred(), calls = [], states = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    onState: status => states.push(status),
    read: async (key, signal) => {
      calls.push({ key, signal });
      if (calls.length === 1) { started.resolve(); await response.promise; }
      return key;
    }
  });
  t.after(() => { reader.dispose(); response.resolve(); });
  reader.schedule('A');
  await started.promise;
  reader.schedule('B');
  assert.equal(calls[0].signal.aborted, true);
  await pause(12);
  assert.deepEqual(calls.map(call => call.key), ['A', 'B']);
  const completedStates = [...states];
  response.resolve();
  await pause(1);
  assert.deepEqual(states, completedStates);
  reader.schedule('A');
  await pause(12);
  assert.deepEqual(calls.map(call => call.key), ['A', 'B', 'A']);
  assert.equal(states.at(-1), 'fresh');
});

test('disabling the same ticket still aborts the active read and allows a later retry', async t => {
  const started = deferred(), response = deferred(), calls = [], states = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    onState: status => states.push(status),
    read: async (key, signal) => {
      calls.push({ key, signal });
      if (calls.length === 1) { started.resolve(); await response.promise; }
      return key;
    }
  });
  t.after(() => { reader.dispose(); response.resolve(); });
  reader.schedule('A');
  await started.promise;
  reader.schedule('A', false);
  assert.equal(calls[0].signal.aborted, true);
  response.resolve();
  await pause(10);
  assert.equal(states.at(-1), 'unavailable');
  assert.equal(calls.length, 1);
  reader.schedule('A');
  await pause(12);
  assert.equal(calls.length, 2);
  assert.equal(states.at(-1), 'fresh');
});

test('a changed source version bypasses the short recent-read delay without changing ticket identity', async t => {
  const calls = [];
  const reader = createTicketLiveRead({ delayMs: 4, read: async key => { calls.push(key); return key; } });
  t.after(() => reader.dispose());
  reader.schedule('A', true, 'modified-v1');
  await pause(12);
  reader.schedule('A', true, 'modified-v1');
  await pause(10);
  assert.deepEqual(calls, ['A']);
  reader.schedule('A', true, 'modified-v2');
  await pause(12);
  assert.deepEqual(calls, ['A', 'A']);
  reader.schedule('A', true, 'modified-v2');
  await pause(10);
  assert.equal(calls.length, 2);
});

test('a new source version cancels an older in-flight read of the same ticket', async t => {
  const started = deferred(), response = deferred(), calls = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    read: async (key, signal) => {
      calls.push({ key, signal });
      if (calls.length === 1) { started.resolve(); await response.promise; }
      return key;
    }
  });
  t.after(() => { reader.dispose(); response.resolve(); });
  reader.schedule('A', true, 'v1');
  await started.promise;
  reader.schedule('A', true, 'v2');
  assert.equal(calls[0].signal.aborted, true);
  await pause(12);
  assert.deepEqual(calls.map(call => call.key), ['A', 'A']);
  response.resolve();
  await pause(1);
  reader.schedule('A', true, 'v2');
  await pause(10);
  assert.equal(calls.length, 2);
});

test('automatic reads carry the cache hint while manual refresh remains forced', async t => {
  const calls = [];
  const reader = createTicketLiveRead({
    delayMs: 4,
    read: async (key, signal, options) => { calls.push({ key, automatic: options.automatic }); return key; }
  });
  t.after(() => reader.dispose());
  reader.schedule('A', true, 'same-version');
  await pause(12);
  assert.deepEqual(calls, [{ key: 'A', automatic: true }]);
  assert.equal(await reader.refresh('A', 'same-version'), 'A');
  assert.deepEqual(calls, [{ key: 'A', automatic: true }, { key: 'A', automatic: false }]);
});
