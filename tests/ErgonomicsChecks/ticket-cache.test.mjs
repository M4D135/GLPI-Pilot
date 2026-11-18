import assert from 'node:assert/strict';
import test from 'node:test';
import { ticketCacheView } from '../../web/js/ticket-cache.js';

const now = Date.parse('2026-11-18T12:00:00Z');
const verified = { state: 'current', hasContent: true, autoRead: false,
  sourceModifiedAt: '2026-11-18T09:00:00Z', cachedModifiedAt: '2026-11-18T09:00:00Z',
  readAt: '2026-11-18T11:00:00Z', sourceObservedAt: '2026-11-18T10:00:00Z' };

test('only a confirmed absent copy is red; omitted, failed or contradictory metadata stays unknown', () => {
  assert.equal(ticketCacheView({ state: 'missing', hasContent: false }, now).tone, 'missing');
  for (const cache of [undefined, {}, { state: 'unknown', reason: 'Cache inaccessible' }, { state: 'missing', hasContent: true }]) {
    const view = ticketCacheView(cache, now);
    assert.equal(view.tone, 'unknown');
    assert.doesNotMatch(view.label, /absent|à jour/i);
  }
});

test('a recent verified copy describes the loaded observation, not real-time GLPI freshness', () => {
  const view = ticketCacheView(verified, now);
  assert.equal(view.tone, 'current');
  assert.match(view.label, /version conforme au relevé/);
  assert.match(view.description, /Relevé observé le/);
  assert.doesNotMatch(view.description, /à jour/);
});

test('an old unchanged copy turns orange without altering the backend decision to skip rereading', () => {
  const cache = { ...verified, sourceModifiedAt: '2026-11-10T09:00:00Z', cachedModifiedAt: '2026-11-10T09:00:00Z', readAt: '2026-11-10T11:00:00Z' }, before = structuredClone(cache);
  const view = ticketCacheView(cache, now);
  assert.equal(view.tone, 'warning');
  assert.match(view.label, /Cache ancien.*version inchangée/);
  assert.deepEqual(cache, before);
  assert.equal(cache.autoRead, false);
});

test('a recent read without a reliable modification date stays visibly unverified', () => {
  const view = ticketCacheView({ state: 'recent', hasContent: true, readAt: verified.readAt }, now);
  assert.equal(view.tone, 'warning');
  assert.match(view.label, /Lu récemment.*version à vérifier/);
  assert.equal(view.dates[0].value, 'non connue');
  assert.doesNotMatch(view.description, /à jour/);
});

test('unknown source dates, read dates and future clocks cannot produce a green recent claim', () => {
  for (const patch of [{ sourceModifiedAt: null }, { sourceModifiedAt: 'non reconnue' }, { readAt: null }, { readAt: '2026-11-19T12:00:00Z' }]) {
    const view = ticketCacheView({ ...verified, ...patch }, now);
    assert.equal(view.tone, 'warning');
    assert.doesNotMatch(view.label, /récent|à jour/);
  }
});

test('source and cached modification dates remain distinguishable when a change was detected', () => {
  const view = ticketCacheView({ ...verified, state: 'stale', cachedModifiedAt: '2026-11-17T09:00:00Z', reason: 'Modification détectée dans le relevé.' }, now);
  assert.equal(view.tone, 'warning');
  assert.equal(view.dates[0].label, 'Dernière modification (relevé)');
  assert.match(view.description, /Modification détectée/);
  assert.match(view.description, /Version de la fiche en cache/);
});

test('known raw dates and an older cached modification are identified without inventing a timestamp', () => {
  const raw = ticketCacheView({ state: 'unknown', sourceModifiedRaw: '18/11/2026 (heure non fournie)' }, now);
  assert.equal(raw.dates[0].value, '18/11/2026 (heure non fournie)');
  assert.equal(raw.dates[1].value, 'non connue');
  const cached = ticketCacheView({ state: 'unknown', cachedModifiedAt: verified.cachedModifiedAt, readAt: verified.readAt }, now);
  assert.equal(cached.dates[0].label, 'Dernière modification (fiche en cache)');
  assert.notEqual(cached.dates[0].value, 'non connue');
});
