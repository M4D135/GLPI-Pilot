import {el, button, icon, input, select, field, dialog, formatDate} from './ui.js';
import {openLibraryTicket} from './library-ticket.js';

const fold = value => String(value || '').normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('fr').trim();
const score = (command, query) => {
  if (!query) return 1;
  const label = fold(command.label), text = `${label} ${fold(command.keywords)}`;
  const words = query.split(/\s+/);
  if (!words.every(word => text.includes(word))) return 0;
  return label === query ? 4 : label.startsWith(query) ? 3 : label.includes(query) ? 2 : 1;
};
let instanceSequence = 0;

/** One local palette for pages, explicit actions and source-qualified cache hits.
 * It never searches GLPI or starts a collection while the user types. */
export function createCommandPalette({api, getCommands, navigate, getPageSignal, notify}) {
  let active = null;
  function open() {
    if (active) { active.search.focus(); active.search.select(); return true; }
    // A modal editor/confirmation owns focus until it is explicitly closed.
    if (document.querySelector('dialog[open]')) return false;
    const id = `command-palette-${++instanceSequence}`;
    const lifetime = new AbortController();
    const search = input('', {role: 'combobox', 'aria-label': 'Rechercher une commande ou un ticket',
      'aria-expanded': 'true', 'aria-controls': `${id}-results`, 'aria-autocomplete': 'list',
      autocomplete: 'off', spellcheck: false, maxLength: 1000, placeholder: 'Numéro de ticket, titre, écran ou action…'});
    const results = el('div', {class: 'command-palette-results', id: `${id}-results`, role: 'listbox', 'aria-label': 'Résultats de la recherche rapide'});
    const status = el('p', {class: 'command-palette-status', role: 'status', 'aria-live': 'polite'});
    const sources = select([{value: '', label: 'Toutes les installations'}]);
    const browse = button('Ouvrir la bibliothèque filtrée', () => {
      const text = search.value.trim(), installation = sources.value;
      close(); return navigate('library', {text, installation});
    }, {icon: 'folder'});
    const body = el('div', {class: 'command-palette-body'},
      el('div', {class: 'command-palette-input'}, icon('search'), search),
      el('div', {class: 'command-palette-scope'}, field('Installation des tickets conservés', sources),
        el('p', {class: 'help', text: 'Recherche locale · la consultation conserve votre écran et votre relevé.'})),
      results, status,
      el('div', {class: 'command-palette-footer'},
        el('span', {text: '↑ ↓ choisir · Entrée ouvrir · Échap fermer'}), browse));
    const modal = dialog('Rechercher un écran, une action ou un ticket', body);
    modal.element.classList.add('command-palette');
    const current = {modal, search}; active = current;
    let items = [], selected = 0, generation = 0, timer, queryRequest = null, rows = [], total = 0;
    let ticketState = 'idle', ticketError = '', choosing = false, lastPointer = '';
    const rank = (commands, query, limit) => commands.map((command, index) => ({command, index, rank: score(command, query)}))
      .filter(item => item.rank > 0).sort((a, b) => b.rank - a.rank || a.index - b.index)
      .slice(0, query ? limit : undefined).map(item => item.command);
    const cacheQuery = () => search.value.trim();
    const canSearch = text => text.length >= 2 || /^[0-9]$/.test(text);
    function syncSelection(scroll = true) {
      const options = [...results.querySelectorAll('[role=option]')];
      options.forEach((option, index) => option.setAttribute('aria-selected', String(index === selected)));
      const option = options[selected];
      if (option) {
        search.setAttribute('aria-activedescendant', option.id);
        if (scroll) option.scrollIntoView({block: 'nearest', inline: 'nearest', behavior: 'instant'});
      } else search.removeAttribute('aria-activedescendant');
    }
    function paint(reset = false) {
      if (active !== current) return;
      const previous = reset ? '' : items[selected]?.id, query = fold(search.value);
      const commands = getCommands();
      const groups = [
        {label: 'Écrans', values: rank(commands.pages || [], query, 8)},
        {label: 'Actions', values: rank(commands.actions || [], query, 6)},
        {label: 'Tickets conservés', values: rows.map(row => ({
          id: `ticket:${row.key}`, kind: 'ticket', key: row.key, icon: 'ticket',
          label: `#${row.ticket.id} · ${row.title || 'Ticket sans titre recueilli'}`,
          hint: `${row.installation} · ${row.status || 'État non recueilli'}`,
          detail: `${row.detailsCurrent && row.detailComplete ? 'Contenu complet observé' : row.readAt ? 'Contenu à recontrôler' : 'Contenu non lu'} · Lecture : ${formatDate(row.readAt)}${row.conflicts ? ' · Versions concurrentes' : ''}`,
          run: () => openLibraryTicket({api, signal: getPageSignal?.()}, row.key)
        }))}
      ].filter(group => group.values.length);
      items = groups.flatMap(group => group.values);
      const oldIndex = previous ? items.findIndex(item => item.id === previous) : -1;
      selected = oldIndex >= 0 ? oldIndex : 0;
      let index = 0;
      results.replaceChildren(...groups.map((group, groupIndex) => el('div', {class: 'command-palette-group', role: 'group', 'aria-labelledby': `${id}-group-${groupIndex}`},
        el('div', {class: 'command-palette-group-title', role: 'presentation', id: `${id}-group-${groupIndex}`, text: group.label}),
        ...group.values.map(command => {
          const number = index++;
          return el('div', {class: 'command-palette-option', role: 'option', id: `${id}-option-${number}`,
            'aria-selected': String(number === selected), 'aria-disabled': command.disabled ? 'true' : undefined,
            dataset: {commandIndex: number, commandId: command.id, ...(command.key ? {ticketKey: command.key} : {})},
            onClick: () => { void choose(number); }}, icon(command.icon || 'arrow'),
            el('span', {class: 'command-palette-option-copy'}, command.kind === 'ticket'
              ? el('strong', {}, el('span', {class: 'command-palette-ticket-id', text: command.label.split(' · ')[0]}), ' · ', el('span', {text: command.label.split(' · ').slice(1).join(' · ')}))
              : el('strong', {text: command.label}),
              command.hint ? el('span', {class: 'command-palette-hint', text: command.hint}) : null,
              command.detail ? el('span', {class: 'command-palette-detail', text: command.detail}) : null),
            command.active ? el('span', {class: 'command-palette-current', text: 'Écran actuel'}) : null);
        }))));
      if (!items.length && ticketState !== 'loading') results.append(el('p', {class: 'command-palette-empty', text: 'Aucun écran, action ou ticket ne correspond.'}));
      const note = ticketState === 'loading' ? 'Recherche dans les tickets conservés…'
        : ticketState === 'error' ? `Recherche locale indisponible : ${ticketError}`
        : ticketState === 'ready' ? `${total} ticket(s) conservé(s) trouvé(s)${total > rows.length ? ` · ${rows.length} affichés, ouvrez la bibliothèque pour la suite` : ''}.`
        : 'Saisissez un numéro ou au moins deux caractères pour chercher dans les tickets conservés.';
      status.textContent = note;
      results.setAttribute('aria-busy', String(ticketState === 'loading'));
      syncSelection(false);
    }
    async function choose(index = selected) {
      const command = items[index];
      if (!command || command.disabled || choosing || active !== current) return;
      choosing = true; close();
      try { await command.run(); }
      catch (error) { if (error?.name !== 'AbortError' && error?.code !== 'CANCELLED') notify(error?.message || String(error), 'danger'); }
    }
    function schedule() {
      clearTimeout(timer); queryRequest?.abort(); generation++;
      rows = []; total = 0; ticketError = '';
      const text = cacheQuery();
      ticketState = canSearch(text) ? 'loading' : 'idle'; paint(true);
      if (!canSearch(text)) return;
      const version = generation, installation = sources.value;
      timer = setTimeout(async () => {
        queryRequest = new AbortController(); const request = queryRequest;
        try {
          const response = await api.call('library.search', {text, installation, limit: 8, offset: 0}, {signal: request.signal});
          if (active !== current || version !== generation || request.signal.aborted) return;
          rows = (response.rows || []).slice(0, 8); total = response.total; ticketState = 'ready'; paint();
        } catch (error) {
          if (active !== current || version !== generation || request.signal.aborted) return;
          ticketState = 'error'; ticketError = error?.message || String(error); paint();
        }
      }, 180);
    }
    modal.element.addEventListener('pilot:closed', () => {
      clearTimeout(timer); generation++; queryRequest?.abort(); lifetime.abort();
      if (active === current) active = null;
    }, {once: true});
    search.addEventListener('input', schedule); sources.addEventListener('change', schedule);
    modal.element.addEventListener('keydown', event => {
      if (event.target !== search && !event.target.closest('[role=listbox]')) return;
      if (event.isComposing) return;
      if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
        event.preventDefault(); event.stopPropagation();
        if (items.length) selected = (selected + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
        syncSelection(); search.focus({preventScroll: true});
      } else if (event.key === 'Enter') { event.preventDefault(); event.stopPropagation(); void choose(); }
      else if ((event.ctrlKey || event.metaKey) && ['Home', 'End'].includes(event.key)) {
        event.preventDefault(); selected = event.key === 'Home' ? 0 : Math.max(0, items.length - 1); syncSelection();
      }
    });
    results.addEventListener('pointermove', event => {
      const position = `${event.clientX},${event.clientY}`;
      if (position === lastPointer) return; lastPointer = position;
      const option = event.target.closest('[data-command-index]');
      if (option) { selected = Number(option.dataset.commandIndex); syncSelection(false); }
    });
    void api.call('library.get', {}, {signal: lifetime.signal}).then(model => {
      if (active !== current) return;
      sources.replaceChildren(el('option', {value: '', text: 'Toutes les installations'}),
        ...(model.sources || []).map(source => el('option', {value: source.installation, text: source.label || source.installation})));
    }).catch(error => {
      if (active === current && !lifetime.signal.aborted) sources.title = 'La liste des installations est indisponible : ' + (error?.message || error);
    });
    paint(true); search.focus(); return true;
  }
  function close() { active?.modal.close(); }
  return {open, close, isOpen: () => !!active, toggle: () => active ? close() : open()};
}
