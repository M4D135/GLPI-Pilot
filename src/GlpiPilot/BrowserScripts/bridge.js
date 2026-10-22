(() => {
  'use strict';

  // Reinstalling after every command is safe; a full navigation creates fresh state.
  const VERSION = '1.1.3';
  if (window.__glpiPilot?.version === VERSION) return VERSION;
  try { window.__glpiPilot?.cancelPick(); } catch (_) { /* Previous document adapter. */ }

  const TABLE = 'table,[role="grid"],[role="table"],[role="treegrid"]';
  const UI = '[data-glpi-pilot-ui]';
  const PAGER = '.pagination,.pager,.pager_container,.paginator,.pagination-container,[data-pagination],nav[aria-label*="pagination" i],nav[aria-label*="pages" i]';
  const CLICKABLE = 'a,button,[role="button"],input[type="button"]';
  const registry = new Map();
  let nextKey = 0;
  let picker = null;
  let highlight = null;

  const compact = text => String(text || '').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
  const plain = text => compact(text).normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
  const css = value => window.CSS?.escape ? CSS.escape(String(value)) : String(value).replace(/[^a-zA-Z0-9_-]/g, c => '\\' + c.codePointAt(0).toString(16) + ' ');
  const all = (selector, root = document) => { try { return [...root.querySelectorAll(selector)]; } catch (_) { return []; } };

  function visible(element) {
    if (!(element instanceof Element) || !element.isConnected || element.closest(UI)) return false;
    if (element.closest('[hidden],[aria-hidden="true"]')) return false;
    const style = getComputedStyle(element);
    return style.display !== 'none' && style.visibility !== 'hidden' && style.visibility !== 'collapse' && element.getClientRects().length > 0;
  }

  function tables() {
    // Nested layout/detail tables are not separate export candidates.
    return all(TABLE).filter(t => visible(t) && !t.parentElement?.closest(TABLE));
  }

  function uniqueSelector(element) {
    if (!(element instanceof Element)) return '';
    const unique = selector => { const matches = all(selector); return matches.length === 1 && matches[0] === element; };
    if (element.id) {
      const selector = '#' + css(element.id);
      if (unique(selector)) return selector;
    }
    const path = [];
    let node = element;
    for (let depth = 0; node && depth < 16; depth++, node = node.parentElement) {
      if (node.id) {
        path.unshift('#' + css(node.id));
        const withId = path.join(' > ');
        if (unique(withId)) return withId;
        path.shift();
      }
      let part = node.tagName.toLowerCase();
      const siblings = node.parentElement ? [...node.parentElement.children].filter(s => s.tagName === node.tagName) : [];
      if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')';
      path.unshift(part);
      const selector = path.join(' > ');
      if (unique(selector)) return selector;
    }
    return path.join(' > ');
  }

  function keyFor(table) {
    let key = table.getAttribute('data-glpi-pilot-table-key');
    if (!key || (registry.has(key) && registry.get(key) !== table)) {
      key = 'table-' + (++nextKey);
      table.setAttribute('data-glpi-pilot-table-key', key);
    }
    registry.set(key, table);
    return key;
  }

  function resolveTable(key) {
    let table = registry.get(key);
    if (!table?.isConnected) {
      table = tables().find(t => t.getAttribute('data-glpi-pilot-table-key') === key);
      if (!table && typeof key === 'string') {
        const matches = all(key).filter(t => t.matches(TABLE));
        if (matches.length === 1) table = matches[0];
      }
    }
    if (!table || !visible(table)) throw new Error('Tableau introuvable ou masqué. Actualisez la liste des tableaux.');
    keyFor(table);
    return table;
  }

  function cellText(cell, evidence = null) {
    const clean = text => text.replace(/\u00a0/g, ' ').split(/\r?\n/).map(s => s.replace(/[\t ]+/g, ' ').trim()).filter(Boolean).join('\n').trim();
    const expandedPreviews = new WeakSet();
    const expandedSources = new WeakMap();
    function omitted(node) {
      if (node.matches('script,style,template,noscript,[hidden],[aria-hidden="true"],' + UI)) return true;
      const style = getComputedStyle(node);
      return style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse';
    }
    const uniquePeerId = node => node.id && [...node.parentElement.children].filter(peer => peer.id === node.id).length === 1;
    function adjacent(first, second) {
      if (!second || first.nextElementSibling !== second) return false;
      for (let node = first.nextSibling; node !== second; node = node.nextSibling) {
        if (node.nodeType === Node.TEXT_NODE && clean(node.nodeValue || '')) return false;
      }
      return true;
    }
    function pairedPopup(marker) {
      if (!marker?.matches('span.fa-comments') || !/^tooltip\d+$/.test(marker.id) || omitted(marker) || !uniquePeerId(marker)) return null;
      const content = marker.nextElementSibling;
      if (!content?.matches('div.invisible,div.tooltip-invisible') || content.id !== 'content' + marker.id ||
          !adjacent(marker, content) || !uniquePeerId(content) || getComputedStyle(content).display !== 'none' ||
          content.matches('[hidden],[aria-hidden="true"],' + UI) || content.children.length !== 1 ||
          !content.firstElementChild.matches('div.fup-popup')) return null;
      const popup = content.firstElementChild;
      if (omitted(popup) || [...content.childNodes].some(node => node.nodeType === Node.TEXT_NODE && clean(node.nodeValue || ''))) return null;
      return popup;
    }
    function readMoreControl(node, popup) {
      // RichText::getEnhancedHtml appends this control to its one long_text
      // wrapper. Exclude only that generated control in a validated popup.
      const wrapper = node.parentElement;
      return popup && node.matches('p.read_more') && wrapper?.matches('div.long_text') &&
        wrapper.parentElement === popup && popup.firstElementChild === wrapper &&
        [...popup.children].every(child => child === wrapper || child.matches('script')) &&
        wrapper.lastElementChild === node && node.children.length === 1 &&
        node.firstElementChild.matches('span.read_more_button') && clean(node.textContent) === '...';
    }
    function expandedPreview(node, shortened) {
      if (!node.matches('span') || !/^text\d+$/.test(node.id) || !uniquePeerId(node) || !shortened.endsWith(' (...)')) return null;
      const marker = node.nextElementSibling;
      if (!adjacent(node, marker)) return null;
      const popup = pairedPopup(marker);
      if (!popup) return null;
      // These are DOM sibling references, not global ID lookups. showItem
      // legitimately clones the first triplet into its outer list popup.
      const prefix = shortened.slice(0, -6);
      if (!prefix) return null;
      const full = clean([...popup.childNodes].map(child => read(child, null, popup)).join(''));
      if (full.length <= prefix.length || !full.startsWith(prefix)) return null;
      expandedSources.set(node, popup);
      return full;
    }
    function read(node, owner = cell, popup = null) {
      if (node.nodeType === Node.TEXT_NODE) return node.nodeValue || '';
      if (!(node instanceof Element)) return '';
      if (node !== cell && (node.matches(TABLE) || node.matches(UI))) return '';
      if (omitted(node) || readMoreControl(node, popup)) return '';
      if (node.matches('input')) {
        const type = (node.getAttribute('type') || 'text').toLowerCase();
        if (['password', 'hidden', 'file', 'checkbox', 'radio', 'button', 'submit', 'reset', 'image'].includes(type)) return '';
        return node.value || '';
      }
      if (node.matches('textarea')) return node.value || '';
      if (node.matches('select')) return [...node.selectedOptions].map(o => o.textContent).join(', ');
      if (node.matches('br,hr')) return '\n';
      if (node.matches('img')) return node.getAttribute('alt') || node.getAttribute('aria-label') || node.getAttribute('title') || '';
      const text = [...node.childNodes].map(child => read(child, owner, popup)).join('');
      const fallback = node.getAttribute('aria-label') || node.getAttribute('title') || '';
      const iconOnly = /^[\p{P}\p{S}\s\uE000-\uF8FF]+$/u.test(compact(text));
      let value = compact(text) && !iconOnly ? text : fallback || text;
      if (fallback && (!compact(text) || iconOnly) && node.parentElement && [...node.parentElement.childNodes].some(sibling => sibling !== node && (sibling.nodeType === Node.TEXT_NODE || sibling instanceof Element && visible(sibling)) && plain(sibling.textContent).includes(plain(fallback)))) value = '';
      if (node.parentElement === owner) {
        const full = expandedPreview(node, clean(value));
        if (full !== null) { value = full; expandedPreviews.add(node); }
      }
      return /^(DIV|P|LI)$/.test(node.tagName) ? '\n' + value + '\n' : value;
    }
    const shown = clean(read(cell));
    // GLPI 9.5/10 showItem abbreviates long multivalue cells to their first item,
    // with no ellipsis. Html::showToolTip supplies the complete static list in a
    // specifically paired fa-comments popup. Read that list only when its first
    // item exactly proves the association with the displayed cell. Ordinary
    // information/title tooltips and every unpaired hidden subtree stay excluded.
    const candidates = [];
    for (const marker of [...cell.children].filter(node => node.matches('span.fa-comments') && visible(node))) {
      if (!shown) break;
      const popup = pairedPopup(marker);
      if (!popup) continue;
      const directBreaks = [...popup.children].filter(node => node.matches('br,hr') && !omitted(node));
      const delimiter = directBreaks.some(node => node.matches('hr')) ? 'hr' : 'br';
      if (directBreaks.filter(node => node.matches(delimiter)).length < 2) continue;
      const parts = [''];
      const groups = [[]];
      for (const node of popup.childNodes) {
        if (node instanceof Element && node.matches(delimiter) && !omitted(node)) { parts.push(''); groups.push([]); }
        else { parts[parts.length - 1] += read(node, popup, popup); groups[groups.length - 1].push(node); }
      }
      // A user item can itself contain a hidden fa-info card with BRs. Its
      // omitted subtree is not a separator or part of the exported value. BRs
      // inside an exactly proven long-text preview belong to that item too.
      const structure = all('br,hr,' + TABLE, popup).filter(node => {
        for (let parent = node; parent !== popup; parent = parent.parentElement) {
          if (omitted(parent) || node.matches('br,hr') && expandedPreviews.has(parent)) return false;
        }
        return true;
      });
      if (structure.some(node => node.matches(TABLE)) || delimiter === 'br' && structure.some(node => node.matches('hr'))) continue;
      const breaks = structure.filter(node => node.matches(delimiter));
      if (breaks.length < 2 || breaks.some(node => node.parentElement !== popup)) continue;
      const items = parts.map(clean);
      if (items.at(-1) === '') { items.pop(); groups.pop(); } // showItem appends a separator after every item.
      if (items.length >= 2 && items.every(Boolean) && items[0] === shown) candidates.push({ text: items.join('\n'), popup, groups, delimiter });
    }
    const selected = candidates.length === 1 ? candidates[0] : null;
    const value = selected ? selected.text : shown;
    if (evidence && value.length <= 262144) {
      const projected = csvProjection(selected);
      if (projected !== null && projected !== value) Object.assign(evidence, { kind: 'glpi10-rich-text-v1', source: value, text: projected });
    }
    return value;

    function csvProjection(selected) {
      // GLPI 10 DataExport/RichText + Html2Text 4: a bounded subset, tied to
      // these exact DOM nodes. Unsupported markup supplies no alternative.
      // No HTML or hidden user-card content crosses the browser bridge.
      let failed = false, changed = !!selected, visits = 0, characters = 0;
      const allowed = new Set(['DIV', 'SPAN', 'P', 'A', 'B', 'STRONG', 'I', 'EM', 'UL', 'OL', 'LI', 'BR', 'HR', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6']);
      const block = new Set(['DIV', 'P', 'UL', 'OL', 'LI', 'BR', 'HR', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6']);
      const formatted = new Set(['B', 'STRONG', 'I', 'EM', 'LI', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6']);
      function project(node, popup = null, depth = 0, formats = []) {
        if (failed) return '';
        if (++visits > 4096 || depth > 64) { failed = true; return ''; }
        if (node.nodeType === Node.TEXT_NODE) {
          const text = node.nodeValue || '';
          characters += text.length;
          if (characters > 262144) failed = true;
          return text;
        }
        if (!(node instanceof Element)) return '';
        if (node.matches('script,style,div.invisible,div.tooltip-invisible') || readMoreControl(node, popup)) return '';
        if (omitted(node) || !allowed.has(node.tagName) || node.tagName === 'LI' && !node.parentElement?.matches('ul,ol')) { failed = true; return ''; }
        // Html2Text's ordered regular expressions do not treat arbitrary nested
        // formatting like a recursive renderer. Keep that ambiguous subset out.
        if (formats.includes(node.tagName) || formats.length && block.has(node.tagName) ||
            [...node.classList].some(name => name === '_html2text_ignore' || name.startsWith('_html2text_link_'))) { failed = true; return ''; }
        const expansion = expandedSources.get(node);
        if (expansion) return [...expansion.childNodes].map(child => project(child, expansion, depth + 1, formats)).join('');
        if (/^text\d+$/.test(node.id) && clean(node.textContent || '').endsWith(' (...)')) { failed = true; return ''; }
        if (node.matches('[class*="fa-"],[class*="ti-"]') && !compact(node.textContent)) return '';
        if ((node.hasAttribute('aria-label') || node.hasAttribute('title')) && !compact(node.textContent)) { failed = true; return ''; }
        const nextFormats = formatted.has(node.tagName) ? formats.concat(node.tagName) : formats;
        const text = [...node.childNodes].map(child => project(child, popup, depth + 1, nextFormats)).join('');
        switch (node.tagName) {
          case 'B': case 'STRONG': changed = true; return text.toUpperCase();
          case 'H1': case 'H2': case 'H3': case 'H4': case 'H5': case 'H6': changed = true; return '\n' + text.toUpperCase() + '\n';
          case 'I': case 'EM': changed = true; return '_' + text + '_';
          case 'LI': changed = true; return '\n* ' + text + '\n';
          case 'HR': changed = true; return '\n-------------------------\n';
          case 'BR': return '\n';
          case 'DIV': return '\n' + text;
          case 'P': case 'UL': case 'OL': return '\n' + text + '\n';
          default: return text;
        }
      }
      const projected = selected
        // Search::showItem replaces its inter-item markers after text conversion.
        // These literal separators differ from an HR inside rich text content.
        ? selected.groups.map(group => clean(group.map(node => project(node, selected.popup)).join(''))).join('<' + selected.delimiter + '>')
        : [...cell.childNodes].map(node => project(node)).join('');
      const normalized = clean(projected).replace(/"/g, "''");
      return failed || !changed || normalized.length > 262144 ? null : normalized;
    }
  }

  function rowCells(row, table) {
    const selector = 'th,td,[role="cell"],[role="gridcell"],[role="columnheader"],[role="rowheader"]';
    return all(selector, row).filter(c => c.closest('tr,[role="row"]') === row && c.closest(TABLE) === table && visible(c));
  }

  function rowInfo(table) {
    const rows = all('tr,[role="row"]', table).filter(r => r.closest(TABLE) === table && visible(r) && !r.closest('tfoot'));
    const cells = rows.map(r => rowCells(r, table));
    const headerIndices = new Set();
    rows.forEach((row, i) => {
      if (row.closest('thead')?.closest(TABLE) === table || cells[i].some(c => c.getAttribute('role') === 'columnheader')) headerIndices.add(i);
    });
    if (!headerIndices.size) {
      for (let i = 0; i < rows.length; i++) {
        if (cells[i].length && cells[i].every(c => c.tagName === 'TH' && c.getAttribute('scope') !== 'row')) headerIndices.add(i);
        else break;
      }
    }
    return { rows, cells, headerIndices };
  }

  function numberAttribute(element, names, fallback, max, warnings) {
    for (const name of names) {
      const raw = element.getAttribute(name);
      if (raw === null) continue;
      const value = Number(raw);
      if (!Number.isInteger(value) || value < 0) return fallback;
      if (value > max) { warnings.add('Une dimension de cellule dépasse la limite de lecture ; vérifiez les colonnes exportées.'); return max; }
      return value || fallback;
    }
    return fallback;
  }

  function gridFor(info, warnings, representations = null) {
    const pending = [];
    const matrix = [];
    let proofCharacters = 0, proofCells = 0;
    for (let rowIndex = 0; rowIndex < info.rows.length; rowIndex++) {
      const output = [];
      const representationRow = [];
      pending.forEach((span, index) => {
        if (span?.remaining > 0) {
          output[index] = span.value;
          representationRow[index] = span.evidence;
          if (--span.remaining === 0) pending[index] = null;
        }
      });
      for (const cell of info.cells[rowIndex]) {
        let col = 0;
        while (output[col] !== undefined) col++;
        const declared = Number(cell.getAttribute('aria-colindex'));
        if (Number.isInteger(declared) && declared > 0 && declared <= 256) col = declared - 1;
        const colSpan = numberAttribute(cell, ['aria-colspan', 'colspan'], 1, 256, warnings);
        const group = info.rows[rowIndex].closest('thead,tbody,tfoot,[role="rowgroup"]');
        let remainingGroupRows = 0;
        for (let i = rowIndex; i < info.rows.length && info.rows[i].closest('thead,tbody,tfoot,[role="rowgroup"]') === group; i++) remainingGroupRows++;
        const rowSpan = cell.getAttribute('rowspan') === '0' ? remainingGroupRows : Math.min(remainingGroupRows, numberAttribute(cell, ['aria-rowspan', 'rowspan'], 1, info.rows.length, warnings));
        const evidence = representations && !info.headerIndices.has(rowIndex) && rowSpan === 1 && colSpan === 1 &&
          proofCells < 512 && proofCharacters < 4194304 ? {} : null;
        const value = cellText(cell, evidence);
        let proof = evidence?.kind ? evidence : null;
        if (proof) {
          const size = proof.source.length + proof.text.length;
          if (proofCharacters + size > 4194304) proof = null;
          else { proofCharacters += size; proofCells++; }
        }
        for (let offset = 0; offset < colSpan && col + offset < 256; offset++) {
          output[col + offset] = value;
          representationRow[col + offset] = proof;
          if (rowSpan > 1) pending[col + offset] = { value, evidence: proof, remaining: rowSpan - 1 };
        }
      }
      matrix.push(output);
      if (representations) representations.push(representationRow);
    }
    return matrix;
  }

  function fingerprint(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) { hash ^= text.charCodeAt(i); hash = Math.imul(hash, 16777619); }
    return (hash >>> 0).toString(16).padStart(8, '0');
  }

  function labelFor(table) {
    const direct = table.getAttribute('aria-label') || (table.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.textContent || '').join(' ');
    if (compact(direct)) return compact(direct).slice(0, 160);
    const caption = [...table.children].find(c => c.tagName === 'CAPTION');
    if (caption && compact(caption.textContent)) return compact(caption.textContent).slice(0, 160);
    let scope = table.parentElement;
    for (let i = 0; scope && i < 4; i++, scope = scope.parentElement) {
      const heading = all('h1,h2,h3,h4,[role="heading"]', scope).find(h => visible(h) && !h.closest(TABLE));
      if (heading) return compact(heading.textContent).slice(0, 160);
    }
    return 'Tableau ' + (tables().indexOf(table) + 1);
  }

  function stableRowKey(row, values, headers) {
    for (const name of ['data-item-id', 'data-itemid', 'data-id', 'data-uid']) {
      const value = row.getAttribute(name);
      if (value && /^[a-zA-Z0-9_.:-]{1,100}$/.test(value)) return (row.getAttribute('data-itemtype') || 'item') + ':' + value;
    }
    if (/^(row|item)[_-][a-zA-Z0-9_-]+$/.test(row.id)) return row.id;
    const idIndex = headers.findIndex(h => /^(id|identifiant|n[°o])$/.test(plain(h)));
    // GLPI can display numeric IDs with ordinary, nonbreaking, or narrow spaces.
    // Normalize only the identity key; keep the displayed cell value untouched.
    const numericId = idIndex < 0 ? '' : (values[idIndex] || '').replace(/[ \u00a0\u202f]/g, '');
    if (/^\d+$/.test(numericId)) return 'id:' + numericId;
    for (const link of all('a[href]', row)) {
      if (link.closest(TABLE) !== row.closest(TABLE)) continue;
      try {
        const url = new URL(link.href, location.href);
        const id = url.searchParams.get('id');
        if (url.origin === location.origin && /\.form\.php$/i.test(url.pathname) && /^\d+$/.test(id || '')) return url.pathname + '#id=' + id;
      } catch (_) { /* Non-URL link. */ }
    }
    return '';
  }

  function isEmptyState(cells) {
    return cells.length === 1 && /^(aucun(e)? (element|resultat|donnee|ticket|ordinateur)|pas de resultat|no (item|result|data|record)|nothing found)/.test(plain(cellText(cells[0])));
  }

  function parseTable(table, withRepresentations = false) {
    const warnings = new Set();
    const info = rowInfo(table);
    const representationGrid = withRepresentations ? [] : null;
    const matrix = gridFor(info, warnings, representationGrid);
    const width = matrix.reduce((n, row) => Math.max(n, row.length), 0);
    const headers = [];
    const used = new Set();
    for (let col = 0; col < width; col++) {
      const parts = [];
      [...info.headerIndices].forEach(i => {
        const part = compact(matrix[i]?.[col]);
        if (part && parts[parts.length - 1] !== part) parts.push(part);
      });
      const base = parts.join(' / ') || 'Colonne ' + (col + 1);
      let name = base;
      let occurrence = 1;
      while (used.has(name)) name = base + ' (' + (++occurrence) + ')';
      used.add(name);
      headers.push(name);
    }
    if (!info.headerIndices.size && info.rows.length) warnings.add('Aucun en-tête explicite : des noms de colonnes ont été générés.');
    const rows = [];
    const csvRepresentations = [];
    const keys = [];
    info.rows.forEach((row, index) => {
      if (info.headerIndices.has(index) || !info.cells[index].length || isEmptyState(info.cells[index])) return;
      const values = Array.from({ length: width }, (_, col) => matrix[index][col] ?? '');
      // Some GLPI themes repeat the column headings at the end of tbody. Require
      // explicit TH semantics and equality to an actual header row; a TD data row
      // containing the same words, or a row-header TH, must remain exportable.
      const repeatedHeader = info.cells[index].every(cell => cell.tagName === 'TH' && cell.getAttribute('scope') !== 'row') &&
        [...info.headerIndices].some(headerIndex => values.every((value, col) => compact(value) === compact(matrix[headerIndex]?.[col])));
      if (repeatedHeader) return;
      rows.push(values);
      if (representationGrid) csvRepresentations.push(Array.from({ length: width }, (_, col) => {
        const proof = representationGrid[index][col];
        return proof ? { ...proof, row: rows.length - 1, column: col } : null;
      }));
      keys.push(stableRowKey(row, values, headers));
    });
    const declaredRows = Number(table.getAttribute('aria-rowcount'));
    const declaredCols = Number(table.getAttribute('aria-colcount'));
    if (declaredRows > info.rows.length || declaredCols > width || all('[aria-rowindex]', table).some(r => Number(r.getAttribute('aria-rowindex')) > info.rows.length)) {
      warnings.add('Grille potentiellement virtualisée : seules les lignes et colonnes présentes dans la page peuvent être lues.');
    }
    if (all(TABLE, table).length) warnings.add('Le contenu des tableaux imbriqués a été exclu.');
    if (keys.some(k => !k)) warnings.add('Certaines lignes n’ont pas d’identifiant stable détectable ; leur déduplication automatique ne peut pas être garantie.');
    return { headers, rows, keys, csvRepresentations, warnings: [...warnings], headerFingerprint: fingerprint(JSON.stringify(headers)), signature: fingerprint(JSON.stringify([headers, rows, keys])) };
  }

  function pagerOwner(root, inventory) {
    const controlled = (root.getAttribute('aria-controls') || '').split(/\s+/);
    const direct = inventory.filter(t => t.id && controlled.includes(t.id));
    if (direct.length === 1) return direct[0];
    let scope = root.parentElement;
    for (let depth = 0; scope && depth < 16; depth++, scope = scope.parentElement) {
      const contained = inventory.filter(t => scope.contains(t));
      if (contained.length) return contained.length === 1 ? contained[0] : null;
    }
    return null;
  }

  function ownedPagers(table) {
    const inventory = tables();
    return all(PAGER).filter(p => visible(p) && !p.closest(TABLE) && pagerOwner(p, inventory) === table);
  }

  function summaryText(node) {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue || '';
    if (!(node instanceof Element) || !visible(node) || node.matches('input,select,textarea,script,style,template')) return '';
    return [...node.childNodes].map(summaryText).join(' ');
  }

  function pageSummary(table) {
    const texts = ownedPagers(table).map(p => compact(summaryText(p)));
    let scope = table.parentElement;
    for (let i = 0; scope && i < 4; i++, scope = scope.parentElement) {
      if (tables().filter(t => scope.contains(t)).length !== 1) break;
      all('.search-results-count,.search-count,.pager_count,.pagination-info,.dataTables_info,[role="status"]', scope).filter(visible).forEach(n => texts.push(compact(summaryText(n))));
    }
    return [...new Set(texts.filter(Boolean))].join(' | ').slice(0, 1200);
  }

  function expectedCount(table) {
    const declared = table.closest('form[data-search-itemtype][data-count]')?.getAttribute('data-count')?.trim();
    if (declared && /^\d+$/.test(declared)) {
      const count = Number(declared);
      if (Number.isSafeInteger(count) && count <= 2147483647) return count;
    }
    const text = plain(pageSummary(table));
    const number = '(?:\\d{1,3}(?:[\\s,.]\\d{3})+|\\d+)';
    const patterns = [
      new RegExp('\\b' + number + '\\s*(?:a|to|[-–])\\s*' + number + '\\s*(?:sur|of|/)\\s*(' + number + ')', 'i'),
      new RegExp('(?:sur|of)\\s+(' + number + ')\\s*(?:resultats?|elements?|entrees?|entries|items?|records?|tickets?|ordinateurs?|computers?)\\b', 'i'),
      new RegExp('\\btotal\\s*:?\\s*(' + number + ')', 'i')
    ];
    for (const pattern of patterns) {
      const match = text.match(pattern);
      if (!match) continue;
      const value = Number(match[1].replace(/\D/g, ''));
      if (Number.isSafeInteger(value) && value >= 0) return value;
    }
    return null;
  }

  function buttonLabel(element) {
    return compact(element.getAttribute('aria-label') || element.getAttribute('title') || element.getAttribute('data-original-title') || element.textContent || element.getAttribute('value')).slice(0, 160);
  }

  function disabled(element) {
    return !!element.disabled || element.getAttribute('aria-disabled') === 'true' || !!element.closest('[aria-disabled="true"],.disabled') || element.hasAttribute('disabled');
  }

  function looksNext(element) {
    const label = plain(buttonLabel(element));
    if (/\b(previous|precedent|premier|first|dernier|last)\b/.test(label)) return false;
    return (element.getAttribute('rel') || '').split(/\s+/).includes('next') || /^(page\s+)?suivante?(\s+page)?$/.test(label) || /^(go to\s+)?next(\s+page)?$/.test(label) || element.getAttribute('data-page') === 'next' || !!element.closest('li.next,.page-item.next');
  }

  function describeNext(element, reason = '') {
    return { selector: uniqueSelector(element), label: buttonLabel(element) || 'Page suivante', disabled: disabled(element), ambiguous: false, reason };
  }

  function getNext(tableKey, overrideSelector) {
    const table = resolveTable(tableKey);
    if (overrideSelector && compact(overrideSelector)) {
      const candidates = all(overrideSelector).filter(visible);
      if (candidates.length !== 1) return { selector: '', label: 'Page suivante', disabled: true, ambiguous: true, reason: 'Le sélecteur choisi ne désigne pas exactement un élément visible.' };
      const element = candidates[0];
      if (!element.matches(CLICKABLE)) return { selector: '', label: 'Page suivante', disabled: true, ambiguous: true, reason: 'Le contrôle choisi n’est pas un lien ou un bouton.' };
      return describeNext(element, 'Contrôle sélectionné explicitement par l’utilisateur.');
    }
    const found = [];
    for (const pager of ownedPagers(table)) {
      all(CLICKABLE, pager).filter(e => visible(e) && looksNext(e)).forEach(e => found.push(e));
    }
    // rel=next is sufficient only when ownership by this table is unambiguous.
    const inventory = tables();
    all('a[rel~="next"],button[rel~="next"]').filter(e => visible(e) && pagerOwner(e, inventory) === table).forEach(e => found.push(e));
    const candidates = [...new Set(found)];
    if (!candidates.length) return null;
    const active = candidates.filter(e => !disabled(e));
    const choices = active.length ? active : candidates;
    const linked = choices.filter(e => e.tagName === 'A' && e.getAttribute('href') && e.getAttribute('href') !== '#');
    const targets = new Set(linked.map(e => e.href));
    if (choices.length > 1 && (targets.size !== 1 || linked.length !== choices.length)) return { selector: '', label: 'Page suivante', disabled: true, ambiguous: true, reason: 'Plusieurs contrôles de pagination ne peuvent pas être considérés comme équivalents.' };
    return describeNext(choices[0], 'Contrôle de pagination associé au tableau.');
  }

  function tableDescription(table) {
    const data = parseTable(table);
    return { key: keyFor(table), label: labelFor(table), selector: uniqueSelector(table), headers: data.headers, rowCount: data.rows.length, columnCount: data.headers.length, headerFingerprint: data.headerFingerprint };
  }

  function inspect() {
    const inventory = tables();
    const descriptions = inventory.map(tableDescription);
    for (const [key, table] of registry) if (!table.isConnected) registry.delete(key);
    const frames = all('iframe,frame').filter(visible);
    const hasPasswordField = all('input[type="password"]').some(visible);
    const authenticatedHint = !hasPasswordField && all('a,button,[role="button"]').some(e => visible(e) && (/^(se deconnecter|deconnexion|log out|logout|sign out)$/.test(plain(buttonLabel(e))) || /^logout(?:\.php)?$/i.test((e.getAttribute('href') || '').split(/[/?#]/).filter(Boolean).pop() || '')));
    const selected = inventory.length === 1 ? inventory[0] : null;
    return {
      url: location.href, title: document.title, origin: location.origin,
      hasPasswordField, authenticatedHint,
      framesHint: frames.length ? frames.length + ' cadre(s) intégré(s) visible(s). Les tableaux de ces cadres nécessitent une inspection séparée.' : '',
      tables: descriptions,
      next: selected ? getNext(keyFor(selected)) : null,
      pageText: selected ? pageSummary(selected) : '',
      expectedTotal: selected ? expectedCount(selected) : null
    };
  }

  function readTable(key, withRepresentations = false) {
    const table = resolveTable(key);
    return { ...parseTable(table, withRepresentations), key: keyFor(table), selector: uniqueSelector(table), label: labelFor(table), next: getNext(keyFor(table)), expectedTotal: expectedCount(table), pageText: pageSummary(table) };
  }

  function clearHighlight() {
    if (!highlight) return;
    clearTimeout(highlight.timer);
    highlight.element.style.outline = highlight.outline;
    highlight.element.style.outlineOffset = highlight.offset;
    highlight = null;
  }

  function highlightTable(key) {
    const table = resolveTable(key);
    clearHighlight();
    highlight = { element: table, outline: table.style.outline, offset: table.style.outlineOffset, timer: null };
    table.style.outline = '3px solid #2563eb';
    table.style.outlineOffset = '3px';
    table.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
    highlight.timer = setTimeout(clearHighlight, 2500);
    return { selector: uniqueSelector(table), key: keyFor(table) };
  }

  function cleanPicker() {
    if (!picker) return;
    document.removeEventListener('pointermove', picker.move, true);
    document.removeEventListener('pointerdown', picker.down, true);
    document.removeEventListener('mousedown', picker.down, true);
    document.removeEventListener('click', picker.click, true);
    document.removeEventListener('keydown', picker.keydown, true);
    picker.overlay.remove();
    picker.banner.remove();
    picker = null;
  }

  function cancelPick() {
    const kind = picker?.kind || '';
    cleanPicker();
    window.__glpiPilotPick = { kind, cancelled: true, selector: '', label: '' };
    return true;
  }

  function pick(kind) {
    if (!['table', 'next', 'ready'].includes(kind)) throw new Error('Type de sélection inconnu.');
    cleanPicker();
    clearHighlight();
    window.__glpiPilotPick = null;
    const overlay = document.createElement('div');
    overlay.setAttribute('data-glpi-pilot-ui', 'selection');
    overlay.style.cssText = 'display:none;position:fixed;pointer-events:none;border:3px solid #2563eb;background:rgba(37,99,235,.10);box-sizing:border-box;border-radius:6px;z-index:2147483646;';
    const banner = document.createElement('div');
    banner.setAttribute('data-glpi-pilot-ui', 'instruction');
    banner.style.cssText = 'position:fixed;left:50%;top:14px;transform:translateX(-50%);pointer-events:none;max-width:calc(100vw - 40px);padding:13px 20px;background:#0f172a;color:#fff;border:1px solid #60a5fa;border-radius:10px;font:600 14px/1.45 Segoe UI,sans-serif;box-shadow:0 8px 30px #0005;z-index:2147483647;';
    banner.textContent = kind === 'table' ? 'GLPI Pilot : cliquez sur le tableau à extraire. Échap pour annuler.' : kind === 'next' ? 'GLPI Pilot : cliquez sur le contrôle « Page suivante ». Échap pour annuler.' : 'GLPI Pilot : cliquez sur un élément visible après la connexion. Échap pour annuler.';
    document.documentElement.append(overlay, banner);

    const targetFor = target => {
      if (!(target instanceof Element) || target.closest(UI)) return null;
      let element = kind === 'table' ? target.closest(TABLE) : kind === 'next' ? target.closest(CLICKABLE) : target.closest('a,button,[role="button"],h1,h2,h3,[role="heading"]') || target;
      if (!element || !visible(element) || element.matches('html,body,input[type="password"],input[type="hidden"]')) return null;
      if (kind === 'table') while (element.parentElement?.closest(TABLE)) element = element.parentElement.closest(TABLE);
      return element;
    };
    const move = event => {
      const target = targetFor(event.target);
      if (!target) { overlay.style.display = 'none'; return; }
      const rect = target.getBoundingClientRect();
      Object.assign(overlay.style, { display: 'block', left: rect.left + 'px', top: rect.top + 'px', width: rect.width + 'px', height: rect.height + 'px' });
    };
    const down = event => { if (event.button === 0) { event.preventDefault(); event.stopImmediatePropagation(); } };
    const click = event => {
      event.preventDefault();
      event.stopImmediatePropagation();
      const target = targetFor(event.target);
      if (!target) return;
      const result = { kind, selector: uniqueSelector(target), label: kind === 'table' ? labelFor(target) : buttonLabel(target), cancelled: false };
      if (kind === 'table') { result.key = keyFor(target); result.headerFingerprint = parseTable(target).headerFingerprint; }
      window.__glpiPilotPick = result;
      cleanPicker();
    };
    const keydown = event => { if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); cancelPick(); } };
    picker = { kind, overlay, banner, move, down, click, keydown };
    document.addEventListener('pointermove', move, true);
    document.addEventListener('pointerdown', down, true);
    document.addEventListener('mousedown', down, true);
    document.addEventListener('click', click, true);
    document.addEventListener('keydown', keydown, true);
    return true;
  }

  window.__glpiPilot = { version: VERSION, inspect, readTable, getNext, highlightTable, pick, cancelPick };
  window.__glpiPilotPick = null;
  return VERSION;
})();
