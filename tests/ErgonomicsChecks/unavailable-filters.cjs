'use strict';

module.exports = async ({ page, reset, ok }) => {
  await reset();
  await page.evaluate(async () => {
    const { widgetContent } = await window.importProduction('/web/js/pages/analytics-charts.js');
    window.unavailableDestinations = [];
    const ctx = { signal: controller.signal, navigate: async page => unavailableDestinations.push(page), notify() {} };
    const root = document.querySelector('#root');
    for (const kind of ['metric', 'history', 'map', 'note']) {
      const section = document.createElement('section'); section.dataset.kind = kind;
      section.append(widgetContent(ctx, { settings: { kind, presentation: 'weather-volume', sourceKey: 'active' },
        available: false, content: null, rowCount: null,
        unavailableReason: 'Le filtre des groupes est indisponible. Vérifiez les correspondances de colonnes.' },
      () => { throw Error('Unavailable widget activated'); }, () => { throw Error('Unavailable widget expanded'); }));
      root.append(section);
    }
  });
  ok(await page.locator('.analytics-widget-unavailable').count() === 4
    && await page.getByText('Donnée indisponible', { exact: true }).count() === 4,
  'Unknown filtered metrics, history, maps and notes all show the explicit unavailable state');
  ok(await page.locator('.analytics-metric-value, .analytics-history-mount, .weather-scene').count() === 0,
    'An unavailable filtered card shows no zero count, weather scene or historical chart');
  await page.getByRole('button', { name: 'Vérifier les correspondances', exact: true }).first().click();
  ok(await page.evaluate(() => unavailableDestinations.join(',') === 'mapping'),
    'The unavailable filter card provides a working path to repair column mappings');
};
