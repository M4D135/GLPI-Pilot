'use strict';

// Every HTTP request is intercepted and fulfilled from a synthetic local fixture.
// These checks do not contact a GLPI server, a user account, or an SSO provider.
// CI may reuse the exact driver package already restored by Microsoft.Playwright,
// avoiding a second dependency/runtime version just for the DOM fixtures.
const playwrightModule = process.env.GLPI_TEST_PLAYWRIGHT_MODULE || 'playwright';
const { chromium } = require(playwrightModule);
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const project = path.resolve(__dirname, '../..');
const rawLogPath = process.argv[2] ? path.resolve(process.argv[2]) : null;
const output = [];
let checks = 0;
let browser;
function log(message) { output.push(String(message)); console.log(message); }
function equal(actual, expected, label) { assert.deepEqual(actual, expected, label); log('PASS ' + (++checks).toString().padStart(2, '0') + ' - ' + label); }
function ok(value, label) { assert.ok(value, label); log('PASS ' + (++checks).toString().padStart(2, '0') + ' - ' + label); }

async function run() {
  log('GLPI Pilot native export DOM checks');
  log('Started UTC: ' + new Date().toISOString());
  log('Node: ' + process.version + ' | Playwright: ' + require(playwrightModule + '/package.json').version);
  const options = { headless: true, args: process.platform === 'linux' ? ['--no-sandbox', '--disable-gpu'] : ['--disable-gpu'] };
  if (process.env.GLPI_TEST_CHROMIUM) options.executablePath = process.env.GLPI_TEST_CHROMIUM;
  else if (process.env.GLPI_TEST_CHANNEL) options.channel = process.env.GLPI_TEST_CHANNEL;
  browser = await chromium.launch(options);
  log('Browser: ' + browser.version());
  const page = await browser.newPage({ acceptDownloads: true });
  const requests = [];
  const displayRequests = [];
  await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== 'http://glpi-native.test') return route.abort();
    if (url.pathname === '/glpi/front/report.dynamic.php') {
      requests.push({ method: request.method(), params: Object.fromEntries(url.searchParams) });
      return route.fulfill({ status: 200, headers: { 'content-type': 'text/csv; charset=utf-8', 'content-disposition': 'attachment; filename="fixture-all.csv"' }, body: '\uFEFF"ID";"Nom"\r\n"01";"Ordinateur"\r\n' });
    }
    if (url.pathname === '/glpi/front/ticket.php') {
      let body = fs.readFileSync(path.join(project, 'tests/fixtures/native-legacy-realistic.html'), 'utf8');
      if (request.method() === 'POST') {
        const params = new URLSearchParams(request.postData() || '');
        displayRequests.push({ method: request.method(), limit: params.get('glpilist_limit') });
        if (Number(params.get('glpilist_limit')) >= 828) body = body.replace('renderLegacyRows(20, 828);', 'renderLegacyRows(828, 828);');
      }
      return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body });
    }
    const name = path.basename(url.pathname);
    if (['native-glpi10.html', 'native-glpi11.html', 'native-legacy.html'].includes(name)) {
      return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: fs.readFileSync(path.join(project, 'tests/fixtures', name), 'utf8') });
    }
    return route.fulfill({ status: 404, body: 'Unknown synthetic fixture' });
  });
  const script = fs.readFileSync(path.join(project, 'src/GlpiPilot/BrowserScripts/native-export.js'), 'utf8');
  const fixture = async name => { await page.goto('http://glpi-native.test/fixtures/' + name); await page.evaluate(script); };
  const legacyFixture = async () => { await page.goto('http://glpi-native.test/glpi/front/ticket.php'); await page.evaluate(script); };
  const inspect = selector => page.evaluate(selector => window.__glpiPilotNative.inspect(selector), selector);
  const downloadClick = async selector => {
    const pending = page.waitForEvent('download');
    await page.locator(selector).click();
    const download = await pending;
    equal(await download.failure(), null, 'Native browser download completes without failure');
    return download;
  };

  await fixture('native-glpi10.html');
  const before = await page.locator('html').innerHTML();
  let state = await inspect();
  equal(await page.locator('html').innerHTML(), before, 'Inspection does not mutate the page DOM');
  equal(await page.evaluate(() => window.menuClicks), 0, 'Inspection never clicks the export toggle');
  equal(state.tableSelector, '#search_tickets', 'Official search-results table wins over layout tables');
  equal(state.expectedTotal, 1234, 'Total is read from GLPI data-count, not visible row count');
  equal(state.plans.length, 1, 'Exactly one reviewed plan is returned');
  equal(state.plans[0].kind, 'menu', 'Hidden export menu must be opened through the browser');
  ok(state.plans[0].allPages && state.plans[0].needsOpen, 'Hidden native all-pages CSV is proven but still needs opening');
  await page.locator(state.plans[0].menuSelector).click();
  state = await inspect();
  equal(state.plans[0].kind, 'direct', 'Open menu reinspection produces a direct action');
  equal(state.plans[0].directSelector, '#all-csv', 'GLPI 10 chooses all-pages CSV, excluding current page and PDF');
  await downloadClick(state.plans[0].directSelector);
  equal(requests.at(-1).params.display_type, '-4', 'Download uses the existing all-pages display_type');
  equal(await page.evaluate(() => window.businessSubmits), 0, 'Export link does not submit the surrounding massive-action form');
  await page.evaluate(script);
  equal((await inspect()).plans[0].directSelector, '#all-csv', 'Adapter reinstallation is idempotent');

  await fixture('native-glpi11.html');
  state = await inspect('#search_computers');
  equal(state.containerSelector, '#computer-card', 'GLPI 11 scope includes the export controls outside the massive-action form');
  equal(state.expectedTotal, 200, 'GLPI 11 total remains attached to the chosen table form');
  await page.locator(state.plans[0].menuSelector).click();
  state = await inspect('#search_computers');
  equal(state.plans[0].directSelector, '#all-csv', 'Two links labelled CSV are disambiguated by the All pages section');
  equal(await page.locator('#export-menu').evaluate(el => el.parentElement.localName), 'body', 'Detached GLPI 11 menu is associated through aria-labelledby');
  await downloadClick(state.plans[0].directSelector);
  equal(requests.at(-1).params.item_type, 'Computer', 'Detached menu retains the existing table export destination');
  equal(await page.evaluate(() => window.businessSubmits), 0, 'GLPI 11 export never submits the massive-action form');

  await fixture('native-legacy.html');
  state = await inspect('#legacy-table');
  equal(state.plans[0].kind, 'form', 'Legacy native GET export form is detected');
  equal(state.plans[0].formatValue, '-4', 'All-pages legacy option value comes from the rendered select');
  equal(state.expectedTotal, null, 'Unproven total stays unknown');
  const legacyPlan = state.plans[0];
  await page.locator(legacyPlan.formatSelector).selectOption(legacyPlan.formatValue);
  await downloadClick(legacyPlan.submitSelector);
  equal(requests.at(-1).method, 'GET', 'Legacy export retains the form HTTP method');
  equal(requests.at(-1).params['criteria[0][value]'], 'filtre exemple', 'Browser form submission preserves actual existing search filters');
  equal(requests.at(-1).params.display_type, '-4', 'Legacy download submits all pages');

  await fixture('native-legacy.html');
  await page.evaluate(() => {
    const select = document.querySelector('select'); select.style.display = 'none'; select.className = 'select2-hidden-accessible';
    const adjacent = document.createElement('span'); adjacent.className = 'select2 select2-container'; adjacent.textContent = 'Toutes les pages en CSV'; select.after(adjacent);
  });
  state = await inspect();
  ok(state.supported && state.plans[0].formatHidden, 'Recognized hidden Select2 native select advertises formatHidden');
  await page.locator(state.plans[0].formatSelector).selectOption(state.plans[0].formatValue, { force: true });
  equal(await page.locator('select').inputValue(), '-4', 'Host can select the underlying native option for Select2');
  await page.evaluate(() => { document.querySelector('select').className = ''; });
  ok(!(await inspect()).supported, 'Unrecognized hidden select is not automated');

  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').href = 'https://another-origin.invalid/front/report.dynamic.php?display_type=-4'; });
  ok(!(await inspect()).supported, 'Cross-origin all-pages links are rejected');
  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').href = '/glpi/front/report.dynamic.php?display_type=4'; });
  ok(!(await inspect()).supported, 'All-pages text with positive current-page mode is rejected');
  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').textContent = 'CSV'; });
  ok(!(await inspect()).supported, 'Negative mode alone does not prove an all-pages CSV command');
  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').remove(); });
  ok(!(await inspect()).supported, 'Current-page-only CSV does not become an all-pages plan');
  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').setAttribute('onclick', 'window.businessSubmits++'); });
  ok(!(await inspect()).supported, 'Custom inline script on an export-looking link requires fallback');
  await fixture('native-glpi10.html');
  await page.evaluate(() => { document.querySelector('#all-csv').href += '&delete=1'; });
  ok(!(await inspect()).supported, 'Mutation-looking query parameters invalidate a native export candidate');

  await fixture('native-legacy.html');
  await page.evaluate(() => { document.querySelector('#native-export').action = '/glpi/front/massiveaction.php'; });
  ok(!(await inspect()).supported, 'Business form with an Export label is never treated as native report export');
  await fixture('native-legacy.html');
  await page.evaluate(() => { document.querySelector('#native-export').method = 'post'; });
  ok(!(await inspect()).supported, 'Unknown POST export form is not substituted for the verified legacy GET form');
  await fixture('native-legacy.html');
  await page.evaluate(() => { document.querySelector('#native-submit').setAttribute('formaction', '/glpi/front/massiveaction.php'); });
  ok(!(await inspect()).supported, 'A submit override cannot redirect an apparently valid export form');

  await fixture('native-glpi10.html');
  await page.evaluate(() => { const duplicate = document.querySelector('#all-csv').cloneNode(true); duplicate.id = 'duplicate'; document.querySelector('#all-csv').parentElement.append(duplicate); });
  equal((await inspect()).plans.length, 1, 'Duplicate links to the exact same export produce one plan');
  await page.evaluate(() => { document.querySelector('#duplicate').href += '&sort=99'; });
  ok(!(await inspect()).supported, 'Distinct all-pages export destinations are ambiguous');

  await fixture('native-glpi10.html');
  await page.evaluate(() => {
    const clone = document.querySelector('.search-display-data').cloneNode(true);
    for (const el of clone.querySelectorAll('[id]')) el.id += '-other';
    clone.querySelector('.dropdown-menu').setAttribute('aria-labelledby', 'dropdown-export-other');
    clone.querySelector('form').setAttribute('data-count', '77'); document.body.append(clone);
  });
  ok(!(await inspect()).supported, 'Multiple GLPI result tables require an explicit saved selection');
  state = await inspect('#search_tickets');
  equal(state.plans[0].directSelector, '#all-csv', 'Explicit table selection scopes the associated native export');
  equal((await inspect('#search_tickets-other')).expectedTotal, 77, 'Table-specific total never leaks from another result card');
  await page.evaluate(() => { const second = document.querySelector('#search_tickets').cloneNode(true); second.id = 'nested-second'; document.querySelector('#ticket-card').append(second); });
  ok(!(await inspect('#search_tickets')).supported, 'Two independent data tables sharing one export region are ambiguous');

  await fixture('native-glpi10.html');
  await page.evaluate(() => {
    const menu = document.querySelector('#export-menu'); menu.innerHTML = '';
    document.querySelector('#dropdown-export').addEventListener('click', () => {
      menu.innerHTML = '<li><a id="lazy-all" href="/glpi/front/report.dynamic.php?item_type=Ticket&amp;display_type=-4">All pages in CSV</a></li>';
    });
  });
  state = await inspect();
  ok(state.supported && state.plans[0].needsOpen && !state.plans[0].allPages, 'Empty native menu may be opened but is not yet proven all-pages');
  await page.locator(state.plans[0].menuSelector).click();
  state = await inspect();
  ok(state.plans[0].allPages && state.plans[0].kind === 'direct', 'Lazy menu is reinspected and must prove all-pages before download');

  await fixture('native-glpi10.html');
  await page.evaluate(() => { const input = document.createElement('input'); input.type = 'password'; input.value = 'NOT-TO-BE-READ'; document.body.prepend(input); });
  state = await inspect();
  ok(!state.supported && !JSON.stringify(state).includes('NOT-TO-BE-READ'), 'Visible login form defers export without reading credentials');
  await fixture('native-glpi10.html');
  await page.evaluate(() => document.querySelector('#massformTicket').setAttribute('data-count', '0'));
  equal((await inspect()).expectedTotal, 0, 'An explicit GLPI zero total remains distinguishable from unknown');
  equal((await inspect('[')).plans.length, 0, 'Invalid saved selector fails closed with no action plan');
  await legacyFixture();
  state = await inspect('#legacy-results');
  equal(await page.locator('table').count(), 6, 'Realistic legacy fixture has six visible layout, pager and result tables');
  equal(await page.locator('#legacy-results tbody tr').count(), 20, 'Legacy result table contains twenty visible data rows');
  equal(await page.locator('#legacy-results thead th').count(), 16, 'Legacy result table has sixteen columns');
  ok(state.supported && state.plans[0].kind === 'form', 'Legacy adjacent native export is found despite pager TH cells and filter tables');
  equal(state.plans[0].formSelector, '#legacy-export', 'Export belongs to the dedicated report form outside the massive-action form');
  equal(state.plans[0].formatValue, '-3', 'Actual GLPI 9 CSV code is read from the DOM instead of hardcoding a modern fixture code');
  equal([state.expectedTotal, state.rangeStart, state.rangeEnd], [828, 1, 20], 'Associated legacy pager proves the total and displayed range');
  ok(state.diagnostics.includes('dom_tables=6;data_tables=1'), 'Structural diagnostics distinguish six HTML tables from the one result table');
  ok(state.diagnostics.every(line => line.length <= 250 && /^[a-z0-9_.=:; -]+$/i.test(line)), 'Diagnostics contain only short allowlisted structural codes and counts');
  ok(!JSON.stringify(state.diagnostics).includes('FAKE-TOKEN') && !JSON.stringify(state.diagnostics).includes('notold') && !JSON.stringify(state.diagnostics).includes('Fixture'), 'Diagnostics omit tokens, filter values and ticket-cell values');
  await page.locator(state.plans[0].formatSelector).selectOption(state.plans[0].formatValue);
  await downloadClick(state.plans[0].submitSelector);
  equal(requests.at(-1).params.display_type, '-3', 'Realistic legacy native submit requests all-pages CSV with the real option code');
  equal(await page.evaluate(() => window.businessSubmits), 0, 'Legacy native export leaves the separate massive-action form untouched');

  await legacyFixture();
  await page.evaluate(() => {
    const button = document.querySelector('#legacy-submit');
    const input = document.createElement('input'); input.type = 'image'; input.name = 'export'; input.id = button.id;
    input.title = 'Exporter'; input.alt = 'Exporter'; input.style.cssText = 'width:24px;height:24px';
    input.src = 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="24" height="24"%3E%3C/svg%3E'; button.replaceWith(input);
  });
  state = await inspect('#legacy-results');
  equal(state.plans[0].submitSelector, '#legacy-submit', 'Official GLPI 9.1 image-submit export control is accepted');
  ok(state.diagnostics.includes('legacy_form_1=verified-get-image-submit'), 'Image-submit support is identifiable without logging labels or URLs');
  await page.locator(state.plans[0].formatSelector).selectOption(state.plans[0].formatValue);
  await downloadClick(state.plans[0].submitSelector);
  equal(requests.at(-1).params.display_type, '-3', 'Image-submit triggers the actual native GET download');

  await legacyFixture();
  await page.evaluate(() => {
    const select = document.querySelector('#legacy-format'); select.style.display = 'none'; select.className = 'select2-offscreen';
    const widget = document.createElement('div'); widget.id = 's2id_' + select.id; widget.className = 'select2-container'; widget.textContent = 'Format export'; select.before(widget);
  });
  state = await inspect('#legacy-results');
  ok(state.supported && state.plans[0].formatHidden, 'Legacy Select2 widget placed before the hidden select is recognized');

  await legacyFixture();
  state = await inspect('#legacy-results');
  equal(state.displayPlan?.selector, '#legacy-limit-top', 'Show-all fallback uses the list-limit select in the associated pager');
  equal([state.displayPlan.value, state.displayPlan.formMethod, state.displayPlan.startsAtZero], ['9999999', 'post', true], 'Existing limit option and dedicated POST form prove the display-only action');
  await Promise.all([
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
    page.locator(state.displayPlan.selector).selectOption(state.displayPlan.value)
  ]);
  await page.evaluate(script);
  state = await inspect('#legacy-results');
  equal(displayRequests, [{ method: 'POST', limit: '9999999' }], 'Changing the actual select invokes the existing native display-limit form');
  equal([state.rangeStart, state.rangeEnd, state.expectedTotal], [1, 828, 828], 'After display-all, the associated pager confirms the full range');
  equal(await page.locator('#legacy-results tbody tr').count(), 828, 'Display-all fixture presents every result in the DOM');

  await legacyFixture();
  await page.evaluate(() => { document.querySelectorAll('select[name="glpilist_limit"] option[value="9999999"]').forEach(option => option.remove()); });
  equal((await inspect('#legacy-results')).displayPlan, null, 'Missing sufficient limit option never causes a fabricated display-all value');
  await legacyFixture();
  await page.evaluate(() => { document.querySelectorAll('select[name="glpilist_limit"]').forEach(select => select.setAttribute('onchange', 'submit();window.businessSubmits++')); });
  equal((await inspect('#legacy-results')).displayPlan, null, 'Unrecognized onchange scripts are not authorized by the display-all plan');
  await legacyFixture();
  await page.evaluate(() => { document.querySelectorAll('form[id^="legacy-limit-form"]').forEach(form => form.action = 'massiveaction.php'); });
  equal((await inspect('#legacy-results')).displayPlan, null, 'Display-limit selectors cannot submit to a business-action endpoint');
  await legacyFixture();
  await page.evaluate(() => { document.querySelectorAll('form[id^="legacy-limit-form"]').forEach(form => form.action = 'ticket.php?start=20'); });
  equal((await inspect('#legacy-results')).displayPlan.startsAtZero, false, 'Nonzero form offset is exposed so the host must return to the first page');
  await legacyFixture();
  await page.evaluate(() => {
    document.querySelectorAll('form[id^="legacy-limit-form"]').forEach(form => form.action = 'ticket.php');
    document.querySelectorAll('[id^="legacy-range-"]').forEach(cell => cell.textContent = 'De 21 à 40 sur 828');
  });
  equal((await inspect('#legacy-results')).displayPlan.startsAtZero, false, 'Missing URL offsets do not prove the first page when its associated pager starts at 21');
  await legacyFixture();
  await page.evaluate(() => { document.querySelector('#legacy-range-bottom').textContent = 'De 1 à 20 sur 999'; });
  state = await inspect('#legacy-results');
  equal([state.expectedTotal, state.rangeStart, state.rangeEnd, state.displayPlan], [null, null, null, null], 'Conflicting associated pager totals cannot certify a range or display-all plan');
  await legacyFixture();
  await page.evaluate(() => {
    const unrelated = document.createElement('section'); unrelated.innerHTML = '<table class="tab_cadrehov"><tr><th>ID</th><th>Autre</th></tr><tr><td>1</td><td>Autre</td></tr></table><table class="tab_cadre_pager"><tr><td>De 1 à 20 sur 99999</td></tr></table>'; document.body.append(unrelated);
  });
  equal((await inspect('#legacy-results')).expectedTotal, 828, 'A separate list pager cannot change the selected table total');
  await legacyFixture();
  await page.evaluate(() => { document.querySelector('#legacy-format option[value="-3"]').remove(); });
  state = await inspect('#legacy-results');
  ok(!state.supported && state.displayPlan?.value === '9999999', 'A missing native all-pages CSV option preserves the independently proven display-all fallback');
  equal(requests.length, 5, 'Only five explicitly executed fixture exports caused a download request');
  log(checks + ' native export DOM checks passed.');
}

run().catch(error => { log('FAILED after ' + checks + ' successful checks.'); log(error.stack || error.message || error); process.exitCode = 1; })
  .finally(async () => {
    if (browser) await browser.close().catch(error => log('Browser cleanup: ' + error.message));
    if (rawLogPath) { fs.mkdirSync(path.dirname(rawLogPath), { recursive: true }); fs.writeFileSync(rawLogPath, output.join('\n') + '\n', 'utf8'); }
  });
