'use strict';

// Exercise the shared SVG outside .analytics-widget: Settings and the animation
// dialog used to lose their cloud palette and silently fall back to SVG black.
module.exports = async ({ page, reset, ok, capture }) => {
  const originalSize = page.viewportSize();
  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await reset();
    await page.evaluate(async () => {
      const { createMotionPreview, createMotionButton } = await window.importProduction('/web/js/motion-controls.js');
      const { weatherScene } = await window.importProduction('/web/js/pages/weather-scene.js');
      const { el, card } = await window.importProduction('/web/js/ui.js');
      const root = document.documentElement;
      Object.assign(root.dataset, { theme: 'dark', contrast: 'false', motion: 'reduce', domeniaMotion: 'off' });
      const state = { settings: { interface: { reduceMotion: true, followWindowsMotion: false } } };
      window.weatherPreview = createMotionPreview(state);
      window.weatherButtons = createMotionButton({ state, signal: controller.signal,
        api: { call: async () => { throw Error('No network or settings mutation expected.'); } } });
      const main = document.querySelector('#root');
      main.replaceChildren(card('Affichage et animations', weatherPreview.element, weatherButtons.element));
      const samples = el('div', { class: 'workspace-settings-grid', 'data-weather-samples': '' });
      for (const value of ['sun', 'watch', 'rain', 'storm', 'partial', 'unknown']) {
        const standalone = weatherScene(value), widget = weatherScene(value);
        standalone.dataset.paletteFixture = 'standalone'; widget.dataset.paletteFixture = 'widget';
        samples.append(card(value, el('div', { class: 'motion-preview-illustration' }, standalone),
          el('div', { class: 'analytics-widget' }, widget)));
      }
      main.append(samples);
      // Inspect the actual SVG paints, not just the presence of CSS variables.
      window.inspectWeatherPaints = selector => [...document.querySelectorAll(selector)].map(scene => {
        const style = getComputedStyle(scene);
        const read = (selector, property) => [...scene.querySelectorAll(selector)].map(node => getComputedStyle(node)[property]);
        return { state: scene.dataset.weatherScene, context: scene.dataset.paletteFixture || 'preview',
          stops: read('linearGradient[id$="-cloud"] stop', 'stopColor'),
          far: read('.pilot-weather-cloud-far:not(.pilot-weather-fair-cloud) path', 'fill'),
          edge: read('.pilot-weather-cloud-near > path:first-child, .pilot-weather-fair-cloud > path', 'stroke'),
          highlight: read('.pilot-weather-cloud-near > path:nth-child(2)', 'stroke'),
          rays: read('.pilot-weather-rays', 'stroke'), rain: read('.pilot-weather-rain', 'stroke'),
          neutral: read('.pilot-weather-neutral text', 'fill').concat(read('.pilot-weather-mist', 'stroke')),
          animation: read('.pilot-weather-cloud-near', 'animationName'),
          cloudLight: style.getPropertyValue('--weather-cloud-light').trim() };
      });
    });
    const usable = value => value && value !== 'none' && value !== 'transparent'
      && value !== 'rgb(0, 0, 0)' && value !== 'rgba(0, 0, 0, 0)';
    const paints = row => [...row.stops, ...row.far, ...row.edge, ...row.highlight, ...row.rays, ...row.rain, ...row.neutral];

    const preview = (await page.evaluate(() => inspectWeatherPaints('.motion-preview .pilot-weather-scene')))[0];
    ok(preview.cloudLight === '', 'Settings preview reproduces the non-widget context with no inherited cloud token');
    ok(preview.stops.length === 2 && preview.stops.every(usable) && preview.stops[0] !== preview.stops[1],
      'Dark Settings preview has a visible cloud gradient instead of solid SVG black');
    ok([...preview.far, ...preview.edge, ...preview.highlight, ...preview.rays].every(usable),
      'Dark Settings preview retains the rear cloud, outline, highlight and sun rays');
    await page.locator('[data-weather-samples]').evaluate(node => { node.hidden = true; });
    await capture('settings-clouds-dark');
    await page.getByRole('button', { name: 'Régler les animations', exact: true }).click();
    const dialog = (await page.evaluate(() => inspectWeatherPaints('.motion-dialog .pilot-weather-scene')))[0];
    ok(dialog.stops.every(usable) && dialog.stops.join() === preview.stops.join(),
      'Animation dialog uses the same visible dark cloud palette as Settings');
    await capture('settings-clouds-dialog-dark');
    await page.keyboard.press('Escape');
    await page.locator('dialog').waitFor({ state: 'detached' });
    await page.locator('[data-weather-samples]').evaluate(node => { node.hidden = false; });

    for (const theme of ['dark', 'light', 'contrast']) {
      await page.evaluate(theme => {
        document.documentElement.dataset.theme = theme;
        document.documentElement.dataset.contrast = String(theme === 'contrast');
      }, theme);
      const rows = await page.evaluate(() => inspectWeatherPaints('[data-palette-fixture]'));
      for (const row of rows) {
        // Contrast themes deliberately use the black surface for the cloud's
        // inner highlight. The edge and gradient must still remain visible.
        const required = theme === 'contrast'
          ? [...row.stops, ...row.far, ...row.edge, ...row.rays, ...row.rain, ...row.neutral] : paints(row);
        ok(required.every(usable), `${theme}: ${row.state} paints stay visible in ${row.context}`);
      }
      const widget = rows.find(row => row.state === 'watch' && row.cloudLight);
      ok(widget && widget.stops[0] === (theme === 'light' ? 'rgb(255, 255, 255)' : 'rgb(242, 250, 255)'),
        `${theme}: existing widget theme tokens still override the fallback palette`);
      ok(await page.evaluate(() => [...document.querySelectorAll('.pilot-weather-scene')].every(scene =>
        scene.getAttribute('aria-hidden') === 'true' && scene.getAttribute('focusable') === 'false')),
        `${theme}: decorative scenes remain outside the accessibility and keyboard navigation trees`);
      await page.locator('[data-weather-samples]').evaluate(node => { node.hidden = true; });
      await capture(`settings-clouds-${theme}`);
      await page.locator('[data-weather-samples]').evaluate(node => { node.hidden = false; });
    }

    await page.evaluate(() => Object.assign(document.documentElement.dataset, { theme: 'dark', contrast: 'false', motion: 'normal', domeniaMotion: 'on' }));
    ok(await page.evaluate(() => inspectWeatherPaints('[data-palette-fixture="standalone"][data-weather-scene="watch"]')[0].animation.includes('pilot-weather-cloud-near')),
      'Expressive mode still animates the cloud component');
    await page.evaluate(() => Object.assign(document.documentElement.dataset, { motion: 'reduce', domeniaMotion: 'off' }));
    ok(await page.evaluate(() => inspectWeatherPaints('[data-palette-fixture="standalone"][data-weather-scene="watch"]')[0].animation.every(value => value === 'none')),
      'Reduced motion keeps a complete static cloud instead of hiding it');
    ok(await page.evaluate(() => { const ids = [...document.querySelectorAll('.pilot-weather-scene [id]')].map(node => node.id); return new Set(ids).size === ids.length; }),
      'Multiple previews and widgets retain distinct gradient and clip identifiers');

    await page.emulateMedia({ forcedColors: 'active' });
    ok(await page.evaluate(() => {
      const scene = document.querySelector('.motion-preview .pilot-weather-scene');
      return getComputedStyle(scene).getPropertyValue('--weather-cloud-light').trim() === 'Canvas'
        && getComputedStyle(scene.querySelector('.pilot-weather-cloud-near')).filter === 'none';
    }), 'Windows forced colors keep the system cloud palette and remove decorative shadows');
    await page.emulateMedia({ forcedColors: 'none' });

    await page.locator('[data-weather-samples]').evaluate(node => { node.hidden = true; });
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 800 });
      ok(await page.evaluate(() => document.querySelector('#root').scrollWidth <= innerWidth + 1),
        `Settings animation preview remains inside the ${width}px viewport`);
    }
    await capture('settings-clouds-mobile-dark');
  } finally {
    await page.emulateMedia({ forcedColors: 'none' });
    await page.evaluate(() => { window.weatherPreview?.dispose(); window.weatherButtons?.dispose(); });
    if (originalSize) await page.setViewportSize(originalSize);
  }
};
