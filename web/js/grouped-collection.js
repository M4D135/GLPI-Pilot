import { el, button, toolbar, empty, badge, table, dialog, isSimulation } from './ui.js';

// A grouped collection replays saved searches; it never merges their tickets.
// The snapshot revision protects both the saved choice and its explicit launch.
export async function openGroupedCollection(ctx, options = {}) {
  await options.beforeOpen?.();
  const snapshot = await ctx.api.call('app.bootstrap', {}, { signal: ctx.signal });
  if (ctx.signal?.aborted) return;
  let settings = structuredClone(snapshot.settings), revision = snapshot.session.revision;
  const searches = settings.sources || [], simulation = isSimulation(snapshot.session);
  const body = el('div', { class: 'grouped-collection', 'data-grouped-collection': 'prepare' },
    el('p', { text: 'Choisissez les recherches GLPI mémorisées à collecter, dans l’ordre de la liste.' }),
    el('p', { class: 'help', text: 'Chaque recherche produit son propre relevé. Les tickets ne sont pas fusionnés dans un tableau commun ; le relevé affiché peut changer pendant la collecte. Les choix sont mémorisés pour la prochaine collecte groupée.' }),
    el('p', { class: 'help', text: 'Cette sélection est aussi utilisée par les collectes groupées programmées, si elles sont activées.' }));
  if (simulation) body.prepend(el('p', { class: 'notice info', text: snapshot.session.demo
    ? 'Démonstration : ces collectes utilisent seulement les données fictives de la simulation. Aucun portail GLPI réel ne sera contacté.'
    : 'Simulation locale : ces collectes réutilisent les données du fichier chargé. Aucun portail GLPI réel ne sera contacté.' }));
  let modal;
  const manage = async close => {
    close();
    try { if (options.manageSources) await options.manageSources(); else await ctx.navigate('sources'); }
    catch (error) { ctx.notify(error.message || 'Les recherches n’ont pas pu être ouvertes.', 'danger'); }
  };
  if (!searches.length) {
    body.append(empty('Aucune recherche mémorisée', 'Renseignez l’adresse GLPI, puis ouvrez Mes recherches : donnez un nom et choisissez « Créer depuis la configuration actuelle ». Revenez ensuite ici pour choisir les recherches à collecter.'),
      el('p', { class: 'help', text: 'Pour collecter uniquement l’adresse du formulaire, utilisez « Lancer la collecte » dans Données.' }));
    modal = dialog('Collecter plusieurs recherches', body, [{ label: 'Ouvrir Mes recherches', primary: true, action: manage }]);
    const onAbort = () => modal.close();
    ctx.signal?.addEventListener('abort', onAbort, { once: true });
    modal.element.addEventListener('pilot:closed', () => ctx.signal?.removeEventListener('abort', onAbort), { once: true });
    return modal;
  }

  const choices = new Map(), selection = el('div', { class: 'grouped-collection-searches', role: 'group', 'aria-label': 'Recherches à collecter' });
  const count = el('p', { class: 'help', role: 'status', 'aria-live': 'polite', 'data-grouped-count': 'true' });
  const status = el('p', { class: 'help', role: 'status', 'aria-live': 'polite' });
  const progress = el('div', { class: 'grouped-collection-progress', hidden: true });
  let busy = false, running = false, completed = false, launched = false, selectionSaved = false, runController = null;
  const selectedCount = () => [...choices.values()].filter(control => control.checked).length;
  const updateCount = () => {
    const value = selectedCount(); count.textContent = `${value} recherche${value > 1 ? 's' : ''} sélectionnée${value > 1 ? 's' : ''} sur ${searches.length}.`;
    launch.disabled = busy || value === 0;
  };
  for (const search of searches) {
    const control = el('input', { type: 'checkbox', checked: search.includedInCampaign === true, 'data-grouped-source': search.id });
    control.addEventListener('change', updateCount); choices.set(search.id, control);
    selection.append(el('label', { class: 'grouped-collection-search' }, control,
      el('span', {}, el('strong', { text: search.name }), el('small', { text: search.url }),
        search.id === settings.activeSourceId ? badge('Recherche active', 'info') : null)));
  }
  const all = button('Tout sélectionner', () => { for (const control of choices.values()) control.checked = true; updateCount(); });
  const none = button('Tout désélectionner', () => { for (const control of choices.values()) control.checked = false; updateCount(); });
  const preparation = el('div', {}, toolbar(all, none), selection, count);
  const setBusy = value => {
    busy = value; preparation.inert = value; save.disabled = value; manageButton.disabled = value;
    for (const control of choices.values()) control.disabled = value;
    modal.element.querySelector(':scope > .modal-heading button').disabled = value;
    cancel.hidden = !running; cancel.disabled = !running; updateCount();
  };
  const saveChoice = async () => {
    const ids = [...choices].filter(([, control]) => control.checked).map(([id]) => id);
    const saved = await ctx.api.call('collect.selection', { revision, ids }, { signal: ctx.signal });
    settings = structuredClone(saved.settings); revision = saved.revision;
    selectionSaved = true;
    options.onSaved?.(saved); status.textContent = 'Sélection enregistrée.';
    return saved;
  };
  const save = button('Enregistrer la sélection', async () => {
    setBusy(true);
    try { await saveChoice(); ctx.notify('Choix des recherches enregistré.'); }
    finally { setBusy(false); }
  });
  const launch = button(simulation ? 'Collecter les recherches simulées' : 'Collecter les recherches sélectionnées', async () => {
    if (selectedCount() === 0) return;
    setBusy(true);
    try {
      await saveChoice();
      runController = new AbortController(); running = true; launched = true; setBusy(true);
      status.textContent = simulation ? 'Collectes simulées en cours…' : 'Collectes GLPI en cours…';
      progress.hidden = false;
      const result = await ctx.api.call('collect.campaign', { revision }, { signal: AbortSignal.any([ctx.signal, runController.signal].filter(Boolean)) });
      const after = await ctx.api.call('app.bootstrap', {}, { signal: ctx.signal });
      completed = true; body.dataset.groupedCollection = 'complete'; preparation.hidden = true; manageButton.hidden = true;
      progress.replaceChildren(table(result.sources, [{ key: 'name', label: 'Recherche' }, { key: 'state', label: 'État' }, { key: 'rows', label: 'Lignes' }, { key: 'detail', label: 'Résultat' }]));
      const received = result.sources.filter(source => ['Terminé', 'À vérifier'].includes(source.state)).length;
      const review = result.sources.filter(source => source.state === 'À vérifier').length;
      const failed = result.sources.filter(source => source.state === 'Échec').length;
      status.textContent = `${received ? `${received} relevé(s) reçu(s), dont ${review} à vérifier.` : 'Aucun nouveau relevé reçu.'} ${failed} recherche(s) en échec. ${after.session.loaded ? 'Le relevé actuellement chargé reste consultable.' : 'Aucun relevé courant n’est chargé ; revenez aux données pour choisir une recherche ou une copie hors ligne.'}`;
      modal.element.querySelector(':scope > .modal-heading h2').textContent = 'Bilan de la collecte groupée';
      modal.element.setAttribute('aria-label', 'Bilan de la collecte groupée');
      actions.replaceChildren(button('Fermer', closeAndRefresh), button(after.session.loaded ? 'Voir le relevé courant' : 'Revenir aux données', async () => { modal.close(); await ctx.navigate(after.session.loaded ? 'pilot' : 'data'); }, { primary: true, icon: after.session.loaded ? 'chart' : 'folder' }));
    } catch (error) {
      if (error.name === 'AbortError' || error.code === 'CANCELLED') {
        if (!ctx.signal?.aborted) {
          // Wait for the engine's cancellation to settle. Its partially applied
          // profiles/results may have changed the revision: a new attempt must
          // open a fresh preparation instead of reusing these displayed choices.
          await ctx.api.call('app.bootstrap', {}, { signal: ctx.signal });
          completed = true; body.dataset.groupedCollection = 'stopped'; preparation.hidden = true; manageButton.hidden = true;
          status.textContent = 'Collecte groupée arrêtée. Consultez le bilan des recherches déjà traitées, puis fermez ce panneau pour préparer une nouvelle collecte.';
          modal.element.querySelector(':scope > .modal-heading h2').textContent = 'Collecte groupée arrêtée';
          modal.element.setAttribute('aria-label', 'Collecte groupée arrêtée');
          actions.replaceChildren(button('Fermer et actualiser', closeAndRefresh, { primary: true }));
        }
      }
      else { status.textContent = 'La collecte groupée n’a pas pu se terminer. Fermez ce panneau et rouvrez-le pour actualiser les recherches.'; throw error; }
    } finally { running = false; runController = null; setBusy(false); }
  }, { primary: true, icon: 'download' });
  const cancel = button('Annuler la collecte', () => { runController?.abort(); status.textContent = 'Arrêt de la collecte demandé…'; }, { icon: 'close' }); cancel.hidden = true;
  const manageButton = button('Mes recherches', () => manage(modal.close), { icon: 'layers' });
  const actions = toolbar(save, launch, cancel);
  body.append(preparation, status, progress, toolbar(manageButton), actions);
  modal = dialog('Collecter plusieurs recherches', body);
  modal.element.classList.add('grouped-collection-modal');
  async function closeAndRefresh() {
    if (busy) return; modal.close();
    if (ctx.signal?.aborted) return;
    if (launched && options.onCollected) await options.onCollected();
    else if (launched || selectionSaved) await ctx.refresh();
  }
  const requestClose = event => {
    event.preventDefault(); event.stopImmediatePropagation();
    if (!busy) void closeAndRefresh().catch(error => ctx.notify(error.message, 'danger'));
  };
  modal.element.querySelector(':scope > .modal-heading button').addEventListener('click', requestClose, { capture: true });
  modal.element.addEventListener('cancel', requestClose, { capture: true });
  const stopProgress = ctx.api.on('campaign.progress', rows => {
    if (!running || completed) return;
    progress.replaceChildren(table(rows, [{ key: 'name', label: 'Recherche' }, { key: 'state', label: 'État' }, { key: 'rows', label: 'Lignes' }]));
  });
  const onAbort = () => { runController?.abort(); modal.close(); };
  ctx.signal?.addEventListener('abort', onAbort, { once: true });
  modal.element.addEventListener('pilot:closed', () => { stopProgress(); ctx.signal?.removeEventListener('abort', onAbort); runController?.abort(); }, { once: true });
  updateCount();
  return modal;
}
