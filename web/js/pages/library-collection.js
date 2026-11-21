import { el, icon, button, toolbar, field, input, confirm, formatNumber, formatDate, showError, isSimulation, isFileSource } from '../ui.js';
import { createCollectionScope } from '../collection-scope.js';
import { openLibraryTicket } from '../library-ticket.js';
import { collectionQuality, collectionResultLabel, collectionQualityCounts } from '../collection-quality.js';

const activeStates = new Set(['running', 'pausing', 'stopping']);
const heldStates = new Set(['paused', 'stopped', 'interrupted', 'attention', 'budget']);
const labels = { idle: 'Prêt', running: 'Collecte en cours', pausing: 'Mise en pause en cours', stopping: 'Arrêt en cours', paused: 'En pause', stopped: 'Arrêtée', interrupted: 'Interrompue : reprise manuelle', attention: 'À vérifier avant de reprendre', completed: 'Dernier passage terminé', budget: 'Budget atteint : progression conservée' };
const time = value => `${String(Math.floor((value % 1440) / 60)).padStart(2, '0')}:${String(value % 60).padStart(2, '0')}`;
const minute = value => { const parts = value.split(':').map(Number); return parts[0] * 60 + parts[1]; };
const eventLevels = { info: 'Information', warning: 'Avertissement', error: 'Erreur' };
const journalPageSize = 25;
const eventTime = value => {
  const date = new Date(value);
  return value && !Number.isNaN(date.getTime()) ? new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'medium' }).format(date) : 'Date non disponible';
};

