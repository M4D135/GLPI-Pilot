'use strict';
// Regression from the two real screenshots: identical widgets must occupy the
// same rectangles before, during and after composition, with production CSS.
module.exports = async ({ page, reset, ok, capture }) => {
  await reset();
  await page.evaluate(async () => {
    const { bindWorkspaceGrid } = await window.importProduction('/web/js/pages/workspace-grid-ui.js');
    const { widgetContent } = await window.importProduction('/web/js/pages/analytics-charts.js');
    const { el, button, toolbar } = await window.importProduction('/web/js/pages/analytics-ui.js');
    const root = document.querySelector('#root'); root.classList.add('pilot-workspace');
    document.documentElement.dataset.theme = 'light';
    const metrics = [['active', 'Tickets en cours', 868], ['active-incidents', 'Incidents', 311],
      ['active-requests', 'Demandes', 557], ['active', 'En cours · attribués', 586],
      ['new', 'Nouveaux', 10], ['active', 'En cours · planifiés', 21]];
    window.gridBoard = { widgets: metrics.map(([key, title], i) => ({ id: `card-${i}`, kind: 'metric',
      sourceKey: key, title, span: 4, height: 320, autoHeight: false, section: 'stock',
      showDescription: true, showMiniHistory: false, accent: 'blue', presentation: 'compact' })) };
    window.gridWrites = 0;
    window.drawGridFixture = (editing, autoHeight = false, positioned = false) => {
      window.fixtureGrid?.dispose(); root.replaceChildren(); root.dataset.layoutEditing = String(editing);
      const grid = el('div', { class: 'pilot-widget-grid analytics-widget-grid' });
      const host = el('div', { class: 'pilot-composition' }, el('section', { class: 'pilot-block', dataset: { sectionKey: 'stock' } },
        el('h2', { text: 'Comprendre le stock' }), el('div', { class: 'pilot-grid-viewport' }, grid)));
      root.append(host);
      for (const [i, setting] of gridBoard.widgets.entries()) {
        setting.autoHeight = autoHeight;
        if (positioned) { setting.gridX = (i % 3) * 4; setting.gridY = i < 3 ? 3 : 24; }
        else { delete setting.gridX; delete setting.gridY; }
        const content = { key: metrics[i][0], value: metrics[i][2], count: metrics[i][2], sample: 868,
          unit: 'tickets', explanation: 'Définition synthétique du contrôle de disposition.' };
        const node = el('article', { class: 'analytics-widget pilot-widget analytics-kpi-card accent-blue',
          dataset: { widgetId: setting.id, autoHeight: String(autoHeight), widgetKind: 'metric' },
          style: { '--widget-height': `${setting.height}px`, '--span': setting.span } },
          el('div', { class: 'analytics-widget-heading' }, el('h3', { text: setting.title }),
            toolbar(button(editing ? 'Régler' : 'Détail', () => {}))),
          widgetContent({}, { id: setting.id, settings: setting, content, rowCount: 868 }, () => {}, () => {}));
        grid.append(node);
      }
      window.fixtureGrid = bindWorkspaceGrid({ host, getBoard: () => gridBoard, canEdit: () => editing,
        persist: () => { window.gridWrites++; }, notify: message => { throw Error(message); }, signal: controller.signal });
    };
    window.gridRects = () => {
      const grid = document.querySelector('.pilot-widget-grid'), parent = grid.getBoundingClientRect();
      return [...grid.children].filter(n => n.matches('.pilot-widget')).map(n => {
        const r = n.getBoundingClientRect(); return { x: r.x - parent.x, y: r.y - parent.y, width: r.width, height: r.height };
      });
    };
  });
  const settle = async () => {
    await page.waitForSelector('[data-grid-ready=true]');
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  for (const width of [1440, 1024, 850, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const auto of [false, true]) for (const positioned of [false, true]) {
      await page.evaluate(({ auto, positioned }) => drawGridFixture(false, auto, positioned), { auto, positioned }); await settle();
      const before = await page.evaluate(() => gridRects());
      const saved = await page.evaluate(() => JSON.stringify(gridBoard));
      await page.evaluate(({ auto, positioned }) => drawGridFixture(true, auto, positioned), { auto, positioned }); await settle();
      const editing = await page.evaluate(() => gridRects());
      await page.evaluate(({ auto, positioned }) => drawGridFixture(false, auto, positioned), { auto, positioned }); await settle();
      const after = await page.evaluate(() => gridRects());
      const equal = rects => before.every((rect, i) => Object.keys(rect).every(key => Math.abs(rect[key] - rects[i][key]) < 1));
      const matches = equal(editing) && equal(after);
      ok(matches, `Reading/editing/validated rectangles agree at ${width}px, auto=${auto}, positioned=${positioned}${matches ? '' : ': ' + JSON.stringify({ before, editing, after })}`);
      ok(await page.evaluate(saved => JSON.stringify(gridBoard) === saved && gridWrites === 0, saved), 'Mode and viewport changes do not save or rewrite the composition');
      ok(after.every((a, i) => after.slice(i + 1).every(b => a.x + a.width <= b.x + 1 || b.x + b.width <= a.x + 1 || a.y + a.height <= b.y + 1 || b.y + b.height <= a.y + 1)), 'Responsive widgets remain non-overlapping');
      if (!positioned && width >= 1024) ok(Math.abs(after[3].y - after[4].y) < 1 && Math.abs(after[4].y - after[5].y) < 1, 'The second default row starts on one baseline despite different indicator content');
    }
    ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Editing and reading stay inside the page at ${width}px`);
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.evaluate(() => drawGridFixture(false, true)); await settle();
  const natural = await page.locator('[data-widget-id=card-0]').evaluate(node => node.getBoundingClientRect().height);
  await page.evaluate(() => {
    const more = document.createElement('p'); more.dataset.lateContent = '';
    more.textContent = 'Complément chargé après le premier rendu. '.repeat(30);
    document.querySelector('[data-widget-id=card-0]>.analytics-widget-body').append(more);
  });
  await page.waitForFunction(height => document.querySelector('[data-widget-id=card-0]').getBoundingClientRect().height > height, natural);
  ok(await page.locator('[data-widget-id=card-0]').evaluate(node => node.getBoundingClientRect().height === 324), 'Late content grows an automatic card up to its saved cap');
  await page.evaluate(() => document.querySelector('[data-late-content]').remove());
  await page.waitForFunction(height => document.querySelector('[data-widget-id=card-0]').getBoundingClientRect().height === height, natural);
  ok(await page.evaluate(() => gridWrites === 0), 'Removing late content restores the automatic height without saving');
  await page.evaluate(async () => {
    fixtureGrid.dispose();
    const { bindWorkspaceGrid } = await window.importProduction('/web/js/pages/workspace-grid-ui.js');
    const { widgetContent } = await window.importProduction('/web/js/pages/analytics-charts.js');
    const { el, button, toolbar } = await window.importProduction('/web/js/pages/analytics-ui.js');
    const root = document.querySelector('#root'); root.replaceChildren(); root.dataset.layoutEditing = 'false';
    const setting = { id: 'weather', kind: 'metric', sourceKey: 'active', span: 12, height: 240, autoHeight: false,
      presentation: 'weather-volume', showDescription: true, warningValue: 10, criticalValue: 20, section: 'weather' };
    const body = widgetContent({}, { id: 'weather', settings: setting, rowCount: 84,
      weatherContext: { reason: 'Seuils de l’équipe Support ; les mêmes tickets actifs composent le périmètre et cette règle.' },
      content: { key: 'active', value: 84, count: 84, sample: 84, unit: 'tickets', explanation: 'Périmètre synthétique du contrôle.',
        volumeWeather: { state: 'rain', title: 'Sous tension', explanation: 'Les seuils sont ceux de l’équipe choisie.' } } }, () => {}, () => {});
    const article = el('article', { class: 'analytics-widget pilot-widget business-weather-hero', dataset: { widgetId: 'weather', autoHeight: 'false' } },
      el('div', { class: 'analytics-widget-heading' }, el('h3', { text: 'Support · tickets en cours' }), toolbar(button('Détail', () => {}))), body);
    const grid = el('div', { class: 'pilot-widget-grid' }, article);
    const host = el('div', { class: 'pilot-composition' }, el('section', { dataset: { sectionKey: 'weather' } }, el('div', { class: 'pilot-grid-viewport' }, grid)));
    root.append(host);
    window.fixtureGrid = bindWorkspaceGrid({ host, getBoard: () => ({ widgets: [setting] }), canEdit: () => false,
      persist: () => { throw Error('Unexpected save'); }, notify: () => {}, signal: controller.signal });
  });
  await settle();
  ok(await page.locator('[data-widget-id=weather]').evaluate(node => node.getBoundingClientRect().height === 244
    && (node.querySelector('.analytics-widget-body').scrollHeight <= node.querySelector('.analytics-widget-body').clientHeight + 1
      || node.dataset.readingLimited === 'true' && node.querySelector('.workspace-reading-caption').textContent.includes('Aperçu'))),
  'A saved small weather card keeps its exact lattice height and explicitly identifies a limited preview');
  await page.locator('[data-widget-expand=weather]').click();
  const track = page.locator('dialog .pilot-weather-thresholds'); await track.scrollIntoViewIfNeeded();
  ok(await track.evaluate(node => {
    const r = node.getBoundingClientRect(), viewport = node.closest('.modal-body').getBoundingClientRect(), body = node.closest('.analytics-widget-body');
    return node.textContent.includes('Vigilance > 10') && node.textContent.includes('Tension > 20')
      && r.top >= viewport.top - 1 && r.bottom <= viewport.bottom + 1
      && body.scrollHeight <= body.clientHeight + 1 && getComputedStyle(body).overflowY === 'visible';
  }), 'Enlargement exposes the exact weather thresholds completely without a nested scrollbar');
  await page.keyboard.press('Escape');
  ok(await page.locator('[data-widget-id=weather]').evaluate(node => node.getBoundingClientRect().height === 244), 'Closing full weather reading restores the same chosen card height');
  await page.evaluate(() => drawGridFixture(true)); await settle(); await capture('workspace-grid-aligned-editing');
  await page.evaluate(() => drawGridFixture(false)); await settle(); await capture('workspace-grid-aligned-reading');
  await page.evaluate(() => controller.abort());
};
