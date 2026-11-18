'use strict';

// Synthetic fixtures follow the native markup in these official sources:
// 9.5: glpi-project/glpi@1a3f445f362983a12c5e960ea3fce38bf866da3e,
//      inc/search.class.php, inc/displaypreference.class.php, inc/ajax.class.php,
//      inc/dropdown.class.php, inc/html.class.php and js/common.js.
// 10:  glpi-project/glpi@02d219b1bc1d343033440bb2a30c053f52f3b98f,
//      src/DisplayPreference.php, templates/components/search/controls.html.twig.
// 11:  glpi-project/glpi@b0897d490548cc2ccf5f82d775eb3690861d3734,
//      templates/components/search/displaypreference_{config,modal}.html.twig.
// All requests stay intercepted. Only the fixture's native event handlers mutate
// its personal preferences; the adapter is checked for zero DOM/network effects.
const fs = require('node:fs');
const path = require('node:path');

module.exports = async function checkColumns({ browser, project, ok, equal }) {
  const script = fs.readFileSync(path.join(project, 'src/GlpiPilot/BrowserScripts/native-columns.js'), 'utf8');
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const origin = 'http://glpi-columns.test';
  const choices = [
    { value: '2', label: 'ID', group: 'Caractéristiques' },
    { value: '12', label: 'Statut', group: 'Caractéristiques' },
    { value: '3', label: 'Priorité', group: 'Caractéristiques' },
    { value: '8', label: 'Groupe de techniciens', group: 'Attribué à' },
    { value: '64', label: 'Rédacteur', group: 'Suivis' },
    { value: '65', label: 'Rédacteur', group: 'Tâches' }
  ];
  let version = 'legacy';
  let active = false;
  let stored = [];
  let renderNumber = 0;
  let preferenceRenderNumber = 0;
  let preferenceReads = 0;
  const inherited = ['12', '2'];
  const requests = [];
  const html = content => '<!doctype html><html lang="fr"><meta charset="utf-8"><style>body{font:14px sans-serif}td{padding:4px}iframe{width:850px;height:640px}.hidden{display:none}.select2-hidden-accessible{position:absolute;width:1px;height:1px;opacity:0}.select2-container{display:inline-block}button{min-height:24px}</style><body>' + content + '</body></html>';
  const field = (name, value) => `<input type="hidden" name="${name}" value="${value}">`;
  const identity = user => field('itemtype', 'Ticket') + field('users_id', user);
  const form = (content, user = '17') => `<form method="post" action="/glpi/front/displaypreference.form.php">${identity(user)}${content}${field('_glpi_csrf_token', 'synthetic-token')}</form>`;
  const tab = '<ul id="tabspanel"><li><a id="global-tab" href="/glpi/ajax/common.tabs.php?_itemtype=DisplayPreference&amp;_glpi_tab=DisplayPreference%241&amp;displaytype=Ticket">Vue globale</a></li><li><a id="personal-tab" href="/glpi/ajax/common.tabs.php?_itemtype=DisplayPreference&amp;_glpi_tab=DisplayPreference%242&amp;displaytype=Ticket">Vue personnelle</a></li></ul>';
  const entryLabel = value => { const item = choices.find(item => item.value === value); return version !== 'legacy' && item.group !== 'Caractéristiques' ? item.group + ' - ' + item.label : item.label; };
  function options(modern) {
    const available = modern ? choices : choices.filter(item => !stored.includes(item.value));
    const groups = [...new Set(available.map(item => item.group))];
    return groups.map(group => `<optgroup label="${group}">${available.filter(item => item.group === group).map(item => `<option value="${item.value}">${item.label}</option>`).join('')}</optgroup>`).join('');
  }
  function preferences(view = 'personal') {
    const modern = version === 'modern';
    if (view === 'global' || view === 'global-readonly') return html(tab + '<div id="tabsbody">' +
      (view === 'global' ? form('<select name="num"><option value="8">Groupe de techniciens</option></select><input type="submit" name="add" value="Ajouter">', '0') : '<p>Statut · ID</p>') + '</div>');
    // Native Dropdown::showFromArray assigns dropdown_num + mt_rand() on each
    // server render. The identifier is a locator, not the identity of a column.
    const dropdown = `<select id="dropdown_num${++preferenceRenderNumber}" name="num">${options(modern)}</select>`;
    let content;
    if (!active) content = modern ? identity('17') + '<button type="button" name="activate">Créer</button>' : form('<input type="submit" name="activate" value="Créer">');
    else if (modern) content = identity('17') + dropdown + '<button type="button" name="add_opt">Ajouter</button><button type="button" name="disable">Supprimer la vue personnelle</button><ul class="list-group">' +
      '<li data-opt-id="1" data-fixed="true"><span>Titre</span></li>' + stored.map(value => `<li data-opt-id="${value}"><span class="flex-fill">${entryLabel(value)}</span><button name="remove_opt">Supprimer</button></li>`).join('') + '</ul>';
    else content = '<table><tr><th>' + form('<input type="submit" name="disable" value="Supprimer">') + '</th></tr><tr><td colspan="4">' +
      form(options(false) ? dropdown + '<input type="submit" name="add" value="Ajouter">' : '') + '</td></tr><tr><td>Titre</td><td colspan="3"></td></tr>' +
      stored.map((value, index) => `<tr><td>${entryLabel(value)}</td><td>${form(field('id', String(700 + index)) + '<button type="submit" name="purge">Supprimer</button>')}</td><td></td><td></td></tr>`).join('') + '</table>';
    const handlers = modern ? `<script>
      document.querySelector('button[name="activate"]')?.addEventListener('click', async () => {
        await fetch('/glpi/ajax/displaypreference.php', {method:'POST',body:new URLSearchParams({activate:'1',itemtype:'Ticket',users_id:'17'})});
        location.reload();
      });
      document.querySelector('button[name="add_opt"]')?.addEventListener('click', async () => {
        const input = document.querySelector('select[name="num"]'); const value = input.value;
        const li = document.createElement('li'); li.dataset.optId = value;
        const label = document.createElement('span'); label.className = 'flex-fill'; label.textContent = input.selectedOptions[0].textContent; li.append(label);
        document.querySelector('ul.list-group').append(li);
        const body = new URLSearchParams({action:'update_order',itemtype:'Ticket',users_id:'17'});
        document.querySelectorAll('li[data-opt-id]:not([data-fixed])').forEach(node => body.append('opts[]', node.dataset.optId));
        await fetch('/glpi/ajax/displaypreference.php', {method:'POST',body});
      });
    </script>` : '';
    return html(tab + `<div id="tabsbody" class="${modern ? 'display_preference_config' : ''}">${content}</div>` + handlers);
  }
  function list() {
    const render = ++renderNumber;
    const headers = ['Titre', ...(active ? stored : inherited).map(value => choices.find(item => item.value === value).label)];
    const table = `<table id="ticket-results" data-render="${render}" class="${version === 'legacy' ? 'tab_cadre_fixehov' : 'search-results'}"><thead><tr>${headers.map(label => '<th>' + label + '</th>').join('')}</tr></thead><tbody><tr>${headers.map(() => '<td>Exemple synthétique</td>').join('')}</tr></tbody></table>`;
    if (version === 'legacy') return html(`<form name="searchformTicket"></form><span id="columns-opener" class="fa fa-wrench pointer" onclick="$('#search_config_top').dialog('open');">Colonnes</span>
      <form id="massformTicket" method="post" action="/glpi/front/massiveaction.php">${table}</form>
      <div id="dialog-top" class="ui-dialog" role="dialog" hidden><button class="ui-dialog-titlebar-close">Fermer</button><div id="search_config_top"><iframe id="Iframesearch_config_top" class="iframe hidden"></iframe></div></div>
      <script>window.$ = () => ({dialog: () => {document.querySelector('#dialog-top').hidden=false;const frame=document.querySelector('iframe');frame.src='/glpi/front/displaypreference.form.php?itemtype=Ticket';frame.classList.remove('hidden');}});document.querySelector('.ui-dialog-titlebar-close').onclick=()=>location.reload();</script>`);
    const modalId = version === 'modern' ? 'displaypreference_modal' + render : 'displayprefence_modal' + render;
    return html(`<div class="search-card"><div class="search-header"><button id="columns-opener" class="show_displaypreference_modal" data-modal="${modalId}" type="button">Colonnes</button></div><form id="massformTicket" data-search-itemtype="Ticket" method="get" action="/glpi/front/massiveaction.php">${table}</form></div>
      <script>
      function bindColumns() { document.querySelector('#columns-opener').onclick = () => {
        const id = document.querySelector('#columns-opener').dataset.modal;
        let node = document.getElementById(id);
        if (!node) {
          node=document.createElement('div');node.id=id;node.className='modal';node.setAttribute('role','dialog');
          node.innerHTML='<button type="button" data-bs-dismiss="modal">Fermer</button><iframe src="/glpi/front/displaypreference.form.php?itemtype=Ticket"></iframe>';document.body.append(node);
          node.querySelector('button').onclick=async()=>{
            if (${version === 'modern'}) {
              node.hidden=true;
              if (window.reusePreferenceFrame) return;
              const response=await fetch('/glpi/ajax/search.php', {method:'POST',body:new URLSearchParams(location.search)});
              const fresh=new DOMParser().parseFromString(await response.text(),'text/html');
              document.querySelector('.search-card').replaceWith(fresh.querySelector('.search-card'));bindColumns();
            } else location.reload();
          };
        }
        node.hidden=false;
      }; }
      bindColumns();
      </script>`);
  }
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) return route.abort();
    if (url.pathname.endsWith('/ajax/search.php')) return route.fulfill({ contentType: 'text/html', body: list() });
    if (request.method() === 'POST') {
      const data = new URLSearchParams(request.postData() || '');
      requests.push({ path: url.pathname, user: data.get('users_id'), values: data.getAll('opts[]') });
      if (data.get('users_id') !== '17' || data.get('itemtype') !== 'Ticket') return route.fulfill({ status: 403, body: 'Wrong personal scope' });
      if (data.has('activate')) { active = true; stored = [...inherited]; }
      else if (data.has('add')) stored.push(data.get('num'));
      else if (data.get('action') === 'update_order') stored = data.getAll('opts[]');
      else return route.fulfill({ status: 400, body: 'Unexpected mutation' });
      if (url.pathname.endsWith('/ajax/displaypreference.php')) return route.fulfill({ status: 200, body: '' });
    }
    if (url.pathname.endsWith('/front/ticket.php')) return route.fulfill({ contentType: 'text/html', body: list() });
    if (url.pathname.endsWith('/front/displaypreference.form.php')) {
      if (request.method() === 'GET') preferenceReads++;
      return route.fulfill({ contentType: 'text/html', body: preferences(url.searchParams.get('view') || 'personal') });
    }
    return route.fulfill({ status: 404, body: 'Unrecognized fixture request' });
  });
  const read = frame => frame.evaluate(() => window.__glpiPilotColumns.inspectPreferences());
  const inspect = () => page.evaluate(() => window.__glpiPilotColumns.inspect('#ticket-results'));
  const open = async () => {
    await page.evaluate(script); const state = await inspect(); await page.locator(state.openerSelector).click();
    await page.waitForFunction(() => window.__glpiPilotColumns.inspect('#ticket-results').iframeSelector.length > 0);
    const windowState = await inspect();
    const element = await page.locator(windowState.iframeSelector).elementHandle(); const frame = await element.contentFrame();
    await frame.locator('#tabsbody').waitFor(); await frame.evaluate(script); return { state: await inspect(), frame };
  };
  const close = async opened => {
    if (version !== 'modern') return Promise.all([page.waitForNavigation(), page.locator(opened.state.closeSelector).click()]);
    const before = await page.locator('#ticket-results').getAttribute('data-render');
    await page.locator(opened.state.closeSelector).click();
    await page.waitForFunction(previous => document.querySelector('#ticket-results').dataset.render !== previous, before);
  };
  const direct = async (view = 'personal') => {
    await page.goto(origin + '/glpi/front/displaypreference.form.php?itemtype=Ticket&view=' + view); await page.evaluate(script); return read(page);
  };
  try {
    for (version of ['legacy', 'ten', 'modern']) {
      active = false; stored = [];
      await page.goto(origin + '/glpi/front/ticket.php?criteria[0][field]=12&criteria[0][value]=notold');
      await page.evaluate(script);
      const before = await page.locator('html').innerHTML(), writes = requests.length;
      const initial = await inspect();
      equal([initial.supported, initial.openerSelector, await page.locator('html').innerHTML(), requests.length], [true, '#columns-opener', before, writes], version + ' discovers the native column opener without DOM or network mutation');
      let opened = await open(), state = await read(opened.frame);
      ok(opened.state.iframeSelector && opened.state.closeSelector && state.activateSelector && state.usersId === '17' && state.personalScope && !state.personalActive, version + ' identifies native close, iframe and a personal view still requiring activation');
      await Promise.all([opened.frame.waitForNavigation(), opened.frame.locator(state.activateSelector).click()]);
      await opened.frame.evaluate(script); state = await read(opened.frame);
      equal(state.selected.map(item => item.label), ['Titre', 'Statut', 'ID'], version + ' personal activation keeps inherited columns in their original order');
      equal(state.selectedIdsKnown, version === 'modern', version + ' does not confuse legacy preference record IDs with column IDs');
      equal(state.options.filter(item => item.label === 'Rédacteur').map(item => [item.group, item.fullLabel]), [['Suivis', 'Suivis - Rédacteur'], ['Tâches', 'Tâches - Rédacteur']], version + ' keeps identical author labels separated by their native group');
      await opened.frame.locator(state.selectSelector).selectOption('8');
      equal((await read(opened.frame)).selectedValue, '8', version + ' reports the actual selected column value before a native submit');
      if (version === 'modern') await Promise.all([page.waitForResponse(response => response.url().endsWith('/ajax/displaypreference.php') && response.request().method() === 'POST'), opened.frame.locator(state.addSelector).click()]);
      else await Promise.all([opened.frame.waitForNavigation(), opened.frame.locator(state.addSelector).click()]);
      await opened.frame.evaluate(script); state = await read(opened.frame);
      ok(version === 'modern' ? state.selected.some(item => item.value === '8') : !state.options.some(item => item.value === '8'), version + ' exposes the native proof that the chosen missing column was added');
      const previousFrame = opened.frame, previousSelect = state.selectSelector;
      await close(opened); const readsBeforeReopen = preferenceReads;
      opened = await open(); state = await read(opened.frame);
      equal(state.selected.map(item => item.label), ['Titre', 'Statut', 'ID', entryLabel('8')], version + ' persists the appended column across native close and reopen');
      ok(new URL(page.url()).searchParams.get('criteria[0][value]') === 'notold' && requests.every(request => request.user === '17') && state.selectSelector !== previousSelect,
        version + ' re-discovers the regenerated dropdown ID, retains search filters and never submits a global preference');
      if (version === 'modern') {
        equal(state.options.length, choices.length, 'GLPI 11 keeps all native options in its select; selected IDs must be subtracted');
        ok(opened.frame !== previousFrame && preferenceReads === readsBeforeReopen + 1 && await page.locator('iframe').count() === 2,
          'GLPI 11 AJAX refresh preserves a hidden old modal while the re-opened visible iframe performs a fresh server read');
        await page.evaluate(() => { window.reusePreferenceFrame = true; });
        const reusedFrame = opened.frame, readsBeforeReuse = preferenceReads;
        await page.locator(opened.state.closeSelector).click(); opened = await open();
        ok(opened.frame === reusedFrame && preferenceReads === readsBeforeReuse,
          'A reused GLPI 11 iframe does not prove persistence merely because its optimistic DOM becomes visible again');
      }
    }

    version = 'ten'; active = true; stored = [...inherited];
    let state = await direct('global');
    equal([state.supported, state.personalActive, state.personalTabSelector, state.addSelector, state.activateSelector], [true, false, '#personal-tab', '', ''], 'A global form yields only a personal-tab action, never an add or activation action');
    await page.evaluate(() => document.querySelector('#personal-tab').remove()); state = await read(page);
    ok(!state.supported && !state.addSelector, 'Global-only rights produce a clear unsupported state without a mutation');
    state = await direct('global-readonly');
    ok(state.supported && state.personalTabSelector === '#personal-tab' && !state.addSelector, 'A read-only global tab still permits the independently proved personal tab');
    await direct('global');
    await page.evaluate(() => { const tabs = document.querySelector('#tabspanel'); tabs.style.display = 'none'; const select = document.createElement('select'); select.id = 'tabspanel-select'; select.innerHTML = '<option value="0">Vue globale</option><option value="1">Vue personnelle</option>'; tabs.after(select); });
    state = await read(page);
    equal([state.personalTabSelector, state.personalTabSelectSelector, state.personalTabValue], ['', '#tabspanel-select', '1'], 'The compact GLPI 10 tab control is matched to its native personal link');

    await direct();
    await page.evaluate(() => document.querySelector('input[name="users_id"]').value = '0');
    ok(!(await read(page)).supported, 'Contradictory user identities inside one preference body are not actionable');
    await direct(); await page.evaluate(() => document.querySelector('select[name="num"]').form.action = 'https://unrelated.invalid/front/displaypreference.form.php');
    ok(!(await read(page)).supported, 'Cross-origin preference form actions are refused');
    await direct(); await page.evaluate(() => document.querySelector('select[name="num"]').form.action = '/glpi/front/ticket.form.php');
    ok(!(await read(page)).supported, 'A ticket business form cannot masquerade as a display-preference form');
    await direct(); await page.evaluate(() => document.querySelector('input[name="add"]').setAttribute('formaction', '/glpi/front/massiveaction.php'));
    ok(!(await read(page)).supported, 'Submit overrides cannot redirect a recognized personal column addition');
    await direct(); await page.evaluate(() => { const select = document.querySelector('select[name="num"]'); select.append(select.options[0].cloneNode(true)); });
    ok(!(await read(page)).supported, 'Duplicated native option IDs are refused instead of selected arbitrarily');
    await direct(); await page.evaluate(() => document.querySelector('select[name="num"]').style.display = 'none');
    ok(!(await read(page)).supported, 'An unrecognized hidden column select is not automated');
    await page.evaluate(() => { const select = document.querySelector('select[name="num"]'); select.className = 'select2-hidden-accessible'; const container = document.createElement('span'); container.className = 'select2-container'; container.textContent = 'Option'; select.after(container); });
    state = await read(page);
    ok(state.supported && state.selectHidden, 'A visible native Select2 wrapper permits controlled selection of its hidden select');
    const beforeRebuild = state;
    await page.evaluate(() => {
      const select = document.querySelector('select[name="num"]');
      const replacement = select.cloneNode(true);
      replacement.id = 'dropdown_num_rebuilt';
      replacement.value = '8';
      select.replaceWith(replacement);
    });
    state = await read(page);
    equal([state.supported, state.usersId, state.proof, state.options, state.selected, state.selectedValue, state.selectSelector !== beforeRebuild.selectSelector],
      [true, beforeRebuild.usersId, beforeRebuild.proof, beforeRebuild.options, beforeRebuild.selected, '8', true],
      'Replacing the native dropdown retains its personal semantics and exposes the current selected value with a fresh locator');
    stored = choices.map(item => item.value); await direct(); state = await read(page);
    ok(state.supported && state.options.length === 0 && state.selected.length === choices.length + 1 && !state.addSelector, 'A complete legacy personal view needs no additional mutations');
    await page.goto(origin + '/glpi/front/ticket.php'); await page.evaluate(script); await page.evaluate(() => document.querySelector('form[data-search-itemtype]').dataset.searchItemtype = 'Computer');
    ok(!(await inspect()).supported, 'Another item type is never prepared as a ticket list');
    await page.goto(origin + '/glpi/front/ticket.php'); await page.evaluate(script);
    await page.evaluate(() => document.querySelector('td').append(document.querySelector('#columns-opener')));
    ok(!(await inspect()).supported, 'A preference-looking control inside ticket content cannot become a native command');
    version = 'legacy'; await page.goto(origin + '/glpi/front/ticket.php'); await page.evaluate(script);
    await page.evaluate(() => document.querySelector('#massformTicket').replaceWith(document.querySelector('#ticket-results')));
    ok((await inspect()).supported, 'GLPI 9.5 users without massive-action rights retain the separately proven ticket search column command');
    equal(requests.filter(request => request.user === '0').length, 0, 'All column preparation fixtures leave global preferences untouched');
  } finally { await page.close(); }
};
