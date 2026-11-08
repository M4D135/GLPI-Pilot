'use strict';

// Local fixture checks only. This script never connects to a GLPI server or an SSO provider.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const assert = require('node:assert/strict');

const project = path.resolve(__dirname, '../..');
const rawLogPath = process.argv[2] ? path.resolve(process.argv[2]) : null;
const output = [];
let checks = 0;
let browser;

function log(message) {
  const line = String(message);
  output.push(line);
  console.log(line);
}

function ok(value, label) {
  assert.ok(value, label);
  log('PASS ' + (++checks).toString().padStart(2, '0') + ' - ' + label);
}

function equal(actual, expected, label) {
  assert.deepEqual(actual, expected, label);
  log('PASS ' + (++checks).toString().padStart(2, '0') + ' - ' + label);
}

async function run() {
  log('GLPI Pilot DOM adapter checks');
  log('Started UTC: ' + new Date().toISOString());
  log('Node: ' + process.version + ' | Playwright: ' + require('playwright/package.json').version);

  const options = {
    headless: true,
    args: process.platform === 'linux' ? ['--no-sandbox', '--disable-gpu'] : ['--disable-gpu']
  };
  if (process.env.GLPI_TEST_CHROMIUM) options.executablePath = process.env.GLPI_TEST_CHROMIUM;
  else if (process.env.GLPI_TEST_CHANNEL) options.channel = process.env.GLPI_TEST_CHANNEL;

  browser = await chromium.launch(options);
  log('Browser: ' + browser.version());
  const page = await browser.newPage();
  const script = fs.readFileSync(path.join(project, 'src/GlpiPilot/BrowserScripts/bridge.js'), 'utf8');
  const fixture = async name => {
    await page.goto(pathToFileURL(path.join(project, 'tests/fixtures', name)).href);
    await page.evaluate(script);
  };
  const inspect = () => page.evaluate(() => window.__glpiPilot.inspect());
  const read = key => page.evaluate(key => window.__glpiPilot.readTable(key), key);

  await fixture('basic-glpi.html');
  let state = await inspect();
  equal(state.tables.length, 1, 'One data table is discovered');
  equal(state.expectedTotal, 5, 'Expected total is parsed');
  equal(state.next.selector, '#next', 'Business Suivant button outside pagination is excluded');
  const first = await read(state.tables[0].key);
  equal(first.rows[0], ['', '101', 'P-MA-001', 'En service', '000123'], 'Accents, icon labels and leading zeros are preserved');
  equal(first.keys, ['Computer:101', 'Computer:102'], 'Stable row identifiers are extracted');
  await page.evaluate(script);
  equal((await inspect()).tables[0].key, state.tables[0].key, 'Bridge reinstallation is idempotent');
  await page.locator('#next').click();
  const second = await read('#search-results');
  ok(second.signature !== first.signature, 'Signature changes when page data changes');
  await page.locator('#next').click();
  const third = await read('#search-results');
  equal(third.rows.length, 1, 'Last page contains the remaining row');
  ok(third.next.disabled, 'Last-page Next control is disabled');
  equal(third.rows[0][4], '=00127', 'DOM extraction does not silently rewrite raw cell values');

  await fixture('basic-glpi.html');
  await page.evaluate(() => window.__glpiPilot.pick('next'));
  await page.locator('#next').click();
  equal(await page.locator('#page-number').innerText(), 'Page 1 sur 3', 'Visual picker suppresses the underlying action');
  equal(await page.evaluate(() => window.__glpiPilotPick.selector), '#next', 'Next picker returns the selected control');
  await page.evaluate(() => window.__glpiPilot.pick('table'));
  await page.locator('#computer-rows tr').first().click();
  equal(await page.evaluate(() => window.__glpiPilotPick.kind), 'table', 'Table picker returns the expected selection kind');
  equal(await page.evaluate(() => window.__glpiPilotPick.selector), '#search-results', 'Table picker resolves the enclosing table');
  await page.evaluate(() => window.__glpiPilot.pick('ready'));
  await page.keyboard.press('Escape');
  ok(await page.evaluate(() => window.__glpiPilotPick.cancelled), 'Escape cancels visual selection');
  equal(await page.locator('[data-glpi-pilot-ui]').count(), 0, 'Picker overlays are cleaned up');

  await fixture('complex-tables.html');
  state = await inspect();
  equal(state.tables.length, 6, 'Nested detail tables are excluded from the inventory');
  const complex = await read('#complex');
  equal(complex.headers, ['ID', 'Demande / Nom', 'Demande / Nom (2)', 'État'], 'Merged and duplicate headers become unique column names');
  equal(complex.rows[1], ['2', 'Réseau', 'Champ modifiable', 'Résolu'], 'Row spans and visible form-control values are read');
  equal(complex.rows[2][2], 'Description\nsur deux lignes', 'Line breaks are kept and nested table content is excluded');
  equal(complex.rows[0][3], 'Ouvert', 'Accessible icon label is extracted');
  equal(complex.expectedTotal, 3, 'Count is parsed next to inline navigation controls');
  ok(!JSON.stringify(complex).includes('NE-PAS-EXPORTER'), 'Password and hidden-input values are excluded');
  ok(!JSON.stringify(complex).includes('INVISIBLE'), 'Hidden columns are excluded');
  const aria = await read('#aria-grid');
  equal(aria.expectedTotal, 50, 'ARIA-grid result count is parsed');
  ok(aria.warnings.some(s => s.includes('virtualisée')), 'Potentially virtualized grids produce a warning');
  equal((await read('#empty-table')).rows.length, 0, 'Empty-state message is not exported as a record');
  equal((await read('#no-header')).rows.length, 2, 'First record is retained when headers are absent');
  ok((await read('#ambiguous')).next.ambiguous, 'Conflicting next-page destinations are ambiguous');
  ok((await read('#ambiguous-buttons')).next.ambiguous, 'Multiple unprovably equivalent Next buttons are ambiguous');

  await page.setContent('<section><table id="t"><thead><tr><th>ID</th><th>A</th><th>A</th><th>A (2)</th></tr></thead><tbody><tr><td>1</td><td>2</td><td>3</td><td>4</td></tr></tbody></table><nav class="pagination"><span>1–50 sur 1 234</span><a href="?page=2" rel="next">Suivant</a><a href="?page=2" rel="next">Suivant</a></nav></section>');
  await page.evaluate(script);
  const extra = await read('#t');
  equal(new Set(extra.headers).size, 4, 'Header suffix collisions still produce unique names');
  equal(extra.expectedTotal, 1234, 'Grouped total is parsed without an item noun');
  ok(!extra.next.ambiguous, 'Duplicated links to the same next page are accepted');

  await fixture('sso-states.html');
  state = await inspect();
  ok(state.hasPasswordField && !state.authenticatedHint, 'Authentication fixture is recognized as waiting');
  equal(state.tables.length, 0, 'Hidden application tables are excluded during authentication');
  await page.locator('#simulate').click();
  state = await inspect();
  ok(!state.hasPasswordField && state.authenticatedHint, 'Completed fixture exposes the authenticated hint');
  equal(state.tables.length, 1, 'Application table appears after simulated authentication');
  ok(!JSON.stringify(await read('#result')).includes('SECRET'), 'Authentication password never appears in extracted data');

  await fixture('grouped-ids-repeated-headers.html');
  const grouped = await read('#grouped');
  equal(grouped.keys.slice(0, 3), ['id:6071016', 'id:6071017', 'id:6071018'], 'Grouped numeric IDs produce stable keys for spaces, NBSP and narrow spaces');
  equal(grouped.rows[0][0], '6 071 016', 'Displayed ID spaces remain in exported cell values');
  equal(grouped.rows[2][0], '6\u202f071\u202f018', 'Displayed narrow spaces remain in exported cell values');
  equal(grouped.rows.length, 6, 'A repeated all-TH tbody header is excluded without dropping actual data rows');
  equal(grouped.rows.filter(row => row[0] === 'ID').length, 2, 'Header-like TD data and explicit row-header TH data are retained');
  equal(grouped.expectedTotal, 123456, 'Official data-count takes precedence over pager text');
  await page.locator('#massform').evaluate(form => form.setAttribute('data-count', '0'));
  equal((await read('#grouped')).expectedTotal, 0, 'A declared GLPI zero total is preserved');
  await page.locator('#massform').evaluate(form => form.setAttribute('data-count', '-1'));
  equal((await read('#grouped')).expectedTotal, 999, 'Invalid data-count falls back to the visible associated pager');

  log(checks + ' browser adapter checks passed.');
}

run().catch(error => {
  log('FAILED after ' + checks + ' successful checks.');
  log(error.stack || error.message || error);
  process.exitCode = 1;
}).finally(async () => {
  if (browser) await browser.close().catch(error => log('Browser cleanup: ' + error.message));
  if (rawLogPath) {
    fs.mkdirSync(path.dirname(rawLogPath), { recursive: true });
    fs.writeFileSync(rawLogPath, output.join('\n') + '\n', 'utf8');
  }
});
