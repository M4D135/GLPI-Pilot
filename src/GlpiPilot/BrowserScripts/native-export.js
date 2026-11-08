(() => {
  'use strict';

  // Read-only DOM discovery. The host performs the reviewed browser actions and
  // captures the resulting download. No endpoint is constructed or requested here.
  const VERSION = '1.1.1';
  const MENU = '.dropdown-menu,[role="menu"]';
  const TABLE = 'table,[role="grid"],[role="table"]';
  const LEGACY_PAGER = 'table.tab_cadre_pager';
  const RESULT_TABLE = 'table.search-results,table.tab_cadrehov,table.tab_cadre_fixehov';
  const EXPORT_CONTROLS = '[id^="dropdown-export"],select[name="display_type"],select[name="glpilist_limit"],[data-bs-toggle="dropdown"],[data-toggle="dropdown"],a[href*="report.dynamic.php"]';
  const tidy = value => String(value || '').replace(/\s+/g, ' ').trim();
  const normalized = value => tidy(value).normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
  const allWords = value => /\b(all\s+pages|toutes?\s+(?:les\s+)?pages|todas?\s+las\s+paginas)\b/.test(normalized(value));
  const currentWords = value => /\b(current\s+page|page\s+(?:courante|actuelle)|pagina\s+actual)\b/.test(normalized(value));
  const csvWords = value => /\bcsv\b/.test(normalized(value));
  const exportWords = value => /\b(export|exporter|exportation|exportar|exportacion)\b/.test(normalized(value));
  const forbiddenAction = /^(?:delete|purge|remove|save|update|create|add|massiveaction|massive_action|archive|unarchive|restore)$/i;

  function visible(element) {
    if (!(element instanceof Element) || !element.isConnected) return false;
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.hidden || node.getAttribute('aria-hidden') === 'true' || node.hasAttribute('inert')) return false;
      const style = getComputedStyle(node);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    return element.getClientRects().length > 0;
  }

  function disabled(element) {
    return !element || element.matches(':disabled,[disabled],[aria-disabled="true"],.disabled') ||
      !!element.closest('fieldset[disabled],[aria-disabled="true"]');
  }

  function label(element) {
    if (!element) return '';
    const values = [element.getAttribute('aria-label'), element.getAttribute('title'), element.getAttribute('alt'), element.textContent];
    for (const child of element.querySelectorAll('[title],[aria-label]')) {
      values.push(child.getAttribute('title'), child.getAttribute('aria-label'));
    }
    return tidy(values.filter(Boolean).join(' '));
  }

  function selector(element) {
    if (!element) return '';
    const parts = [];
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.id) {
        const id = '#' + CSS.escape(node.id);
        if (document.querySelectorAll(id).length === 1) {
          parts.unshift(id);
          return parts.join(' > ');
        }
      }
      const tag = node.localName;
      const siblings = node.parentElement ? [...node.parentElement.children].filter(item => item.localName === tag) : [node];
      parts.unshift(tag + (siblings.length > 1 ? ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')' : ''));
    }
    return parts.join(' > ');
  }

  function nativeUrl(value, requireAll = false) {
    try {
      const url = new URL(value, document.baseURI);
      if (!/^https?:$/.test(url.protocol) || url.origin !== location.origin || url.username || url.password) return null;
      // Validate an existing DOM destination against the native GLPI export form.
      if (!/\/front\/report\.dynamic\.php\/?$/i.test(url.pathname)) return null;
      if ([...url.searchParams.keys()].some(key => forbiddenAction.test(key))) return null;
      if (requireAll) {
        const modes = url.searchParams.getAll('display_type');
        if (modes.length !== 1 || !/^-[1-9]\d*$/.test(modes[0])) return null;
      }
      return url;
    } catch { return null; }
  }

  function dataTable(element) {
    if (!visible(element)) return false;
    // GLPI 9.x pagers use TH for navigation arrows. They are controls, not result
    // tables; counting them as data prevented reaching the adjacent export form.
    if (element.matches(LEGACY_PAGER) || element.closest('form[name^="searchform"],form[id^="searchform"]')) return false;
    if (element.matches(RESULT_TABLE + ',[role="grid"],[role="table"]')) return true;
    const rows = [...element.querySelectorAll('tr,[role="row"]')].filter(row => row.closest(TABLE) === element && visible(row));
    const headers = [...element.querySelectorAll('th,[role="columnheader"]')].filter(cell => cell.closest(TABLE) === element && visible(cell));
    return headers.length > 0 && rows.length > 1 && rows.some(row => {
      const cells = [...row.querySelectorAll('td,[role="cell"],[role="gridcell"]')].filter(cell => cell.closest(TABLE) === element && visible(cell));
      return cells.length >= 2 && cells.some(cell => !cell.querySelector('input,select,button,textarea'));
    });
  }

  function tablesIn(element) {
    return [...element.querySelectorAll(TABLE)].filter(dataTable);
  }

  function regionFor(table) {
    const card = table.closest('.search-card');
    if (card) return card;
    const display = table.closest('.search-display-data');
    if (display && tablesIn(display).length === 1) return display;
    for (let node = table.parentElement; node; node = node.parentElement) {
      if (tablesIn(node).length > 1) break;
      if (node.querySelector(EXPORT_CONTROLS)) return node;
    }
    return table.parentElement;
  }

  function associatedPagers(table, region) {
    return [...region.querySelectorAll(LEGACY_PAGER)].filter(pager => {
      if (!visible(pager) || table.contains(pager)) return false;
      for (let owner = pager.parentElement; owner; owner = owner.parentElement) {
        const candidates = tablesIn(owner);
        if (candidates.length) return candidates.length === 1 && candidates[0] === table;
        if (owner === region) break;
      }
      return false;
    });
  }

  function pagerRange(pager) {
    // Examine only the dedicated pager, excluding form values and result cells.
    const text = normalized([...pager.querySelectorAll('td,th')]
      .filter(cell => cell.closest('table') === pager && !cell.querySelector('select,input,textarea,button'))
      .map(cell => cell.textContent).join(' '));
    const number = '(\\d(?:[\\d\\s,.]*\\d)?)';
    const match = text.match(new RegExp('\\b(?:de|from)\\s+' + number + '\\s+(?:a|to)\\s+' + number + '\\s+(?:sur|of)\\s+' + number + '(?:\\b|$)', 'i'));
    if (!match) return null;
    const values = match.slice(1, 4).map(value => Number(value.replace(/\D/g, '')));
    if (values.some(value => !Number.isSafeInteger(value) || value < 0 || value > 2147483647)) return null;
    if (values[0] > values[1] || values[1] > values[2]) return null;
    return { start: values[0], end: values[1], total: values[2] };
  }

  function expectedTotal(table, pagers, diagnostics) {
    const form = table.closest('form[data-search-itemtype][data-count]');
    const raw = form?.getAttribute('data-count')?.trim();
    if (raw && /^\d+$/.test(raw)) {
      const value = Number(raw);
      if (Number.isSafeInteger(value) && value <= 2147483647) { diagnostics.push('total_source=data-count'); return value; }
    }
    const totals = [...new Set(pagers.map(pagerRange).filter(value => value !== null).map(range => range.total))];
    if (totals.length === 1) { diagnostics.push('total_source=associated-legacy-pager'); return totals[0]; }
    diagnostics.push(totals.length > 1 ? 'total_source=conflicting-legacy-pagers' : 'total_source=unknown');
    return null;
  }

  function selectState(select) {
    const marker = select.matches('.select2-hidden-accessible,.select2-offscreen,[data-select2-id]');
    const widgets = [select.nextElementSibling, select.previousElementSibling,
      select.id ? document.getElementById('s2id_' + select.id) : null];
    const widget = widgets.some(node => node?.matches('.select2,.select2-container') && visible(node));
    if (marker && widget) return { hidden: true };
    return visible(select) ? { hidden: false } : null;
  }

  function displayPlan(pagers, total, diagnostics) {
    if (!Number.isSafeInteger(total) || total <= 0) return null;
    const candidates = [];
    for (const pager of pagers) {
      for (const select of pager.querySelectorAll('select[name="glpilist_limit"]')) {
        const state = selectState(select);
        const form = select.form;
        if (!state || disabled(select) || !form || !pager.contains(form)) continue;
        const method = (form.getAttribute('method') || 'get').toLowerCase();
        if (!['get', 'post'].includes(method)) continue;
        let action;
        try { action = new URL(form.getAttribute('action') || location.href, document.baseURI); }
        catch { continue; }
        if (!/^https?:$/.test(action.protocol) || action.origin !== location.origin || action.pathname !== location.pathname || action.username || action.password) continue;
        if ([...action.searchParams.keys()].some(key => forbiddenAction.test(key)) ||
            [...form.querySelectorAll('[name]')].some(element => forbiddenAction.test(element.getAttribute('name')))) continue;
        if (form.hasAttribute('onsubmit')) continue;
        const onchange = (select.getAttribute('onchange') || '').trim();
        if (!/^(?:(?:this\.form|form)\.)?submit\(\)\s*;?$/.test(onchange)) continue;
        const options = [...select.options].filter(option => !option.disabled && !option.closest('optgroup[disabled]') &&
          /^[1-9]\d*$/.test(option.value) && Number.isSafeInteger(Number(option.value)) && Number(option.value) >= total);
        options.sort((a, b) => Number(a.value) - Number(b.value));
        if (!options.length) continue;
        const offsets = action.searchParams.getAll('start');
        const currentOffsets = new URL(location.href).searchParams.getAll('start');
        const range = pagerRange(pager);
        const startsAtZero = !!range && range.start <= 1 &&
          (offsets.length ? offsets : currentOffsets).every(value => /^0+$/.test(value));
        candidates.push({
          kind: 'select-change', selector: selector(select), value: options[0].value,
          expectedTotal: total, formSelector: selector(form), formMethod: method,
          hidden: state.hidden, startsAtZero, proof: 'native-pager-list-limit-submit'
        });
      }
    }
    // Multiple dedicated pagers can expose the same list limit. Prefer the first
    // visible associated pager; all candidates must agree on the required size.
    if (!candidates.length) { diagnostics.push('display_all=unavailable'); return null; }
    if (new Set(candidates.map(candidate => candidate.value + '|' + candidate.formMethod + '|' + candidate.startsAtZero)).size > 1) {
      diagnostics.push('display_all=ambiguous'); return null;
    }
    diagnostics.push('display_all=verified-existing-option');
    return candidates[0];
  }

  function exportToggle(element) {
    if (!visible(element) || disabled(element)) return false;
    if (element.closest('.massiveactions-control')) return false;
    const type = (element.getAttribute('type') || '').toLowerCase();
    if (element.localName === 'button' && (type === 'submit' || (!type && element.closest('form')))) return false;
    const dropdown = element.getAttribute('data-bs-toggle') === 'dropdown' ||
      element.getAttribute('data-toggle') === 'dropdown' || element.getAttribute('aria-haspopup') === 'menu';
    const known = /^dropdown-export(?:-|$)/.test(element.id) || element.getAttribute('name') === 'export';
    return dropdown && (known || exportWords(label(element)));
  }

  function associatedMenu(toggle, region) {
    const id = toggle.id;
    const controlled = tidy(toggle.getAttribute('aria-controls')).split(' ').filter(Boolean);
    const target = toggle.getAttribute('data-bs-target') || toggle.getAttribute('data-target');
    if (target && /^#[A-Za-z0-9_:-]+$/.test(target)) controlled.push(target.slice(1));
    const menus = [...document.querySelectorAll(MENU)].filter(menu =>
      (id && tidy(menu.getAttribute('aria-labelledby')).split(' ').includes(id)) ||
      (menu.id && controlled.includes(menu.id))
    );
    const local = menus.filter(menu => region.contains(menu));
    if (local.length === 1) return local[0];
    if (local.length > 1) {
      const sibling = local.filter(menu => menu.parentElement === toggle.parentElement);
      return sibling.length === 1 ? sibling[0] : null;
    }
    // GLPI 11 can move the opened menu under body. Its ARIA association survives.
    if (menus.length === 1 && (!id || document.querySelectorAll('#' + CSS.escape(id)).length === 1)) return menus[0];
    const sibling = [...(toggle.parentElement?.children || [])].filter(node => node.matches(MENU));
    return sibling.length === 1 ? sibling[0] : null;
  }

  function sectionLabel(anchor, menu) {
    let item = anchor;
    while (item.parentElement && item.parentElement !== menu) item = item.parentElement;
    if (item.parentElement !== menu) return '';
    for (let previous = item.previousElementSibling; previous; previous = previous.previousElementSibling) {
      if (previous.matches('.dropdown-header,h1,h2,h3,h4,h5,h6')) return label(previous);
      const heading = previous.querySelector('.dropdown-header,h1,h2,h3,h4,h5,h6');
      if (heading) return label(heading);
      if (previous.matches('.dropdown-divider,hr')) break;
    }
    return '';
  }

  function allCsvLink(anchor, menu) {
    if (disabled(anchor) || !anchor.hasAttribute('href') || anchor.hasAttribute('onclick')) return null;
    const own = label(anchor);
    const group = menu ? sectionLabel(anchor, menu) : '';
    if (!csvWords(own) || !allWords(own + ' ' + group) || currentWords(own + ' ' + group)) return null;
    if (/\b(pdf|slk|ods|xlsx)\b/.test(normalized(own))) return null;
    const url = nativeUrl(anchor.getAttribute('href'), true);
    if (!url) return null;
    return { anchor, url, label: own, proof: group ? 'csv-in-all-pages-section-and-negative-display-type' : 'all-pages-csv-label-and-negative-display-type' };
  }

  function plan(kind, fields) {
    return Object.assign({
      kind, menuSelector: '', formatSelector: '', formatValue: '', scopeSelector: '', scopeValue: '',
      submitSelector: '', directSelector: '', formSelector: '', label: 'CSV — Toutes les pages',
      allPages: true, format: 'csv', needsOpen: kind === 'menu', formatHidden: false, proof: ''
    }, fields);
  }

  function legacyForms(region, diagnostics) {
    const results = [];
    const forms = [...region.querySelectorAll('form')];
    if (region.matches('form')) forms.unshift(region);
    let index = 0;
    for (const form of forms) {
      if (!form.querySelector('select[name="display_type"]')) continue;
      const prefix = 'legacy_form_' + (++index) + '=';
      const reject = code => { if (index <= 8) diagnostics.push(prefix + code); };
      if (!visible(form)) { reject('hidden'); continue; }
      if ((form.getAttribute('method') || 'get').toLowerCase() !== 'get') { reject('method-not-get'); continue; }
      const url = nativeUrl(form.getAttribute('action') || location.href);
      if (!url) { reject('endpoint-not-native-same-origin'); continue; }
      if (form.hasAttribute('onsubmit') || [...form.querySelectorAll('[name]')].some(element => forbiddenAction.test(element.getAttribute('name')))) {
        reject('custom-submit-or-business-field'); continue;
      }
      const selects = [...form.querySelectorAll('select[name="display_type"]')].filter(item => item.form === form && !disabled(item));
      if (selects.length !== 1) { reject('format-select-absent-or-ambiguous'); continue; }
      const select = selects[0];
      const state = selectState(select);
      if (!state) { reject('format-select-hidden-without-widget'); continue; }
      const options = [...select.options].filter(option =>
        !option.disabled && !option.closest('optgroup[disabled]') &&
        csvWords(option.textContent) && allWords(option.textContent) && !currentWords(option.textContent) &&
        /^-[1-9]\d*$/.test(option.value)
      );
      if (options.length !== 1) { reject('all-pages-csv-option-absent-or-ambiguous'); continue; }
      const submits = [...form.querySelectorAll('button,input[type="submit"],input[type="image"]')].filter(button =>
        button.form === form && visible(button) && !disabled(button) &&
        (button.localName === 'input' || (button.getAttribute('type') || 'submit').toLowerCase() === 'submit') &&
        (button.getAttribute('name') === 'export' || exportWords(label(button))) &&
        !button.hasAttribute('formaction') && !button.hasAttribute('formmethod') && !button.hasAttribute('onclick')
      );
      if (submits.length !== 1) { reject('export-submit-absent-or-ambiguous'); continue; }
      // A GLPI native export form is isolated from the massive-action form.
      // Browser submission preserves its actual fields; discovery never copies them.
      results.push({
        identity: form,
        plan: plan('form', {
          formatSelector: selector(select), formatValue: options[0].value,
          submitSelector: selector(submits[0]), formSelector: selector(form),
          formatHidden: state.hidden, proof: 'native-get-export-form-and-all-pages-csv-option'
        })
      });
      reject(submits[0].matches('input[type="image"]') ? 'verified-get-image-submit' : 'verified-get-submit');
    }
    return results;
  }

  function inspect(tableSelector) {
    // Diagnostics are an allowlisted structural summary. Never include labels,
    // cell values, selectors, URL queries, form values, or authentication material.
    const diagnostics = [
      'adapter=' + VERSION,
      'dom_tables=' + [...document.querySelectorAll(TABLE)].filter(visible).length + ';data_tables=' + tablesIn(document).length,
      'legacy_pagers=' + document.querySelectorAll(LEGACY_PAGER).length + ';format_lists=' + document.querySelectorAll('select[name="display_type"]').length + ';limit_lists=' + document.querySelectorAll('select[name="glpilist_limit"]').length
    ];
    const base = { supported: false, reason: '', origin: location.origin, tableSelector: '', containerSelector: '', expectedTotal: null, rangeStart: null, rangeEnd: null, plans: [], displayPlan: null, diagnostics };
    const fail = reason => Object.assign(base, { reason, supported: false, plans: [] });
    if (!/^https?:$/.test(location.protocol)) return fail('La page doit être une page GLPI HTTP ou HTTPS.');
    if ([...document.querySelectorAll('input[type="password"]')].some(visible)) return fail('La connexion est encore en cours.');
    let table;
    if (typeof tableSelector === 'string' && tableSelector.trim()) {
      let found;
      try { found = [...document.querySelectorAll(tableSelector)].filter(visible); }
      catch { return fail('Le sélecteur du tableau est invalide.'); }
      if (found.length !== 1 || !found[0].matches(TABLE)) return fail('Le tableau mémorisé ne correspond pas à un tableau visible unique.');
      table = found[0];
    } else {
      const official = [...document.querySelectorAll(RESULT_TABLE)].filter(visible);
      if (official.length > 1) return fail('Plusieurs tableaux GLPI sont présents : choisissez le tableau à exporter.');
      if (official.length === 1) table = official[0];
      else {
        const found = tablesIn(document);
        if (found.length !== 1) return fail(found.length ? 'Plusieurs tableaux sont présents : choisissez le tableau à exporter.' : 'Aucun tableau visible n’est prêt.');
        table = found[0];
      }
    }
    base.tableSelector = selector(table);
    const region = regionFor(table);
    if (!region) return fail('Les commandes du tableau sont introuvables.');
    base.containerSelector = selector(region);
    const tables = tablesIn(region).filter(item => !item.contains(table) && !table.contains(item) || item === table);
    diagnostics.push('scope_data_tables=' + tables.length + ';scope_format_lists=' + region.querySelectorAll('select[name="display_type"]').length);
    if (tables.length > 1) return fail('Les commandes d’export ne sont pas associées à un tableau unique.');
    const pagers = associatedPagers(table, region);
    diagnostics.push('associated_legacy_pagers=' + pagers.length);
    base.expectedTotal = expectedTotal(table, pagers, diagnostics);
    const ranges = [...new Set(pagers.map(pagerRange).filter(Boolean).map(range => JSON.stringify(range)))];
    if (ranges.length === 1) {
      const range = JSON.parse(ranges[0]);
      if (range.total === base.expectedTotal) { base.rangeStart = range.start; base.rangeEnd = range.end; }
    }
    base.displayPlan = displayPlan(pagers, base.expectedTotal, diagnostics);

    const candidates = [];
    const lazyMenus = [];
    const toggles = [...region.querySelectorAll('button,[role="button"],a')].filter(exportToggle);
    for (const toggle of toggles) {
      const menu = associatedMenu(toggle, region);
      if (!menu) continue;
      const links = [...menu.querySelectorAll('a[href]')].map(anchor => allCsvLink(anchor, menu)).filter(Boolean);
      for (const link of links) {
        const shown = visible(link.anchor);
        const actionable = shown || (!visible(menu) && !disabled(toggle));
        if (!actionable) continue;
        candidates.push({
          identity: link.url.href,
          visible: shown,
          plan: plan(shown ? 'direct' : 'menu', {
            menuSelector: selector(toggle), directSelector: selector(link.anchor), proof: link.proof
          })
        });
      }
      // Only truly empty, recognized export menus are eligible for lazy loading.
      // Menus that advertise current-page-only CSV are not a valid all-pages plan.
      if (!links.length && !visible(menu) && !tidy(menu.textContent) && !menu.querySelector('a,button,input,select')) {
        lazyMenus.push(plan('menu', { menuSelector: selector(toggle), allPages: false, proof: 'empty-native-export-menu-open-and-reinspect' }));
      }
    }
    // Some themes expose native export links directly in the selected card.
    for (const anchor of region.querySelectorAll('a[href]')) {
      if (!visible(anchor) || anchor.closest(MENU)) continue;
      const link = allCsvLink(anchor, null);
      if (link) candidates.push({ identity: link.url.href, visible: true, plan: plan('direct', { directSelector: selector(anchor), proof: link.proof }) });
    }
    const forms = legacyForms(region, diagnostics);
    const destinations = new Set(candidates.map(item => item.identity));
    diagnostics.push('export_candidates:links=' + destinations.size + ';forms=' + forms.length + ';menus=' + lazyMenus.length);
    if (destinations.size > 1 || forms.length > 1 || (forms.length && candidates.length)) return fail('Plusieurs exports natifs distincts correspondent au tableau.');
    let chosen;
    if (candidates.length) chosen = candidates.find(item => item.visible)?.plan || candidates[0].plan;
    else if (forms.length === 1) chosen = forms[0].plan;
    else if (lazyMenus.length === 1) chosen = lazyMenus[0];
    else if (lazyMenus.length > 1) return fail('Plusieurs menus d’export doivent être distingués.');
    if (!chosen) return fail('Aucune commande native CSV pour toutes les pages n’a été reconnue autour de ce tableau.');
    return Object.assign(base, { supported: true, reason: chosen.needsOpen ? 'Ouvrir le menu d’export puis vérifier sa commande CSV toutes pages.' : 'La commande native CSV toutes pages est identifiée.', plans: [chosen] });
  }

  window.__glpiPilotNative = Object.freeze({ version: VERSION, inspect });
})();
