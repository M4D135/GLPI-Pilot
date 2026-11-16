'use strict';

// The actual home module plus the production stylesheets, with explicitly
// synthetic replies. The SSO bridge and source rows are never contacted here.
module.exports = async ({ page, reset, ok, capture }) => {
  async function home(sourceLoaded) {
    await reset();
    await page.evaluate(async loaded => {
      document.documentElement.dataset.theme = 'dark';
      document.documentElement.dataset.motion = 'normal';
      document.documentElement.dataset.domeniaMotion = 'on';
      const { renderHomeWorkspace } = await window.importProduction('/web/js/pages/home-workspace.js');
      const root = document.querySelector('#root');
      const state = {
        session: { loaded, rows: loaded ? 25 : 0, title: loaded ? 'Relevé de test fictif' : '', revision: 'fixture' },
        settings: { dashboard: { personalTechnician: '' } }
      };
      window.hyperRoutes = [];
      window.hyperDispose = await renderHomeWorkspace({ root, state, signal: controller.signal,
        navigate: destination => hyperRoutes.push(destination), notify: () => {},
        api: { call: async operation => {
          const replies = {
            'cache.state': { available: false },
            'workplace.day.get': { rows: [], total: 0, search: '', scope: 0, templates: {} },
            'alerts.get': { incidents: [], health: [], rules: [] },
            'workplace.overview': { metrics: [], priority: [], hasSource: loaded, revision: 'fixture' },
            'calendar.summary': { summary: {}, upcoming: [], issues: [] },
            'dashboard.business.bootstrap': { focus: [], groups: [], hasSource: loaded }
          };
          if (!(operation in replies)) throw Error('Operation outside synthetic home test: ' + operation);
          return replies[operation];
        } }
      });
    }, sourceLoaded);
    await page.waitForFunction(() => document.querySelectorAll('.home-counter').length === 4
      && !document.querySelector('.home-priority-panel .loading'));
  }

  await home(false);
  ok(await page.locator('.home-overview-heading h2').innerText() === 'Votre journée en un regard', 'The home heading tells the user what the page does');
  ok(await page.locator('.home-overview-heading .button.primary').innerText() === 'Collecter depuis GLPI', 'No-source home puts GLPI collection first');
  ok(await page.locator('.home-counter').count() === 4, 'The actual home renders the four source-derived counters without fabricated values');
  await page.getByRole('button', { name: 'Collecter depuis GLPI', exact: true }).first().click();
  ok(await page.evaluate(() => hyperRoutes.at(-1) === 'data'), 'The primary no-source action reaches collection');
  ok(await page.locator('.home-overview-heading').evaluate(node => getComputedStyle(node).backgroundImage.includes('gradient')), 'HyperMotion gradients are applied to the real home');
  await capture('hypermotion-home-dark');

  for (const [width, height] of [[1280, 800], [1366, 768], [390, 800]]) {
    await page.setViewportSize({ width, height });
    ok(await page.locator('#root').evaluate(node => node.scrollWidth <= node.clientWidth + 1), `HyperMotion home has no horizontal overflow at ${width}×${height}`);
  }
  await page.evaluate(() => { document.documentElement.dataset.theme = 'light'; });
  ok(await page.locator('.home-overview-heading').evaluate(node => getComputedStyle(node).backgroundImage.includes('gradient')), 'The same home design remains styled in light mode');
  await capture('hypermotion-home-light');
  await page.evaluate(() => controller.abort());

  await page.setViewportSize({ width: 1280, height: 800 });
  await home(true);
  ok(await page.locator('.home-overview-heading .button.primary').innerText() === 'Voir les tickets', 'Loaded home prioritizes opening tickets');
  await page.getByRole('button', { name: 'Actualiser le relevé', exact: true }).click();
  ok(await page.evaluate(() => hyperRoutes.at(-1) === 'data'), 'The loaded home keeps an explicit source-refresh action');
  await page.getByRole('button', { name: 'Voir les tickets', exact: true }).first().click();
  ok(await page.evaluate(() => hyperRoutes.at(-1) === 'records'), 'The loaded home opens the ticket workspace directly');
  await page.evaluate(() => controller.abort());
};
