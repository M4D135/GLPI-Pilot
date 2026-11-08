import test from 'node:test';
import assert from 'node:assert/strict';
import { createTerrainActivity, createReadyAction } from '../../web/js/terrain-activity.js';

const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const turn = async () => { await Promise.resolve(); await Promise.resolve(); };

// These scenarios use the same gates as terrain-common and terrain-map. No sleep,
// larger UI timeout, forced DOM removal or skipped dirty-draft guard is involved.
test('terrain ordinary actions continue to refuse re-entry', () => {
  const activity = createTerrainActivity();
  const release = activity.begin();
  assert.equal(typeof release, 'function');
  assert.equal(activity.begin(), null);
  release();
  const second = activity.begin();
  assert.equal(typeof second, 'function');
  release();
  assert.equal(activity.begin(), null, 'an old release must not unlock a newer activity');
  second();
});

test('terrain lifecycle requests wait for the current activity', async () => {
  const activity = createTerrainActivity();
  const release = activity.begin();
  let called = 0;
  const waiting = activity.afterCurrent(() => { called++; return 'closed'; });
  await turn(); assert.equal(called, 0);
  release();
  assert.equal(await waiting, 'closed'); assert.equal(called, 1);
});

test('terrain page cancellation does not replay a queued close on another page', async () => {
  const abort = new AbortController(), activity = createTerrainActivity(abort.signal);
  const release = activity.begin();
  let called = false;
  const waiting = activity.afterCurrent(() => { called = true; });
  abort.abort(); release(); await waiting;
  assert.equal(called, false); assert.equal(activity.begin(), null);
});

test('closing during a module import waits for the renderer and its cleanup', async () => {
  const activity = createTerrainActivity();
  const finishOpening = activity.begin();
  let cleanup = null, closed = false;
  const close = createReadyAction(() => activity.afterCurrent(async () => {
    const release = activity.begin(); assert.ok(release);
    try { assert.equal(typeof cleanup, 'function'); await cleanup(); closed = true; }
    finally { release(); }
  }));
  const pending = close.request(); await turn(); assert.equal(closed, false);
  let cleaned = 0;
  cleanup = () => { cleaned++; };
  close.settled(); await turn(); assert.equal(closed, false, 'the parent opening action still owns the lock');
  finishOpening(); await pending;
  assert.equal(closed, true); assert.equal(cleaned, 1);
});

test('a visible panel outside an activity still waits for its renderer', async () => {
  let closed = false;
  const close = createReadyAction(() => { closed = true; });
  const pending = close.request(); await turn(); assert.equal(closed, false);
  close.settled(); await pending; assert.equal(closed, true);
});

test('repeated close and Escape requests share one guard, cleanup and refresh', async () => {
  const guard = deferred();
  let checked = 0, cleaned = 0, refreshed = 0;
  const close = createReadyAction(async () => { checked++; await guard.promise; cleaned++; refreshed++; });
  const first = close.request(), second = close.request();
  assert.equal(first, second);
  close.settled(); await turn(); assert.equal(checked, 1);
  assert.equal(close.request(), first); assert.equal(cleaned, 0);
  guard.resolve(); await first;
  assert.equal(cleaned, 1); assert.equal(refreshed, 1);
});

test('a rejected dirty-draft guard keeps the draft and allows an explicit retry', async () => {
  const activity = createTerrainActivity();
  let reject = true, closed = false, cleaned = 0;
  const draft = { site: 'fictitious-site', note: 'Unsaved fictional note' };
  const original = structuredClone(draft);
  const close = createReadyAction(() => activity.afterCurrent(async () => {
    const release = activity.begin(); assert.ok(release);
    try {
      if (reject) throw new Error('keep draft');
      cleaned++; closed = true;
    } finally { release(); }
  }));
  close.settled();
  await assert.rejects(close.request(), /keep draft/);
  assert.equal(closed, false); assert.equal(cleaned, 0); assert.deepEqual(draft, original);
  reject = false; await close.request();
  assert.equal(closed, true); assert.equal(cleaned, 1); assert.deepEqual(draft, original);
});

test('a renderer failure can settle a pending request without closing an already removed panel twice', async () => {
  let finished = false, cleaned = 0;
  const close = createReadyAction(() => { if (!finished) { finished = true; cleaned++; } });
  const pending = close.request();
  // Mirrors the renderer catch/finally: remove its failed panel, then release readiness.
  finished = true; cleaned++; close.settled();
  await pending; assert.equal(cleaned, 1);
});

test('a panel that was already rendered closes without a second render', async () => {
  let closes = 0;
  const close = createReadyAction(() => { closes++; });
  close.settled(); close.settled();
  await close.request(); assert.equal(closes, 1);
});

test('lifecycle waiters recheck the activity instead of losing a request behind another waiter', async () => {
  const activity = createTerrainActivity(), releaseOpening = activity.begin(), holdFirst = deferred();
  const order = [];
  const first = activity.afterCurrent(async () => {
    const release = activity.begin(); assert.ok(release);
    try { order.push('first'); await holdFirst.promise; } finally { release(); }
  });
  const second = activity.afterCurrent(async () => {
    const release = activity.begin(); assert.ok(release);
    try { order.push('second'); } finally { release(); }
  });
  releaseOpening(); await turn(); assert.deepEqual(order, ['first']);
  holdFirst.resolve(); await Promise.all([first, second]);
  assert.deepEqual(order, ['first', 'second']);
});
