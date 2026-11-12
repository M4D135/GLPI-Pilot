'use strict';
// Detailed street background: the tile addresses a real Leaflet asks for, and what one
// failing view does. Tiles are synthetic and never leave the test process.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
const pattern = 'https://tile.openstreetmap.org/**';
// Same rule as EmbeddedMapsService.IsTileUrl: the host refuses every other address.
const inGrid = url => {
  const match = /^https:\/\/tile\.openstreetmap\.org\/(0|[1-9]\d*)\/(0|[1-9]\d*)\/(0|[1-9]\d*)\.png$/.exec(url); if (!match) return false;
  const [zoom, x, y] = match.slice(1).map(Number); return zoom <= 19 && x < 2 ** zoom && y < 2 ** zoom;
};
module.exports = async ({ page, reset, ok }) => {
  const size = page.viewportSize(); let requested = [], refused = [], failing = () => false;
  const tiles = route => {
    const url = route.request().url(); requested.push(url);
    if (!inGrid(url)) { refused.push(url); return route.abort(); }
    return failing() ? route.abort() : route.fulfill({ contentType: 'image/png', body: png });
  };
  await page.context().route(pattern, tiles);
  const mount = async (places, view) => {
    requested = []; refused = []; await reset();
    await page.evaluate(async ({ places, view }) => {
      const { mountMap } = await window.importProduction('/web/js/pages/maps.js'); window.streetCalls = [];
      window.streetMap = mountMap({ signal: controller.signal, api: { call: async (operation, args) => { window.streetCalls.push(`${operation}:${args.enabled}`); return { enabled: !!args.enabled }; } } }, {
        title: 'Carte synthétique', token: 'fixture', routes: [{ id: 'r', title: 'Lieux fictifs', ordered: false,
          places: places.map(([latitude, longitude], index) => ({ id: 'site:' + index, label: 'Site fictif ' + index, latitude, longitude, precision: 'Site' })) }]
      }, document.querySelector('#root'), view ? { view } : {});
    }, { places, view });
  };
  const state = text => page.waitForFunction(text => document.querySelector('.maps-state')?.textContent === text, text);
  const tilesMode = () => page.locator('.maps-canvas').getAttribute('data-tiles');
  try {
    // Places on both sides of the world on a wide screen: the fitted view is wider than the tile grid.
    await page.setViewportSize({ width: 2560, height: 1200 }); failing = () => false;
    await mount([[44, 5], [16.25, -61.55], [-21.1, 55.5]]); await state('Rues · OpenStreetMap');
    ok(requested.length > 0 && refused.length === 0 && await tilesMode() === 'online' && await page.evaluate(() => streetMap.getView().zoom) <= 3,
      `A view wider than the world requests only tiles of the real grid and keeps its streets (${refused.slice(0, 3).join(' ')})`);
    // A view remembered while the map had nothing to place is the world placeholder.
    await mount([[44, 5], [44.1, 5.1]], { center: [20, 0], zoom: 2, streets: true }); await state('Rues · OpenStreetMap');
    ok(refused.length === 0 && await page.evaluate(() => streetMap.getView().zoom) >= 10,
      'A remembered world placeholder is replaced by a view fitted to the places, with their streets');
    await mount([]);
    ok(await page.evaluate(() => streetMap.getView()) === null && requested.length === 0, 'A map with nothing to place offers no view to remember and requests no tile');
    // One tile missing: the streets stay, the local land shows through.
    await page.setViewportSize({ width: 1440, height: 900 }); let count = 0; failing = () => ++count === 1;
    await mount([[44, 5], [44.1, 5.1]]); await state('Rues · OpenStreetMap');
    ok(await tilesMode() === 'online' && await page.locator('.leaflet-tile-loaded').count() > 0 && await page.evaluate(() => streetCalls.join()) === 'maps.tiles:true',
      'A single missing tile keeps the detailed streets and their grant');
    // A first view that fails entirely is retried once without any click.
    let broken = true; failing = () => broken;
    await mount([[44, 5], [44.1, 5.1]]);
    await page.waitForFunction(() => document.querySelector('.maps-state')?.textContent === 'Chargement du fond détaillé…');
    await page.waitForTimeout(400); const firstAttempt = requested.length; broken = false; await state('Rues · OpenStreetMap');
    ok(firstAttempt > 0 && requested.length > firstAttempt && await tilesMode() === 'online' && await page.evaluate(() => streetCalls.join()) === 'maps.tiles:true',
      'A first view without any usable tile is requested again by itself and shows its streets');
    // Two failed views in a row: local map, released grant, and the wish for streets is kept.
    failing = () => true; await mount([[44, 5], [44.1, 5.1]]); await state('Fond détaillé indisponible · le plan local reste affiché');
    ok(await tilesMode() === 'offline' && await page.locator('.leaflet-tile').count() === 0 && await page.locator('.maps-marker').count() === 2
      && await page.evaluate(() => streetCalls.join()) === 'maps.tiles:true,maps.tiles:false' && await page.evaluate(() => streetMap.getView().streets) === true,
      'An unreachable detailed background restores the local map, releases its grant and stays the preferred background');
    await page.evaluate(() => controller.abort());
  } finally { await page.context().unroute(pattern, tiles); if (size) await page.setViewportSize(size); }
};
