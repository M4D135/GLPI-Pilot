import {el, button, empty, dialog, formatNumber, formatDate, showError} from './ui.js';
import {createTicketResolution} from './library-resolution.js';

/** Work tickets use their own storage key. Resolve the validated ID and URL as a
 * pair, so the same number on another GLPI installation can never stand in. */
export async function openLibraryPrecedents({api, signal}, reference, options = {}) {
  if (signal?.aborted) return;
  // The initiating control may blur while this lookup disables it. Its caller
  // retains the exact control instead of consulting activeElement after await.
  const result = await api.call('library.reference', {id: reference.id, url: reference.url}, {signal});
  if (signal?.aborted) return;
  if (result.found) return openLibraryTicket({api, signal}, result.key, {resolution: true, returnFocus: options.returnFocus});
  const modal = dialog('Ticket absent de la bibliothèque', el('div', {class: 'library-reference-missing'},
    el('p', {}, el('strong', {class: 'library-ticket-id', text: `Ticket ${result.ticket.id}`}), ' · ', result.installation),
    el('p', {text: 'Aucune fiche de ce ticket n’est conservée dans le cache de cette installation GLPI. Recueillez-la dans la Bibliothèque pour rechercher ses précédents.'}),
    el('p', {class: 'help', text: 'Votre fiche de travail et votre saisie restent ouvertes.'})),
    [{label: 'Revenir à la fiche', action: close => close()}], {returnFocus: options.returnFocus});
  const abort = () => modal.close();
  signal?.addEventListener('abort', abort, {once: true});
  modal.element.addEventListener('pilot:closed', () => signal?.removeEventListener('abort', abort), {once: true});
  return modal;
}

/** One read-only dialog keeps the original page and the ticket/precedent views.
 * At most the source view, result cards and one candidate body are retained. */
export async function openLibraryTicket({api, signal}, key, options = {}) {
  const request = new AbortController();
  const call = (operation, args = {}) => api.call(operation, args, {signal: request.signal});
  const body = el('div', {class: 'library-detail', 'aria-busy': 'true'},
    el('p', {role: 'status', text: 'Lecture du contenu conservé sur ce poste…'}));
  let sourceUrl = '', activeKey = key, original = null, currentView = null, resolution = null, resolutionView = null;
  let preview = null, previewGeneration = 0, backAction = null, resultFocus = null;
  const modal = dialog('Ticket conservé', body, [
    {label: 'Retrouver des précédents résolus', action: () => showResolution()},
    {label: 'Copier la référence', action: () => sourceUrl ? call('app.copy', {text: sourceUrl}) : undefined},
    {label: 'Ouvrir dans GLPI', action: () => sourceUrl ? call('library.open', {key: activeKey}) : undefined}
  ], {returnFocus: options.returnFocus});
  modal.element.classList.add('library-ticket-modal');
  const actions = [...modal.element.querySelectorAll('.modal-actions button')];
  actions.forEach(control => { control.disabled = true; });
  const heading = modal.element.querySelector('.modal-heading h2'), scroller = modal.element.querySelector('.modal-body');
  const backButton = button('Retour à la fiche', () => backAction?.(), {icon: 'arrow'});
  backButton.hidden = true; heading.after(backButton);
  const abort = () => modal.close();
  signal?.addEventListener('abort', abort, {once: true});
  modal.element.addEventListener('pilot:closed', () => {
    request.abort(); preview?.abort(); resolution?.dispose(); signal?.removeEventListener('abort', abort);
  }, {once: true});
  if (signal?.aborted) { modal.close(); return modal; }
  function present(view, back = null, label = '') {
    if (currentView) currentView.scroll = scroller.scrollTop;
    currentView = view; sourceUrl = view.sourceUrl || ''; activeKey = view.key || key;
    body.replaceChildren(view.element); body.dataset.readerView = view.kind;
    modal.element.setAttribute('aria-label', view.title); heading.replaceChildren(...view.heading());
    backAction = back; backButton.hidden = !back;
    if (label) backButton.querySelector('span').textContent = label;
    actions[0].hidden = view.kind !== 'original'; actions[0].disabled = !original;
    actions[1].disabled = actions[2].disabled = !sourceUrl;
    scroller.scrollTop = view.scroll || 0;
  }
  function showOriginal() {
    previewGeneration++; preview?.abort();
    present(original); actions[0].focus({preventScroll: true});
  }
  async function showResolution() {
    if (!original || request.signal.aborted) return;
    previewGeneration++; preview?.abort();
    if (!resolution) {
      resolution = createTicketResolution({api, signal: request.signal, key, onRead: readPrecedent});
      resolutionView = {kind: 'resolution', key, sourceUrl: original.sourceUrl, element: resolution.element, scroll: 0,
        title: `Précédents résolus · ticket ${original.id}`,
        heading: () => ['Précédents résolus · ', el('span', {class: 'library-ticket-id', text: `ticket ${original.id}`})]};
    }
    present(resolutionView, showOriginal, 'Retour à la fiche');
    if (resultFocus?.isConnected) resultFocus.focus({preventScroll: true});
    else resolution.element.querySelector('input')?.focus({preventScroll: true});
    await resolution.load();
  }
  async function readPrecedent(nextKey) {
    resultFocus = document.activeElement;
    const version = ++previewGeneration; preview?.abort(); preview = new AbortController();
    const current = preview;
    const loading = el('div', {}, el('p', {role: 'status', text: 'Lecture du précédent conservé…'}));
    present({kind: 'preview', key: nextKey, title: 'Précédent conservé', heading: () => ['Précédent conservé'], element: loading}, showResolution, 'Retour aux précédents');
    try {
      const document = await api.call('library.ticket', {key: nextKey}, {signal: current.signal});
      if (request.signal.aborted || current.signal.aborted || version !== previewGeneration) return;
      present(ticketView(document, 'preview'), showResolution, 'Retour aux précédents');
      backButton.focus({preventScroll: true});
    } catch (error) {
      if (!request.signal.aborted && !current.signal.aborted && version === previewGeneration) { loading.replaceChildren(); showError(loading, error); }
    }
  }
  try {
    const document = await call('library.ticket', {key});
    if (request.signal.aborted || !modal.element.isConnected) return modal;
    original = ticketView(document, 'original'); present(original);
  } catch (error) {
    if (!request.signal.aborted) { body.replaceChildren(); showError(body, error); }
  } finally { body.removeAttribute('aria-busy'); }
  if (original && options.resolution && !request.signal.aborted) await showResolution();
  return modal;
}

