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
