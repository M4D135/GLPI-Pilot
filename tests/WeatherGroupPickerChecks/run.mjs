import test from 'node:test';
import assert from 'node:assert/strict';
import { weatherGroupCatalog, addWeatherGroups, selectedGroupValues } from '../../web/js/weather-groups.js';
let serial = 0;
const id = () => String(++serial).padStart(32, '0');
const rule = (values, extra = {}) => ({ id: id(), kind: 'team', values, label: 'Équipe exemple', sourceKey: 'active', visible: true, orangeAbove: 7, redAbove: 11, ...extra });
const presets = [{ kind: 'team', sourceKey: 'active', orangeAbove: 10, redAbove: 20 }];

test('all observed groups are offered, including beyond the default eighteen', () => {
  const groups = Array.from({ length: 35 }, (_, i) => `Équipe ${i}`);
  assert.equal(weatherGroupCatalog(groups, []).length, 35);
  const business = { weatherRules: [] };
  addWeatherGroups(business, groups, ['group:Équipe 34'], presets, id);
  assert.deepEqual(business.weatherRules[0].values, ['Équipe 34']);
});
test('case aliases merge but spaces, accents and Unicode identity remain significant', () => {
  const entries = weatherGroupCatalog(['Service Assistance', 'service assistance', 'Service  Assistance', 'Équipe', 'Equipe', 'ß', 'SS'], []);
  assert.equal(entries.length, 6);
  assert.equal(entries[0].aliases.length, 2);
});
test('bulk addition preserves existing rules and appends exact names with preset thresholds', () => {
  const existing = rule(['Service A']); const business = { weatherRules: [existing] }; const before = JSON.stringify(existing);
  const outcome = addWeatherGroups(business, ['Service A', 'Service B', 'Service C'], ['group:Service A', 'group:Service B', 'group:Service C'], presets, id);
  assert.deepEqual(outcome, { added: 2, restored: 0 }); assert.equal(JSON.stringify(existing), before);
  assert.equal(business.weatherRules[1].orangeAbove, 10); assert.equal(business.weatherRules[2].redAbove, 20);
});
test('a hidden first matching rule is restored once without duplicates or changed thresholds', () => {
  const existing = rule(['A', 'B'], { visible: false }); const business = { weatherRules: [existing, rule(['A'])] };
  const outcome = addWeatherGroups(business, ['A', 'B'], ['group:A', 'group:B'], presets, id);
  assert.deepEqual(outcome, { added: 0, restored: 1 }); assert.equal(existing.visible, true); assert.equal(existing.orangeAbove, 7);
  assert.equal(business.weatherRules.length, 2);
});
test('capacity and stale selections fail atomically without partial additions or unmasking', () => {
  const business = { weatherRules: [rule(['A'], { visible: false }), ...Array.from({ length: 47 }, () => ({ kind: 'metric' }))] };
  const before = JSON.stringify(business);
  assert.throws(() => addWeatherGroups(business, ['A', 'B'], ['group:A', 'group:B'], presets, id), /place/);
  assert.equal(JSON.stringify(business), before);
  assert.throws(() => addWeatherGroups(business, ['A'], ['group:unknown'], presets, id), /disponible/);
  assert.equal(JSON.stringify(business), before);
});
test('editing keeps absent saved groups and rejects oversized selections', () => {
  const entries = weatherGroupCatalog(['Present'], [rule(['Absent'])]);
  assert.deepEqual(selectedGroupValues(entries, ['group:Present', 'group:Absent']), ['Present', 'Absent']);
  const many = weatherGroupCatalog(Array.from({ length: 33 }, (_, i) => `G${i}`), []);
  assert.throws(() => selectedGroupValues(many, many.map(item => item.key)), /32/);
});