function ticketView(document, kind) {
  const seed = document.seed || document, detail = document.detail;
  const sourceUrl = seed.ticket?.url || detail?.ticket?.url || '', id = seed.ticket?.id || detail?.ticket?.id || '';
  const label = seed.title || detail?.title || 'Contenu conservé', title = `Ticket ${id} · ${label}`;
  const body = el('div', {class: 'library-ticket-content'}, el('dl', {class: 'library-detail-summary'}, ...[
    ['Installation', document.installation], ['État', seed.status || 'Non recueilli'],
    ['Dernière modification GLPI', seed.lastModifiedRaw || formatDate(seed.lastModifiedAt)],
    ['Lecture du contenu', formatDate(detail?.readAt)], ['Source', sourceUrl],
    ['Fraîcheur', document.detailsCurrent && document.detailComplete ? 'Contenu confirmé dans le cache' : 'Contenu partiel ou à recontrôler']
  ].map(([name, value]) => el('div', {}, el('dt', {text: name}), el('dd', {text: value || '—'})))));
  if (document.conflicts) body.append(el('p', {class: 'library-conflict', text: 'Plusieurs versions de cette fiche sont conservées. Leur coexistence reste signalée tant que le conflit n’est pas résolu.' }));
  for (const warning of [...(detail?.warnings || []), ...(document.coverage?.limitations || [])]) body.append(el('p', {class: 'help', text: warning}));
  if (!detail?.sections?.length) body.append(empty('Aucun contenu recueilli', 'La prochaine mise à jour du cache tentera de lire cette fiche et ses onglets de consultation.'));
  for (const section of detail?.sections || []) {
    const data = section.data || {}, rows = data.rows || [], rowsHost = el('div');
    let page = 0, rendered = false;
    const drawRows = () => {
      rendered = true;
      rowsHost.replaceChildren(...rows.slice(page * 50, (page + 1) * 50).map(row => el('div', {class: 'library-detail-row'}, ...row.map((value, index) => el('div', {},
        data.headers?.[index] ? el('span', {class: 'help', text: data.headers[index]}) : null,
        el('pre', {class: 'library-detail-text', text: value ?? ''}))))));
      if (rows.length > 50) rowsHost.append(el('div', {class: 'library-pagination'},
        button('Lignes précédentes', () => { page--; drawRows(); }, {disabled: page === 0}),
        el('span', {text: `${page * 50 + 1}–${Math.min(rows.length, (page + 1) * 50)} sur ${formatNumber(rows.length)}`}),
        button('Lignes suivantes', () => { page++; drawRows(); }, {disabled: (page + 1) * 50 >= rows.length})));
    };
    const disclosure = el('details', {class: 'library-detail-section', open: detail.sections.length === 1},
      el('summary', {text: `${section.name || section.tabName || 'Rubrique recueillie'} · ${formatNumber(rows.length)} ligne(s)`}), rowsHost);
    if (data.complete === false) disclosure.append(el('p', {class: 'help', text: 'Exhaustivité de cette rubrique non confirmée.'}));
    disclosure.addEventListener('toggle', () => { if (disclosure.open && !rendered) drawRows(); });
    if (disclosure.open) drawRows();
    body.append(disclosure);
  }
  return {kind, key: document.key, sourceUrl, id, title, element: body, scroll: 0,
    heading: () => [el('span', {class: 'library-ticket-id', text: `Ticket ${id}`}), ' · ', el('span', {text: label})]};
}
