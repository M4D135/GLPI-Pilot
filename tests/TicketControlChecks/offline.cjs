'use strict';
// Offline renderer for containers whose browser disallows URL navigation.
// Only local production modules are loaded. The import map rewrites their
// static relative specifiers to in-memory Blob URLs, not their implementation.
// This mode does not exercise the production CSP or host; CI must do those.
const fs = require('node:fs');
const path = require('node:path');
const project = path.resolve(__dirname, '../..');
const files = {};
function walk(dir) {
  for (const entry of fs.readdirSync(path.join(project, dir), { withFileTypes: true })) {
    const relative = path.posix.join(dir, entry.name);
    if (entry.isDirectory()) walk(relative);
    else if (relative.endsWith('.js')) {
      const source = fs.readFileSync(path.join(project, relative), 'utf8');
      files['/' + relative] = source.replace(/(\bfrom\s*|\bimport\s*\(\s*|\bimport\s*)(['"])(\.\.?\/[^'"]+\.js)\2/g,
        (match, prefix, quote, specifier) => prefix + quote + '@pilot/' + path.posix.normalize(path.posix.join(path.posix.dirname(relative), specifier)) + quote);
    }
  }
}
walk('web/js'); walk('web/vendor/natural-earth');
let html = fs.readFileSync(path.join(project, 'web/index.html'), 'utf8')
  .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, '')
  .replace(/<script[^>]*src="[^"]*"[^>]*><\/script>/g, '')
  .replace(/<link rel="stylesheet" href="([^"]+)">/g, (_, file) => '<style>' + fs.readFileSync(path.join(project, 'web', file), 'utf8') + '</style>')
  .replace('<div id="app" class="app-loading" role="status">Ouverture de GLPI Pilot…</div>', '<main class="workspace" id="root"></main>');
const leaflet = fs.readFileSync(path.join(project, 'web/vendor/leaflet/leaflet.js'), 'utf8');
module.exports = async function offline(page) {
  await page.goto('about:blank');
  await page.setContent(html);
  await page.addScriptTag({ content: leaflet });
  await page.evaluate(modules => {
    // about:blank is not a secure app origin; provide only the UUID primitive
    // normally supplied by the HTTPS WebView host, using the browser CSPRNG.
    if (!crypto.randomUUID) crypto.randomUUID = () => {
      const bytes = crypto.getRandomValues(new Uint8Array(16)); bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
      const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
      return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
    };
    const imports = {};
    for (const [name, source] of Object.entries(modules)) imports['@pilot' + name] = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    const map = document.createElement('script'); map.type = 'importmap'; map.textContent = JSON.stringify({ imports }); document.head.append(map);
    window.importProduction = name => import('@pilot' + name);
  }, files);
};
