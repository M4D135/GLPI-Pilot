import { el, icon, button, toolbar, field, input, confirm, formatNumber, formatDate, showError, isSimulation, isFileSource } from '../ui.js';

const activeStates = new Set(['running', 'pausing', 'stopping']);
const labels = { idle: 'Prêt', running: 'Collecte en cours', pausing: 'Mise en pause en cours', stopping: 'Arrêt en cours', paused: 'En pause', stopped: 'Arrêtée', interrupted: 'Interrompue : reprise manuelle', attention: 'À vérifier avant de reprendre', completed: 'Dernier passage terminé', budget: 'Budget atteint : progression conservée' };
const time = value => `${String(Math.floor((value % 1440) / 60)).padStart(2, '0')}:${String(value % 60).padStart(2, '0')}`;
const minute = value => { const parts = value.split(':').map(Number); return parts[0] * 60 + parts[1]; };

export async function renderLibraryCollection(ctx) {
  const { root, api, signal, notify, navigate } = ctx;
  if (isSimulation(ctx.state.session) || isFileSource(ctx.state.session)) {
    root.replaceChildren(el('section', { class: 'card' }, el('h2', { text: 'Collecte GLPI réelle' }), el('p', { text: 'Choisissez une source GLPI active. Les imports CSV/Excel et la simulation ne déclenchent aucune collecte réelle.' }), button('Retour à la bibliothèque', () => navigate('library'))));
    return () => {};
  }
  let disposed = false, pending = false, settingsDirty = false, settingsRevision = 0, activity = {}, model = null, journalOffset = 0, generation = 0, polling = false;
  const call = (op, args = {}) => api.call(op, args, { signal });
  const safe = fn => async (...args) => { try { await fn(...args); } catch (error) { if (!disposed && error?.name !== 'AbortError') showError(errors, error); } };
  const errors = el('div', { class: 'collection-errors', 'aria-live': 'polite' });
  const heading = el('h2', { id: 'collection-state-title', role: 'status', 'aria-live': 'polite' });
  const detail = el('p', { class: 'collection-phase' }), counters = el('p', { class: 'collection-counters' }), next = el('p', { class: 'collection-next' });
  const progress = el('progress', { max: 1, value: 0, 'aria-label': 'Tickets examinés pendant ce passage' });
  const settingsHost = el('div'), journalHost = el('div'), transferHost = el('div');
  const start = controlButton('Mettre à jour', safe(() => collect('incremental')), { primary: true, icon: 'refresh' });
  const resume = controlButton('Reprendre', safe(() => collect('resume')), { primary: true, icon: 'play' });
  const pause = controlButton('Pause', safe(() => control('pause')), { primary: true });
  const stop = controlButton('Arrêter', safe(() => control('stop')));
  const retry = controlButton('Reprendre les erreurs', safe(() => collect('retry')));
  const actions = toolbar(start, resume, pause, stop); actions.classList.add('collection-actions');
  const statusCard = el('section', { class: 'card collection-status', 'aria-label': 'État de la collecte' },
    el('div', { class: 'collection-status-heading' }, el('span', { class: 'collection-state-mark', 'aria-hidden': 'true' }, icon('layers')),
      el('div', {}, el('p', { class: 'collection-eyebrow', text: 'BIBLIOTHÈQUE DE TICKETS' }), heading)),
    detail, actions, progress, counters, next, el('p', { class: 'collection-reassurance', text: 'Pause ou arrêt : les contenus déjà enregistrés restent disponibles.' }));
  const settings = el('details', { class: 'card collection-disclosure' }, el('summary', { text: 'Rythme et programmation' }), settingsHost);
  const journal = el('details', { class: 'card collection-disclosure' }, el('summary', { text: 'Journal et fiches à reprendre' }), journalHost);
  const transfer = el('details', { class: 'card collection-disclosure' }, el('summary', { text: 'Transférer une bibliothèque préparée' }), transferHost);
  const shell = el('section', { class: 'collection-workspace', 'aria-label': 'Collecte des tickets' },
    el('header', { class: 'collection-header' }, button('Bibliothèque', () => navigate('library'), { icon: 'layers' }), el('h1', { text: 'Collecte des tickets' })),
    el('p', { class: 'collection-intro', text: 'Constituez votre bibliothèque une fois, puis actualisez les contenus nouveaux ou modifiés. Une reprise vérifie l’index et conserve les fiches encore valides.' }),
    errors, statusCard, settings, transfer, journal);
  root.replaceChildren(shell);
  function controlButton(label, handler, options) {
    // The generic button restores its pre-click disabled value; collection state
    // is dynamic and must instead be recomputed after the long operation ends.
    const node = button(label, null, options);
    node.addEventListener('click', async () => {
      if (node.disabled) return;
      node.disabled = true; node.setAttribute('aria-busy', 'true');
      try { await handler(); } finally { node.removeAttribute('aria-busy'); if (!disposed) paint(); }
    });
    return node;
  }
  function paint() {
    // A terminal progress event wins over the older database snapshot.
    const running = activeStates.has(activity.state), sameRun = activity.runId && activity.runId === model?.state?.runId;
    const state = running || sameRun ? activity.state : model?.state?.state || 'idle';
    heading.textContent = state === 'completed' && model?.report?.retryCount > 0 ? 'Passage à compléter' : labels[state] || state;
    statusCard.dataset.state = state;
    shell.dataset.collectionState = state;
    // After a failed pass the reason recorded by the engine is shown with the totals, not only the zero counters.
    const reason = !running && state === 'attention' ? model?.report?.limitations?.[0] || '' : '';
    detail.textContent = running ? `${activity.phase || ''}${activity.waitSeconds > 0 ? ` · pause de rythme ${Math.ceil(activity.waitSeconds)} s` : ''}` : [model?.report?.message, reason].filter(Boolean).join(' · ') || 'La programmation est désactivée par défaut ; une pause ou un arrêt reste respecté après redémarrage.';
    const report = model?.report;
    counters.textContent = running ? `${formatNumber(activity.processed || 0)} / ${formatNumber(activity.total || 0)} tickets examinés · ${formatNumber(activity.reused || 0)} contenus réutilisés · ${formatNumber(activity.requests || 0)} navigations déclenchées`
      : report ? `${formatNumber(report.read)} contenus lus · ${formatNumber(report.reused)} réutilisés · ${formatNumber(report.retryCount)} à reprendre ou confirmer` : 'Les contenus sauvegardés sont conservés même en cas d’interruption.';
    progress.hidden = !running; progress.max = Math.max(1, activity.total || 1);
    if (activity.total > 0) progress.value = activity.processed || 0; else progress.removeAttribute('value');
    next.textContent = running ? `Dernière activité : ${formatDate(activity.updatedAt)}` : model?.state?.nextAt ? `Prochain passage au plus tôt : ${formatDate(model.state.nextAt)} · application ouverte et plage autorisée.` : 'Aucune relance automatique programmée.';
    const held = ['paused', 'stopped', 'interrupted', 'attention', 'budget'].includes(state);
    const resumable = held && !!model?.state?.runId;
    start.hidden = running || resumable; resume.hidden = running || !resumable; pause.hidden = !running; stop.hidden = !running;
    start.disabled = pending || running || !model;
    resume.disabled = pending || running || !model?.state?.runId || !held;
    retry.disabled = pending || running || !report?.retryCount;
    pause.disabled = !running || !activity.sessionId || state !== 'running';
    stop.disabled = !running || !activity.sessionId || state === 'stopping';
    settingsHost.inert = running || pending; transferHost.inert = running || pending;
  }
  async function reload() {
    const stamp = ++generation;
    const current = await call('library.collection.status');
    if (disposed || stamp !== generation) return;
    activity = current; paint();
    if (activeStates.has(current.state)) return;
    const data = await call('library.collection.get');
    if (disposed || stamp !== generation) return;
    model = data; activity = data.activity || current; if (!settingsDirty) drawSettings(); drawTransfer(); paint();
    if (journal.open) await loadJournal();
  }
  async function collect(mode) {
    if (pending || activeStates.has(activity.state)) return;
    const missingJournal = mode === 'incremental' && model?.state?.state === 'interrupted' && !model.state.runId;
    if (missingJournal && !await confirm('Reprendre à partir des contenus conservés ?', 'Le journal ancien n’est plus disponible. Un nouvel index sera lu, sans réaspirer les contenus complets toujours valides.', 'Reprendre depuis la bibliothèque')) return;
    pending = true; errors.replaceChildren(); paint();
    try {
      const result = await call('library.capture', { mode, runId: model?.state?.runId || model?.report?.runId || '', confirmedNewCycle: missingJournal });
      if (!disposed) notify(labels[result?.collectionState] || result?.message || 'Point de reprise conservé.', result?.collectionState === 'attention' ? 'warning' : 'info');
    } finally { pending = false; if (!disposed) await reload(); }
  }
  async function control(action) {
    const sessionId = activity.sessionId;
    const result = await call('library.collection.control', { action, sessionId });
    // The terminal event can arrive before the command acknowledgement. Never
    // turn an already paused/stopped session back into an in-progress state.
    if (!disposed) {
      if (result.requested && activity.sessionId === sessionId && activity.state === 'running')
        activity = { ...activity, state: action === 'pause' ? 'pausing' : 'stopping' };
      paint();
    }
  }
  async function idleAction(action) {
    if (pending || activeStates.has(activity.state)) return;
    pending = true; paint();
    try { await action(); } finally { pending = false; if (!disposed) paint(); }
  }
  function drawSettings() {
    const options = model.state.options; settingsRevision = model.state.revision;
    const automatic = el('input', { type: 'checkbox', checked: options.automatic });
    const form = el('form', { class: 'collection-settings-form' });
    const numbers = {};
    const numeric = (key, title, min, max, help) => {
      const control = input(options[key], { type: 'number', min, max, step: 1, required: true }); numbers[key] = control; return field(title, control, help);
    };
    const from = input(time(options.startMinute), { type: 'time', required: true }), to = input(time(options.endMinute), { type: 'time', required: true });
    const zone = input(options.timeZone, { type: 'text', required: true, maxLength: 200 });
    const tuning = el('details', { class: 'collection-tuning' }, el('summary', { text: 'Affiner le rythme et les limites' }),
      el('div', { class: 'collection-fields' },
        numeric('batchSize', 'Navigations par lot', 1, 500), numeric('batchPauseSeconds', 'Pause entre lots (secondes)', 0, 3600),
        numeric('maximumMinutes', 'Budget par passage (minutes)', 1, 1440), numeric('consecutiveErrorLimit', 'Arrêt après erreurs consécutives', 1, 10),
        field('Début du créneau automatique', from), field('Fin du créneau automatique', to), field('Fuseau horaire', zone)));
    form.append(el('p', { text: 'Désignez un seul poste collecteur par périmètre. Les autres postes peuvent importer la bibliothèque et synchroniser le partage sans solliciter GLPI. Ces réglages ne changent pas la fréquence du partage entre postes.' }),
      field('Ce poste effectue les mises à jour automatiques', automatic),
      el('div', { class: 'collection-fields' },
        numeric('intervalMinutes', 'Intervalle entre passages (minutes)', 5, 10080),
        numeric('delayMilliseconds', 'Délai minimal entre navigations (millisecondes)', 1000, 60000, '3 000 ms = 3 secondes. Une seule lecture à la fois ; ce délai ne limite pas les sous-requêtes HTTP du navigateur.'),
        ), tuning,
      el('p', { text: '00:00 à 00:00 autorise toute la journée. La programmation nécessite GLPI Pilot ouvert. Pause et Arrêter bloquent toute relance jusqu’à une reprise explicite.' }),
      button('Enregistrer le rythme', safe(async () => {
        if (!form.reportValidity()) return;
        if (automatic.checked && !await confirm('Activer la collecte sur ce poste ?', 'Confirmez que ce poste est le collecteur désigné pour le périmètre GLPI actuel. La première exécution est différée ; aucune collecte ne démarre par cette confirmation.', 'Activer la programmation')) return;
        await call('library.collection.settings', { revision: settingsRevision, confirmedCollector: automatic.checked,
          options: { ...options, ...Object.fromEntries(Object.entries(numbers).map(([key, value]) => [key, Number(value.value)])), automatic: automatic.checked, startMinute: minute(from.value), endMinute: minute(to.value) || 1440, timeZone: zone.value } });
        settingsDirty = false; await reload(); notify('Rythme enregistré. Les réglages restent propres à ce poste et à cette source.');
      }), { primary: true }));
    form.addEventListener('input', () => { settingsDirty = true; });
    form.addEventListener('submit', event => event.preventDefault()); settingsHost.replaceChildren(form);
  }
  function drawTransfer() {
    transferHost.replaceChildren(el('p', { text: 'Paquet interne non chiffré, réservé aux destinataires habilités. Il peut contenir des suivis privés. Aucun cookie SSO, mot de passe, réglage personnel ni programmation de collecte n’est transféré. Ne pas joindre ce paquet aux releases publiques.' }), toolbar(
      button('Exporter la bibliothèque préparée', safe(() => idleAction(async () => {
        if (!await confirm('Exporter des contenus internes ?', 'Confirmez que les destinataires sont autorisés à consulter tous les contenus du périmètre exporté, y compris les suivis privés.', 'Exporter pour un usage interne')) return;
        const result = await call('library.bootstrap.export', { confirmedInternalUse: true }); if (!result.cancelled) notify(result.message);
      })), { icon: 'download' }),
      button('Importer une bibliothèque préparée', safe(() => idleAction(async () => {
        const result = await call('library.bootstrap.preview'); if (result.cancelled) return;
        const p = result.preview;
        try {
          if (!await confirm('Importer cette bibliothèque ?', `${formatNumber(p.manifest.records)} tickets · ${formatNumber(p.complete)} fiches complètes · export du ${formatDate(p.manifest.exportedAt)}.\nInstallation : ${p.manifest.installation}\n${result.warning}\nConfirmez votre habilitation et la fusion avec vos données locales. Aucune suppression ni collecte GLPI n’est lancée.`, 'Importer et conserver les conflits')) return;
          const imported = await call('library.bootstrap.apply', { token: result.token, confirmedAuthorized: true, confirmedMerge: true });
          notify(`${formatNumber(imported.added)} ajoutés · ${formatNumber(imported.identical)} identiques · ${formatNumber(imported.conflicts)} conflits conservés.`); await reload();
        } finally { if (!signal?.aborted) await call('library.bootstrap.cancel'); }
      })), { icon: 'layers' })));
  }
  journalHost.before(el('div', { class: 'collection-retry-action' }, retry));
  async function loadJournal() {
    const runId = model?.state?.runId; if (!runId || activeStates.has(activity.state)) return;
    const page = await call('library.capture.entries', { runId, offset: journalOffset, limit: 25, retryOnly: true }); if (disposed) return;
    journalHost.replaceChildren(el('p', { text: `${formatNumber(page.total)} fiches à reprendre ou confirmer. Aucun ticket absent de la liste n’est supprimé automatiquement.` }),
      ...page.items.map(item => el('p', { text: `#${item.ticketId} · ${item.stateLabel} · ${item.message}` })),
      toolbar(button('Précédent', safe(async () => { journalOffset = Math.max(0, journalOffset - 25); await loadJournal(); }), { disabled: journalOffset === 0 }),
        button('Suivant', safe(async () => { journalOffset += 25; await loadJournal(); }), { disabled: journalOffset + page.items.length >= page.total })));
  }
  journal.addEventListener('toggle', () => { if (journal.open) void safe(loadJournal)(); });
  const unsubscribe = api.on('library.collection.progress', event => { if (!disposed) { activity = event; paint(); } });
  const timer = setInterval(() => {
    if (polling || disposed || document.visibilityState === 'hidden') return;
    polling = true;
    void call('library.collection.status').then(data => {
      if (disposed) return; const wasActive = activeStates.has(activity.state); activity = data; paint();
      if (wasActive && !activeStates.has(data.state) && !pending) return reload();
    }).catch(error => { if (!disposed && error?.name !== 'AbortError') detail.textContent = 'État de la collecte non confirmé. Aucun nouvel envoi demandé.'; }).finally(() => { polling = false; });
  }, 2000);
  const dispose = () => { if (disposed) return; disposed = true; generation++; clearInterval(timer); unsubscribe?.(); };
  signal?.addEventListener('abort', dispose, { once: true });
  paint();
  try { await reload(); } catch (error) { if (!disposed) showError(errors, error); }
  return () => { dispose(); signal?.removeEventListener('abort', dispose); };
}
