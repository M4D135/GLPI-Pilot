(() => {
  'use strict';

  // Read-only discovery of the native GLPI display-preference controls.
  // The host clicks/selects through Playwright. No form, URL, request or DOM
  // mutation is manufactured by this adapter; users_id=0 is never actionable.
  const VERSION = '1.0.1';
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
  function inspect(tableSelector = '') {
    const state = { supported: false, reason: '', proof: '', tableSelector: '', openerSelector: '', modalSelector: '', iframeSelector: '', closeSelector: '' };
    let tables;
    try { tables = find(document, tableSelector || TABLES).filter(node => node.matches(TABLES) && visible(node)); }
    catch { return { ...state, reason: 'Le sélecteur du tableau ne peut pas être vérifié.' }; }
    const table = only(tables);
    if (!table) return { ...state, reason: 'Le tableau de tickets doit être identifié sans ambiguïté avant de préparer ses colonnes.' };
    state.tableSelector = selector(table);
    const owner = table.closest('form[data-search-itemtype],form[id^="massform"],form[name^="massform"]');
    const itemtype = owner?.getAttribute('data-search-itemtype');
    if (itemtype && itemtype !== 'Ticket') return { ...state, reason: 'La préparation des colonnes est réservée à la liste des tickets.' };

    const frames = find(document, 'iframe').filter(frame => visible(frame) &&
      (frame.matches('[id^="Iframesearch_config_"]') || frame.closest('[role="dialog"][id^="displaypref"]')));
    if (frames.length > 0) {
      const frame = only(frames);
      if (!frame || !preferenceUrl(frame.getAttribute('src'))) return { ...state, reason: 'La fenêtre des colonnes ne correspond pas à un formulaire personnel de tickets vérifiable.' };
      const modal = frame.closest('[role="dialog"],.ui-dialog');
      const closes = modal ? find(modal, 'button[data-bs-dismiss="modal"],button.ui-dialog-titlebar-close').filter(node => visible(node) && !disabled(node)) : [];
      const close = only(closes);
      if (!modal || !close) return { ...state, reason: 'La fermeture native de la fenêtre des colonnes est introuvable.' };
      return { ...state, supported: true, proof: 'native-preference-frame', modalSelector: selector(modal), iframeSelector: selector(frame), closeSelector: selector(close) };
    }

    const card = table.closest('.search-card');
    if (card) {
      if (!owner || itemtype !== 'Ticket') return { ...state, reason: 'Le type Ticket du tableau GLPI n’est pas confirmé.' };
      const openers = find(card, 'button.show_displaypreference_modal').filter(node =>
        visible(node) && !disabled(node) && !node.closest('table,[role="grid"],[role="table"]') && node.closest('.search-card') === card);
      if (openers.length === 1) return { ...state, supported: true, proof: 'native-search-preference-button', openerSelector: selector(openers[0]) };
      return { ...state, reason: openers.length ? 'Plusieurs commandes de colonnes sont associées à ce tableau.' : 'GLPI ne propose pas la configuration des colonnes pour cette liste ou ce profil.' };
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
    return chosen ? { ...state, supported: true, proof: 'native-legacy-preference-dialog', openerSelector: selector(chosen.node) }
      : { ...state, reason: 'La commande personnelle de colonnes GLPI n’a pas été reconnue.' };
  }

  function hiddenValue(root, name) {
    const controls = find(root, 'input[name="' + name + '"]');
    if (!controls.length || controls.some(node => node.type !== 'hidden' || node.disabled)) return null;
    const values = [...new Set(controls.map(node => node.value))];
    return values.length === 1 ? values[0] : null;
  }
  function personalTab() {
    const link = only(find(document, 'a[href]').filter(node => {
      if (disabled(node)) return false;
      const url = localUrl(node.getAttribute('href'));
      return url && /\/ajax\/common\.tabs\.php$/.test(url.pathname) &&
        url.searchParams.get('_itemtype') === 'DisplayPreference' &&
        url.searchParams.get('_glpi_tab') === 'DisplayPreference$2' && url.searchParams.get('displaytype') === 'Ticket';
    }));
    if (!link) return null;
    if (visible(link)) return { link, select: null, value: '' };
    // GLPI 10's compact tab selector mirrors the ordered native tab links.
    const tabs = link.closest('#tabspanel');
    const compact = document.getElementById('tabspanel-select');
    if (!tabs || !compact?.matches('select') || !visible(compact) || disabled(compact)) return null;
    const links = find(tabs, 'a[href]');
    const index = links.indexOf(link);
    const option = [...compact.options].find(item => item.value === String(index));
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
  function inspectPreferences() {
    const state = { supported: false, reason: '', proof: '', layout: '', itemType: '', usersId: '', personalScope: false, personalActive: false,
      personalTabSelector: '', personalTabSelectSelector: '', personalTabValue: '', formSelector: '', activateSelector: '', selectSelector: '', selectHidden: false, addSelector: '',
      options: [], selected: [], selectedValue: '', selectedIdsKnown: false };
    const url = localUrl(location.href);
    // Legacy POST navigation does not retain the GET itemtype parameter.
    // The personal form/tab below must still prove Ticket from its rendered DOM.
    if (!url || !PREF_PATH.test(url.pathname) || url.searchParams.has('itemtype') &&
      (url.searchParams.getAll('itemtype').length !== 1 || url.searchParams.get('itemtype') !== 'Ticket'))
      return { ...state, reason: 'Ce document n’est pas le formulaire GLPI de colonnes des tickets.' };
    const tab = personalTab();
    state.personalTabSelector = selector(tab?.link);
    state.personalTabSelectSelector = selector(tab?.select);
    state.personalTabValue = tab?.value || '';
    const body = only(find(document, '#tabsbody').filter(visible));
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
      if (!activate || disabled(activate) || !modern && !validForm(activate.form, usersId)) return { ...state, reason: 'La création native de la vue personnelle n’est pas disponible.' };
      return { ...state, supported: true, proof: 'native-personal-activation', formSelector: selector(modern ? body : activate.form), activateSelector: selector(activate) };
    }
    state.personalActive = true;
    if (modern) {
      state.selectedIdsKnown = true;
      state.selected = find(body, 'ul.list-group > li[data-opt-id]').filter(visible).map(node => ({
        value: node.getAttribute('data-opt-id'), label: tidy(node.querySelector('span.flex-fill,span')?.textContent),
        preferenceId: '', fixed: node.getAttribute('data-fixed') === 'true'
      }));
      if (state.selected.some(item => !positive(item.value)) || new Set(state.selected.map(item => item.value)).size !== state.selected.length)
        return { ...state, reason: 'La liste des colonnes présentes n’est pas identifiable sans ambiguïté.' };
    } else {
      const forms = find(body, 'form');
      if (!forms.length || forms.some(form => !validForm(form, usersId))) return { ...state, reason: 'Les formulaires de colonnes ne correspondent pas tous à la même vue personnelle.' };
      state.selected = find(body, 'tr').filter(visible).flatMap(row => {
        const cells = [...row.children].filter(node => node.tagName === 'TD');
        if (cells.length < 2 || cells[0].querySelector('form,input,select,button') || !tidy(cells[0].textContent)) return [];
        const ids = find(row, 'input[name="id"]').map(node => node.value);
        return [{ value: '', label: tidy(cells[0].textContent), preferenceId: ids[0] || '', fixed: ids.length === 0 }];
      });
    }
    const selects = find(body, 'select[name="num"]');
    const additions = controls(modern ? 'add_opt' : 'add');
    const select = only(selects), add = only(additions);
    if (!select && !add && !modern && state.selected.length && controls('disable').length === 1)
      return { ...state, supported: true, proof: 'native-personal-columns-complete', formSelector: selector(body) };
    if (!select || !add || !selectUsable(select) || disabled(add) || select.multiple ||
      !modern && (select.form !== add.form || !validForm(add.form, usersId)))
      return { ...state, reason: 'GLPI ne propose pas une commande personnelle d’ajout de colonne vérifiable.' };
    const options = optionList(select);
    if (new Set(options.map(option => option.value)).size !== options.length)
      return { ...state, reason: 'Plusieurs options de colonne partagent le même identifiant.' };
    return { ...state, supported: true, proof: modern ? 'native-personal-options-ajax' : 'native-personal-options-form',
      formSelector: selector(modern ? body : select.form), selectSelector: selector(select), selectHidden: !visible(select), addSelector: selector(add), selectedValue: select.value, options };
  }

  window.__glpiPilotColumns = { version: VERSION, inspect, inspectPreferences };
})();
