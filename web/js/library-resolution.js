import {el, button, icon, input, field, empty, badge, formatDate, formatNumber, showError, replaceChildren} from './ui.js';

export function createTicketResolution({api, signal, key, onRead}) {
  const search = input('', {type: 'search', maxLength: 300, placeholder: 'Symptôme, équipement, code d’erreur…'});
  const feedback = el('div', {class: 'library-resolution-feedback', role: 'status'});
  const results = el('div', {class: 'library-resolution-results', 'aria-label': 'Précédents résolus du ticket'});
  let request = null, generation = 0, disposed = false, loaded = false;
  const form = el('form', {class: 'library-resolution-search', onSubmit: event => { event.preventDefault(); void load(search.value); }},
    field('Affiner la recherche dans les précédents', search),
    el('div', {class: 'library-resolution-search-actions'}, button('Rechercher', () => load(search.value), {icon: 'search', primary: true}),
      button('Reprendre le titre conservé', () => load(), {icon: 'refresh'})));
  const element = el('section', {class: 'library-resolution'},
    el('div', {class: 'library-resolution-intro'}, icon('spark'), el('div', {},
      el('h3', {text: 'Des précédents, avec leurs sources'}),
      el('p', {text: 'Tickets résolus ou clos, complets et confirmés dans votre cache. La recherche reste limitée à la même installation GLPI.'}))),
    form, feedback, results);
  const abort = () => { disposed = true; generation++; request?.abort(); };
  signal?.addEventListener('abort', abort, {once: true});
  function card(item) {
    const reasons = [];
    if (item.titleTerms?.length) reasons.push(`Dans le titre : ${item.titleTerms.join(', ')}`);
    if (item.contentTerms?.length) reasons.push(`Dans les extraits : ${item.contentTerms.join(', ')}`);
    if (item.sameCategory) reasons.push(`Catégorie commune : ${item.category}`);
    return el('article', {class: 'library-resolution-card', dataset: {resolutionKey: item.key}},
      el('div', {class: 'library-resolution-card-heading'},
        el('h3', {}, el('span', {class: 'library-resolution-ticket-id', text: `#${item.ticket.id}`}), ' · ', el('span', {text: item.title || 'Titre non recueilli'})),
        badge(item.status || 'Résolu ou clos', 'success')),
      el('ul', {class: 'library-resolution-reasons'}, ...reasons.map(reason => el('li', {text: reason}))),
      el('p', {class: 'library-resolution-observation', text: `Contenu observé le ${formatDate(item.observedAt)}`} ),
      ...(item.citations || []).map((citation, index) => el('details', {class: 'library-resolution-evidence', open: index === 0 && citation.isSolution},
        el('summary', {text: `${citation.isSolution ? 'Solution recueillie' : 'Extrait recueilli'} · ${citation.section}`}),
        el('blockquote', {text: citation.excerpt}),
        el('p', {class: 'library-resolution-citation', text: `Ticket ${citation.ticket.id} · ${citation.section} · ${formatDate(citation.observedAt)} · ${citation.url}`}))),
      !(item.citations || []).some(citation => citation.isSolution)
        ? el('p', {class: 'help', text: 'Aucun extrait identifié comme solution dans cette sélection. Consultez la fiche pour son contexte.'}) : null,
      el('div', {class: 'library-resolution-card-actions'},
        button('Lire ce précédent', () => onRead(item.key), {icon: 'ticket'}),
        button('Ouvrir ce précédent dans GLPI', () => api.call('library.open', {key: item.key}, {signal}), {icon: 'arrow'})));
  }
  async function load(text) {
    if (disposed || signal?.aborted) return;
    loaded = true; const version = ++generation; request?.abort(); request = new AbortController();
    const current = request;
    results.setAttribute('aria-busy', 'true'); feedback.replaceChildren(el('p', {text: 'Recherche dans les précédents confirmés du cache…'}));
    results.replaceChildren();
    try {
      const response = await api.call('library.resolution', {key, ...(text === undefined ? {} : {text})}, {signal: current.signal});
      if (disposed || version !== generation || current.signal.aborted) return;
      if (text === undefined) search.value = (response.terms || []).join(' ').slice(0, 300);
      const examined = `${formatNumber(response.candidatesExamined)} candidat(s) examiné(s)`;
      const displayed = `${formatNumber(response.matches?.length)} précédent(s) affiché(s).`;
      replaceChildren(feedback,
        !response.sourceContentConfirmed ? el('p', {class: 'library-resolution-source-warning', text: response.manual
          ? 'Le contenu de la fiche de départ reste à recontrôler. La recherche utilise vos termes saisis ; les précédents proposés ont leur propre confirmation.'
          : 'Le contenu de la fiche de départ reste à recontrôler. Seul son titre conservé sert de point de départ ; les précédents proposés ont leur propre confirmation.'}) : null,
        el('p', {text: response.message}),
        response.available ? el('p', {class: 'library-resolution-limits', text: response.limited
          ? `${examined} sur ${formatNumber(response.candidatesFound)} correspondance(s) initiale(s) · ${displayed} Une portion limitée du cache est présentée : au plus ${response.candidateLimit} candidats examinés et ${response.resultLimit} cartes affichées.`
          : `${examined} dans le cache · ${displayed}`}) : null);
      results.replaceChildren(...(response.matches || []).map(card));
      if (!(response.matches || []).length) results.append(empty(response.available ? 'Aucun précédent retenu' : 'Précisez le symptôme',
        'Essayez le nom de l’équipement, le symptôme ou un code d’erreur. Les données et la fiche en cours restent inchangées.'));
    } catch (error) {
      if (!disposed && version === generation && !current.signal.aborted) { feedback.replaceChildren(); showError(feedback, error); }
    } finally { if (version === generation) results.removeAttribute('aria-busy'); }
  }
  return {element, load: () => loaded ? Promise.resolve() : load(), dispose: () => { abort(); signal?.removeEventListener('abort', abort); }};
}
