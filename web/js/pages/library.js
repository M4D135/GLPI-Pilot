import { el, button, toolbar, empty, badge, input, select, field, table, dialog, confirm, formatNumber, formatDate, icon, showError } from '../ui.js';

import { sharedOperationKinds, reviewSharedOperation } from './shared-operations.js';
import { openLibraryTicket } from '../library-ticket.js';

const kinds = { comment: 'Commentaire', knowledge: 'Connaissance', assignment: 'Affectation', note: 'Note', configuration: 'Configuration partagée', statistics: 'Statistiques partagées', ...sharedOperationKinds };
const states = { open: 'Ouvert', done: 'Terminé', draft: 'Brouillon', published: 'Publié' };
const count = value => Number.isFinite(value) ? formatNumber(value) : '—';
const text = value => value == null ? '' : String(value);
const check = (label, checked = false) => {
  const control = el('input', { type: 'checkbox', checked });
  return { control, element: el('label', { class: 'library-check' }, control, el('span', { text: label })) };
};

async function library(ctx) {
  const { root, api, signal, navigate, notify } = ctx;
  const call = (op, args = {}, options = {}) => api.call(op, args, { signal, ...options });
  let model = await call('library.get'), latestCapture = null;
  let view = ctx.params?.resolvedOnly ? 'resolution' : ['library', 'resolution', 'team', 'connection'].includes(ctx.params?.view) ? ctx.params.view : 'library';
  let query = text(ctx.params?.text), installation = text(ctx.params?.installation), offset = 0, searchVersion = 0;
  let operation = null, passwordInput = null, busy = false, disposed = false;
  const actionButtons = [], header = el('div'), statistics = el('div', { class: 'library-stats', 'aria-label': 'État de la bibliothèque' });
  const storage = el('div'), tabs = el('nav', { class: 'library-modes', 'aria-label': 'Vues de la bibliothèque' }), content = el('div', { class: 'library-content' });
  const progressText = el('strong', { text: '' });
  const progressDetail = el('p');
  const progress = el('section', { class: 'library-progress', hidden: true, role: 'status', 'aria-live': 'polite' },
    toolbar(progressText, button('Annuler le traitement', () => operation?.abort(), { icon: 'close' })), el('progress', { 'aria-label': 'Opération de bibliothèque en cours' }), progressDetail);
  const outcome = el('section', { class: 'card library-outcome', hidden: true, 'aria-label': 'Bilan de la bibliothèque' });
  const captureSummary = el('section', { class: 'card library-outcome library-capture-summary', hidden: true, 'aria-label': 'Dernière collecte conservée' });
  root.classList.add('ticket-library');
  root.replaceChildren(header, statistics, storage, progress, captureSummary, outcome, tabs, content);
  const stopProgress = api.on('operation.progress', data => { if (busy && !disposed) progressDetail.textContent = text(data.message); });
  const stopLibrary = api.on('library.changed', () => { if (!busy && !disposed) void Promise.all([refreshModel(), refreshCapture()]).catch(error => { if (!disposed) showError(storage, error); }); });
  const abort = () => operation?.abort(); signal?.addEventListener('abort', abort, { once: true });

  function action(label, handler, options = {}) {
    const control = button(label, handler, options); control.disabled = busy || !!options.disabled; actionButtons.push({ control, disabled: !!options.disabled }); return control;
  }
  function lock(value) {
    busy = value;
    for (const entry of actionButtons) if (entry.control.isConnected) entry.control.disabled = value || entry.disabled;
    progress.hidden = !value;
  }
  async function refreshModel() { model = await call('library.get'); if (!disposed) drawSummary(); }
  async function refreshCapture() { latestCapture = await call('library.capture.latest'); if (!disposed) drawCapture(); }
  async function perform(label, op, args = {}) {
    if (busy) return;
    operation = new AbortController(); lock(true); outcome.hidden = true; progressText.textContent = label; progressDetail.textContent = 'Les observations et les limites du traitement sont indiquées au fil de son exécution.';
    try {
      const result = await call(op, args, { signal: operation.signal });
      if (!result?.cancelled) notify(result?.message || result?.summary || 'Opération terminée.');
      if (!disposed && result && !result.cancelled && op !== 'library.capture') drawOutcome(result);
      return result;
    } catch (error) {
      if (error?.name === 'AbortError' || error?.code === 'CANCELLED') { if (!disposed) notify('Opération annulée. Les observations déjà enregistrées sont conservées.', 'info'); return; }
      throw error;
    } finally {
      operation = null;
      if (!disposed && !signal?.aborted) {
        try {
          await Promise.all([refreshModel(), refreshCapture()]);
          if (['library', 'resolution', 'team'].includes(view)) await drawView();
        } catch (error) { if (!disposed) showError(storage, error); }
      }
      lock(false);
    }
  }
  function drawCapture() {
    captureSummary.hidden = !latestCapture;
    if (!latestCapture) { captureSummary.replaceChildren(); return; }
    const run = latestCapture, canRetry = run.retryCount > 0 && run.status !== 'running';
    captureSummary.replaceChildren(el('div', { class: 'library-outcome-heading' },
      el('div', {}, el('h3', { text: 'Dernière collecte' }),
        el('p', { class: 'help', text: `Démarrée le ${formatDate(run.startedAt)}${run.completedAt ? ' · Terminée le ' + formatDate(run.completedAt) : ''}` })),
      badge(run.statusLabel || 'Bilan conservé', run.status === 'completed' && !run.retryCount ? '' : 'info')),
      el('p', { text: run.message || 'Le bilan et les observations de cette collecte sont conservés sur ce poste.' }),
      toolbar(action('Détail de la collecte', () => showCapture(run), { icon: 'log' }),
        canRetry ? action(`Reprendre les ${count(run.retryCount)} ticket(s) à relire`, () => retryCapture(run), { icon: 'refresh' }) : null));
    if (run.finalScopeVerified !== true) captureSummary.append(el('p', { class: 'help', text: 'La vérification finale n’est pas confirmée. Les nouveaux contenus restent consultables ; leur fraîcheur devra être contrôlée.' }));
    if (canRetry) captureSummary.append(el('p', { class: 'help', text: 'La reprise réactualise la liste GLPI et relit les tickets encore accessibles dans le même contexte.' }));
  }
  function retryCapture(run) {
    return perform('Reprise des tickets à relire…', 'library.capture', { mode: 'retry', runId: run.runId });
  }
  async function showCapture(run) {
    const request = new AbortController(), rows = el('div'), pager = el('div', { class: 'library-pagination' }),
      summary = el('p', { class: 'help', role: 'status' }), retryOnly = check('Afficher uniquement les tickets à relire', run.retryCount > 0);
    let offset = 0, generation = 0;
    const body = el('div', { class: 'library-detail library-capture-detail' },
      el('p', { text: `${run.statusLabel || 'Collecte'} · ${formatDate(run.startedAt)}` }),
      el('dl', { class: 'library-outcome-counts' }, ...[
        [run.observed, 'Tickets observés'], [run.read, 'Contenus lus'], [run.reused, 'Contenus réutilisés'],
        [run.failed, 'Lectures en échec'], [run.partial, 'Lectures partielles'], [run.unconfirmed, 'Fraîcheur à confirmer']
      ].map(([value, label]) => el('div', {}, el('dt', { text: label }), el('dd', { text: count(value) })))),
      el('p', { class: 'help', text: 'Ce bilan décrit la tentative de lecture. Une fiche complète plus ancienne peut rester conservée lorsqu’une nouvelle lecture est partielle.' }),
      el('p', { class: 'help', text: run.scope || '' }),
      ...(run.limitations || []).map(value => el('p', { class: 'help', text: value })),
      retryOnly.element, summary, rows, pager);
    const modal = dialog('Détail et reprise de la collecte', body, run.retryCount > 0 && run.status !== 'running' ? [
      { label: 'Reprendre les tickets à relire', primary: true, disabled: busy, action: async close => { close(); await retryCapture(run); } }
    ] : []);
    const close = () => modal.close();
    signal?.addEventListener('abort', close, { once: true });
    modal.element.addEventListener('pilot:closed', () => { request.abort(); signal?.removeEventListener('abort', close); }, { once: true });
    async function load() {
      const version = ++generation; rows.setAttribute('aria-busy', 'true');
      try {
        const response = await call('library.capture.entries', { runId: run.runId, offset, limit: 25, retryOnly: retryOnly.control.checked }, { signal: request.signal });
        if (request.signal.aborted || version !== generation) return;
        offset = response.offset; const items = response.items || [];
        summary.textContent = `${count(response.total)} ticket(s)${retryOnly.control.checked ? ' à relire' : ' dans ce bilan'} · ${count(run.processed)} traité(s) sur ${count(run.targeted)} ciblé(s)`;
        rows.replaceChildren(items.length ? table(items, [
          { label: 'Ticket', render: item => el('span', { class: 'library-ticket-id', text: '#' + item.ticketId }) },
          { label: 'État de la lecture', render: item => badge(item.stateLabel || 'Observation conservée', item.retryable ? 'info' : '') },
          { label: 'Observation', render: item => el('p', { class: 'library-capture-message', text: item.message || 'Observation enregistrée.' }) },
          { label: 'Contenu local', render: item => item.ticketKey ? button('Lire le cache', () => openTicket(item.ticketKey), { icon: 'ticket' }) : el('span', { text: '—' }) }
        ]) : empty('Aucun ticket dans cette sélection', retryOnly.control.checked ? 'Tous les tickets de ce bilan peuvent être affichés en désactivant le filtre.' : 'Aucune lecture individuelle n’avait encore été enregistrée.'));
        pager.replaceChildren(button('Tickets précédents', async () => { offset = Math.max(0, offset - response.limit); await load(); }, { disabled: offset === 0 }),
          el('span', { text: `${items.length ? offset + 1 : 0}–${Math.min(response.total, offset + items.length)} sur ${count(response.total)}` }),
          button('Tickets suivants', async () => { offset += response.limit; await load(); }, { disabled: offset + items.length >= response.total || !items.length }));
      } catch (error) { if (!request.signal.aborted && version === generation) showError(body, error); }
      finally { if (version === generation) rows.removeAttribute('aria-busy'); }
    }
    retryOnly.control.addEventListener('change', () => { offset = 0; void load(); });
    if (signal?.aborted) modal.close(); else await load();
  }
  function drawOutcome(result) {
    const captured = Number.isFinite(result.observed);
    outcome.replaceChildren(el('div', { class: 'library-outcome-heading' }, el('h3', { text: captured ? 'Bilan de la collecte' : 'Bilan de la synchronisation' }),
      button('Masquer le bilan', () => { outcome.hidden = true; }, { icon: 'close' })),
      el('p', { text: result.message || result.summary || 'Opération terminée.' }));
    if (captured) {
      outcome.append(el('dl', { class: 'library-outcome-counts' }, ...[
        [result.observed, 'Tickets observés'], [result.read, 'Contenus lus'], [result.reused, 'Contenus réutilisés'],
        [result.failed, 'Lectures à reprendre'], [result.partial, 'Contenus partiels'], [result.conflicts, 'Versions concurrentes']
      ].map(([value, label]) => el('div', {}, el('dt', { text: label }), el('dd', { text: count(value) })))),
        el('p', { class: 'help', text: result.scope || 'Périmètre non décrit par la collecte.' }),
        el('p', { class: result.finalScopeVerified === true ? 'help' : 'library-conflict',
          text: result.finalScopeVerified === true ? 'Le périmètre final a été confirmé.' : 'Le périmètre final reste à vérifier. Les observations recueillies restent conservées.' }));
    }
    if (Array.isArray(result.limitations) && result.limitations.length)
      outcome.append(el('div', { class: 'library-outcome-limits' }, el('h4', { text: 'Portée et limites de la collecte' }),
        el('ul', {}, ...result.limitations.map(value => el('li', { text: value })))));
    if (result.sync) outcome.append(el('p', { class: 'help', text: result.sync }));
    outcome.hidden = false;
  }
  function drawSummary() {
    actionButtons.splice(0, actionButtons.length, ...actionButtons.filter(entry => entry.control.isConnected));
    const s = model.stats || {}, settings = model.settings || {}, hasTickets = s.ticketCount > 0;
    header.replaceChildren(el('section', { class: 'card library-overview' }, el('div', {},
      el('div', { class: 'library-overline' }, icon('layers'), el('span', { text: 'Votre mémoire de travail GLPI' }), badge('Stockage local', 'info')),
      el('h2', { text: hasTickets ? 'Votre bibliothèque GLPI' : 'Collecter les tickets accessibles' }),
      el('p', { text: hasTickets ? 'Retrouvez les fiches et leurs solutions conservées. Une mise à jour relit les contenus nouveaux, modifiés ou à confirmer dans les entités actives de votre session.' : 'Tous les statuts, dans les entités actives de votre session GLPI. Les contenus confirmés alimentent la recherche de solutions sourcées.' })),
      toolbar(action(hasTickets ? 'Mettre à jour le cache' : 'Aspirer tous les tickets', () => perform(hasTickets ? 'Mise à jour du cache…' : 'Collecte des tickets et de leurs contenus…', 'library.capture', { mode: 'incremental' }), { primary: true, icon: hasTickets ? 'refresh' : 'download' }))));
    statistics.replaceChildren(...[
      [s.ticketCount, 'Tickets conservés', 'Index de la bibliothèque'],
      [s.contentCount, 'Fiches avec contenu', 'Lectures effectivement recueillies'],
      [s.resolvedCount, 'Tickets résolus ou clos', 'Statut conservé dans l’index'],
      [s.conflictCount, 'Conflits à examiner', 'Versions concurrentes conservées']
    ].map(([value, label, detail]) => el('div', { class: 'library-stat' }, el('div', { class: 'library-stat-label', text: label }), el('div', { class: 'library-stat-value', text: count(value) }), el('div', { class: 'library-stat-detail', text: detail }))));
    storage.replaceChildren(el('section', { class: 'card library-storage', 'aria-label': 'Stockage et synchronisation' },
      el('div', {}, toolbar(badge(settings.postgresEnabled ? 'Central PostgreSQL configuré' : 'Bibliothèque locale'),
        el('span', { class: 'help', text: model.sync?.label || (settings.postgresEnabled ? 'Synchronisation disponible à la demande.' : 'Activez une connexion pour partager avec l’équipe.') })),
      el('div', { class: 'library-storage-path', text: settings.localFolder || 'Dossier local géré par l’application' }),
      el('p', { class: 'help', text: `${count(s.pendingCount)} ticket(s) et ${count(model.team?.pending)} contribution(s) en attente · Dernière synchronisation : ${formatDate(s.lastSyncAt)}` })),
      toolbar(action('Synchroniser', () => perform('Synchronisation avec PostgreSQL…', 'library.sync'), { icon: 'refresh', disabled: !settings.postgresEnabled }),
        action('Exporter le corpus RAG', async () => { const result = await call('library.export'); if (!result.cancelled) notify('Corpus exporté : ' + result.path); }, { icon: 'download' }))));
    if (model.sync?.lastError) storage.append(el('p', { class: 'library-conflict', text: model.sync.lastError }));
    if (busy) lock(true);
  }
  async function setView(next) { if (next === view) return; view = next; offset = 0; await drawView(); }
  async function drawView() {
    if (disposed) return;
    if (passwordInput) { passwordInput.value = ''; passwordInput = null; }
    searchVersion++;
    tabs.replaceChildren(...[['library', 'Bibliothèque', 'layers'], ['resolution', 'Résolution', 'spark'], ['team', 'Équipe', 'users'], ['connection', 'Connexion', 'settings']].map(([id, label, symbol]) =>
      button(label, () => setView(id), { icon: symbol, attributes: { 'aria-pressed': String(view === id) } })));
    content.replaceChildren();
    if (view === 'connection') drawSettings();
    else if (view === 'team') await drawTeam();
    else await drawSearch();
  }
  async function drawSearch() {
    const resolved = view === 'resolution';
    const search = input(query, { type: 'search', maxLength: 1000, placeholder: resolved ? 'Erreur, symptôme, équipement, solution…' : 'Numéro, titre ou contenu du ticket…' });
    const sources = select([{ value: '', label: 'Toutes les installations' }, ...(model.sources || []).map(source => ({ value: source.installation, label: source.label || source.installation }))], installation);
    const results = el('div', { class: 'library-results', 'aria-label': resolved ? 'Solutions de tickets résolus' : 'Tickets de la bibliothèque' });
    const summary = el('span', { class: 'library-results-count', role: 'status' });
    const pager = el('div', { class: 'library-pagination' });
    const searchForm = el('form', { class: 'card library-search', onSubmit: event => { event.preventDefault(); void submit().catch(error => showError(content, error)); } },
      el('div', {}, el('h2', { text: resolved ? 'Retrouver une solution déjà appliquée' : 'Explorer la bibliothèque' }),
        el('p', { class: 'help', text: resolved ? 'Les rapprochements proviennent des tickets résolus ou clos recueillis. Chaque extrait garde son ticket, sa rubrique et sa date d’observation.' : 'Recherchez dans les titres et les contenus conservés, même sans ouvrir GLPI.' })),
      el('div', { class: 'library-search-controls' }, field('Rechercher dans la bibliothèque', search), field('Installation GLPI', sources), button('Rechercher', () => submit(), { primary: true, icon: 'search' })),
      el('div', { class: 'library-search-footer' }, summary, action('Utiliser le cache comme relevé', async () => { await call('library.loadRecords', { installation }); await navigate('dashboard'); }, { icon: 'chart' })));
    content.replaceChildren(searchForm, results, pager);
    async function submit() { query = search.value; installation = sources.value; offset = 0; await load(); }
    async function load() {
      const generation = ++searchVersion; results.setAttribute('aria-busy', 'true');
      try {
        const response = await call('library.search', { text: query, installation, resolvedOnly: resolved, offset, limit: 25 });
        if (disposed || generation !== searchVersion || !results.isConnected) return;
        offset = response.offset; const rows = response.rows || [];
        summary.textContent = `${count(response.total)} résultat(s)${resolved ? ' parmi les tickets résolus' : ''}`;
        results.replaceChildren(...rows.map(row => resultCard(row, resolved)));
        if (!rows.length) results.append(empty(query ? 'Aucun ticket ne correspond' : resolved ? 'Aucun précédent résolu confirmé' : 'Votre bibliothèque est prête à être alimentée',
          query ? 'Essayez un numéro, un terme plus court ou une autre installation.' : resolved ? 'Mettez à jour le cache pour recueillir et confirmer les contenus des tickets résolus ou clos. Les fiches à recontrôler restent consultables dans Bibliothèque.' : 'Aspirez les tickets accessibles depuis votre session GLPI pour recueillir leur contenu.'));
        pager.replaceChildren(button('Précédent', async () => { offset = Math.max(0, offset - response.limit); await load(); }, { disabled: offset === 0 }),
          el('span', { text: `${response.total ? offset + 1 : 0}–${Math.min(response.total, offset + rows.length)} sur ${count(response.total)}` }),
          button('Suivant', async () => { offset += response.limit; await load(); }, { disabled: offset + response.limit >= response.total }));
      } finally { if (generation === searchVersion) results.removeAttribute('aria-busy'); }
    }
    sources.addEventListener('change', () => { void submit().catch(error => showError(content, error)); });
    await load();
  }
  function resultCard(row, resolved) {
    const citations = row.citations || [];
    const node = el('article', { class: 'card library-result', dataset: { ticketKey: row.key } },
      el('div', {}, el('div', { class: 'library-result-heading' }, el('span', { class: 'library-ticket-id', text: '#' + row.ticket.id }), el('h3', { text: row.title || 'Ticket sans titre recueilli' })),
        el('div', { class: 'library-result-meta' }, badge(row.status || 'État non recueilli'), el('span', { text: row.installation }), row.category ? el('span', { text: row.category }) : null,
          badge(row.detailsCurrent && row.detailComplete ? 'Contenu à jour' : row.readAt ? 'Contenu à recontrôler' : 'Contenu non lu', row.detailsCurrent && row.detailComplete ? '' : 'warning')),
        el('p', { class: 'library-evidence', text: `Modification GLPI : ${row.lastModifiedRaw || formatDate(row.lastModifiedAt)} · Lecture : ${formatDate(row.readAt)}` }),
        row.conflicts ? el('p', { class: 'library-conflict', text: `${count(row.conflicts)} version(s) concurrente(s) conservée(s). La synchronisation ne les fusionne pas silencieusement.` }) : null),
      el('div', { class: 'library-result-actions' }, button('Lire le contenu conservé', () => openTicket(row.key), { icon: 'ticket' }),
        button('Ouvrir dans GLPI', () => call('library.open', { key: row.key }), { icon: 'arrow' }),
        button('Ajouter une note d’équipe', () => editWorkspace({ kind: 'note', ticketKey: row.key, title: 'Ticket ' + row.ticket.id, body: '', status: 'draft' }, 'Ticket ' + row.ticket.id)),
        row.conflicts ? button('Examiner les versions', () => showConflicts('library.conflicts', { key: row.key }, true)) : null));
    if (citations.length) node.append(el('div', { class: 'library-solution' },
      el('div', { class: 'library-solution-heading' }, icon(resolved ? 'check' : 'search'), el('span', { text: resolved ? 'Extraits sourcés de ce ticket résolu' : 'Extraits recueillis' })),
      ...citations.slice(0, 3).map(citation => el('div', {}, citation.isSolution ? el('span', { class: 'help', text: 'Solution effectivement recueillie' }) : null,
        el('blockquote', { text: citation.excerpt }),
        el('p', { class: 'library-evidence', text: `Ticket ${citation.ticket?.id || row.ticket.id} · ${citation.section || 'Fiche GLPI'} · Observé le ${formatDate(citation.observedAt)}${citation.current === false ? ' · Observation à recontrôler' : ''}` })))));
    return node;
  }
  function openTicket(key) { return openLibraryTicket({ api, signal }, key); }
  function drawSettings() {
    const settings = model.settings || {};
    const controls = {
      localFolder: input(settings.localFolder || '', { maxLength: 1500 }),
      sourceTimeZone: input(settings.sourceTimeZone || '', { maxLength: 150, placeholder: 'Ex. Europe/Paris' }),
      sourceDateOrder: select([{ value: '', label: 'Détection prudente' }, { value: 'dmy', label: 'Jour / mois / année' }, { value: 'mdy', label: 'Mois / jour / année' }, { value: 'ymd', label: 'Année / mois / jour' }], settings.sourceDateOrder || ''),
      maxAgeDays: input(settings.maxAgeDays ?? 7, { type: 'number', min: 1, max: 365, step: 1 }),
      host: input(settings.host || '', { maxLength: 250, autocomplete: 'off' }),
      port: input(settings.port ?? 5432, { type: 'number', min: 1, max: 65535, step: 1 }),
      database: input(settings.database || '', { maxLength: 150, autocomplete: 'off' }),
      username: input(settings.username || '', { maxLength: 150, autocomplete: 'off' }),
      tlsMode: select([{ value: 'VerifyFull', label: 'TLS · certificat et nom vérifiés' }, { value: 'Require', label: 'TLS · chiffrement requis' }, { value: 'Disable', label: 'Sans TLS' }], settings.tlsMode || 'VerifyFull'),
      intervalMinutes: input(settings.intervalMinutes ?? 5, { type: 'number', min: 1, max: 1440, step: 1 }),
      displayName: input(settings.displayName || '', { maxLength: 120 })
    };
    const enabled = check('Activer le central PostgreSQL', settings.postgresEnabled === true);
    const automatic = check('Synchroniser automatiquement', settings.autoSync === true);
    const remember = check('Mémoriser ce mot de passe sur ce poste', false);
    const password = input('', { type: 'password', autocomplete: 'new-password', maxLength: 1000, placeholder: settings.hasPassword ? 'Un mot de passe est déjà disponible' : 'Mot de passe de connexion' });
    passwordInput = password;
    const connectionStatus = el('span', { class: 'library-connection-status', role: 'status' });
    const wide = (label, control, help) => { const node = field(label, control, help); node.classList.add('library-field-wide'); return node; };
    const pg = el('div', { class: 'library-settings-fields' }, field('Hôte PostgreSQL', controls.host), field('Port PostgreSQL', controls.port),
      field('Base de données', controls.database), field('Utilisateur PostgreSQL', controls.username), wide('Protection de la connexion', controls.tlsMode),
      wide('Mot de passe PostgreSQL', password, settings.hasPassword ? 'Laissez vide pour conserver le secret déjà fourni. Il n’est jamais renvoyé à cette interface.' : 'Le secret reste disponible pendant cette session. Cochez la mémorisation pour le conserver après fermeture.'),
      remember.element, automatic.element, wide('Intervalle de synchronisation (minutes)', controls.intervalMinutes));
    const editor = el('form', { class: 'card library-settings', onSubmit: event => event.preventDefault() },
      el('div', { class: 'library-settings-grid' },
        el('section', { class: 'library-settings-section' }, el('h2', { text: 'Bibliothèque locale' }),
          field('Dossier de la bibliothèque', controls.localFolder, 'Les tickets, contenus et contributions sont conservés dans ce dossier.'),
          button('Choisir le dossier', async () => { const result = await call('library.folder.pick'); const folder = result.folder ?? result.path; if (folder) controls.localFolder.value = folder; }, { icon: 'folder' }),
          field('Fuseau des dates GLPI', controls.sourceTimeZone, 'Choisissez le fuseau d’affichage de GLPI. Sans ce choix, la collecte reste possible ; une date sans décalage horaire ne permet pas de réutiliser un contenu sans le relire.'),
          field('Format des dates GLPI', controls.sourceDateOrder, 'Choisissez l’ordre affiché dans GLPI. En détection prudente, une date telle que 05/06 reste ambiguë et le contenu sera relu.'),
          field('Recontrôler les contenus après (jours)', controls.maxAgeDays, 'Une relecture périodique complète le contrôle incrémental de la date de modification.'),
          field('Votre nom dans l’espace d’équipe', controls.displayName),
          el('p', { class: 'library-setting-note', text: 'Les pièces jointes sont conservées sous forme de références. La bibliothèque recueille les onglets de consultation accessibles ; elle conserve les limites et les lectures partielles.' }),
          action('Relire tous les contenus', () => perform('Relecture de tous les contenus accessibles…', 'library.capture', { mode: 'all' }), { icon: 'refresh' })),
        el('section', { class: 'library-settings-section' }, el('h2', { text: 'Central PostgreSQL · facultatif' }), enabled.element,
          el('p', { class: 'library-setting-note', text: 'La synchronisation échange les tickets et les contributions avec le central configuré. Les versions concurrentes sont conservées et restent visibles comme conflits.' }), pg)),
      el('div', { class: 'library-settings-actions' },
        button('Enregistrer la connexion', async () => {
          await call('library.settings.save', { settings: read(), password: password.value, rememberPassword: remember.control.checked, forgetPassword: false });
          password.value = ''; await refreshModel(); notify('Paramètres de la bibliothèque enregistrés.'); drawSettings();
        }, { primary: true }),
        button('Tester la connexion', async () => { connectionStatus.textContent = 'Test de connexion en cours…'; connectionStatus.dataset.tone = '';
          try { const result = await call('library.connection.test', { settings: read(), password: password.value }); connectionStatus.textContent = result.message || 'Connexion établie.'; connectionStatus.dataset.tone = result.success === false ? 'danger' : 'success'; }
          catch (error) { connectionStatus.textContent = error.message; connectionStatus.dataset.tone = 'danger'; }
        }),
        button('Oublier le mot de passe', async () => { await call('library.settings.save', { settings: read(), forgetPassword: true }); password.value = ''; await refreshModel(); notify('Mot de passe retiré de cette connexion.'); drawSettings(); }, { disabled: !settings.hasPassword }), connectionStatus));
    function read() {
      if (!editor.reportValidity()) throw new Error('Vérifiez les champs de connexion signalés.');
      return { ...Object.fromEntries(Object.entries(controls).map(([key, control]) => [key, ['port', 'maxAgeDays', 'intervalMinutes'].includes(key) ? Number(control.value) : control.value])), postgresEnabled: enabled.control.checked, autoSync: automatic.control.checked };
    }
    const update = () => { for (const control of [controls.host, controls.port, controls.database, controls.username, controls.tlsMode, password, remember.control, automatic.control]) control.disabled = !enabled.control.checked; controls.intervalMinutes.disabled = !enabled.control.checked || !automatic.control.checked; };
    enabled.control.addEventListener('change', update); automatic.control.addEventListener('change', update); update();
    content.replaceChildren(editor);
  }
  async function drawTeam() {
    const filter = select([{ value: '', label: 'Toutes les contributions' }, ...Object.entries(kinds).map(([value, label]) => ({ value, label }))]);
    const search = input('', { type: 'search', maxLength: 500, placeholder: 'Rechercher une connaissance ou une contribution…', 'aria-label': 'Rechercher dans l’espace d’équipe' });
    const list = el('div', { class: 'library-results', 'aria-label': 'Contributions de l’équipe' });
    const pager = el('div', { class: 'library-pagination' }), summary = el('p', { class: 'library-results-count', role: 'status' }); let teamOffset = 0;
    const submit = async () => { teamOffset = 0; await load(); };
    const form = el('form', { class: 'card library-search', onSubmit: event => { event.preventDefault(); void submit().catch(error => showError(content, error)); } },
      el('h2', { text: 'Conserver et partager le travail de l’équipe' }),
      el('p', { class: 'help', text: 'Retrouvez les notes, connaissances, tournées, répartitions et compétences publiées depuis vos outils. La synchronisation les échange avec l’équipe ; examinez une contribution avant de la reprendre sur ce poste.' }),
      toolbar(search, field('Type de contribution', filter), button('Rechercher', submit, { icon: 'search' }), button('Ajouter une contribution', () => editWorkspace(), { primary: true, icon: 'plus' })),
      toolbar(button('Partager la configuration actuelle', () => publishWorkspace('workspace.publishConfiguration', 'Partager la configuration', 'Configuration de l’équipe')),
        button('Partager les statistiques actuelles', () => publishWorkspace('workspace.publishStatistics', 'Partager les statistiques', 'Statistiques du relevé')),
        button('Exporter l’espace d’équipe', async () => { const result = await call('workspace.export'); if (!result.cancelled) notify('Espace exporté : ' + result.path); }, { icon: 'download' })), summary);
    content.replaceChildren(form, list, pager);
    async function load() {
      const generation = ++searchVersion;
      const response = await call('workspace.list', { kind: filter.value, text: search.value, ticketKey: ctx.params?.ticketKey || '', offset: teamOffset, limit: 25 });
      if (disposed || generation !== searchVersion || !list.isConnected) return;
      summary.textContent = `${count(response.total)} contribution(s)`;
      list.replaceChildren(...(response.items || []).map(item => el('article', { class: 'card library-result', dataset: { workspaceId: item.id } },
        el('div', {}, el('div', { class: 'library-result-heading' }, badge(kinds[item.kind] || 'Contribution'), el('h3', { text: item.title || 'Sans titre' })),
          el('div', { class: 'library-result-meta' }, el('span', { text: item.author || 'Auteur non précisé' }), el('span', { text: formatDate(item.updatedAt) }), badge(states[item.status] || item.status || 'Non précisé'), item.pending ? badge('En attente de synchronisation') : null),
          item.assignee ? el('p', { class: 'help', text: 'Affecté à ' + item.assignee }) : null,
          el('p', { class: 'library-result-excerpt', text: item.body || '' }),
          item.conflicts ? el('p', { class: 'library-conflict', text: 'Versions concurrentes conservées. Actualisez avant de modifier cette contribution.' }) : null),
        el('div', { class: 'library-result-actions' }, button('Lire la contribution', () => viewWorkspace(item)),
          ['comment', 'knowledge', 'assignment', 'note'].includes(item.kind) ? button('Modifier', () => editWorkspace(item)) : null,
          item.kind === 'configuration' ? button('Examiner cette configuration', () => previewConfiguration(item.id)) : null,
          sharedOperationKinds[item.kind] ? button('Examiner et reprendre', () => reviewSharedOperation(ctx, item.id)) : null,
          item.conflicts ? button('Examiner les versions', () => showConflicts('workspace.conflicts', { id: item.id }, false)) : null,
          button('Retirer', async () => { if (await confirm('Retirer cette contribution ?', 'Le retrait sera partagé lors de la prochaine synchronisation. Les tickets GLPI ne sont pas modifiés.', 'Retirer')) { await call('workspace.remove', { id: item.id, expectedRevision: item.revision }); await load(); } }, { danger: true })))));
      if (!(response.items || []).length) list.append(empty('Aucune contribution pour ce filtre', 'Ajoutez une note, une connaissance ou une affectation pour préparer le travail de l’équipe.'));
      pager.replaceChildren(button('Contributions précédentes', async () => { teamOffset = Math.max(0, teamOffset - 25); await load(); }, { disabled: teamOffset === 0 }),
        el('span', { text: `${response.total ? teamOffset + 1 : 0}–${Math.min(response.total, teamOffset + (response.items || []).length)} sur ${count(response.total)}` }),
        button('Contributions suivantes', async () => { teamOffset += 25; await load(); }, { disabled: teamOffset + 25 >= response.total }));
    }
    filter.addEventListener('change', () => { void submit().catch(error => showError(content, error)); });
    await load();
  }
  async function viewWorkspace(item) {
    item = await call('workspace.get', { id: item.id });
    dialog(item.title || kinds[item.kind] || 'Contribution', el('div', { class: 'library-detail' }, toolbar(badge(kinds[item.kind] || 'Contribution'), badge(states[item.status] || item.status || '')),
      el('p', { class: 'help', text: `${item.author || 'Auteur non précisé'} · ${formatDate(item.updatedAt)}` }),
      el('pre', { class: 'library-detail-text', text: item.body || 'Aucun texte ajouté.' }),
      item.assignee ? el('p', { text: 'Affecté à ' + item.assignee }) : null,
      item.conflicts ? el('p', { class: 'library-conflict', text: 'Des versions concurrentes de cette contribution sont conservées.' }) : null),
      sharedOperationKinds[item.kind] ? [{ label: 'Fermer', action: close => close() }, { label: 'Examiner et reprendre', primary: true, action: async close => { close(); await reviewSharedOperation(ctx, item.id); } }] : []);
  }
  async function editWorkspace(item = {}, ticketLabel = '') {
    if (item.id) item = await call('workspace.get', { id: item.id });
    const kind = select(Object.entries(kinds).filter(([key]) => ['comment', 'knowledge', 'assignment', 'note'].includes(key)).map(([value, label]) => ({ value, label })), item.kind || 'note');
    const title = input(item.title || '', { maxLength: 300, required: true });
    kind.disabled = !!item.id;
    const body = input(item.body || '', { multiline: true, rows: 8, maxLength: 200000 });
    const assignee = input(item.assignee || '', { maxLength: 200 });
    const status = select([], item.status || 'draft');
    const fields = el('form', { class: 'form-grid', onSubmit: event => event.preventDefault() }, field('Type', kind), field('Titre', title), field('Texte de la contribution', body), field('Affecté à', assignee), field('État', status));
    const update = () => { const previous = status.value || item.status; const choices = kind.value === 'assignment' ? ['open', 'done'] : ['draft', 'published']; if (item.id && states[previous] && !choices.includes(previous)) choices.push(previous); status.replaceChildren(...choices.map(value => el('option', { value, text: states[value] }))); status.value = choices.includes(previous) ? previous : choices[0]; assignee.disabled = kind.value !== 'assignment'; };
    kind.addEventListener('change', update); update();
    dialog(item.id ? 'Modifier la contribution' : 'Ajouter une contribution', el('div', {},
      el('p', { class: 'notice', text: 'Destination : espace d’équipe. Cette contribution, même en brouillon, sera partagée lors de la synchronisation. Les notes personnelles se rédigent dans les fiches de l’écran Tickets.' }),
      item.ticketKey ? el('p', { class: 'help', text: ticketLabel || 'Contribution liée à un ticket de la bibliothèque.' }) : null, fields), [
      { label: 'Enregistrer la contribution', primary: true, action: async close => {
        if (!fields.reportValidity()) return;
        await call('workspace.save', { item: { id: item.id, kind: kind.value, title: title.value, body: body.value, ticketKey: item.ticketKey || '', assignee: kind.value === 'assignment' ? assignee.value : '', status: status.value }, expectedRevision: item.revision });
        close(); notify('Contribution enregistrée.'); await refreshModel(); if (view === 'team') await drawTeam();
      } }
    ]);
  }
  function publishWorkspace(op, heading, suggestion) {
    const title = input(suggestion, { maxLength: 300, required: true });
    dialog(heading, field('Titre du partage', title), [{ label: 'Publier dans l’espace d’équipe', primary: true, action: async close => {
      if (!title.reportValidity()) return; await call(op, { title: title.value }); close(); notify('Partage enregistré dans l’espace d’équipe.'); await drawTeam();
    } }]);
  }
  async function showConflicts(op, args, tickets) {
    const body = el('div', { class: 'library-detail' }, el('p', { class: 'help', text: 'Les deux versions restent conservées. Comparez leurs observations avant de relire le ticket ou d’enregistrer une nouvelle contribution.' }));
    const versions = el('div'), pager = el('div', { class: 'library-pagination' }), previousOffsets = []; let offset = 0;
    body.append(versions, pager);
    const documentText = document => tickets ? (document.detail?.sections || []).map(section => `${section.name}\n${(section.data?.rows || []).map(row => row.join('\n')).join('\n\n')}`).join('\n\n') : document.body;
    const reasons = { 'concurrent-edit': 'Modifications concurrentes', 'same-version-different-content': 'Contenus différents pour la même version GLPI',
      'incomparable-source-version': 'Versions GLPI non comparables', 'older-source-version': 'Ancienne version conservée', 'newer-partial-read': 'Nouvelle lecture partielle' };
    async function load() {
    const response = await call(op, { ...args, offset, limit: 5 }), conflicts = Array.isArray(response) ? response : response.items || [];
    versions.replaceChildren();
    for (const conflict of conflicts) versions.append(el('details', { class: 'library-detail-section', open: conflicts.length === 1 },
      el('summary', { text: `${formatDate(conflict.recordedAt)} · ${reasons[conflict.reason] || 'Versions concurrentes'}` }),
      !tickets ? toolbar(button('Comparer tous les champs', () => compareWorkspaceConflict(args.id, conflict.id), { icon: 'table' }),
        button('Exporter les deux versions', () => exportWorkspaceConflict(args.id, conflict.id), { icon: 'download' })) : null,
      el('div', { class: 'library-conflict-pair' }, ...[['Version retenue', conflict.current], ['Version concurrente conservée', conflict.incoming]].map(([label, version]) =>
        el('section', {}, el('h3', { text: label }), el('p', { class: 'help', text: tickets ? `Modification GLPI : ${version.seed?.lastModifiedRaw || 'Non recueillie'} · Lecture : ${formatDate(version.readAt)}` : `${version.author || 'Auteur non précisé'} · ${formatDate(version.updatedAt)}` }),
          el('pre', { class: 'library-detail-text', text: documentText(version) || 'Aucun contenu recueilli.' }),
          button('Copier ce texte', () => call('app.copy', { text: documentText(version) || '' })))))));
    if (!conflicts.length) versions.append(empty('Aucune version concurrente disponible', 'Actualisez la bibliothèque pour retrouver son état courant.'));
    pager.replaceChildren();
    if (!tickets && response.total > conflicts.length) pager.append(
      button('Versions précédentes', async () => { offset = previousOffsets.pop() ?? 0; await load(); }, { disabled: offset === 0 }),
      el('span', { text: `${response.total ? offset + 1 : 0}–${Math.min(response.total, offset + conflicts.length)} sur ${count(response.total)}` }),
      button('Versions suivantes', async () => { previousOffsets.push(offset); offset += conflicts.length; await load(); }, { disabled: !conflicts.length || offset + conflicts.length >= response.total }));
    }
    await load();
    dialog('Versions conservées', body);
  }
  async function exportWorkspaceConflict(id, conflictId) {
    const result = await call('workspace.conflict.export', { id, conflictId });
    if (!result.cancelled) notify('Les deux versions complètes ont été exportées : ' + result.path);
  }
  async function compareWorkspaceConflict(id, conflictId) {
    const review = await call('workspace.conflict.compare', { id, conflictId });
    const comparison = review.comparison, fields = el('div'), pager = el('div', { class: 'library-pagination' }); let offset = 0;
    const search = input('', { type: 'search', placeholder: 'Filtrer les champs ou les valeurs…', 'aria-label': 'Filtrer les différences des versions' });
    const labels = { kind: 'Type', title: 'Titre', body: 'Texte', ticketKey: 'Ticket', assignee: 'Intervenant', status: 'État', author: 'Auteur', deleted: 'Retrait',
      payload: 'Données', installation: 'Installation GLPI', tour: 'Tournée', scenario: 'Répartition', assignments: 'Propositions', technicians: 'Techniciens',
      skills: 'Compétences', sectors: 'Secteurs', capacity: 'Capacité', availability: 'Disponibilités', exceptions: 'Exceptions', estimates: 'Estimations',
      minutes: 'Durée en minutes', start: 'Début', end: 'Fin', date: 'Date', category: 'Catégorie', name: 'Nom', ticket: 'Ticket', notes: 'Notes' };
    const fieldLabel = path => path.split('/').slice(1).map(part => { const key = part.replace(/~1/g, '/').replace(/~0/g, '~'); return labels[key] || key; }).join(' › ');
    const value = (row, side) => {
      const type = row[side + 'Type'], raw = row[side];
      const shown = !type ? 'Champ absent' : type === 'null' ? 'Valeur nulle' : type === 'text' && raw === '' ? 'Texte vide' : type === 'boolean' ? (raw === 'true' ? 'Oui' : 'Non') : raw;
      return el('div', {}, el('pre', { class: 'library-detail-text', text: shown }), row.truncated ? el('span', { class: 'help', text: 'Aperçu limité ; valeur entière dans l’export.' }) : null);
    };
    function paint() {
      const query = search.value.toLocaleLowerCase('fr');
      const rows = comparison.differences.filter(row => (row.path + ' ' + (row.current ?? '') + ' ' + (row.incoming ?? '')).toLocaleLowerCase('fr').includes(query));
      fields.replaceChildren(table(rows.slice(offset, offset + 50), [
        { label: 'Champ', render: row => el('div', {}, el('strong', { text: fieldLabel(row.path) }), el('code', { class: 'library-field-path', text: row.path }), el('span', { class: 'help', text: { added: 'Ajouté', removed: 'Retiré', changed: 'Modifié' }[row.change] || row.change })) },
        { label: 'Version retenue', render: row => value(row, 'current') }, { label: 'Version concurrente', render: row => value(row, 'incoming') }
      ]));
      pager.replaceChildren(button('Différences précédentes', () => { offset = Math.max(0, offset - 50); paint(); }, { disabled: offset === 0 }),
        el('span', { text: `${rows.length ? offset + 1 : 0}–${Math.min(rows.length, offset + 50)} sur ${count(rows.length)}` }),
        button('Différences suivantes', () => { offset += 50; paint(); }, { disabled: offset + 50 >= rows.length }));
    }
    search.addEventListener('input', () => { offset = 0; paint(); }); paint();
    dialog('Comparer les versions de la contribution', el('div', { class: 'library-detail library-version-comparison' },
      el('p', { text: `${comparison.scanComplete ? '' : 'Au moins '}${count(comparison.differencesTotal)} différence(s) dans les champs et données conservés.` }),
      el('p', { class: 'help', text: 'Comparaison en lecture seule des deux versions archivées. Les horaires, affectations, configurations et autres données structurées sont inclus.' }),
      comparison.truncated ? el('p', { class: 'notice', text: 'L’aperçu est limité en nombre de champs ou en longueur. Exportez les deux versions pour examiner leurs contenus entiers.' }) : null,
      search, fields, pager), [{ label: 'Exporter les deux versions complètes', action: () => exportWorkspaceConflict(id, conflictId) }]);
  }
  async function previewConfiguration(id) {
    const review = await call('workspace.configuration.preview', { id });
    const choices = {}, differences = el('div'), pager = el('div'); let offset = 0;
    const paint = () => {
      differences.replaceChildren(table((review.fields || []).slice(offset, offset + 80), [{ key: 'path', label: 'Champ' }, { key: 'current', label: 'Actuel' }, { key: 'incoming', label: 'Partagé' }]));
      pager.replaceChildren(button('Différences précédentes', () => { offset -= 80; paint(); }, { disabled: offset === 0 }),
        el('span', { text: `${Math.min(offset + 1, review.fields?.length || 0)}–${Math.min(offset + 80, review.fields?.length || 0)} sur ${count(review.fields?.length)}` }),
        button('Différences suivantes', () => { offset += 80; paint(); }, { disabled: offset + 80 >= (review.fields?.length || 0) }));
    };
    pager.classList.add('library-pagination');
    const body = el('div', { class: 'library-detail' }, el('p', { class: 'help', text: 'Examinez les sections et leurs différences. Vos choix seront appliqués ensemble après validation.' }),
      table(review.sections || [], [{ key: 'section', label: 'Section' }, { key: 'current', label: 'Actuel' }, { key: 'incoming', label: 'Partagé' }]),
      table(review.conflicts || [], [{ key: 'section', label: 'Collection' }, { key: 'name', label: 'Élément' }, { label: 'Choix', render: conflict => {
        const choice = select([{ value: 'current', label: 'Conserver l’actuel' }, { value: 'incoming', label: 'Prendre le partagé' }, { value: 'both', label: 'Conserver les deux' }]);
        choices[conflict.key] = 'current'; choice.addEventListener('change', () => { choices[conflict.key] = choice.value; }); return choice;
      } }]), differences, pager);
    paint(); dialog('Examiner la configuration partagée', body, [{ label: 'Appliquer les sections partagées', primary: true, action: async close => {
      await call('configuration.apply', { token: review.token, parts: 120, merge: true, choices }); close(); notify('Configuration partagée appliquée.');
    } }]);
  }
  drawSummary(); await drawView();
  try { await refreshCapture(); } catch (error) { if (!signal?.aborted) showError(captureSummary, error); captureSummary.hidden = false; }
  if (ctx.params?.captureResult && !latestCapture) drawOutcome(ctx.params.captureResult);
  return () => { disposed = true; searchVersion++; operation?.abort(); stopProgress(); stopLibrary(); signal?.removeEventListener('abort', abort); if (passwordInput) passwordInput.value = ''; };
}

export const pages = [{ id: 'library', title: 'Bibliothèque de tickets', group: 'Collecter', icon: 'layers', render: library }];
