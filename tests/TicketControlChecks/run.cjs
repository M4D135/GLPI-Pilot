'use strict';
// Real production ES modules and CSS, with explicitly synthetic API replies.
// This suite does not replace C# bridge, persistence, or native Windows checks.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const project = path.resolve(__dirname, '../..');
const { chromium } = require(process.env.GLPI_TEST_PLAYWRIGHT_MODULE || require.resolve('playwright', { paths: [path.join(project, 'tests/DomChecks')] }));
let checks = 0, browser, server;
const ok = (condition, label) => { assert.ok(condition, label); console.log(`PASS ${++checks} ${label}`); };
const html = fs.readFileSync(path.join(project, 'web/index.html'), 'utf8')
  .replaceAll('href="css/', 'href="/web/css/').replaceAll('href="vendor/', 'href="/web/vendor/')
  .replace('src="vendor/', 'src="/web/vendor/').replace('<script type="module" src="js/app.js"></script>', '')
  .replace('<div id="app" class="app-loading" role="status">Ouverture de GLPI Pilot…</div>', '<main class="workspace" id="root"></main>');
(async () => {
  try {
    const origin = 'https://glpi-ergonomics.test';
    browser = await chromium.launch({ headless: true, executablePath: process.env.GLPI_TEST_CHROMIUM, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage(); page.setDefaultTimeout(6000);
    const errors = []; let external = 0;
    page.on('pageerror', error => errors.push(error.message));
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.origin !== origin) { external++; return route.abort(); }
      if (url.pathname === '/') return route.fulfill({ contentType: 'text/html; charset=utf-8', body: html });
      const file = path.resolve(project, '.' + url.pathname);
      if (!file.startsWith(path.join(project, 'web') + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, body: 'Unknown synthetic fixture' });
      return route.fulfill({ contentType: file.endsWith('.js') ? 'text/javascript; charset=utf-8' : file.endsWith('.css') ? 'text/css; charset=utf-8' : 'application/octet-stream', body: fs.readFileSync(file) });
    });
    const reset = async () => {
      if (process.env.GLPI_TEST_OFFLINE_BLOBS === '1') await require('./offline.cjs')(page);
      else { await page.goto(origin); await page.evaluate(() => { window.importProduction = name => import(name); }); }
      await page.evaluate(() => { const root = document.querySelector('#root'); Object.assign(root.style, { height: '100dvh', width: '100%', overflow: 'auto', padding: '16px', boxSizing: 'border-box' }); window.controller = new AbortController(); });
    };
    const capture = async name => {
      if (!process.env.GLPI_TEST_SCREENSHOTS) return;
      fs.mkdirSync(process.env.GLPI_TEST_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, name + '.png') });
    };
    await require('./tickets.cjs')({page,reset,ok,capture});
    ok(errors.length===0,`Production modules report no JavaScript errors (${errors.join('; ')})`);
    ok(external===0,'Synthetic ergonomics checks make no external requests');
    console.log(`${checks}/${checks} ticket control checks passed.`);
  } catch(error) { console.error(error);process.exitCode=1; }
  finally { if(browser)await browser.close();if(server)await new Promise(resolve=>server.close(resolve)); }
})();
