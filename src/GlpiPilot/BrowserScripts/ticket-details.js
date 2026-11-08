(() => {
  'use strict';
  // Read-only discovery and extraction. The host alone may click a proven GLPI
  // read tab; this adapter never sets a field, submits, fetches, or reads auth storage.
  const TABLE = 'table,[role="grid"],[role="table"]';
  const ROOTS = '#itil-form,#itil-object-container,#tabsbody';
  const TIMELINE = '.timeline_history,.itil-timeline,.timeline-container';
  const TIMELINE_ITEMS = '.h_item,.timeline-item,[data-timeline-item]';
  const tidy = value => String(value || '').replace(/\s+/g, ' ').trim();
  const norm = value => tidy(value).normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
  const sensitive = value => /(?:password|passwd|mot\s+de\s+passe|\bpwd\b|token|jeton|csrf|cookie|secret|api[_ -]?key|cle\s+api|authorization|access[_ -]?key|session[_ -]?(?:id|key)|one[_ -]?time[_ -]?code)/i.test(norm(value));
  const canonicalId = value => /^\d+$/.test(String(value || '').trim()) ? String(value).trim().replace(/^0+(?=\d)/, '') : '';

  function visible(element) {
    if (!(element instanceof Element) || !element.isConnected) return false;
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.hidden || node.getAttribute('aria-hidden') === 'true' || node.hasAttribute('inert')) return false;
      const style = getComputedStyle(node);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    return element.getClientRects().length > 0;
  }

  function css(element) {
    const parts = [];
    for (let node = element; node instanceof Element; node = node.parentElement) {
      const id = node.getAttribute('id'); // form.id may be shadowed by input[name=id].
      if (id) {
        const selector = '#' + CSS.escape(id);
        if (document.querySelectorAll(selector).length === 1) { parts.unshift(selector); return parts.join(' > '); }
      }
      const siblings = node.parentElement ? [...node.parentElement.children].filter(item => item.localName === node.localName) : [node];
      parts.unshift(node.localName + (siblings.length > 1 ? ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')' : ''));
    }
    return parts.join(' > ');
  }

  function query(selector) {
    if (!selector || typeof selector !== 'string' || selector.length > 2048) return { nodes: [], reason: 'Sélecteur absent ou trop long.' };
    try { return { nodes: [...document.querySelectorAll(selector)], reason: '' }; }
    catch { return { nodes: [], reason: 'Le sélecteur CSS est invalide.' }; }
  }

  function unsafeElement(element) {
    if (element.matches('script,style,noscript,template,input[type="hidden"],input[type="password"],input[type="file"],[data-sensitive],[autocomplete="current-password"],[autocomplete="new-password"],[autocomplete="one-time-code"]')) return true;
    if (sensitive([element.getAttribute('name'), element.getAttribute('id'), element.getAttribute('aria-label')].filter(Boolean).join(' '))) return true;
    return false;
  }

  function isSelectVisible(select) {
    if (visible(select)) return true;
    if (!select.matches('.select2-hidden-accessible,.select2-offscreen,[data-select2-id]')) return false;
    return [select.previousElementSibling, select.nextElementSibling, select.id ? document.getElementById('s2id_' + select.id) : null]
      .some(widget => widget?.matches('.select2,.select2-container') && visible(widget));
  }

  function controlValue(element) {
    if (unsafeElement(element)) return null;
    if (element instanceof HTMLSelectElement) return isSelectVisible(element)
      ? [...element.selectedOptions].map(option => tidy(option.textContent)).join('\n') : null;
    if (element instanceof HTMLTextAreaElement) {
      const rich = richEditorValue(element);
      if (rich !== null) return rich;
    }
    if (!visible(element)) return null;
    if (element instanceof HTMLTextAreaElement) return element.value;
    if (!(element instanceof HTMLInputElement)) return null;
    if (['button', 'submit', 'reset', 'image', 'file', 'hidden', 'password'].includes(element.type)) return null;
    if (element.type === 'checkbox') return element.checked ? 'Oui' : 'Non';
    if (element.type === 'radio') return element.checked ? tidy([...element.labels || []].map(label => label.textContent).join(' ')) || 'Oui' : null;
    return element.value;
  }

  // TinyMCE hides its source textarea. Only a visible, explicitly associated
  // editor may supply its replacement value. Browser same-origin checks still
  // apply to its body, including inherited-origin about:blank editor frames.
  function richEditorValue(textarea) {
    if (!(textarea instanceof HTMLTextAreaElement) || unsafeElement(textarea)) return null;
    const id = textarea.getAttribute('id');
    if (!id) return null;
    let body = null;
    try {
      const editor = window.tinymce?.get?.(id) || window.tinyMCE?.get?.(id);
      if (editor && editor.getElement?.() === textarea && visible(editor.getContainer?.())) body = editor.getBody?.();
      if (!body) {
        const frame = document.getElementById(id + '_ifr');
        if (frame?.localName === 'iframe' && visible(frame) && frame.closest('.mce-tinymce,.tox-tinymce,.mceEditor'))
          body = frame.contentDocument?.body;
      }
      if (!body || body.nodeType !== Node.ELEMENT_NODE) return null;
      function walk(node) {
        if (node.nodeType === Node.TEXT_NODE) return node.nodeValue || '';
        if (node.nodeType !== Node.ELEMENT_NODE || node.matches('script,style,template,noscript,form,input,select,textarea,button,[hidden],[aria-hidden="true"]')) return '';
        const style = node.ownerDocument.defaultView.getComputedStyle(node);
        if (style.display === 'none' || style.visibility === 'hidden') return '';
        if (node.localName === 'br') return '\n';
        if (node.localName === 'img') return node.getAttribute('alt') || '';
        const value = [...node.childNodes].map(walk).join('');
        return /^(?:p|div|li|tr|h[1-6])$/.test(node.localName) ? '\n' + value + '\n' : value;
      }
      return walk(body).replace(/\r\n?/g, '\n').replace(/[\t \u00a0]+/g, ' ').replace(/ *\n */g, '\n').replace(/\n{3,}/g, '\n\n').trim();
    } catch { return null; }
  }

  function secretGroup(element) {
    if (element.matches('.form-field,.form-group')) {
      const label = element.querySelector(':scope > label');
      if (label && sensitive(label.textContent)) return true;
    }
    if (element.localName === 'tr') {
      const labels = [...element.children].filter(cell => cell.matches('th,label'));
      if (labels.some(label => sensitive(label.textContent))) return true;
    }
    return false;
  }

  function textOf(root, excludeNestedTables = false) {
    function walk(node, first = false) {
      if (node.nodeType === Node.TEXT_NODE) return node.nodeValue || '';
      if (!(node instanceof Element) || unsafeElement(node) || secretGroup(node)) return '';
      if (node.matches('input,select,textarea')) return controlValue(node) ?? '';
      if (!visible(node) || node.matches('button,input[type="submit"],[role="button"],.select2-container,.select2,[role="listbox"],option,optgroup,.h_controls,.control_item,.read_more,.edit_item_content,.cancel_edit_item_content,.timeline_form,.timeline_choices,.filter_timeline,.approbation_form,.fileupload,.fileupload-container,.fileupload_info,.dropzone,.tooltip_picture_border,img.user_picture,.buttons,.tox-tinymce,.mce-tinymce')) return '';
      if (!first && excludeNestedTables && node.matches(TABLE)) return '';
      if (node.localName === 'br') return '\n';
      if (node.matches('img,[role="img"]')) return node.getAttribute('alt') || node.getAttribute('aria-label') || '';
      const value = [...node.childNodes].map(child => walk(child)).join('');
      return /^(?:p|div|section|article|li|dt|dd|tr|h[1-6])$/.test(node.localName) ? '\n' + value + '\n' : value;
    }
    return walk(root, true).replace(/\r\n?/g, '\n').replace(/[\t \u00A0]+/g, ' ').replace(/ *\n */g, '\n').replace(/\n{3,}/g, '\n\n').trim();
  }

  function fieldValue(element) {
    const controls = (element.matches('input,select,textarea') ? [element] : [...element.querySelectorAll('input,select,textarea')])
      .filter(control => !unsafeElement(control) && (visible(control) || control instanceof HTMLSelectElement && isSelectVisible(control) || control instanceof HTMLTextAreaElement && richEditorValue(control) !== null));
    const values = controls.map(controlValue).filter(value => value !== null);
    return values.length ? values.join('\n') : textOf(element, true);
  }

  function roots() {
    const candidates = [...document.querySelectorAll(ROOTS)].filter(visible);
    const forms = candidates.filter(element => element.getAttribute('id') === 'itil-form');
    if (forms.length) return forms;
    const legacy = candidates.filter(element => element.getAttribute('id') === 'tabsbody');
    if (legacy.length) return legacy;
    if (candidates.length) return candidates;
    return [...document.querySelectorAll('form[action]')].filter(form => {
      if (!visible(form)) return false;
      try { const url = new URL(form.getAttribute('action'), document.baseURI); return url.origin === location.origin && /\/front\/ticket\.form\.php$/i.test(url.pathname); }
      catch { return false; }
    });
  }

  function ownRows(table) {
    return [...table.querySelectorAll('tr,[role="row"]')].filter(row => row.closest(TABLE) === table && !row.closest('tfoot') && visible(row));
  }

  function ownCells(row, table) {
    return [...row.querySelectorAll('th,td,[role="columnheader"],[role="cell"],[role="gridcell"]')]
      .filter(cell => cell.closest('tr,[role="row"]') === row && cell.closest(TABLE) === table && visible(cell));
  }

  function dataTable(table) {
    if (!visible(table) || table.matches('.tab_cadre_pager,.tab_format,[id^="mainformtable"]') || table.closest('nav,[role="navigation"],#tabspanel,.ui-tabs-nav')) return false;
    const rows = ownRows(table);
    const heading = rows.some(row => {
      const cells = ownCells(row, table);
      return cells.length >= 2 && cells.every(cell => cell.matches('th,[role="columnheader"]'));
    });
    return heading && rows.some(row => ownCells(row, table).filter(cell => cell.matches('td,[role="cell"],[role="gridcell"]')).some(cell => textOf(cell, true).length > 0));
  }

  function sectionName(element, fallback) {
    const caption = element.querySelector(':scope > caption');
    if (caption && visible(caption)) return tidy(textOf(caption)).slice(0,200) || fallback;
    const labelled = (element.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)).filter(visible);
    if (labelled.length) return tidy(labelled.map(element => textOf(element)).join(' ')).slice(0,200) || fallback;
    for (let previous = element.previousElementSibling; previous; previous = previous.previousElementSibling) {
      if (previous.matches('h1,h2,h3,h4,h5,h6') && visible(previous)) return tidy(textOf(previous)).slice(0,200) || fallback;
    }
    return fallback;
  }

  function timelineRoots() {
    return [...document.querySelectorAll(TIMELINE)].filter(visible)
      .filter(root => !root.parentElement?.closest(TIMELINE));
  }

  function tabs(argument = {}) {
    const result = [];
    for (const element of document.querySelectorAll('[role="tablist"] a,[role="tablist"] button,.nav-tabs a,.nav-tabs button,.ui-tabs-nav a,.ui-tabs-nav button')) {
      const selector = css(element), proof = tab({ ...argument, selector });
      if (!proof.allowed || !proof.targetSelector || result.some(item => item.selector === selector)) continue;
      const label = tidy(element.innerText || element.textContent).replace(/\s*\(\d+\)\s*$/, '').replace(/\s+\d+\s*$/, '').slice(0,120);
      if (!label) continue;
      const name = norm(label);
      const category = /traitement|processing|timeline|assistance/.test(name) ? 'treatment' : /^(ticket|incident|overview|general|fiche)$/.test(name) ? 'overview' : 'other';
      result.push({ name: label, key: proof.key, selector, category, isActive: proof.alreadyActive });
    }
    return result.slice(0,60);
  }

  function inspect(argument = {}) {
    if (argument.origin && argument.origin !== location.origin)
      return { ready: false, authentication: false, busy: false, wrongId: false, identityConfirmed: false, title: '', sections: [], reason: 'Le cadre a changé d’origine.' };
    const expected = canonicalId(argument.id);
    const identityFieldRoots = roots();
    let fieldRoots = identityFieldRoots;
    let scope = null;
    if (argument.scopeSelector) {
      const found = query(argument.scopeSelector);
      if (found.nodes.length === 1 && visible(found.nodes[0])) scope = found.nodes[0];
      if (!scope) return { ready: false, authentication: false, busy: false, wrongId: false, identityConfirmed: false, title: '', sections: [], reason: 'Le panneau demandé n’est pas encore visible ou son repère a changé.' };
      fieldRoots = [scope];
    }
    // Hidden values are never extracted. Only the exact ID field is read to
    // verify identity; CSRF/session/other hidden inputs are not accessed.
    const identityRoots = identityFieldRoots.length ? identityFieldRoots : [...document.querySelectorAll('#tabsbody,#itil-object-container')].filter(visible);
    const ids = [...new Set(identityRoots.flatMap(root => [...root.querySelectorAll('input[type="hidden"][name="id"]')]
      .filter(input => !input.form || identityFieldRoots.includes(input.form) || input.form.getAttribute('name') === 'form' || input.form.getAttribute('id') === 'itil-form')
      .map(input => canonicalId(input.value)).filter(Boolean)))];
    const wrongId = ids.length > 0 && (ids.length !== 1 || ids[0] !== expected);
    const explicitLogin = [...document.querySelectorAll('form#login,form[name="login"],.login-form')].some(visible);
    const authentication = explicitLogin || [...document.querySelectorAll('input[type="password"],input[autocomplete="one-time-code"],input[name="otp"],input[name="otc"]')]
      .some(element => visible(element) && !fieldRoots.some(root => root.contains(element)));
    const busy = [...document.querySelectorAll('#loadingtabs,.loadingindicator,[aria-busy="true"]')].some(visible);
    const tables = [...document.querySelectorAll(TABLE)].filter(dataTable).filter(table => !scope || scope.contains(table));
    const manualVisible = (argument.selectors || []).some(selector => { const found = query(selector); return found.nodes.length === 1 && visible(found.nodes[0]); });
    let title = '';
    for (const root of fieldRoots) {
      const name = [...root.querySelectorAll('input[name="name"],textarea[name="name"]')].find(visible);
      if (name && !unsafeElement(name)) { title = controlValue(name) || ''; break; }
    }
    if (!title) title = [...document.querySelectorAll('h1')].filter(visible).map(element => textOf(element)).find(Boolean) || document.title;
    const sections = fieldRoots.filter(root => !scope || fields(root, 1).totalRows > 0).map((root, index) => ({ name: index ? 'Champs du ticket ' + (index + 1) : 'Champs du ticket', selector: css(root), kind: 'fields' }));
    for (const timeline of timelineRoots().filter(root => !scope || scope.contains(root))) sections.push({ name: 'Traitement du ticket', selector: css(timeline), kind: 'timeline' });
    for (const table of tables) sections.push({ name: sectionName(table, 'Tableau ' + (sections.filter(section => section.kind === 'table').length + 1)), selector: css(table), kind: 'table' });
    if (scope && !sections.length && textOf(scope)) sections.push({ name: 'Contenu de l’onglet', selector: css(scope), kind: 'text' });
    return {
      ready: !authentication && !busy && !wrongId && (fieldRoots.length > 0 || tables.length > 0 || manualVisible || sections.some(section => section.kind === 'timeline')),
      authentication, busy, wrongId, identityConfirmed: ids.length === 1 && ids[0] === expected,
      title: title.slice(0,1000), sections,
      reason: wrongId ? 'La fiche affichée ne correspond pas au ticket sélectionné.' : authentication ? 'Une authentification est demandée.' : busy ? 'Le contenu de la fiche est en cours de chargement.' : ''
    };
  }

  function fields(root, maximum) {
    const rows = [], used = new Set(), seen = new Set();
    let excluded = false;
    const add = (name, value, element) => {
      name = tidy(name).replace(/\s*:\s*$/, '').trim();
      if (!name || element.closest(TIMELINE + ',.timeline_form,.approbation_form') || element.querySelector('input[type="file"],.dropzone,.fileupload,.fileupload-container')) return;
      if (sensitive(name) || unsafeElement(element)) { excluded = true; return; }
      if (seen.has(element)) return;
      seen.add(element); rows.push([name, value]);
      if (element.matches('input,select,textarea')) used.add(element);
      for (const control of element.querySelectorAll('input,select,textarea')) used.add(control);
    };
    const groups = (root.matches('.form-field,.form-group') ? [root] : []).concat([...root.querySelectorAll('.form-field,.form-group')]);
    for (const group of groups.filter(visible)) {
      const label = group.querySelector(':scope > label') || group.querySelector('label[for]');
      const value = group.querySelector(':scope > .field-container') || group.querySelector('.field-container');
      if (!label || !value) continue;
      if (sensitive(label.textContent)) { excluded = true; for (const control of group.querySelectorAll('input,select,textarea')) used.add(control); continue; }
      add(textOf(label), fieldValue(value), value);
    }
    const tables = (root.matches('table') ? [root] : []).concat([...root.querySelectorAll('table')]);
    for (const table of tables.filter(visible)) {
      if (dataTable(table)) continue;
      for (const row of ownRows(table)) {
        const cells = ownCells(row, table);
        if (cells.length < 2 || cells.every(cell => cell.localName === 'th')) continue;
        for (let index = 0; index < cells.length - 1; index++) {
          const label = cells[index], value = cells[index + 1];
          if (label.localName !== 'th' && !label.querySelector('label[for]')) continue;
          if (value.localName !== 'td') continue;
          if (sensitive(label.textContent)) { excluded = true; for (const control of value.querySelectorAll('input,select,textarea')) used.add(control); index++; continue; }
          if (label.querySelector('table') || value.querySelector('table')) continue;
          add(textOf(label), fieldValue(value), value); index++;
        }
      }
    }
    const controls = (root.matches('input,select,textarea') ? [root] : []).concat([...root.querySelectorAll('input,select,textarea')]);
    for (const control of controls) {
      if (used.has(control)) continue;
      const containingTable = control.closest(TABLE);
      if (containingTable && dataTable(containingTable)) continue;
      if (unsafeElement(control)) { if (visible(control)) excluded = true; continue; }
      const value = controlValue(control);
      if (value === null) continue;
      const labels = [...control.labels || []].filter(visible).map(label => textOf(label));
      add(labels.join(' ') || control.getAttribute('aria-label') || control.getAttribute('name') || '', value, control);
    }
    const warnings = excluded ? ['Des champs sensibles ont été exclus de la lecture.'] : [];
    if (rows.length > maximum) warnings.push('La limite de lignes de cette section a été atteinte.');
    return { headers: ['Champ', 'Valeur'], rows: rows.slice(0, maximum), totalRows: rows.length, warnings };
  }

  function timelineData(root, maximum) {
    const items = [...root.querySelectorAll(TIMELINE_ITEMS)].filter(item => visible(item) && !item.parentElement?.closest(TIMELINE_ITEMS));
    const rows = [];
    for (const item of items) {
      const content = item.querySelector('.h_content,.timeline-content,.timeline-item-content') || item;
      const signature = [content.className, item.className, item.getAttribute('data-itemtype'), item.getAttribute('data-type')].join(' ');
      const type = /ITILContent|original-description/i.test(signature) ? 'Description initiale'
        : /ITILFollowup|followup/i.test(signature) ? 'Suivi'
        : /ITILTask|TicketTask|task/i.test(signature) ? 'Tâche'
        : /ITILSolution|Solution/i.test(signature) ? 'Solution'
        : /Document_Item|document/i.test(signature) ? 'Document'
        : /Validation/i.test(signature) ? 'Validation' : 'Événement';
      const visibleText = selector => { const element = item.querySelector(selector); return element && visible(element) ? textOf(element) : ''; };
      const date = visibleText('.h_date,time,.timeline-date,[data-timeline-date]');
      const author = visibleText('.h_user_name,.timeline-author,[data-timeline-author]') || visibleText('.h_user');
      const body = content.querySelector('.rich_text_container,.timeline-item-content,.item_content,.timeline-content-body');
      let text = body && visible(body) ? textOf(body) : textOf(content);
      const metadata = content.querySelector('.b_right,.timeline-item-footer');
      const details = metadata && visible(metadata) ? textOf(metadata) : '';
      const statusControl = content.querySelector('.state[title]');
      const state = statusControl && visible(statusControl) ? tidy(statusControl.getAttribute('title')) : '';
      const privacy = [...content.querySelectorAll('.private,[title="Private"],[title="Privé"]')].some(visible) ? 'Privé' : '';
      if (!text && !date && !author && !details) continue;
      rows.push([type, date, author.replace(/^Picture\s*/i, '').trim(), text, [state, privacy, details].filter(Boolean).join('\n')]);
    }
    const warnings = [];
    const allItems = root.querySelectorAll(TIMELINE_ITEMS).length;
    if (allItems > items.length) warnings.push('Certains événements sont masqués par la vue GLPI ; seuls les événements affichés sont lus.');
    if (rows.length > maximum) warnings.push('La limite de lignes de cette section a été atteinte.');
    if (!rows.length) warnings.push('Aucun événement de traitement visible dans cette section.');
    return { headers: ['Type', 'Date', 'Auteur', 'Contenu', 'Informations'], rows: rows.slice(0, maximum), totalRows: rows.length, warnings };
  }

  function tableData(table, maximum) {
    const sourceRows = ownRows(table), matrix = [], headingRows = new Set();
    let expanded = false, excluded = false;
    for (let rowIndex = 0; rowIndex < sourceRows.length; rowIndex++) {
      matrix[rowIndex] ||= [];
      const row = sourceRows[rowIndex], cells = ownCells(row, table);
      if (!cells.length) continue;
      if (row.closest('thead') || cells.every(cell => cell.matches('th,[role="columnheader"]') && cell.getAttribute('scope') !== 'row')) headingRows.add(rowIndex);
      let column = 0;
      for (const cell of cells) {
        while (matrix[rowIndex][column] !== undefined) column++;
        const span = Math.min(256, Math.max(1, Number(cell.getAttribute('colspan')) || 1));
        const height = Math.min(sourceRows.length - rowIndex, Math.max(1, Number(cell.getAttribute('rowspan')) || 1));
        if (span > 1 || height > 1) expanded = true;
        const value = textOf(cell, true);
        for (let dy = 0; dy < height; dy++) {
          matrix[rowIndex + dy] ||= [];
          for (let dx = 0; dx < span && column + dx < 256; dx++) matrix[rowIndex + dy][column + dx] = value;
        }
        column += span;
      }
    }
    let width = Math.min(256, matrix.reduce((value, row) => Math.max(value, row.length), 0));
    const headerIndices = [...headingRows];
    const rawHeaders = Array.from({length: width}, (_, column) => [...new Set(headerIndices.map(index => matrix[index]?.[column] || '').filter(Boolean))].join(' / ') || 'Colonne ' + (column + 1));
    const kept = rawHeaders.map((header, index) => sensitive(header) ? (excluded = true, -1) : index).filter(index => index >= 0);
    const usedNames = new Map();
    const headers = kept.map(index => { const value = rawHeaders[index]; const count = (usedNames.get(value) || 0) + 1; usedNames.set(value, count); return count === 1 ? value : value + ' (' + count + ')'; });
    const allRows = matrix.filter((row, index) => !headingRows.has(index) && row.some(value => value !== ''))
      .map(row => kept.map(index => row[index] || ''));
    const warnings = [];
    if (!headerIndices.length) warnings.push('En-têtes absents : les colonnes ont reçu un nom neutre.');
    if (expanded) warnings.push('Les cellules fusionnées ont été développées dans le tableau de lecture.');
    if (excluded) warnings.push('Des colonnes sensibles ont été exclues de la lecture.');
    if (allRows.length > maximum) warnings.push('La limite de lignes de cette section a été atteinte.');
    return { headers, rows: allRows.slice(0, maximum), totalRows: allRows.length, warnings };
  }

  function read(argument = {}) {
    if (argument.origin && argument.origin !== location.origin)
      return { found: false, matches: 0, reason: 'Le cadre a changé d’origine.', headers: [], rows: [], totalRows: 0, warnings: [] };
    if (argument.id) {
      const identity = inspect({ id: argument.id, origin: argument.origin });
      if (identity.wrongId || identity.authentication)
        return { found: false, matches: 0, reason: 'La fiche a changé ou attend une authentification.', headers: [], rows: [], totalRows: 0, warnings: [] };
    }
    const found = query(argument.selector);
    if (found.reason || found.nodes.length !== 1) return { found: false, matches: found.nodes.length, reason: found.reason || (found.nodes.length ? 'Le sélecteur correspond à plusieurs éléments.' : 'La section configurée est absente.'), headers: [], rows: [], totalRows: 0, warnings: [] };
    const root = found.nodes[0];
    if (!visible(root)) return { found: false, matches: 1, reason: 'La section existe mais reste masquée.', headers: [], rows: [], totalRows: 0, warnings: [] };
    if (root.closest('[aria-busy="true"]') || [...document.querySelectorAll('#loadingtabs,.loadingindicator')].some(visible))
      return { found: false, matches: 1, reason: 'La section est en cours de chargement.', headers: [], rows: [], totalRows: 0, warnings: [] };
    if (unsafeElement(root) || secretGroup(root)) return { found: false, matches: 1, reason: 'Cette section contient un champ sensible exclu de la lecture.', headers: [], rows: [], totalRows: 0, warnings: [] };
    const maximum = Math.max(1, Math.min(100000, Number(argument.maxRows) || 5000));
    let result;
    if (argument.kind === 'table') {
      if (!root.matches(TABLE)) return { found: false, matches: 1, reason: 'Le sélecteur ne désigne pas un tableau.', headers: [], rows: [], totalRows: 0, warnings: [] };
      result = tableData(root, maximum);
    } else if (argument.kind === 'fields') result = fields(root, maximum);
    else if (argument.kind === 'timeline') result = timelineData(root, maximum);
    else if (argument.kind === 'text') {
      const value = textOf(root);
      result = { headers: ['Texte'], rows: value ? [[value]] : [], totalRows: value ? 1 : 0, warnings: [] };
    } else return { found: false, matches: 1, reason: 'Le type de section est inconnu.', headers: [], rows: [], totalRows: 0, warnings: [] };
    return { found: true, matches: 1, reason: '', ...result };
  }

  function tab(argument = {}) {
    const found = query(argument.selector);
    const reject = reason => ({ allowed: false, matches: found.nodes.length, reason, selector: '', targetSelector: '', alreadyActive: false, proof: '', key: '' });
    if (argument.origin && argument.origin !== location.origin) return reject('Le cadre a changé d’origine.');
    if (found.reason || found.nodes.length !== 1) return reject(found.reason || 'Le sélecteur d’onglet doit désigner un seul élément.');
    const element = found.nodes[0];
    if (!visible(element) || element.matches(':disabled,[aria-disabled="true"],.disabled')) return reject('L’onglet est masqué ou désactivé.');
    const action = norm(element.textContent);
    if (/^(?:supprimer|purger|enregistrer|mettre a jour|delete|purge|save|update|ajouter|assigner|clore|fermer le ticket|close ticket)\b/.test(action) || /^(?:purge|delete|update|save|add|restore|massiveaction)(?:_|$)/i.test(element.getAttribute('name') || ''))
      return reject('Ce contrôle indique une action métier et ne sera pas cliqué.');
    if (!element.matches('a,button') || element.matches('button:not([type="button"])')) return reject('Ce contrôle n’est pas un onglet de consultation reconnu.');
    const group = element.closest('[role="tablist"],.nav-tabs,.ui-tabs-nav');
    if (!group) return reject('Le contrôle n’appartient pas à une barre d’onglets reconnue.');
    for (let node = element; node; node = node.parentElement) {
      if ([...node.attributes].some(attribute => /^on/i.test(attribute.name))) return reject('Un gestionnaire inline non reconnu empêche le clic automatique.');
      if (node === group) break;
    }
    if (['formaction','formmethod','formtarget','download'].some(name => element.hasAttribute(name))) return reject('Le contrôle porte une action de formulaire ou de téléchargement.');
    const role = element.getAttribute('role') === 'tab' || element.matches('.ui-tabs-anchor,[data-bs-toggle="tab"],[data-toggle="tab"]') || !!element.closest('li[role="tab"]');
    if (!role) return reject('Le rôle d’onglet de consultation n’est pas prouvé.');
    const href = element.getAttribute('href') || '';
    const controls = element.getAttribute('aria-controls') || element.closest('li[aria-controls]')?.getAttribute('aria-controls');
    const targetSelector = element.getAttribute('data-bs-target') || element.getAttribute('data-target') || (href.startsWith('#') ? href : '') || (controls ? '#' + CSS.escape(controls) : '');
    let proof = '', key = '';
    if (href && !href.startsWith('#')) {
      let url;
      try { url = new URL(href, document.baseURI); } catch { return reject('La destination de l’onglet est invalide.'); }
      const ticketPath = argument.ticketPath || (/\/front\/ticket\.form\.php$/i.test(location.pathname) ? location.pathname : '');
      const expectedAjax = ticketPath.replace(/\/front\/ticket\.form\.php$/i, '/ajax/common.tabs.php');
      if (!ticketPath || url.origin !== location.origin || !/^https?:$/.test(url.protocol) || url.username || url.password || url.hash || url.pathname !== expectedAjax) return reject('La destination n’est pas le lecteur d’onglets de cette installation GLPI.');
      const allowed = new Set(['_target','_itemtype','_glpi_tab','id','withtemplate','sort','order','_in_modal']);
      if ([...url.searchParams.keys()].some(key => !allowed.has(key)) || [...allowed].some(key => url.searchParams.getAll(key).length > 1)) return reject('Les paramètres de l’onglet contiennent une action non reconnue.');
      if (url.searchParams.get('_itemtype') !== 'Ticket' || canonicalId(url.searchParams.get('id')) !== canonicalId(argument.id) || !/^[A-Za-z0-9_$\\.\-]+$/.test(url.searchParams.get('_glpi_tab') || '')) return reject('L’onglet ne correspond pas au ticket sélectionné.');
      if (url.searchParams.has('withtemplate') && !['','0'].includes(url.searchParams.get('withtemplate'))) return reject('Un onglet de modèle de ticket ne peut pas être lu automatiquement.');
      const target = url.searchParams.get('_target');
      if (target) {
        let targetUrl;
        try { targetUrl = new URL(target, document.baseURI); } catch { return reject('La cible du ticket est invalide.'); }
        if (targetUrl.origin !== location.origin || targetUrl.username || targetUrl.password || targetUrl.hash || targetUrl.pathname !== ticketPath || [...targetUrl.searchParams.keys()].some(key => key !== 'id') || targetUrl.searchParams.getAll('id').length > 1 || (targetUrl.searchParams.has('id') && canonicalId(targetUrl.searchParams.get('id')) !== canonicalId(argument.id))) return reject('La cible secondaire de l’onglet ne correspond pas à une fiche ticket.');
      }
      proof = 'glpi-common-ticket-tab';
      key = 'remote:' + url.pathname + ':' + url.searchParams.get('_glpi_tab');
    } else {
      const panel = query(targetSelector);
      if (!targetSelector.startsWith('#') || panel.nodes.length !== 1 || !panel.nodes[0].matches('[role="tabpanel"],.tab-pane,.ui-tabs-panel')) return reject('Le panneau local de consultation n’est pas reconnu.');
      proof = 'local-ticket-tab-panel';
      key = 'local:' + targetSelector;
    }
    return { allowed: true, matches: 1, reason: '', selector: css(element), targetSelector,
      alreadyActive: element.getAttribute('aria-selected') === 'true' || element.classList.contains('active') || !!element.closest('.ui-tabs-active'), proof, key };
  }

  function observe(argument = {}) {
    if (location.origin !== argument.origin) return [];
    const identity = inspect({ id: argument.id, origin: argument.origin });
    if (identity.authentication || identity.wrongId) return [];
    const found = query(argument.selector);
    if (found.nodes.length !== 1 || !visible(found.nodes[0])) return [];
    const root = found.nodes[0], values = [];
    const value = (node, selectors) => {
      for (const selector of selectors.split(',')) {
        const child = node.querySelector(selector.trim());
        if (child && visible(child) && !unsafeElement(child) && !secretGroup(child))
          return tidy(child.getAttribute('datetime') || textOf(child));
      }
      return '';
    };
    const labelled = (node, aliases) => {
      for (const row of node.querySelectorAll('tr,.row,.form-group,dl')) {
        if (!visible(row)) continue;
        const label = row.querySelector('th,label,dt,.col-form-label');
        if (!label || !aliases.includes(norm(textOf(label)).replace(/[:*]$/g, '').trim())) continue;
        const cell = row.querySelector('td,dd,.field-value,.form-control-plaintext');
        if (cell && visible(cell) && !unsafeElement(cell) && !secretGroup(cell)) return textOf(cell);
      }
      return '';
    };
    const nodes = [...root.querySelectorAll(TIMELINE_ITEMS)].filter(n => visible(n) && !n.parentElement?.closest(TIMELINE_ITEMS));
    for (const node of nodes.slice(0, Math.min(5000, Math.max(1, argument.maxRows || 5000)))) {
      const content = node.querySelector('.h_content,.timeline-content,.timeline-item-content') || node;
      const signature = [content.className, node.className, node.getAttribute('data-itemtype'), node.getAttribute('data-type')].join(' ');
      const kind = /ITILFollowup|followup/i.test(signature) ? 'followup' : /ITILTask|TicketTask|task/i.test(signature) ? 'task'
        : /Validation/i.test(signature) ? 'validation' : /waiting|pending/i.test(signature) ? 'waiting' : '';
      if (!kind) continue;
      const roleElement = node.querySelector('[data-actor-role],.actor-role');
      const role = roleElement && visible(roleElement) ? tidy(roleElement.getAttribute('data-actor-role') || textOf(roleElement)) : '';
      const state = node.querySelector('.state[title]');
      values.push({ kind, identity: node.getAttribute('data-items-id') || node.id || '', sourceSection: argument.selector,
        title: value(content, '.rich_text_container,.timeline-item-content,.item_content,.timeline-content-body'),
        date: value(node, '.h_date,time,.timeline-date,[data-timeline-date]'), author: value(node, '.h_user_name,.timeline-author,[data-timeline-author]'), authorRole: role,
        owner: value(node, '[data-task-owner],[data-validation-owner],.task-assignee,.validation-user') || labelled(node, ['responsable','validateur','assigne a','assigned to']),
        status: state && visible(state) ? tidy(state.getAttribute('title')) : value(node, '[data-task-status],[data-validation-status],.validation-status') || labelled(node, ['etat','statut','status']),
        due: value(node, '[data-due-date],.task-due,time[itemprop="endDate"]') || labelled(node, ['echeance','date limite','due date']),
        plannedStart: value(node, '[data-planned-start],.planning-start') || labelled(node, ['debut planifie','date de debut','begin','start date']),
        plannedEnd: value(node, '[data-planned-end],.planning-end') || labelled(node, ['fin planifiee','date de fin','end date']),
        completedAt: value(node, '[data-completed-at],.completed-at') || labelled(node, ['termine le','date achevement','completed at']),
        requestedAt: value(node, '[data-requested-at],.validation-requested-at') || labelled(node, ['date de demande','requested at']),
        waitingStart: value(node, '[data-waiting-start],.waiting-start') || labelled(node, ['debut de mise en attente','waiting start']),
        waitingEnd: value(node, '[data-waiting-end],.waiting-end') || labelled(node, ['fin de mise en attente','waiting end']),
        waitingReason: value(node, '[data-waiting-reason],.waiting-reason') || labelled(node, ['motif attente','motif de mise en attente','waiting reason']) });
    }
    const path = argument.ticketPath || location.pathname;
    const installation = path.slice(0, path.lastIndexOf('/front/') + 1);
    for (const link of root.querySelectorAll('a[href]')) {
      if (!visible(link) || unsafeElement(link) || secretGroup(link)) continue;
      let url; try { url = new URL(link.href, location.href); } catch { continue; }
      if (url.origin !== argument.origin || !url.pathname.startsWith(installation + 'front/') || url.username || url.password || url.hash || new Set(url.searchParams.keys()).size !== [...url.searchParams.keys()].length
        || [...url.searchParams.keys()].some(k => /delete|purge|update|add|token|action|submit|force|clone/i.test(k))) continue;
      const endpoint = url.pathname.slice((installation + 'front/').length), title = textOf(link);
      if (!title) continue;
      if (endpoint === 'document.send.php' && /^\d+$/.test(url.searchParams.get('docid') || '')
        && [...url.searchParams.keys()].every(k => ['docid','tickets_id','itemtype','items_id'].includes(k))
        && ['tickets_id','items_id'].every(k => !url.searchParams.has(k) || url.searchParams.get(k) === argument.id)
        && (!url.searchParams.has('itemtype') || url.searchParams.get('itemtype') === 'Ticket'))
        values.push({kind:'document', identity:url.searchParams.get('docid'), title, url:url.href, sourceSection:argument.selector});
      else {
        const match = /^(ticket|problem|change|computer|monitor|printer|networkequipment|peripheral|phone|software|user|group)\.form\.php$/.exec(endpoint);
        if (match && /^\d+$/.test(url.searchParams.get('id') || '') && [...url.searchParams.keys()].every(k => k === 'id'))
          values.push({kind:'relation', identity:url.searchParams.get('id'), title, url:url.href, relatedType:match[1], sourceSection:argument.selector});
      }
      if (values.length >= 6000) break;
    }
    return values;
  }

  window.__glpiPilotTicket = Object.freeze({ version: '1.4.0', inspect, read, tab, tabs, observe });
})();
