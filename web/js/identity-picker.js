import { el, button, icon, toolbar, input, field, dialog, empty, formatNumber } from './ui.js';

// The picker reads proposed identities; only an explicit choice writes the local profile.
export function openIdentityPicker({ api, state, signal }) {
  return new Promise(resolve => {
    let model = null, busy = false, reading = false, closed = false, serial = 0, timer = null, readController = null;
    const search = input('', { type: 'search', placeholder: 'Nom, prénom ou identifiant du technicien',
      maxLength: 300, 'aria-label': 'Rechercher une identité', autocomplete: 'off' });
    const current = el('div', { class: 'identity-current', hidden: true });
    const message = el('p', { class: 'help identity-source-message', hidden: true });
    const resultStatus = el('p', { class: 'identity-result-status', role: 'status', 'aria-live': 'polite' });
    const results = el('div', { class: 'identity-results', 'aria-label': 'Identités proposées', 'aria-busy': 'true' });
    const pagination = toolbar();
    const error = el('p', { class: 'notice danger', role: 'alert', hidden: true });
    const name = input('', { maxLength: 200, required: true, autocomplete: 'off',
      placeholder: 'Ex. : Marie Dupont ou m.dupont' });
    const manual = el('form', { class: 'identity-manual', hidden: true, onSubmit: event => {
      event.preventDefault(); void create();
    } }, el('h3', { text: 'Saisie manuelle' }), field('Nom tel qu’il apparaît dans les tickets', name),
    el('p', { class: 'help', text: 'Nom ou identifiant utilisé dans les affectations de tickets.' }),
    toolbar(button('Enregistrer l’identité', () => create(), { primary: true, icon: 'check' }),
      button('Revenir à la liste', () => { manual.hidden = true; add.hidden = false; search.focus(); })));
    const add = button('Ajouter une identité', () => {
      manual.hidden = false; add.hidden = true; name.value = search.value.trim(); name.focus();
    }, { icon: 'plus' });
    const body = el('div', { class: 'identity-picker' },
      el('p', { class: 'identity-intro', text: 'Identité utilisée pour filtrer les tickets et les interventions.' }),
      state.session?.testMode || state.session?.demo ? el('p', { class: 'notice info', text: 'Simulation : identité limitée à cet espace de test.' }) : null,
      current, field('Rechercher une identité', search), message, resultStatus, results, pagination, error, add, manual,
      el('p', { class: 'help identity-local-note', text: 'Identité enregistrée dans le profil. Liste issue des tickets chargés et des participants du planning.' }));
    const modal = dialog('Identité', body, [{ label: 'Fermer', action: close => { if (!busy) close(); } }]);
    modal.element.classList.add('identity-dialog');
    modal.element.addEventListener('cancel', event => {
      if (busy) { event.preventDefault(); event.stopImmediatePropagation(); }
    }, true);

    function showError(value) { error.textContent = value || ''; error.hidden = !value; }
    function lock(value) {
      busy = value; modal.element.setAttribute('aria-busy', String(value));
      for (const control of modal.element.querySelectorAll('button,input')) control.disabled = value;
      if (!value && model) paint();
    }
    function paint() {
      if (closed || !model) return;
      current.hidden = !model.current;
      current.replaceChildren(...(model.current ? [el('span', { class: 'identity-avatar', 'aria-hidden': 'true' }, icon('users')),
        el('div', { class: 'identity-current-copy' }, el('small', { text: 'Identité actuelle' }), el('strong', { text: model.current.label })),
        button('Retirer l’identité', () => commit('identity.clear', { revision: model.revision }), { icon: 'close' })] : []));
      message.textContent = model.message || ''; message.hidden = !message.textContent || !!model.people?.length;
      const offset = model.offset || 0, people = model.people || [];
      resultStatus.textContent = model.total ? `${formatNumber(offset + 1)}–${formatNumber(offset + people.length)} sur ${formatNumber(model.total)} personne${model.total > 1 ? 's' : ''}`
        : search.value.trim() ? 'Aucun résultat pour cette recherche.' : 'Aucune identité proposée pour le moment.';
      const nameKey=value=>String(value||'').normalize('NFC').replace(/\s+/g,' ').trim().toUpperCase();
      results.replaceChildren(...people.map(person => {
        const selected = person.key === model.current?.key;
        const origin = person.fromSource ? `${formatNumber(person.ticketCount)} ticket${person.ticketCount === 1 ? '' : 's'} dans le relevé`
          : person.fromCalendar ? 'Participant du planning · absent de ce relevé' : 'Identité mémorisée · absente de ce relevé';
        const choose = button(selected ? 'Identité actuelle' : 'Sélectionner', () => commit('identity.set', { key: person.key, revision: model.revision }),
          { primary: !selected, icon: selected ? 'check' : undefined, disabled: selected || busy || reading,
            attributes: { 'aria-label': selected ? `Identité actuelle : ${person.label}` : `Sélectionner : ${person.label}` } });
        return el('article', { class: `identity-person${selected ? ' selected' : ''}`, dataset: { identityKey: person.key } },
          el('span', { class: 'identity-avatar', 'aria-hidden': 'true', text: person.label.split(/[\s._-]+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase() }),
          el('div', { class: 'identity-person-copy' }, el('strong', { text: person.label }), el('small', { text: origin }),
            person.key!==nameKey(person.label)||people.some(other => other.key !== person.key && nameKey(other.label) === nameKey(person.label)) ? el('small', { class: 'identity-key', text: 'Repère : '+person.key }) : null), choose);
      }));
      if (!people.length) results.append(empty(search.value.trim() ? 'Aucune identité correspondante' : 'Rechercher une identité',
        'Saisie manuelle disponible ci-dessous. Un relevé peut aussi fournir les identités attribuées aux tickets.'));
      pagination.replaceChildren(...(model.total > model.limit ? [
        button('Précédent', () => load(Math.max(0, offset - model.limit)), { disabled: !offset || busy || reading }),
        button('Suivant', () => load(offset + model.limit), { disabled: offset + people.length >= model.total || busy || reading })] : []));
      if (!model.total && !search.value.trim() && !model.current) { manual.hidden = false; add.hidden = true; }
    }
    async function load(offset = 0) {
      const generation = ++serial; readController?.abort(); readController = new AbortController();
      reading = true; results.setAttribute('aria-busy', 'true'); resultStatus.textContent = 'Recherche des personnes…';
      for (const control of [...results.querySelectorAll('button'), ...pagination.querySelectorAll('button')]) control.disabled = true;
      try {
        const value = await api.call('identity.get', { search: search.value.trim(), offset, limit: 30 }, { signal: readController.signal });
        if (closed || generation !== serial) return;
        model = value; reading = false; paint();
      } catch (reason) {
        if (closed || generation !== serial || reason.name === 'AbortError') return;
        reading = false; resultStatus.textContent = 'La liste n’a pas pu être chargée.'; showError(reason.message);
        results.replaceChildren(button('Réessayer', () => { showError(''); return load(offset); }, { icon: 'refresh' }));
      } finally { if (!closed && generation === serial) results.setAttribute('aria-busy', 'false'); }
    }
    async function commit(operation, args) {
      if (closed || busy || reading || !model) return;
      showError(''); lock(true);
      try {
        const result = await api.call(operation, args, { signal });
        modal.close(result);
      } catch (reason) {
        if (reason.name === 'AbortError') { modal.close(); return; }
        showError(reason.message); lock(false);
        // A revision can change while the picker is open. Reload proposals without selecting one.
        await load(0);
      } finally { if (!closed && busy) lock(false); }
    }
    async function create() {
      if (!manual.reportValidity() || !name.value.trim()) return;
      return commit('identity.create', { label: name.value.trim(), revision: model?.revision });
    }
    search.addEventListener('input', () => {
      clearTimeout(timer); ++serial; readController?.abort(); reading = true; results.setAttribute('aria-busy', 'true');
      for (const control of results.querySelectorAll('button')) control.disabled = true;
      timer = setTimeout(() => { showError(''); void load(0); }, 160);
    });
    const abort = () => { if (!busy) modal.close(); };
    signal?.addEventListener('abort', abort, { once: true });
    modal.element.addEventListener('pilot:closed', event => {
      closed = true; clearTimeout(timer); readController?.abort(); signal?.removeEventListener('abort', abort); resolve(event.detail || null);
    }, { once: true });
    void load(); search.focus();
  });
}
