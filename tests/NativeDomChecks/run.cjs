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
    if (name === 'glpi-cell-popup.html') {
      return route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: fs.readFileSync(path.join(__dirname, name), 'utf8') });
    }
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

  const tableScript = fs.readFileSync(path.join(project, 'src/GlpiPilot/BrowserScripts/bridge.js'), 'utf8');
  const setVersion = version => page.evaluate(version => {
    document.querySelector('body > div.page')?.remove();
    if (!version) return;
    const root = document.createElement('div'); root.className = 'page';
    root.innerHTML = `<header class="navbar"><a id="show_about_modal_100" href="#">À propos</a><div class="modal fade" id="about_modal_100" role="dialog" style="display:none"><div class="modal-dialog"><div class="modal-content"><div class="modal-body"><p><a href="http://glpi-project.org/" title="Powered by Teclib and contributors" class="copyright">GLPI ${version} Copyright (C) 2015-2025 Teclib' and contributors</a></p></div></div></div></div></header>`;
    document.body.prepend(root);
  }, version);
  const popupFixture = async (version = null) => { await page.goto('http://glpi-native.test/fixtures/glpi-cell-popup.html'); await page.evaluate(tableScript); await setVersion(version); };
  const readPopupTable = () => page.evaluate(() => window.__glpiPilot.readTable('#popup-results'));
  await popupFixture();
  await readPopupTable(); // Establish the adapter's normal table-key annotation.
  const popupDom = await page.locator('html').innerHTML();
  let values = await readPopupTable();
  equal(values.rows.map(row => row.slice(0, 4)), [['101', 'Écran (101)', 'En attente', 'Haute'], ['102', 'Réseau (102)', 'En cours (attribué)', 'Moyenne']], 'Official GLPI status icons, priority decoration and title IDs retain their exact displayed values');
  equal(values.rows.map(row => row[4]), ['Alice Exemple\nBob Exemple', 'Charlie Exemple\nDana Exemple'], 'Paired static GLPI 9.5 and 10 multivalue popups supply every actual item for native comparison');
  equal(await page.locator('html').innerHTML(), popupDom, 'Reading paired multivalue popups does not change the DOM or open a tooltip');
  ok(!JSON.stringify(values.rows).includes('CACHE'), 'Ticket-description and loading tooltips remain excluded');
  await page.evaluate(() => { document.querySelector('#contenttooltip110').id = 'contenttooltip999'; });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple', 'A popup with another marker ID cannot replace the visible cell');
  await popupFixture();
  await page.evaluate(() => { document.querySelector('#contenttooltip110 .fup-popup a').textContent = 'Autre Exemple'; });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple', 'A mismatched first popup item cannot replace the visible value');
  await popupFixture();
  await page.evaluate(() => { document.querySelector('#tooltip110').className = 'fas fa-info pointer'; });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple', 'An ordinary information tooltip is never expanded into a result value');
  await popupFixture();
  await page.evaluate(() => { document.querySelectorAll('#contenttooltip110 br').forEach(br => br.replaceWith(document.createElement('hr'))); });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple\nBob Exemple', 'Horizontal-rule popups preserve a text boundary without imitating CSV punctuation or lost delimiters');
  await popupFixture();
  await page.evaluate(() => { const content = document.querySelector('#contenttooltip110'); content.after(content.cloneNode(true)); });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple', 'Duplicate popup IDs cannot choose a hidden source ambiguously');
  await popupFixture();
  await page.evaluate(() => { document.querySelectorAll('#contenttooltip110 .fup-popup a')[1].textContent = 'Bob Modifié'; });
  equal((await readPopupTable()).rows[0][4], 'Alice Exemple\nBob Modifié', 'A changed later popup item remains in the compared data instead of being discarded');

  for (const hiddenClass of ['invisible', 'tooltip-invisible']) {
    await popupFixture();
    await page.evaluate(hiddenClass => {
      const user = (name, id) => `${name} <a id="tooltiplink${id}" href="/glpi/front/user.form.php?id=${id}"><span id="tooltip${id}" class="fas fa-info pointer"></span></a><div id="contenttooltip${id}" class="${hiddenClass}"><strong>Nom</strong>: ${name}<br><strong>Identifiant</strong>: HIDDEN-USER-CARD-${id}<br></div>`;
      const first = user('Alice Exemple', 301), second = user('Bob Exemple', 302);
      document.querySelector('#popup-results tbody tr td:last-child').innerHTML = `${first} <span id="tooltip310" class="fas fa-comments pointer"></span><div id="contenttooltip310" class="${hiddenClass}"><div class="fup-popup">${first}<br>${second}<br></div></div>`;
    }, hiddenClass);
    equal((await readPopupTable()).rows[0][4], 'Alice Exemple\nBob Exemple', hiddenClass + ': nested hidden user-card BRs do not hide later requesters from the native comparison');
    ok(!JSON.stringify((await readPopupTable()).rows).includes('HIDDEN-USER-CARD'), hiddenClass + ': nested user-card contents remain excluded from every extracted value');
  }

  // Search::giveItem creates each shortened span and its own static tooltip;
  // Search::showItem then copies the first complete triplet into an outer list
  // tooltip. Those two owners legitimately contain the same generated IDs.
  const longTextFixture = async (hiddenClass = 'tooltip-invisible', delimiter = null, version = null) => {
    await popupFixture(version);
    await page.evaluate(({ hiddenClass, delimiter }) => {
      const item = (prefix, full, id) => `<span id="text${id}">${prefix}&nbsp;(...)</span> <span id="tooltip${id + 100}" class="fas fa-comments fa-fw"></span><div id="contenttooltip${id + 100}" class="${hiddenClass}"><div class="fup-popup">${full}</div></div><script type="application/x-synthetic-tooltip">ignored</script>`;
      const first = item('Premier contenu', 'Premier contenu complet avec détail.', 401);
      const second = item('Deuxième contenu', 'Deuxième contenu complet avec détail.', 402);
      document.querySelector('#popup-results tbody tr td:last-child').innerHTML = delimiter
        ? `${first} <span id="tooltip599" class="fas fa-comments fa-fw"></span><div id="contenttooltip599" class="${hiddenClass}"><div class="fup-popup">${first}<${delimiter}>${second}<${delimiter}></div></div>`
        : first;
    }, { hiddenClass, delimiter });
  };
  for (const hiddenClass of ['invisible', 'tooltip-invisible']) {
    for (const delimiter of ['br', 'hr']) {
      await longTextFixture(hiddenClass, delimiter);
      equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail.\nDeuxième contenu complet avec détail.', hiddenClass + '/' + delimiter + ': cloned first-item IDs and independently paired text popups retain every complete value');
    }
    await longTextFixture(hiddenClass);
    equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail.', hiddenClass + ': the paired long-text popup replaces the exact shortened preview');
  }
  await longTextFixture();
  await page.evaluate(() => {
    document.querySelector('#contenttooltip501 .fup-popup').innerHTML = '<div class="long_text">Premier contenu complet avec détail. ...<p class="read_more"><span class="read_more_button">...</span></p></div><script type="application/x-synthetic-tooltip">ignored</script>';
  });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail. ...', 'The generated RichText read-more control is excluded while literal ellipsis in its content stays intact');
  const longTextDom = await page.locator('html').innerHTML();
  await readPopupTable();
  equal(await page.locator('html').innerHTML(), longTextDom, 'Long-text expansion reads the existing DOM without opening or rewriting the popup');

  const rejectedLongText = [
    ['A different full-text prefix cannot replace the visible preview', () => { document.querySelector('#contenttooltip501 .fup-popup').textContent = 'Autre contenu complet avec détail.'; }],
    ['A full value no longer than the preview prefix does not prove truncation', () => { document.querySelector('#contenttooltip501 .fup-popup').textContent = 'Premier contenu'; }],
    ['A different popup ID cannot supply the long text', () => { document.querySelector('#contenttooltip501').id = 'contenttooltip999'; }],
    ['An ordinary fa-info tooltip cannot supply the long text', () => { document.querySelector('#tooltip501').className = 'fas fa-info'; }],
    ['Duplicate popup IDs under the same owner reject long-text expansion', () => { const content = document.querySelector('#contenttooltip501'); content.after(content.cloneNode(true)); }],
    ['Duplicate preview IDs under the same owner reject long-text expansion', () => { const preview = document.querySelector('#text401'); const duplicate = document.createElement('span'); duplicate.id = preview.id; preview.before(duplicate); }],
    ['A hidden marker does not authorize reading its long-text popup', () => { document.querySelector('#tooltip501').style.display = 'none'; }],
    ['An AJAX placeholder without a static fup-popup is left unread', () => { document.querySelector('#contenttooltip501').innerHTML = '<div>HIDDEN-AJAX-PLACEHOLDER</div>'; }],
    ['An explicitly excluded popup stays excluded despite matching IDs', () => { document.querySelector('#contenttooltip501').setAttribute('aria-hidden', 'true'); }]
  ];
  for (const [label, mutate] of rejectedLongText) {
    await longTextFixture();
    await page.evaluate(mutate);
    equal((await readPopupTable()).rows[0][4], 'Premier contenu (...)', label);
  }
  await longTextFixture();
  await page.evaluate(() => { document.querySelector('#text401').textContent = 'Premier contenu visible'; });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu visible', 'A span without the exact GLPI abbreviation suffix remains literal');
  await longTextFixture();
  await page.evaluate(() => { document.querySelector('#text401').after(document.createTextNode(' TEXTE AJOUTÉ')); });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu (...) TEXTE AJOUTÉ', 'Unrelated text between preview and marker prevents their association');
  await longTextFixture();
  await page.evaluate(() => {
    document.querySelector('#text401').textContent = 'Premier\u0092 contenu\u0085 (...)';
    document.querySelector('#contenttooltip501 .fup-popup').textContent = 'Premier\u0092 contenu\u0085 complet\u0092 avec détail.';
  });
  equal((await readPopupTable()).rows[0][4], 'Premier\u0092 contenu\u0085 complet\u0092 avec détail.', 'C1 code points remain exact in both the prefix proof and the recovered full value');
  await longTextFixture('tooltip-invisible', 'br');
  await page.evaluate(() => { document.querySelector('#contenttooltip502 .fup-popup').textContent = 'Deuxième contenu réellement modifié.'; });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail.\nDeuxième contenu réellement modifié.', 'A changed later long-text item remains visible to the native comparison');
  await longTextFixture('tooltip-invisible', 'br');
  await page.evaluate(() => { document.querySelector('#contenttooltip599 #contenttooltip501 .fup-popup').textContent = 'Premier contenu complet différent.'; });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail.', 'A different expanded first clone cannot authorize the outer multivalue popup');
  await longTextFixture();
  await page.evaluate(() => { document.querySelector('#contenttooltip501 .fup-popup').innerHTML += '<div class="tooltip-invisible">HIDDEN-UNRELATED-CONTENT</div><div hidden>HIDDEN-UNRELATED-CONTENT</div>'; });
  equal((await readPopupTable()).rows[0][4], 'Premier contenu complet avec détail.', 'Unrelated hidden descendants stay excluded inside a validated long-text popup');
  await longTextFixture('tooltip-invisible', 'br');
  await page.evaluate(() => {
    document.querySelectorAll('[id="text401"]').forEach(span => { span.innerHTML = 'Premier<br>contenu&nbsp;(...)'; });
    document.querySelectorAll('[id="contenttooltip501"] .fup-popup').forEach(popup => { popup.innerHTML = 'Premier<br>contenu complet avec détail.'; });
  });
  equal((await readPopupTable()).rows[0][4], 'Premier\ncontenu complet avec détail.\nDeuxième contenu complet avec détail.', 'Line breaks inside a proven long preview remain part of its first item rather than outer-list separators');

  // GLPI 10.0.22 exports rich-text presentation through DataExport/RichText and
  // Html2Text. Project only the bounded, supported markup of the same cell.
  const readPopupProofs = () => page.evaluate(() => window.__glpiPilot.readTable('#popup-results', true));
  await popupFixture('10.0.22');
  equal((await readPopupTable()).csvRepresentations, [], 'Ordinary pagination reads do not carry optional CSV representations');
  await page.evaluate(() => {
    document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<p><strong>Bonjour</strong> monde "exemple"</p><p><em>note</em></p><ul><li>Premier</li><li>Second</li></ul><hr><p>fin.</p>';
  });
  values = await readPopupProofs();
  equal(values.rows[0][4], 'Bonjour monde "exemple"\nnote\nPremier\nSecond\nfin.', 'Rich-text proof generation leaves the normal displayed text unchanged');
  equal(values.csvRepresentations[0][4], { row: 0, column: 4, kind: 'glpi10-rich-text-v1', source: values.rows[0][4], text: "BONJOUR monde ''exemple''\n_note_\n* Premier\n* Second\n-------------------------\nfin." }, 'The GLPI 10 projection records exact coordinates, source and presentation without exporting HTML');
  const proofDom = await page.locator('html').innerHTML();
  await readPopupProofs();
  equal(await page.locator('html').innerHTML(), proofDom, 'Computing representations never rewrites the DOM or opens controls');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').textContent = 'Bonjour monde'; });
  equal((await readPopupProofs()).csvRepresentations[0][4], null, 'Plain text has no fabricated uppercase or generic compatibility projection');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong><pre>Texte préformaté</pre>'; });
  equal((await readPopupProofs()).csvRepresentations[0][4], null, 'Unsupported PRE markup cannot supply a partly stripped CSV equivalence');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong><span hidden>Texte masqué</span>'; });
  equal((await readPopupProofs()).csvRepresentations[0][4], null, 'An unsupported hidden descendant prevents projection rather than silently changing semantics');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = 'avant<h2>titre</h2>après<div><strong>bloc</strong></div>suite'; });
  equal((await readPopupProofs()).csvRepresentations[0][4]?.text, 'avant\nTITRE\naprès\nBLOCsuite', 'Html2Text heading boundaries and DIV opening-only boundary retain their exact distinction');
  for (const unsupported of ['<strong>début<br>suite</strong>', '<strong>début<strong>milieu</strong>suite</strong>', '<ul><li>début<ul><li>suite</li></ul></li></ul>', '<strong>début</strong><a class="_html2text_link_inline" href="/target">suite</a>', '<strong>début</strong><span class="_html2text_ignore">suite</span>', '<ins>texte</ins>', '<li>texte</li>']) {
    await page.evaluate(html => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = html; }, unsupported);
    equal((await readPopupProofs()).csvRepresentations[0][4], null, 'Unsupported or ambiguous Html2Text structure yields no projected equivalence');
  }

  await longTextFixture('tooltip-invisible', null, '10.0.22');
  await page.evaluate(() => { document.querySelector('#contenttooltip501 .fup-popup').innerHTML = 'Premier contenu <strong>formaté</strong> avec détail.'; });
  values = await readPopupProofs();
  equal([values.rows[0][4], values.csvRepresentations[0][4]?.text], ['Premier contenu formaté avec détail.', 'Premier contenu FORMATÉ avec détail.'], 'An exactly paired long-text popup supplies its own rich presentation after proving the visible prefix');
  await page.evaluate(() => { document.querySelector('#contenttooltip501').id = 'contenttooltip999'; });
  values = await readPopupProofs();
  equal([values.rows[0][4], values.csvRepresentations[0][4]], ['Premier contenu (...)', null], 'An unassociated popup cannot furnish a format equivalence for a shortened cell');

  for (const delimiter of ['br', 'hr']) {
    await longTextFixture('tooltip-invisible', delimiter, '10.0.22');
    values = await readPopupProofs();
    equal(values.csvRepresentations[0][4]?.text, `Premier contenu complet avec détail.<${delimiter}>Deuxième contenu complet avec détail.`, 'GLPI 10 ' + delimiter + ': inter-item export markers are preserved separately from rich-text formatting');
    await page.evaluate(() => { document.querySelector('#contenttooltip599 #contenttooltip501 .fup-popup').textContent = 'Premier contenu complet différent.'; });
    equal((await readPopupProofs()).csvRepresentations[0][4], null, 'GLPI 10 ' + delimiter + ': a different expanded first clone cannot justify an outer-list projection');
  }

  await popupFixture('10.0.22');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour\u0092</strong> monde\u0085'; });
  values = await readPopupProofs();
  equal([values.rows[0][4], values.csvRepresentations[0][4]?.text], ['Bonjour\u0092 monde\u0085', 'BONJOUR\u0092 monde\u0085'], 'Presentation evidence preserves exact C1 code points; the C# verifier may reject the alternative, never recode it');
  await page.evaluate(() => { const cell = document.querySelector('#popup-results tbody tr td:last-child'); cell.innerHTML = '<strong></strong>'; cell.firstChild.textContent = 'x'.repeat(262145); });
  values = await readPopupProofs();
  equal([values.rows[0][4].length, values.csvRepresentations[0][4]], [262145, null], 'Oversized evidence is omitted while the ordinary cell text remains available');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>mixte</strong>' + '<span>x</span>'.repeat(4100); });
  values = await readPopupProofs();
  equal([values.rows[0][4].length, values.csvRepresentations[0][4]], [4105, null], 'The evidence node budget rejects excessive markup without truncating the normal value');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<span>'.repeat(65) + '<strong>mixte</strong>' + '</span>'.repeat(65); });
  values = await readPopupProofs();
  equal([values.rows[0][4], values.csvRepresentations[0][4]], ['mixte', null], 'Excessive markup depth produces no equivalence proof');
  await page.evaluate(() => {
    document.querySelector('#popup-results').innerHTML = '<thead><tr><th>ID</th><th>Nom</th></tr></thead><tbody>' + Array.from({ length: 513 }, (_, index) => `<tr><td>${index + 1}</td><td><strong>mixte</strong></td></tr>`).join('') + '</tbody>';
  });
  values = await readPopupProofs();
  equal([values.rows.length, values.csvRepresentations.flat().filter(Boolean).length], [513, 512], 'A table-wide evidence budget keeps all rows but bounds optional representation records');

  for (const version of [null, '9.5.13', '10.0.0', '10.0.21', '10.0.23', '10.0.22-rc1', '11.0.0']) {
    await popupFixture(version);
    await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong>'; });
    values = await readPopupProofs();
    equal([values.rows[0][4], values.csvRepresentations], ['Bonjour', []], 'Unknown or unverified GLPI version ' + version + ' retains the cell without a 10.0.22 CSV projection');
  }
  await popupFixture('10.0.22');
  await page.evaluate(() => { document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong>'; });
  equal((await readPopupProofs()).csvRepresentations[0][4]?.text, 'BONJOUR', 'The official hidden About modal permits the exact verified 10.0.22 projection without clicking');
  await page.evaluate(() => {
    const copy = document.querySelector('body > .page > header').cloneNode(true);
    copy.querySelector('#show_about_modal_100').id = 'show_about_modal_101'; copy.querySelector('#about_modal_100').id = 'about_modal_101';
    const aside = document.createElement('aside'); aside.className = 'navbar'; aside.innerHTML = copy.innerHTML; document.querySelector('body > .page').append(aside);
  });
  equal((await readPopupProofs()).csvRepresentations[0][4]?.text, 'BONJOUR', 'Consistent official responsive header copies share the same proven version');
  await page.evaluate(() => { document.querySelector('aside a.copyright').textContent = "GLPI 9.5.13 Copyright (C) 2015-2025 Teclib' and contributors"; });
  equal((await readPopupProofs()).csvRepresentations, [], 'Contradictory official header markers cannot choose the favorable version');
  await popupFixture('10.0.22');
  await page.evaluate(() => {
    document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong>';
    document.querySelector('#popup-results tbody tr td:last-child').append(document.querySelector('body > .page'));
  });
  equal((await readPopupProofs()).csvRepresentations, [], 'A copied application header inside ticket data cannot authorize a CSV projection');
  await popupFixture('10.0.22');
  await page.evaluate(() => { document.querySelector('a.copyright').href = 'https://unrelated.invalid/'; });
  equal((await readPopupProofs()).csvRepresentations, [], 'The About text with a noncanonical publisher link is not a version proof');
  await popupFixture('10.0.22');
  await page.evaluate(() => {
    document.querySelector('#popup-results tbody tr td:last-child').innerHTML = '<strong>Bonjour</strong>';
    document.querySelector('#about_modal_100').removeAttribute('role');
  });
  equal((await readPopupProofs()).csvRepresentations, [], 'An incomplete About modal structure is not a version proof');
  log(checks + ' native export DOM checks passed.');
}

run().catch(error => { log('FAILED after ' + checks + ' successful checks.'); log(error.stack || error.message || error); process.exitCode = 1; })
  .finally(async () => {
    if (browser) await browser.close().catch(error => log('Browser cleanup: ' + error.message));
    if (rawLogPath) { fs.mkdirSync(path.dirname(rawLogPath), { recursive: true }); fs.writeFileSync(rawLogPath, output.join('\n') + '\n', 'utf8'); }
  });
