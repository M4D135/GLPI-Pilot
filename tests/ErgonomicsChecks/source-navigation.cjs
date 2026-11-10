'use strict';

module.exports = async ({ page, reset, ok }) => {
  await reset();
  await page.evaluate(async () => {
    const { sourceCacheCard } = await window.importProduction('/web/js/source-cache.js');
    window.sourceNavigation = { calls: [], destinations: [], notices: [], refreshes: 0, fail: false };
    const fixture = sourceNavigation;
    const ctx = {
      signal: controller.signal, state: { session: { loaded: true, cachedSource: true } },
      api: { call: async operation => {
        fixture.calls.push(operation);
        if (operation === 'cache.state') return { available: true, sourceKind: 'glpi', title: 'Relevé synthétique', rows: 12,
          complete: true, extractedAt: '2026-11-10T08:00:00Z', savedAt: '2026-11-10T08:00:00Z' };
        if (operation === 'collect.run') {
          if (fixture.fail) throw Error('Échec synthétique de collecte');
          return { session: { loaded: true, rows: 14 } };
        }
        throw Error('Unexpected source operation: ' + operation);
      } },
      refresh: async () => { fixture.refreshes++; },
      navigate: async destination => { fixture.destinations.push(destination); },
      notify: message => { fixture.notices.push(message); }
    };
    document.querySelector('#root').append(await sourceCacheCard(ctx, { compact: true }));
  });
  const live = page.getByRole('button', { name: 'Revenir au GLPI Live', exact: true });
  const idle = () => page.waitForFunction(() => !document.querySelector('button[aria-busy=true]'));
  await live.click(); await idle();
  ok(await page.evaluate(() => sourceNavigation.calls.join(',') === 'cache.state,collect.run'
    && sourceNavigation.refreshes === 1 && sourceNavigation.destinations.length === 0),
  'Returning from the cached card to GLPI refreshes its owning page without opening Pilotage');
  await page.evaluate(() => { sourceNavigation.fail = true; });
  await live.click(); await idle();
  ok(await page.evaluate(() => sourceNavigation.refreshes === 1 && sourceNavigation.destinations.length === 0
    && sourceNavigation.notices.length === 1 && sourceNavigation.notices[0].includes('Échec synthétique')),
  'A failed return to GLPI keeps the cached page and reports the collection error');
  await page.getByRole('button', { name: 'Consulter le pilotage hors ligne', exact: true }).click(); await idle();
  ok(await page.evaluate(() => sourceNavigation.destinations.join(',') === 'pilot'
    && sourceNavigation.calls.filter(operation => operation === 'collect.run').length === 2),
  'The explicit cached Pilotage action still opens Pilotage without another collection');
};
