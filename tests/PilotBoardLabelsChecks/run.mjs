import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../../web/js/pages/pilot-dashboards.js', import.meta.url), 'utf8');
const helper = source.match(/function customBoardLabel\(board\) \{[\s\S]*?\n\}/)?.[0];
assert.ok(helper, 'The display-only board label helper must remain present.');
const label = runInNewContext(`${helper}\ncustomBoardLabel`);

test('legacy default board has an explicit display label without migration', () => {
  assert.equal(label({ name: 'Pilotage' }), 'Tableau personnalisé');
  assert.equal(label({ name: '' }), 'Tableau personnalisé');
  assert.equal(label(null), 'Tableau personnalisé');
  assert.ok(source.includes("const serviceBoardLabel = 'Vue d’ensemble du service';"));
});
test('custom board names retain spaces and accents in the display', () => {
  assert.equal(label({ name: 'Revue équipe' }), 'Personnalisé · Revue équipe');
  assert.equal(label({ name: 'Suivi  hebdomadaire' }), 'Personnalisé · Suivi  hebdomadaire');
});
test('labels leave a saved configuration and its internal board IDs untouched', () => {
  const settings = { boards: [
    { id: 'saved-default-id', name: 'Pilotage', widgets: [{ id: 'card', warningValue: 7, criticalValue: 11 }] },
    { id: 'saved-custom-id', name: 'Revue équipe', filters: [{ values: ['Groupe exemple'] }] }
  ] };
  const before = JSON.stringify(settings);
  settings.boards.forEach(label);
  assert.equal(JSON.stringify(settings), before);
});
