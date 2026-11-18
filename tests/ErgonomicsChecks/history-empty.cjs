'use strict';

module.exports = async ({ page, reset, ok }) => {
  await reset();
  await page.evaluate(async () => {
    const { sparkline } = await window.importProduction('/web/js/pages/analytics-ui.js');
    const root = document.querySelector('#root');
    const point = (day, value, comparable = true, note = 'Même recherche et mêmes règles.', snapshotId = `snapshot-${day}`) => ({
      capturedAt: `2026-11-${String(day).padStart(2, '0')}T08:00:00Z`, value, rows: value ?? 0, comparable, complete: true, note, snapshotId
    });
    const render = (id, points, explanation) => {
      const section = document.createElement('section'); section.id = id;
      section.append(sparkline({ title: 'Stock actif', unit: 'tickets', points, explanation })); root.append(section);
    };
    render('empty-file-history', [], 'Le fichier CSV / Excel donne un état du stock. Deux collectes archivées de la même recherche GLPI sont nécessaires pour suivre son évolution.');
    render('incompatible-history', [
      point(1, null, false, 'Colonnes différentes'), point(2, null, false, 'Absence de collecte à la cadence prévue', ''),
      point(3, null, false, 'Archive incomplète ou identité/périmètre non confirmé')
    ], 'Les archives sont conservées avec leurs limites.');
    render('single-history', [point(1, null, false, 'Archive incomplète'), point(2, 0)], 'Une valeur nulle observée reste une mesure connue.');
    render('paired-history', [point(1, 12), point(2, 8)], 'Deux observations comparables.');
    render('broken-history', [point(1, 12), point(2, null, false, 'Collecte absente', ''), point(3, 8)], 'Une rupture doit rester une rupture.');
  });

  const empty = page.locator('#empty-file-history');
  ok(await empty.locator('[data-history-state=empty]').count() === 1 && await empty.locator('svg').count() === 0,
    'An unarchived source displays an explicit waiting state without an invented stock curve');
  ok(await empty.getByText('Le fichier CSV / Excel donne un état du stock.', { exact: false }).isVisible()
    && (await empty.innerText()).includes('Deux collectes archivées') && await empty.locator('details').count() === 0,
    'The CSV/Excel limitation and the next step are visible immediately instead of hidden behind a generic warning');

  const incompatible = page.locator('#incompatible-history');
  ok(await incompatible.locator('[data-history-state=incompatible]').count() === 1 && await incompatible.locator('svg').count() === 0
    && await incompatible.getByText('Relevés conservés, comparaison indisponible.', { exact: true }).isVisible()
    && await incompatible.getByText('Colonnes différentes', { exact: true }).first().isVisible()
    && await incompatible.getByText('Archive incomplète ou identité/périmètre non confirmé', { exact: true }).first().isVisible(),
    'Existing incompatible archives expose their reasons without counting cadence gaps as archives or presenting missing values as zero');
  await incompatible.locator('summary').click();
  ok(await incompatible.locator('tbody tr').count() === 3 && (await incompatible.innerText()).includes('Les archives sont conservées')
    && (await incompatible.innerText()).includes('Absence de collecte à la cadence prévue'),
    'Unavailable observations and synthetic cadence gaps remain inspectable with their dates and limitations');

  const single = page.locator('#single-history');
  ok(await single.locator('[data-history-state=single]').count() === 1 && await single.locator('svg').count() === 0
    && (await single.innerText()).includes('0 tickets') && (await single.innerText()).includes('Une deuxième observation'),
    'One comparable observed zero is displayed as a value, not as a trend');
  await single.locator('summary').click();
  ok(await single.locator('tbody tr').count() === 2 && (await single.innerText()).includes('Archive incomplète'),
    'A single usable observation does not discard its surrounding historical gaps');

  const pair = page.locator('#paired-history');
  ok(await pair.locator('[data-history-state=ready]').count() === 1 && await pair.locator('circle').count() === 2
    && await pair.locator('polyline').count() === 1,
    'Two adjacent comparable observations produce the measured stock evolution');
  const broken = page.locator('#broken-history');
  ok(await broken.locator('circle').count() === 2 && await broken.locator('polyline').count() === 0,
    'Comparable observations separated by a missing collection remain unconnected');
  await broken.locator('summary').click();
  ok(await broken.locator('tbody tr').count() === 3 && (await broken.innerText()).includes('Collecte absente'),
    'The detailed series retains every observed and missing historical point');
};
