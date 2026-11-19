(() => {
  'use strict';

  // Read-only discovery of the native GLPI display-preference controls.
  // The host clicks/selects through Playwright. No form, URL, request or DOM
  // mutation is manufactured by this adapter; users_id=0 is never actionable.
  const VERSION = '1.2.0';
  const TABLES = 'table.search-results,table.tab_cadrehov,table.tab_cadre_fixehov';
  const PREF_PATH = /\/front\/displaypreference\.form\.php\/?$/;
  const tidy = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const positive = value => /^[1-9]\d*$/.test(String(value ?? ''));
  const visible = element => {
    if (!(element instanceof Element) || !element.isConnected) return false;
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.hidden || node.getAttribute('aria-hidden') === 'true' || node.hasAttribute('inert')) return false;
      const style = getComputedStyle(node);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    return element.getClientRects().length > 0;
  };
  const disabled = element => !element || element.matches(':disabled,[disabled],[aria-disabled="true"],.disabled') ||
    !!element.closest('fieldset[disabled],[aria-disabled="true"]');
  const find = (root, query) => [...root.querySelectorAll(query)];
  const only = values => values.length === 1 ? values[0] : null;
  const ambiguity = (role, count) => count > 1 ? { ambiguousRole: role, matchCount: count } : {};
  const duplicateCount = values => {
    const counts = new Map(); let maximum = 0;
    for (const value of values) {
      const count = (counts.get(value) || 0) + 1;
      counts.set(value, count); maximum = Math.max(maximum, count);
    }
    return maximum;
  };
  function selector(element) {
    if (!element) return '';
    const parts = [];
    for (let node = element; node instanceof Element; node = node.parentElement) {
      if (node.id) {
        const id = '#' + CSS.escape(node.id);
        if (document.querySelectorAll(id).length === 1) return [id, ...parts].join(' > ');
      }
      const siblings = node.parentElement ? [...node.parentElement.children].filter(item => item.localName === node.localName) : [node];
      parts.unshift(node.localName + (siblings.length > 1 ? ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')' : ''));
    }
    return parts.join(' > ');
  }
  function localUrl(raw) {
    try {
      const url = new URL(raw, document.baseURI);
      return /^https?:$/.test(url.protocol) && url.origin === location.origin && !url.username && !url.password ? url : null;
    } catch { return null; }
  }
  function preferenceUrl(raw) {
    const url = localUrl(raw);
    return url && PREF_PATH.test(url.pathname) && url.searchParams.getAll('itemtype').length === 1 &&
      url.searchParams.get('itemtype') === 'Ticket' ? url : null;
  }
  function inspectWindow(tableSelector = '', references = {}) {
    const state = { supported: false, reason: '', proof: '', ambiguousRole: '', matchCount: 0,
      tableSelector: '', openerSelector: '', modalSelector: '', iframeSelector: '', closeSelector: '' };
    let tables;
    try { tables = find(document, tableSelector || TABLES).filter(node => node.matches(TABLES) && visible(node)); }
    catch { return { ...state, reason: 'Le sélecteur du tableau ne peut pas être vérifié.' }; }
    const table = only(tables);
    if (!table) return { ...state, ...ambiguity('table', tables.length), reason: 'Le tableau de tickets doit être identifié sans ambiguïté avant de préparer ses colonnes.' };
    references.table = table;
    state.tableSelector = selector(table);
    const owner = table.closest('form[data-search-itemtype],form[id^="massform"],form[name^="massform"]');
    const itemtype = owner?.getAttribute('data-search-itemtype');
    if (itemtype && itemtype !== 'Ticket') return { ...state, reason: 'La préparation des colonnes est réservée à la liste des tickets.' };

    const frames = find(document, 'iframe').filter(frame => visible(frame) &&
      (frame.matches('[id^="Iframesearch_config_"]') || frame.closest('[role="dialog"][id^="displaypref"]')));
    if (frames.length > 0) {
      const frame = only(frames);
      if (!frame || !preferenceUrl(frame.getAttribute('src'))) return { ...state, ...ambiguity('iframe', frames.length), reason: 'La fenêtre des colonnes ne correspond pas à un formulaire personnel de tickets vérifiable.' };
      const modal = frame.closest('[role="dialog"],.ui-dialog');
      const closes = modal ? find(modal, 'button[data-bs-dismiss="modal"],button.ui-dialog-titlebar-close').filter(node => visible(node) && !disabled(node)) : [];
      const close = only(closes);
      if (!modal || !close) return { ...state, ...ambiguity('close', closes.length), reason: 'La fermeture native de la fenêtre des colonnes est introuvable.' };
      references.iframe = frame; references.close = close;
      return { ...state, supported: true, proof: 'native-preference-frame', modalSelector: selector(modal), iframeSelector: selector(frame), closeSelector: selector(close) };
    }

    const card = table.closest('.search-card');
    if (card) {
      if (!owner || itemtype !== 'Ticket') return { ...state, reason: 'Le type Ticket du tableau GLPI n’est pas confirmé.' };
      const openers = find(card, 'button.show_displaypreference_modal').filter(node =>
        visible(node) && !disabled(node) && !node.closest('table,[role="grid"],[role="table"]') && node.closest('.search-card') === card);
      if (openers.length === 1) {
        references.opener = openers[0];
        return { ...state, supported: true, proof: 'native-search-preference-button', openerSelector: selector(openers[0]) };
      }
      return { ...state, ...ambiguity('opener', openers.length), reason: openers.length ? 'Plusieurs commandes de colonnes sont associées à ce tableau.' : 'GLPI ne propose pas la configuration des colonnes pour cette liste ou ce profil.' };
    }

    // GLPI 9.5 renders equivalent top/bottom pagers. Recognize only the
    // reviewed jQuery dialog call and its already rendered matching iframe.
    const ticketSearch = find(document, 'form[name="searchformTicket"],form#searchformTicket');
    if ((!owner || !/^massformTicket\d*$/.test(owner.id || owner.getAttribute('name') || '')) && ticketSearch.length !== 1)
      return { ...state, reason: 'Le formulaire natif de la liste des tickets n’est pas reconnu.' };
    const openers = find(document, 'span.fa-wrench.pointer[onclick]').filter(node => visible(node) && !disabled(node))
      .map(node => {
        const match = /^\s*\$\(\s*(['"])#(search_config_(?:top|bottom))\1\s*\)\.dialog\(\s*(['"])open\3\s*\);?\s*$/.exec(node.getAttribute('onclick') || '');
        return match && document.getElementById(match[2])?.querySelector('#' + CSS.escape('Iframe' + match[2])) ? { node, target: match[2] } : null;
      }).filter(Boolean);
    const chosen = only(openers.filter(item => item.target === 'search_config_top')) || only(openers);
    if (chosen) references.opener = chosen.node;
    return chosen ? { ...state, supported: true, proof: 'native-legacy-preference-dialog', openerSelector: selector(chosen.node) }
      : { ...state, ...ambiguity('opener', openers.length), reason: 'La commande personnelle de colonnes GLPI n’a pas été reconnue.' };
  }

  function inspect(tableSelector = '') { return inspectWindow(tableSelector); }

  function hiddenValue(root, name) {
    const controls = find(root, 'input[name="' + name + '"]');
    if (!controls.length || controls.some(node => node.type !== 'hidden' || node.disabled)) return null;
    const values = [...new Set(controls.map(node => node.value))];
    return values.length === 1 ? values[0] : null;
  }
  function personalTab(state) {
    const candidates = find(document, 'a[href]').filter(node => {
      if (disabled(node)) return false;
      const url = localUrl(node.getAttribute('href'));
      return url && /\/ajax\/common\.tabs\.php$/.test(url.pathname) &&
        url.searchParams.get('_itemtype') === 'DisplayPreference' &&
        url.searchParams.get('_glpi_tab') === 'DisplayPreference$2' && url.searchParams.get('displaytype') === 'Ticket';
    });
    // A retained hidden tab must not obscure the one actionable native link.
    // Compact mode still requires exactly one hidden link to prove its index.
    const displayed = candidates.filter(visible);
    const eligible = displayed.length ? displayed : candidates;
    const link = only(eligible);
    if (eligible.length > 1) Object.assign(state, ambiguity('personal-tab', eligible.length));
    if (!link) return null;
    if (visible(link)) return { link, select: null, value: '' };
    // GLPI 10's compact tab selector mirrors the ordered native tab links.
    const tabs = link.closest('#tabspanel');
    const compacts = find(document, 'select#tabspanel-select').filter(node => visible(node) && !disabled(node));
    const compact = only(compacts);
    if (compacts.length > 1) Object.assign(state, ambiguity('personal-tab-select', compacts.length));
    if (!tabs || !compact) return null;
    const links = find(tabs, 'a[href]');
    const index = links.indexOf(link);
    const options = [...compact.options].filter(item => item.value === String(index));
    if (options.length > 1) Object.assign(state, ambiguity('options', options.length));
    const option = only(options);
    return option && !option.disabled && tidy(option.textContent) === tidy(link.textContent) ? { link: null, select: compact, value: option.value } : null;
  }
  function validForm(form, usersId) {
    if (!form || form.tagName !== 'FORM' || form.method.toLowerCase() !== 'post') return false;
    const target = localUrl(form.getAttribute('action'));
    return target && PREF_PATH.test(target.pathname) && hiddenValue(form, 'itemtype') === 'Ticket' && hiddenValue(form, 'users_id') === usersId &&
      !find(form, '[formaction],[formmethod],[formtarget],[form]').length;
  }
  function selectUsable(control) {
    if (disabled(control)) return false;
    if (visible(control)) return true;
    return control.classList.contains('select2-hidden-accessible') &&
      !!control.parentElement && find(control.parentElement, '.select2-container').some(visible);
  }
  function optionList(control) {
    if (!control) return [];
    return [...control.options].filter(option => positive(option.value)).map(option => {
      const label = tidy(option.textContent), group = tidy(option.closest('optgroup')?.label);
      return { value: option.value, label, group, fullLabel: group ? group + ' - ' + label : label,
        disabled: option.disabled || !!option.closest('optgroup[disabled]') };
    });
  }
  function inspectPersonalPreferences(references = {}) {
    const state = { supported: false, reason: '', proof: '', ambiguousRole: '', matchCount: 0,
      layout: '', itemType: '', usersId: '', personalScope: false, personalActive: false,
      personalTabSelector: '', personalTabSelectSelector: '', personalTabValue: '', formSelector: '', activateSelector: '', disableSelector: '',
      restorationSupported: false, restorationReason: '', selectSelector: '', selectHidden: false, addSelector: '',
      options: [], selected: [], selectedValue: '', selectedIdsKnown: false };
    const url = localUrl(location.href);
    // Legacy POST navigation does not retain the GET itemtype parameter.
    // The personal form/tab below must still prove Ticket from its rendered DOM.
    if (!url || !PREF_PATH.test(url.pathname) || url.searchParams.has('itemtype') &&
      (url.searchParams.getAll('itemtype').length !== 1 || url.searchParams.get('itemtype') !== 'Ticket'))
      return { ...state, reason: 'Ce document n’est pas le formulaire GLPI de colonnes des tickets.' };
    const tab = personalTab(state);
    if (state.ambiguousRole) return { ...state, reason: 'La commande de vue personnelle n’est pas identifiable sans ambiguïté.' };
    state.personalTabSelector = selector(tab?.link);
    state.personalTabSelectSelector = selector(tab?.select);
    state.personalTabValue = tab?.value || '';
    references['personal-tab'] = tab?.link || null;
    references['personal-tab-select'] = tab?.select || null;
    const bodies = find(document, '#tabsbody').filter(visible);
    if (bodies.length > 1) return { ...state, ...ambiguity('personal-view', bodies.length), reason: 'Plusieurs vues de colonnes sont visibles dans le formulaire personnel.' };
    const body = only(bodies);
    if (!body) return { ...state, supported: !!tab, proof: tab ? 'native-personal-tab' : '', reason: tab ? 'Ouvrir la vue personnelle.' : 'La vue personnelle n’est pas proposée par GLPI.' };
    const itemType = hiddenValue(body, 'itemtype'), usersId = hiddenValue(body, 'users_id');
    state.itemType = itemType ?? ''; state.usersId = usersId ?? '';
    if (itemType === null && usersId === null && tab)
      return { ...state, supported: true, proof: 'native-personal-tab', reason: 'Ouvrir la vue personnelle depuis l’affichage global en lecture seule.' };
    if (itemType !== 'Ticket') return { ...state, reason: 'Le formulaire ne désigne pas uniquement les tickets.' };
    if (usersId === '0') return { ...state, supported: !!tab, proof: tab ? 'native-personal-tab' : '', reason: tab ? 'Ouvrir la vue personnelle ; la vue globale reste intacte.' : 'Le profil ne propose pas de vue personnelle modifiable.' };
    if (!positive(usersId)) return { ...state, reason: 'L’identité de la vue personnelle n’est pas vérifiable.' };
    const modern = body.classList.contains('display_preference_config');
    state.layout = modern ? 'modern' : 'legacy';
    state.personalScope = true;
    const controls = name => find(body, (modern ? 'button' : 'input[type="submit"]') + '[name="' + name + '"]').filter(visible);
    const activates = controls('activate');
    if (activates.length) {
      const activate = only(activates);
      if (!activate || disabled(activate) || !modern && !validForm(activate.form, usersId)) return { ...state, ...ambiguity('activate', activates.length), reason: 'La création native de la vue personnelle n’est pas disponible.' };
      references.activate = activate;
      // The reviewed native activation creates an ordinary personal view.
      // Its resulting disable control must be observed before any addition;
      // no inverse POST is manufactured from this capability declaration.
      return { ...state, supported: true, restorationSupported: true, proof: 'native-personal-activation',
        formSelector: selector(modern ? body : activate.form), activateSelector: selector(activate) };
    }
    state.personalActive = true;
    const disables = controls('disable');
    if (disables.length > 1)
      return { ...state, ...ambiguity('disable', disables.length), reason: 'Plusieurs commandes de désactivation personnelle sont visibles.' };
    const disable = only(disables);
    if (disable && !disabled(disable) && (modern || validForm(disable.form, usersId))) {
      references.disable = disable;
      state.disableSelector = selector(disable);
    }
    if (modern) {
      state.selectedIdsKnown = true;
      if (find(body, 'ul.list-group > li[data-opt-id]').some(node => !visible(node)))
        return { ...state, reason: 'Une colonne masquée reste présente dans la liste native soumise par GLPI. Sa sauvegarde exacte ne peut pas être certifiée.' };
      state.selected = find(body, 'ul.list-group > li[data-opt-id]').filter(visible).map(node => {
        const value = node.getAttribute('data-opt-id'), fixed = node.getAttribute('data-fixed') === 'true';
        const removals = find(node, 'button[name="remove_opt"]').filter(control => visible(control) && !disabled(control)
          && control.closest('li[data-opt-id]') === node && !control.hasAttribute('form') && !control.hasAttribute('formaction'));
        if (removals.length > 1) Object.assign(state, ambiguity('remove', removals.length));
        const remove = !fixed ? only(removals) : null;
        if (remove && positive(value)) references['remove:option:' + value] = remove;
        return { value, label: tidy(node.querySelector('span.flex-fill,span')?.textContent),
          preferenceId: '', fixed, removeSelector: selector(remove) };
      });
      const duplicates = duplicateCount(state.selected.map(item => item.value));
      if (state.selected.some(item => !positive(item.value)) || duplicates > 1)
        return { ...state, ...ambiguity('options', duplicates), reason: 'La liste des colonnes présentes n’est pas identifiable sans ambiguïté.' };
    } else {
      const forms = find(body, 'form');
      if (!forms.length || forms.some(form => !validForm(form, usersId))) return { ...state, reason: 'Les formulaires de colonnes ne correspondent pas tous à la même vue personnelle.' };
      if (find(body, 'tr').some(row => !visible(row) && row.querySelector('input[name="id"]')))
        return { ...state, reason: 'Une préférence personnelle masquée empêche une sauvegarde complète de l’ordre initial.' };
      state.selected = find(body, 'tr').filter(visible).flatMap(row => {
        const cells = [...row.children].filter(node => node.tagName === 'TD');
        if (cells.length < 2 || cells[0].querySelector('form,input,select,button') || !tidy(cells[0].textContent)) return [];
        const ids = [...new Set(find(row, 'input[name="id"]').map(node => node.value))];
        if (ids.length > 1 || ids.some(id => !positive(id))) {
          Object.assign(state, ambiguity('options', Math.max(2, ids.length)));
          return [];
        }
        const removals = find(row, 'button[name="purge"],input[type="submit"][name="purge"]').filter(control =>
          visible(control) && !disabled(control) && validForm(control.form, usersId)
          && hiddenValue(control.form, 'id') === ids[0]);
        if (removals.length > 1) Object.assign(state, ambiguity('remove', removals.length));
        const remove = only(removals);
        if (remove && ids.length === 1) references['remove:preference:' + ids[0]] = remove;
        return [{ value: '', label: tidy(cells[0].textContent), preferenceId: ids[0] || '', fixed: ids.length === 0,
          removeSelector: selector(remove) }];
      });
    }
    if (state.ambiguousRole) return { ...state, reason: 'La commande de retrait personnel n’est pas identifiable sans ambiguïté.' };
    const identities = state.selected.map(item => item.preferenceId ? 'preference:' + item.preferenceId
      : item.value ? 'option:' + item.value : item.fixed ? 'fixed:' + item.label : '');
    const duplicateIdentity = duplicateCount(identities);
    if (identities.some(id => !id) || duplicateIdentity > 1)
      return { ...state, ...ambiguity('options', duplicateIdentity), reason: 'Les identifiants des colonnes initiales ne peuvent pas être sauvegardés sans ambiguïté.' };
    state.restorationSupported = state.selected.length > 0 && state.selected.some(item => item.removeSelector);
    state.restorationReason = state.restorationSupported ? '' : 'Aucune commande native de retrait individuel des colonnes n’est vérifiable.';
    const selects = find(body, 'select[name="num"]');
    const additions = controls(modern ? 'add_opt' : 'add');
    // A CSS-hidden enabled select inside the active form is still submitted by
    // HTML. Keep every native num control in this ambiguity check, including
    // Select2; only a wholly inactive preference body was excluded above.
    if (selects.length > 1 || additions.length > 1)
      return { ...state, ...ambiguity(selects.length > 1 ? 'select' : 'add', selects.length > 1 ? selects.length : additions.length),
        reason: 'GLPI ne propose pas une commande personnelle d’ajout de colonne vérifiable.' };
    const select = only(selects), add = only(additions);
    if (!select && !add && state.selected.length && (state.disableSelector || state.restorationSupported))
      return { ...state, supported: true, proof: 'native-personal-columns-complete', formSelector: selector(body) };
    if (!select || !add || !selectUsable(select) || disabled(add) || select.multiple ||
      !modern && (select.form !== add.form || !validForm(add.form, usersId)))
      return { ...state, reason: 'GLPI ne propose pas une commande personnelle d’ajout de colonne vérifiable.' };
    const options = optionList(select);
    const duplicates = duplicateCount(options.map(option => option.value));
    if (duplicates > 1)
      return { ...state, ...ambiguity('options', duplicates), reason: 'Plusieurs options de colonne partagent le même identifiant.' };
    references.select = select; references.add = add;
    return { ...state, supported: true, proof: modern ? 'native-personal-options-ajax' : 'native-personal-options-form',
      formSelector: selector(modern ? body : select.form), selectSelector: selector(select), selectHidden: !visible(select), addSelector: selector(add), selectedValue: select.value, options };
  }

  function inspectPreferences() { return inspectPersonalPreferences(); }

  function capture(read, role, roles) {
    const references = {};
    const state = read(references);
    const selected = (state, references) => roles.includes(role) && (state.supported || role === 'table') ? references[role] || null : null;
    const element = selected(state, references);
    const table = references.table || null;
    // This object retains the exact inspected nodes outside the DOM. Reading
    // its properties through a JSHandle never resolves a CSS selector through
    // Playwright's shadow-piercing engine. Reinstallation does not replace the
    // retained object or the inspector closure used for its validation.
    return { state, element, table, validate() {
      const current = {};
      const state = read(current);
      return { state, sameElement: !!element && element.isConnected && element === selected(state, current),
        sameTable: table === (current.table || null) && (!table || table.isConnected) };
    } };
  }

  function captureWindow(tableSelector = '', role = '') {
    return capture(references => inspectWindow(tableSelector, references), role, ['opener', 'iframe', 'close', 'table']);
  }

  function capturePreferences(role = '') {
    const roles = ['personal-tab', 'personal-tab-select', 'activate', 'select', 'add', 'disable'];
    if (/^remove:(?:option|preference):[1-9]\d*$/.test(role)) roles.push(role);
    return capture(inspectPersonalPreferences, role, roles);
  }

  window.__glpiPilotColumns = { version: VERSION, inspect, inspectPreferences, captureWindow, capturePreferences };
})();
