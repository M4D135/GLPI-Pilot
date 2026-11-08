import { el, button, toolbar, field, input, formatNumber, showError } from '../ui.js';
import { pager } from './terrain-common.js';

/** A correction targets explicit ticket identities prepared by the C# runtime. */
export async function editLocationCorrection(p, { revision, rows, onApplied }) {
  const prepared = await p.call('terrain.correction.prepare', { revision, rows });
  if (p.ctx.signal.aborted) return;
  const count = prepared.count, plural = count !== 1;
  const errors = el('div', { class: 'maps-correction-errors', role: 'alert' });
  const search = input('', { type: 'search', maxLength: 120, placeholder: 'Nom, alias ou commune', 'aria-label': 'Rechercher un site' });
  const reason = input('Lieu confirmé manuellement', { maxLength: 1000, 'aria-label': 'Motif de la correction' });
  const picked = el('div', { class: 'maps-correction-picked', role: 'status', 'aria-live': 'polite' });
  const results = el('div', { class: 'maps-correction-results' });
  let choice = null, lookupGeneration = 0;
  const readOnly = prepared.readOnlyReason || '';
  const summary = el('p', { class: 'maps-correction-summary', text: `${formatNumber(count)} ticket${plural ? 's' : ''} ${plural ? 'seront rattachés' : 'sera rattaché'} au site choisi. La commune sera reprise automatiquement.` });
  const preview = el('details', { class: 'maps-correction-tickets' },
    el('summary', { text: plural ? `Vérifier les ${formatNumber(count)} tickets concernés` : 'Vérifier le ticket concerné' }),
    el('div', {}, ...prepared.items.map(item => el('p', {},
      el('strong', { text: `#${item.id}` }), document.createTextNode(` · ${item.title || 'Titre non renseigné'}`)))));
  const body = el('div', { class: 'maps-correction-editor' }, errors, summary,
    readOnly ? el('p', { class: 'notice warning', text: readOnly }) : null,
    preview,
    el('p', { class: 'help', text: 'La correction est mémorisée pour ces tickets. Elle ne modifie pas leur titre ni les règles de détection des autres tickets.' }),
    toolbar(field('Rechercher un site', search), button('Rechercher', () => p.run(() => loadSites(0), errors), { icon: 'search', disabled: !!readOnly })),
    picked, results, field('Motif de la correction', reason));
  const actions = [{ label: 'Annuler', action: close => close() }];
  if (prepared.canRemove && !readOnly) actions.push({ label: plural ? 'Retirer les corrections' : 'Retirer la correction', danger: true,
    action: close => p.run(() => apply(close, true), errors) });
  actions.push({ label: plural ? `Appliquer aux ${formatNumber(count)} tickets` : 'Enregistrer la correction', primary: true,
    disabled: !!readOnly, action: close => p.run(() => apply(close, false), errors) });
  const popup = p.dialog(plural ? 'Corriger les sites des tickets sélectionnés' : 'Corriger le site du ticket', body, actions);
  popup.element.classList.add('maps-correction-dialog');
  popup.element.dataset.correctionCount = String(count);
  const save = popup.element.querySelector('.modal-actions .primary');
  function paintChoice() {
    picked.replaceChildren(el('strong', { text: choice ? choice.label : 'Choisissez un site dans la liste' }),
      ...(choice ? [el('span', { text: choice.detail || 'Commune non renseignée' })] : []));
    if (save) save.disabled = !!readOnly || !choice;
    for (const row of results.querySelectorAll('[data-correction-site]')) {
      const chosen = row.dataset.correctionSite === choice?.value;
      row.classList.toggle('is-selected', chosen); row.setAttribute('aria-pressed', String(chosen));
    }
  }
  async function loadSites(offset) {
    const sequence = ++lookupGeneration;
    const data = await p.call('terrain.correction.sites', { token: prepared.token, search: search.value, offset, limit: 20 });
    if (p.ctx.signal.aborted || !popup.element.isConnected || sequence !== lookupGeneration) return;
    results.replaceChildren(el('div', { class: 'maps-correction-options' }, ...data.items.map(item => {
      const control = button('', () => { choice = item; paintChoice(); }, { attributes: { class: 'button maps-correction-option', dataset: { correctionSite: item.value }, 'aria-pressed': 'false' } });
      control.replaceChildren(el('strong', { text: item.label }), el('small', { text: item.detail || 'Commune non renseignée' }));
      return control;
    })), ...(!data.total ? [el('p', { class: 'help', text: 'Aucun site ne correspond. Essayez un autre nom, un alias ou une commune.' })] : []),
    pager(data, next => p.run(() => loadSites(next), errors)));
    paintChoice();
  }
  async function apply(close, remove) {
    if (!remove && !choice) throw new Error('Choisissez le site à attribuer aux tickets.');
    const motif = reason.value.trim();
    if (!remove && !motif) throw new Error('Renseignez un motif pour conserver le sens de cette correction.');
    // Keep the chosen site and all fields intact if the prepared revision expires.
    const result = await p.call('terrain.correction.apply', { token: prepared.token, siteId: choice?.value || '', reason: motif, remove });
    close();
    try { await onApplied(result); }
    catch (error) {
      if (p.ctx.signal.aborted) return;
      p.message('La correction est enregistrée. Actualisez la carte pour afficher le résultat.');
      showError(p.errors, error);
    }
  }
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); void p.run(() => loadSites(0), errors); } });
  paintChoice();
  if (!readOnly) { await loadSites(0); search.focus(); }
  return popup;
}