export async function renderLibraryCollection(ctx) {
  const { root, api, signal, notify, navigate } = ctx;
  const offline = ctx.state.session?.cachedSource === true;
  if (isSimulation(ctx.state.session) || isFileSource(ctx.state.session)) {
    root.replaceChildren(el('section', { class: 'card' }, el('h2', { text: 'Collecte GLPI réelle' }), el('p', { text: 'Choisissez une source GLPI active. Les imports CSV/Excel et la simulation ne déclenchent aucune collecte réelle.' }), button('Retour à la bibliothèque', () => navigate('library'))));
    return () => {};
  }
  let disposed = false, pending = false, settingsDirty = false, settingsRevision = 0, activity = {}, activityRevision = 0, model = null, generation = 0, polling = false, newScope = false;
  let journalOffset = 0, journalIdentity = '', journalGeneration = 0, journalRequest = null, journalPage = null, journalOpenedFor = '';
  let retryOffset = 0, retryGeneration = 0, retriesOpenedFor = '', qualityRenderKey = '';
  const call = (op, args = {}) => api.call(op, args, { signal });
  const reportError = error => {
    if (disposed || error?.name === 'AbortError') return;
    showError(errors, error);
    // A retry can be clicked far below the status card. Make its failure visible
    // instead of leaving an apparently inert control below an off-screen error.
    errors.scrollIntoView?.({ block: 'nearest' });
  };
  const safe = fn => async (...args) => { try { await fn(...args); } catch (error) { reportError(error); } };
  const errors = el('div', { class: 'collection-errors', 'aria-live': 'polite' });
  const scope = createCollectionScope({
    preview: selected => call('library.collection.preview', { scope: selected }),
    search: args => call('library.collection.scope', args),
    onDateSettings: () => navigate('library', { view: 'connection', focus: 'source-dates' }),
    onChange: () => { if (!disposed) paint(); },
    onError: reportError
  });
  const modelError = el('p', { class: 'notice warning collection-model-error', role: 'status', 'aria-live': 'polite', hidden: true,
    text: 'Le bilan et les réglages locaux de collecte n’ont pas pu être chargés. Le journal de cette tentative reste consultable. Vérifiez le dossier local de la bibliothèque avant de réessayer.' });
  const heading = el('h2', { id: 'collection-state-title', role: 'status', 'aria-live': 'polite' });
  const detail = el('p', { class: 'collection-phase' }), counters = el('p', { class: 'collection-counters' }), next = el('p', { class: 'collection-next' });
  const recovery = el('p', { class: 'collection-recovery', hidden: true });
  const qualityHost = el('section', { class: 'collection-quality', 'aria-label': 'Qualité du passage', hidden: true });
  const progress = el('progress', { max: 1, value: 0, 'aria-label': 'Tickets examinés pendant ce passage' });
  const settingsHost = el('div'), transferHost = el('div'), retryHost = el('div');
  const journalCount = el('p', { class: 'collection-journal-count' });
  const journalStatus = el('p', { class: 'collection-journal-status', role: 'status', 'aria-live': 'polite' });
  const journalError = el('p', { class: 'collection-journal-error', role: 'status', 'aria-live': 'polite', hidden: true });
  const journalEmpty = el('p', { class: 'collection-journal-empty', text: 'Aucune collecte enregistrée pour cette source.' });
  const journalRows = el('ol', { class: 'collection-journal-rows', 'aria-label': 'Événements de la collecte, du plus récent au plus ancien', hidden: true });
  const journalReport = el('div', { class: 'collection-journal-report', hidden: true });
  const newerEvents = controlButton('Plus récents', safe(async () => { journalOffset = Math.max(0, journalOffset - journalPageSize); await loadJournal(); }), { disabled: true });
  const olderEvents = controlButton('Plus anciens', safe(async () => { journalOffset += journalPageSize; await loadJournal(); }), { disabled: true });
  const latestEvents = controlButton('Revenir au suivi', safe(async () => { journalOffset = 0; await loadJournal(); }), { attributes: { hidden: true } });
  const refreshEvents = controlButton('Actualiser le journal', safe(() => loadJournal()), { icon: 'refresh' });
  const journalNavigation = toolbar(newerEvents, olderEvents, latestEvents); journalNavigation.classList.add('collection-journal-navigation'); journalNavigation.hidden = true;
  const journalHost = el('div', { class: 'collection-journal' },
    el('p', { class: 'collection-journal-intro', text: 'Les étapes de lecture, avertissements et interruptions de ce passage sont présentés du plus récent au plus ancien.' }),
    journalCount, journalStatus, journalError, toolbar(refreshEvents), journalEmpty, journalRows, journalNavigation, journalReport);
  const start = controlButton('Mettre à jour', safe(() => collect('incremental')), { primary: true, icon: 'refresh' });
  const resume = controlButton('Reprendre', safe(() => collect('resume')), { primary: true, icon: 'play' });
  const retryCompleted = controlButton('Reprendre les fiches à vérifier', safe(() => collect('retry')), { primary: true, icon: 'refresh' });
  const pause = controlButton('Pause', safe(() => control('pause')), { primary: true });
  const stop = controlButton('Arrêter', safe(() => control('stop')));
  const retry = controlButton('Reprendre les erreurs', safe(() => collect('retry')));
  const missing = controlButton('Compléter seulement les contenus absents', safe(() => collect('retry', { missingOnly: true })), { icon: 'download' });
  const localStatus = el('p', { class: 'collection-local-status', role: 'status', 'aria-live': 'polite' });
  const localContext = el('p', { class: 'help collection-local-context' });
  const localMetrics = el('div', { class: 'collection-scope-metrics' });
  const inspectLocal = controlButton('Vérifier les contenus conservés', safe(async () => {
    if (pending || activeStates.has(activity.state)) return;
    pending = true; errors.replaceChildren(); localStatus.textContent = 'Vérification de la bibliothèque sur ce poste…'; paint();
    try {
      const refreshed = await reload();
      if (!disposed && !modelError.hidden) throw new Error('La vérification locale n’a pas abouti. Les contenus existants sont conservés.');
      if (!disposed && refreshed && model?.availability?.runId && model.availability.runId === collectionView().runId)
        notify('Vérification locale terminée. Aucune lecture GLPI et aucune modification des contenus.');
      else if (!disposed && refreshed) notify('Aucun bilan de passage conservé pour cette source. Utilisez « Consulter les contenus » pour explorer la bibliothèque locale.');
      else if (!disposed && activeStates.has(activity.state)) notify('Une collecte est en cours. La vérification locale pourra être relancée à la fin du passage.');
    } finally { pending = false; if (!disposed) paint(); }
  }), { primary: true, icon: 'shield' });
  const localCard = el('section', { class: 'card collection-local', 'aria-label': 'Contenus conservés sur ce poste' },
    el('h2', { text: 'Contenus conservés sur ce poste' }),
    el('p', { text: 'Les fiches déjà enregistrées restent lisibles, même si leur date ou leur exhaustivité est à confirmer. Cette vérification recense les contenus présents dans la base locale, sans les comparer à GLPI.' }),
    toolbar(inspectLocal, button('Consulter les contenus', () => navigate('library'), { icon: 'ticket' })), localContext, localStatus, localMetrics);
  const launchHint = el('p', { class: 'help collection-launch-hint', role: 'status' });
  const prepare = controlButton('Préparer un autre périmètre', safe(async () => { newScope = true; scope.beginDraft(); paint(); }));
  const returnToRun = controlButton('Revenir à la reprise', safe(async () => { newScope = false; scope.restoreSaved(); paint(); }));
  const actions = toolbar(start, resume, retryCompleted, pause, stop, prepare, returnToRun); actions.classList.add('collection-actions');
  const statusCard = el('section', { class: 'card collection-status', 'aria-label': 'État de la collecte' },
    el('div', { class: 'collection-status-heading' }, el('span', { class: 'collection-state-mark', 'aria-hidden': 'true' }, icon('layers')),
      el('div', {}, el('p', { class: 'collection-eyebrow', text: 'BIBLIOTHÈQUE DE TICKETS' }), heading)),
    detail, actions, launchHint, progress, counters, qualityHost, recovery, next, el('p', { class: 'collection-reassurance', text: 'Pause ou arrêt : les contenus déjà enregistrés restent disponibles.' }));
  const settings = el('details', { class: 'card collection-disclosure' }, el('summary', { text: 'Rythme et programmation' }), settingsHost);
  scope.element.hidden = offline;
  settings.hidden = offline;
  const journal = el('details', { class: 'card collection-disclosure', dataset: { collectionPanel: 'journal' } }, el('summary', { text: 'Journal de la collecte' }), journalHost);
  const retries = el('details', { class: 'card collection-disclosure', dataset: { collectionPanel: 'retry' } }, el('summary', { text: 'Fiches à reprendre' }),
    el('div', {}, el('p', { class: 'collection-retry-intro', text: 'Cette liste concerne les tickets déjà identifiés dont le contenu doit être relu ou confirmé. Les erreurs de préparation et de lecture de l’index figurent dans le journal de la collecte.' }),
      el('div', { class: 'collection-retry-action' }, toolbar(missing, retry)),
      el('p', { class: 'help collection-retry-help', text: offline
        ? 'Le cache est consulté hors connexion. Vous pouvez lire les contenus conservés ci-dessous ; une reprise en ligne nécessite une source GLPI active.'
        : 'Compléter les contenus absents conserve toutes les fiches déjà lues. « Reprendre les erreurs » peut aussi relire les fiches partielles ou dont la version reste à confirmer.' }), retryHost));
  const transfer = el('details', { class: 'card collection-disclosure' }, el('summary', { text: 'Transférer une bibliothèque préparée' }), transferHost);
  const shell = el('section', { class: 'collection-workspace', 'aria-label': 'Collecte des tickets' },
    el('header', { class: 'collection-header' }, button('Bibliothèque', () => navigate('library'), { icon: 'layers' }), el('h1', { text: 'Collecte des tickets' })),
    el('p', { class: 'collection-intro', text: 'Choisir les tickets à partir du relevé chargé, vérifier le volume, puis collecter seulement les contenus manquants ou à actualiser.' }),
    errors, modelError, localCard, scope.element, statusCard, journal, retries, settings, transfer);
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
    // Activity is contextualized by the engine. A terminal event also wins when
    // preparation failed before a new durable run could replace the old one.
    const { running, state, report, resumeId, runId } = collectionView();
    const readiness = report ? model?.readiness : null;
    const reportCompleted = state === 'completed' || state === 'attention' && report?.status === 'completed';
    const qualityReport = report ? { ...report, collectionState: state } : null;
    const quality = collectionQuality(qualityReport, reportCompleted, readiness);
    heading.textContent = reportCompleted ? collectionResultLabel(qualityReport, true, readiness) : labels[state] || state;
    statusCard.dataset.state = state;
    statusCard.dataset.quality = quality.needsAttention ? quality.state : '';
    shell.dataset.collectionState = state;
    // After a failed pass the reason recorded by the engine is shown with the totals, not only the zero counters.
    const reason = !running && state === 'attention' ? activity.message || report?.limitations?.[0] || '' : '';
    detail.textContent = running ? `${activity.phase || ''}${activity.waitSeconds > 0 ? ` · pause de rythme ${Math.ceil(activity.waitSeconds)} s` : ''}`
      : reportCompleted && quality.needsAttention
        ? quality.state === 'incomplete' ? 'Le passage est terminé, mais des contenus ou leur version restent à vérifier. Les contenus enregistrés sont conservés.'
          : 'Le bilan disponible ne suffit pas à confirmer la qualité de ce passage. Les contenus enregistrés sont conservés.'
        : [...new Set([report?.message, reason || activity.message].filter(Boolean))].join(' · ') || 'La programmation est désactivée par défaut ; une pause ou un arrêt reste respecté après redémarrage.';
    counters.textContent = running ? `${formatNumber(activity.processed || 0)} / ${formatNumber(activity.total || 0)} tickets examinés · ${formatNumber(activity.reused || 0)} contenus réutilisés · ${formatNumber(activity.requests || 0)} navigations déclenchées`
      : report ? `${formatNumber(report.read)} contenus lus · ${formatNumber(report.reused)} réutilisés · ${formatNumber(report.retryCount)} à reprendre ou confirmer` : activity.sessionId ? `${formatNumber(activity.processed || 0)} tickets examinés pendant cette tentative · ${formatNumber(activity.requests || 0)} navigations déclenchées` : 'Les contenus sauvegardés sont conservés même en cas d’interruption.';
    progress.hidden = !running; progress.max = Math.max(1, activity.total || 1);
    if (activity.total > 0) progress.value = activity.processed || 0; else progress.removeAttribute('value');
    next.textContent = running ? `Dernière activité : ${formatDate(activity.updatedAt)}`
      : model?.automaticBlocked ? model.automaticBlockReason || 'Programmation suspendue : lancer une première collecte ciblée pour enregistrer le périmètre.'
        : model?.state?.nextAt ? `Prochain passage au plus tôt : ${formatDate(model.state.nextAt)} · application ouverte et plage autorisée.` : 'Aucune relance automatique programmée.';
    const held = heldStates.has(state), resumable = held && !!resumeId;
    const completedHeld = state === 'attention' && report?.status === 'completed';
    recovery.hidden = !held;
    recovery.textContent = completedHeld
      ? report.retryCount > 0
        ? 'Le passage a terminé ses tentatives. Examinez les motifs puis reprenez les seules fiches à vérifier ; le point de fin du passage ne relit pas ces erreurs.'
        : 'Le passage est terminé mais sa qualité reste à vérifier. Aucun ticket n’est signalé comme réessayable dans son journal. Examinez les motifs et les versions conservées avant de préparer un autre périmètre.'
      : resumable
      ? `${['attention', 'interrupted'].includes(state) ? 'Vérifier la cause de l’arrêt dans le journal avant de reprendre. ' : ''}${model?.scope?.enabled || model?.state?.scope?.enabled ? 'La reprise conserve les tickets du périmètre et les contenus déjà enregistrés.' : 'La reprise vérifie l’index et conserve les contenus valides.'}`
      : 'Vérifier le journal avant de relancer une collecte. Les contenus déjà enregistrés restent disponibles.';
    start.hidden = running || resumable && !newScope; resume.hidden = running || !resumable || newScope || completedHeld;
    retryCompleted.hidden = running || !resumable || newScope || !completedHeld || !(report?.retryCount > 0);
    pause.hidden = !running; stop.hidden = !running;
    prepare.hidden = offline || running || !resumable || newScope;
    returnToRun.hidden = offline || running || !resumable || !newScope;
    const liveCoverage = activity.runId && activity.runId === runId ? activity.coverage : null;
    const storedCoverage = runId && model?.state?.runId === runId ? model?.coverage : null;
    scope.setState({ locked: offline || running || pending || resumable && !newScope, held: resumable && !newScope,
      coverage: running || resumable && !newScope ? liveCoverage || storedCoverage || null : null,
      preflight: (running || resumable && !newScope) && activity.sessionId ? activity.preflight : null,
      coverageVerifiedAt: liveCoverage ? activity.coverageVerifiedAt : storedCoverage ? model?.inventory?.verifiedAt : null });
    start.disabled = offline || pending || running || !model || !scope.ready();
    prepare.disabled = pending || running;
    returnToRun.disabled = pending || running;
    resume.disabled = offline || pending || running || !resumeId || !held || completedHeld;
    retryCompleted.disabled = offline || pending || running || !resumeId || !completedHeld || !report?.retryCount;
    retry.disabled = offline || pending || running || !resumeId || !report?.retryCount;
    missing.disabled = offline || pending || running || !resumeId || !report?.retryCount || !model?.availability?.missing;
    inspectLocal.disabled = pending || running;
    launchHint.textContent = offline
      ? 'Cache local : la vérification et la lecture des contenus conservés sont disponibles sans recollecte. Les commandes de collecte en ligne sont désactivées dans cette vue.'
      : !running && !resumable && !scope.ready()
        ? 'Prévisualisez le périmètre avant de lancer une nouvelle collecte. Pour conserver les fiches déjà lues, utilisez la vérification locale ou le complément des seuls contenus absents.' : '';
    launchHint.hidden = !launchHint.textContent;
    for (const control of [start, resume, retryCompleted, retry, missing]) control.title = offline ? 'Une source GLPI active est nécessaire pour lire en ligne. Les contenus locaux restent consultables.' : '';
    if (!offline && ['blocked', 'warning'].includes(scope.preflightState())) for (const control of [start, resume, retryCompleted, retry, missing])
      control.title = 'L’index GLPI sera vérifié à nouveau avant toute lecture de fiche. Les dates d’un ancien relevé ne confirment pas son état actuel.';
    if (start.disabled && !offline && !pending && !running) start.title = 'Prévisualiser le périmètre avant de démarrer.';
    pause.disabled = !running || !activity.sessionId || state !== 'running';
    stop.disabled = !running || !activity.sessionId || state === 'stopping';
    settingsHost.inert = offline || running || pending; transferHost.inert = running || pending;
    drawLocalAvailability();
    drawQuality(report, quality, running, readiness);
    syncJournalIdentity(); paintJournalControls();
    const openFor = `${journalIdentity}:${running ? 'active' : state}`;
    if ((running || ['attention', 'interrupted'].includes(state) || quality.needsAttention) && journalOpenedFor !== openFor) {
      journalOpenedFor = openFor; journal.open = true;
    }
    if (!running && quality.needsAttention && report && runId && retriesOpenedFor !== runId
      && (report.retryCount > 0 || collectionQualityCounts(report).some(item => item.value > 0)
        || readiness?.available === true && readiness.isReady === false)) {
      retriesOpenedFor = runId; retries.open = true;
    }
    if (running && retries.open) retryHost.replaceChildren(el('p', { text: 'La liste des fiches sera actualisée à la fin du passage. Le journal reste consultable pendant la collecte.' }));
  }
  function drawQuality(report, quality, running, readiness) {
    const renderKey = JSON.stringify([running, report?.runId, report?.finalScopeVerified, report?.limitations,
      collectionQualityCounts(report), quality, readiness]);
    if (renderKey === qualityRenderKey) return;
    qualityRenderKey = renderKey;
    qualityHost.hidden = running || !report;
    qualityHost.replaceChildren();
    if (qualityHost.hidden) return;
    const metrics = el('div', { class: 'collection-scope-metrics' }, ...collectionQualityCounts(report).map(item =>
      el('div', { class: 'collection-scope-metric' }, el('strong', { text: item.value == null ? '—' : formatNumber(item.value) }), el('span', { text: item.label }))));
    qualityHost.append(el('h3', { text: 'Qualité du passage' }), metrics,
      el('p', { class: 'help', text: 'Les tickets examinés et les contenus lus mesurent le travail effectué. Ils ne prouvent pas que les contenus sont complets et confirmés. Les motifs peuvent concerner une même fiche.' }));
    if (readiness?.available === true) {
      const count = value => Number.isSafeInteger(value) && value >= 0 ? formatNumber(value) : '—';
      const readyHost = el('div', { class: 'collection-readiness' },
        el('h3', { text: 'Contenus conservés complets et confirmés' }),
        el('p', { class: 'collection-readiness-count', text: `${count(readiness.eligibleTickets)} / ${count(readiness.tickets)} fiches` }),
        el('div', { class: 'collection-scope-metrics' }, ...[['incomplete', 'Contenu incomplet'], ['missingContent', 'Sans contenu'], ['unconfirmed', 'Version non confirmée'], ['conflicts', 'Avec conflit']]
          .map(([key, label]) => el('div', { class: 'collection-scope-metric' }, el('strong', { text: count(readiness[key]) }), el('span', { text: label })))),
        el('p', { class: 'help', text: 'Ce bilan vérifie les fiches actuellement conservées selon le contrat de données GLPI-Pilot. Relais vérifie ensuite l’admissibilité de chaque fragment. Les motifs ci-dessus peuvent se recouper.' }));
      qualityHost.append(readyHost);
    }
    if (report.finalScopeVerified !== true) qualityHost.append(el('p', { class: 'notice warning', text: 'Le contrôle final du périmètre n’est pas confirmé.' }));
    if (quality.needsAttention) {
      const limits = Array.isArray(report.limitations) ? report.limitations.filter(value => typeof value === 'string') : [];
      if (limits.length) qualityHost.append(el('ul', { class: 'collection-quality-reasons' }, ...limits.map(value => el('li', { text: value }))));
      qualityHost.append(el('p', { class: 'help', text: 'Consultez les motifs des fiches avant de relancer. Relire des contenus ne corrige pas un format de date, un fuseau ou un contrôle final manquant.' }),
        toolbar(button('Voir les motifs des fiches', () => { if (retries.open) void safe(loadRetries)(); else retries.open = true; retries.scrollIntoView?.({ block: 'nearest' }); }),
          !offline ? button('Régler les dates GLPI', () => navigate('library', { view: 'connection', focus: 'source-dates' }), { icon: 'settings' }) : null));
    }
  }
  function drawLocalAvailability() {
    const availability = model?.availability;
    localContext.textContent = model?.offlineSavedRun ? model.message || '' : model?.installation ? `Bilan du passage pour ${model.installation}.` : '';
    localContext.hidden = !localContext.textContent;
    if (!availability || availability.runId !== collectionView().runId) {
      localMetrics.replaceChildren();
      if (!pending) localStatus.textContent = modelError.hidden
        ? 'Aucun bilan local lié à ce passage. La bibliothèque reste consultable depuis « Consulter les contenus ».'
        : 'La vérification locale n’a pas abouti. Les contenus existants sont conservés.';
      return;
    }
    const metric = (label, value) => el('div', { class: 'collection-scope-metric' }, el('strong', { text: formatNumber(value || 0) }), el('span', { text: label }));
    localMetrics.replaceChildren(metric('Fiches du passage', availability.observed), metric('Contenus conservés', availability.stored),
      metric('Complets conservés', availability.complete), metric('Partiels conservés', availability.partial), metric('Sans contenu', availability.missing),
      metric('Version à confirmer', availability.unconfirmed), ...(Number.isSafeInteger(availability.confirmed) && availability.confirmed >= 0 ? [metric('Versions confirmées', availability.confirmed)] : []), ...(availability.archived ? [metric('Copie archivée', availability.archived)] : []),
      ...(availability.invalid ? [metric('Copie à examiner', availability.invalid)] : []));
    localStatus.textContent = `Vérification locale du ${formatDate(availability.checkedAt)} · ${formatNumber(availability.stored)} contenus présents dans la base locale. Leur consultation ne nécessite aucune nouvelle collecte. La fraîcheur GLPI reste indiquée séparément.`;
  }
  function collectionView() {
    const running = activeStates.has(activity.state), live = !!activity.sessionId && activity.state !== 'idle';
    const state = running || live ? activity.state : model?.state?.state || 'idle';
    const runId = running || live ? activity.runId || '' : model?.state?.runId || '';
    const report = runId && model?.report?.runId === runId ? model.report : null;
    const resumeId = runId || (heldStates.has(model?.state?.state) ? model?.state?.runId || '' : '');
    return { running, state, runId, report, resumeId };
  }
  async function reload() {
    const stamp = ++generation, revision = activityRevision;
    const current = await call('library.collection.status');
    if (disposed || stamp !== generation) return false;
    if (revision === activityRevision) { activity = current; activityRevision++; } paint();
    if (activeStates.has(activity.state)) { if (journal.open) await loadJournal(); return false; }
    const modelActivityRevision = activityRevision;
    let data;
    try { data = await call('library.collection.get'); }
    catch (error) {
      if (disposed || stamp !== generation || error?.name === 'AbortError') return false;
      // Preparation failures can leave only the in-memory activity and events.
      // A broken local store must not prevent those diagnostics from loading or
      // expose the raw storage exception in the collection panel.
      modelError.hidden = false;
      if (journal.open) await loadJournal();
      return false;
    }
    if (disposed || stamp !== generation) return false;
    modelError.hidden = true;
    model = data; if (modelActivityRevision === activityRevision) { activity = data.activity || current; activityRevision++; }
    scope.setModel(data, { preserveSaved: heldStates.has(collectionView().state) && !newScope });
    if (!settingsDirty) drawSettings(); drawTransfer(); paint(); drawJournalReport();
    if (journal.open) await loadJournal();
    if (retries.open) await loadRetries();
    return !disposed && stamp === generation && !activeStates.has(activity.state);
  }
  async function collect(mode, options = {}) {
    if (offline || pending || activeStates.has(activity.state)) return;
    const scopeArgs = mode === 'incremental' ? scope.args() : {};
    if (!scopeArgs) return;
    const missingJournal = mode === 'incremental' && model?.state?.state === 'interrupted' && !model.state.runId;
    if (missingJournal && !await confirm('Reprendre à partir des contenus conservés ?', 'Le journal ancien n’est plus disponible. Un nouvel index sera lu, sans réaspirer les contenus complets toujours valides.', 'Reprendre depuis la bibliothèque')) return;
    pending = true; errors.replaceChildren(); journalOffset = 0; journal.open = true; paint();
    try {
      const result = await call('library.capture', { mode, runId: mode === 'incremental' ? '' : collectionView().resumeId || '',
        confirmedNewCycle: missingJournal || newScope, ...scopeArgs, ...options });
      newScope = false;
      if (!disposed) {
        const completed = result?.collectionState === 'completed' || result?.status === 'completed';
        const quality = collectionQuality(result, completed, result?.readiness);
        const counts = collectionQualityCounts(result).filter(item => item.value > 0).map(item => `${formatNumber(item.value)} ${item.label.toLocaleLowerCase('fr-FR')}`);
        const readyText = result?.readiness?.available === true && Number.isSafeInteger(result.readiness.eligibleTickets) && Number.isSafeInteger(result.readiness.tickets)
          && result.readiness.eligibleTickets >= 0 && result.readiness.tickets >= result.readiness.eligibleTickets
          ? `${formatNumber(result.readiness.eligibleTickets)} / ${formatNumber(result.readiness.tickets)} fiches complètes et confirmées` : '';
        const detail = [readyText, ...counts].filter(Boolean).join(' · ');
        const message = completed ? `${collectionResultLabel(result, true, result?.readiness)}${detail ? ` · ${detail}` : ''}.`
          : labels[result?.collectionState] || result?.message || 'Point de reprise conservé.';
        notify(message, quality.needsAttention || result?.collectionState === 'attention' ? 'warning' : 'info');
      }
    } catch (error) {
      if (mode === 'incremental') scope.invalidatePreview();
      throw error;
    } finally { pending = false; if (!disposed) await reload(); }
  }
  async function control(action) {
    const sessionId = activity.sessionId;
    const result = await call('library.collection.control', { action, sessionId });
    // The terminal event can arrive before the command acknowledgement. Never
    // turn an already paused/stopped session back into an in-progress state.
    if (!disposed) {
      if (result.requested && activity.sessionId === sessionId && activity.state === 'running') {
        activity = { ...activity, state: action === 'pause' ? 'pausing' : 'stopping' }; activityRevision++;
      }
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
    const canSchedule = model?.state?.scope?.enabled === true && !!model?.state?.runId;
    const automatic = el('input', { type: 'checkbox', checked: options.automatic, disabled: !canSchedule && !options.automatic });
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
        field('Début du créneau automatique', from), field('Fin du créneau automatique', to), field('Fuseau du créneau automatique', zone, 'Ce fuseau règle la programmation. Le fuseau et le format des dates affichées dans GLPI se règlent dans la bibliothèque.')));
    form.append(el('p', { text: 'Désignez un seul poste collecteur par périmètre. Les autres postes peuvent importer la bibliothèque et synchroniser le partage sans solliciter GLPI. Ces réglages ne changent pas la fréquence du partage entre postes.' }),
      field('Ce poste effectue les mises à jour automatiques', automatic, canSchedule
        ? 'La programmation utilise le dernier périmètre lancé. Modifier les filtres de l’aperçu ne change pas ce périmètre.'
        : 'Lancer une première collecte ciblée pour enregistrer son périmètre avant de programmer les suivantes.'),
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
          if (!await confirm('Importer cette bibliothèque ?', `${formatNumber(p.manifest.records)} tickets · ${formatNumber(p.complete)} fiches complètes · export du ${formatDate(p.manifest.exportedAt)}.\nInstallation : ${p.manifest.installation}\n${result.warning}\nConfirmer l’habilitation et la fusion avec les données locales. Aucune suppression ni collecte GLPI n’est lancée.`, 'Importer et conserver les conflits')) return;
          const imported = await call('library.bootstrap.apply', { token: result.token, confirmedAuthorized: true, confirmedMerge: true });
          notify(`${formatNumber(imported.added)} ajoutés · ${formatNumber(imported.identical)} identiques · ${formatNumber(imported.conflicts)} conflits conservés.`); await reload();
        } finally { if (!signal?.aborted) await call('library.bootstrap.cancel'); }
      })), { icon: 'layers' })));
  }
  function syncJournalIdentity() {
    const { runId } = collectionView();
    // A resumed durable run gets a new session. Its previous in-flight journal
    // response must not replace events from the resumed attempt.
    const identity = runId ? `${runId}:${activity.sessionId || 'saved'}` : activity.sessionId || '';
    if (identity === journalIdentity) return;
    journalIdentity = identity; journalOffset = 0; journalGeneration++; journalRequest = null; journalPage = null;
    retryOffset = 0; retryGeneration++;
    journalRows.replaceChildren(); journalRows.hidden = true; journalCount.textContent = '';
    journalError.hidden = true; journalEmpty.hidden = false;
    journalEmpty.textContent = 'Chargement des événements de ce passage…';
    drawJournalReport();
  }
  function paintJournalControls() {
    const { running } = collectionView();
    const loading = !!journalRequest, total = journalPage?.total || 0;
    refreshEvents.disabled = loading;
    newerEvents.disabled = loading || journalOffset === 0;
    olderEvents.disabled = loading || !journalPage || journalOffset + journalPage.items.length >= total;
    latestEvents.disabled = loading; latestEvents.hidden = journalOffset === 0;
    journalNavigation.hidden = !total || (total <= journalPageSize && journalOffset === 0);
    const refresh = running ? journalOffset === 0 ? 'Actualisation automatique pendant la collecte.' : 'Historique affiché. « Revenir au suivi » affiche les nouveaux événements.' : 'Lecture du journal local ; cette action ne lance aucune lecture GLPI.';
    const status = journalPage?.persisted === false ? `Événements de cette tentative conservés en mémoire uniquement. ${refresh}` : refresh;
    if (journalStatus.textContent !== status) journalStatus.textContent = status;
  }
  function drawJournalReport() {
    const { report, running } = collectionView();
    journalReport.replaceChildren(); journalReport.hidden = running || !report;
    if (journalReport.hidden) return;
    const limits = report.limitations || [];
    journalReport.append(el('h3', { text: 'Bilan conservé du passage' }),
      el('p', { text: [report.startedAt ? `Début : ${formatDate(report.startedAt)}` : '', report.completedAt ? `Dernier arrêt : ${formatDate(report.completedAt)}` : ''].filter(Boolean).join(' · ') }),
      el('p', { text: report.message || 'Le bilan de ce passage est conservé.' }));
    if (limits.length) journalReport.append(el('ul', {}, ...limits.map(message => el('li', { text: message }))));
  }
  function drawJournalPage(page) {
    // Only the explicit diagnostic fields are rendered. Never dump an API object
    // or exception: the engine supplies messages without ticket bodies or secrets.
    journalCount.textContent = `${formatNumber(page.total)} événements${page.persisted === false ? '' : ' enregistrés'}${page.items.length ? ` · ${formatNumber(journalOffset + 1)} à ${formatNumber(journalOffset + page.items.length)} affichés` : ''}`;
    journalRows.hidden = !page.items.length; journalEmpty.hidden = !!page.items.length;
    if (!page.items.length) journalEmpty.textContent = collectionView().running ? 'La collecte est en cours ; aucun événement n’est disponible pour le moment.' : collectionView().runId
      ? 'Aucun événement détaillé enregistré pour ce passage. Le bilan conservé ci-dessous reste disponible ; les prochains passages enregistreront leurs étapes.'
      : activity.sessionId ? 'Aucun événement disponible pour cette tentative. Consultez le résultat affiché au-dessus avant de relancer.' : 'Aucune collecte enregistrée pour cette source.';
    journalRows.replaceChildren(...page.items.map(item => {
      const level = Object.hasOwn(eventLevels, item.level) ? item.level : 'info';
      return el('li', { class: 'collection-event', dataset: { level, sequence: item.sequence } },
        el('div', { class: 'collection-event-meta' },
          el('time', { dateTime: item.at, text: eventTime(item.at) }),
          el('span', { class: 'collection-event-level', text: eventLevels[level] }),
          el('strong', { class: 'collection-event-stage', text: item.stage || 'Collecte' }),
          item.ticketId ? el('span', { class: 'collection-event-ticket', text: `Fiche #${item.ticketId}` }) : null),
        el('p', { class: 'collection-event-message', text: item.message }),
        item.code ? el('details', { class: 'collection-event-diagnostic' }, el('summary', { text: 'Référence du diagnostic' }), el('code', { text: item.code })) : null);
    }));
    drawJournalReport();
  }
  async function loadJournal() {
    if (disposed || !journal.open) return;
    syncJournalIdentity();
    const { runId } = collectionView(), identity = journalIdentity, offset = journalOffset;
    if (journalRequest?.identity === identity && journalRequest.offset === offset) return journalRequest.promise;
    const request = { identity, offset, stamp: ++journalGeneration, promise: null };
    journalRequest = request; paintJournalControls();
    const current = () => !disposed && request.stamp === journalGeneration && identity === journalIdentity && offset === journalOffset;
    request.promise = (async () => {
      try {
        const page = await call('library.collection.events', { runId, offset, limit: journalPageSize });
        if (!current()) return;
        if (!Array.isArray(page?.items) || (runId && page.runId !== runId)) throw new Error('Journal indisponible pour ce passage.');
        if (page.total > 0 && offset >= page.total) {
          journalOffset = Math.floor((page.total - 1) / journalPageSize) * journalPageSize;
          return await loadJournal();
        }
        // Keeping unchanged rows also preserves an opened diagnostic reference
        // and keyboard focus during automatic refresh.
        const changed = JSON.stringify(journalPage) !== JSON.stringify(page);
        journalPage = page; journalError.hidden = true;
        if (changed) drawJournalPage(page);
      } catch (error) {
        if (!current() || error?.name === 'AbortError') return;
        journalError.hidden = false;
        journalError.textContent = 'Le journal n’a pas pu être actualisé. Les événements déjà affichés restent disponibles. Utilisez « Actualiser le journal » pour réessayer.';
        if (!journalPage) journalEmpty.textContent = 'Les événements ne sont pas disponibles pour le moment. Cela ne confirme pas la réussite du passage.';
        else journalOffset = journalPage.offset;
      } finally {
        if (journalRequest === request) { journalRequest = null; if (!disposed) paintJournalControls(); }
      }
    })();
    return request.promise;
  }
  async function loadRetries() {
    if (disposed || !retries.open) return;
    const { running, runId, state, report } = collectionView();
    if (running) { retryHost.replaceChildren(el('p', { text: 'La liste des fiches sera actualisée à la fin du passage. Le journal reste consultable pendant la collecte.' })); return; }
    if (!runId) { retryHost.replaceChildren(el('p', { text: 'Aucune liste de fiches enregistrée pour cette tentative. Les erreurs avant la lecture des tickets figurent dans le journal.' })); return; }
    const stamp = ++retryGeneration, offset = retryOffset;
    const allEntries = (state === 'completed' || state === 'attention' && report?.status === 'completed')
      && collectionQuality({ ...report, collectionState: state }, true, model?.readiness).needsAttention && !report?.retryCount;
    const page = await call('library.capture.entries', { runId, offset, limit: journalPageSize, retryOnly: !allEntries });
    if (disposed || stamp !== retryGeneration || collectionView().runId !== runId || activeStates.has(activity.state)) return;
    retryHost.replaceChildren(el('p', { text: page.total
      ? allEntries ? `${formatNumber(page.total)} fiches dans ce passage. Le compteur de reprises ne suffit pas à confirmer leur qualité ; les états et motifs conservés sont affichés ci-dessous.`
        : `${formatNumber(page.total)} fiches à reprendre ou confirmer. Aucun ticket absent de la liste n’est supprimé automatiquement.`
      : `Aucune fiche individuelle à reprendre dans ce passage.${['attention', 'interrupted'].includes(state) ? ' Une erreur de lecture de l’index peut néanmoins nécessiter « Reprendre » : consultez le journal.' : allEntries ? ' La qualité du passage reste à vérifier dans le bilan et le journal.' : ''}` }),
      ...page.items.map(item => el('div', { class: 'collection-retry-entry' },
        el('p', { text: `#${item.ticketId} · ${item.stateLabel} · ${item.message}` }),
        ...(item.detailLimitations || []).map(message => el('p', { class: 'help', text: message })),
        item.hasContent && item.ticketKey ? button('Lire le contenu conservé', safe(() => openLibraryTicket({ api, signal, state: ctx.state, navigate }, item.ticketKey)), { icon: 'ticket' }) : null)),
      ...(page.total > journalPageSize ? [toolbar(button('Précédent', safe(async () => { retryOffset = Math.max(0, retryOffset - journalPageSize); await loadRetries(); }), { disabled: retryOffset === 0 }),
        button('Suivant', safe(async () => { retryOffset += journalPageSize; await loadRetries(); }), { disabled: retryOffset + page.items.length >= page.total }))] : []));
  }
  journal.addEventListener('toggle', () => { if (journal.open) void safe(loadJournal)(); });
  retries.addEventListener('toggle', () => { if (retries.open) void safe(loadRetries)(); });
  const unsubscribe = api.on('library.collection.progress', event => {
    if (disposed) return;
    const wasActive = activeStates.has(activity.state); activity = event; activityRevision++; paint();
    if (wasActive && !activeStates.has(event.state) && !pending) void safe(reload)();
  });
  const timer = setInterval(() => {
    if (polling || disposed || document.visibilityState === 'hidden') return;
    polling = true; const revision = activityRevision;
    void call('library.collection.status').then(async data => {
      if (disposed || revision !== activityRevision) return;
      const wasActive = activeStates.has(activity.state); activity = data; activityRevision++; paint();
      if (wasActive && !activeStates.has(data.state) && !pending) return safe(reload)();
      if (journal.open && journalOffset === 0 && (activeStates.has(data.state) || wasActive)) await loadJournal();
    }).catch(error => { if (!disposed && revision === activityRevision && error?.name !== 'AbortError') detail.textContent = 'État de la collecte non confirmé. Aucun nouvel envoi demandé.'; }).finally(() => { polling = false; });
  }, 2000);
  const dispose = () => { if (disposed) return; disposed = true; generation++; journalGeneration++; retryGeneration++; scope.dispose(); clearInterval(timer); unsubscribe?.(); };
  signal?.addEventListener('abort', dispose, { once: true });
  paint();
  try { await reload(); } catch (error) { if (!disposed) showError(errors, error); }
  return () => { dispose(); signal?.removeEventListener('abort', dispose); };
}
